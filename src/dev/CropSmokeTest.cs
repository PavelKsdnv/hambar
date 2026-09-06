using System;
using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Headless smoke test for M4's field lifecycle: one field driven through the
/// whole cycle — fallow, ploughed, sown, growing, harvestable, stubble and back
/// to ploughed — plus every out-of-order operation the state machine has to
/// refuse. Run with:
/// godot --headless --path . res://scenes/dev/CropSmokeTest.tscn
/// Exits 0 on pass, 1 on failure.
///
/// <b>Stepped, not played</b>, like <see cref="SimSmokeTest"/>: the sim is
/// paused on arrival and every tick is one this test asked for through
/// <see cref="Simulation.Step"/>, so a five-day wheat crop takes milliseconds
/// and nothing about the result depends on how the frames fell. The waits are
/// therefore counted in <i>ticks</i> — a frame count would say nothing about
/// how far a crop got.
///
/// <b>The refusals are the point, not a postscript.</b> Every stage is left
/// having been offered all three operations, because M5's whole job is issuing
/// these from a machine that may arrive late, early, or at a field somebody
/// else already harvested; an operation that silently succeeded would leave a
/// field in a state no sequence of real work could have produced, and nothing
/// downstream would ever notice.
///
/// The cells are searched, never written in, so a seed change cannot quietly
/// turn an assertion into a test of something else — and the fertile and poor
/// patches the growth comparison needs are searched for by <i>fertility</i>,
/// not picked off the map by eye.
///
/// <b>Growth is no longer one tick per tick</b>, so nothing here counts ticks
/// against a hard-coded schedule: a field's rate is read from the system
/// (<see cref="CropSystem.GrowthPerTick(EntityId)"/>) and every wait is
/// budgeted from it. The invariant asserted instead is the one that cannot
/// drift — a stage is exactly the growth banked against its threshold.
///
/// <b>The harvest has to leave something real behind.</b> The sections on the
/// output buffer assert the exact stack a cut deposits, that the projection
/// shown before the cut is the number that lands, and what a harvest with
/// nowhere to put itself does — refuse whole, leaving the crop standing. That
/// last one is checked on a bare system with one harvest of room, because the
/// world's fields are deliberately sized never to fill on their first cut.
///
/// <b>The season table is flattened for this world</b> (see
/// <see cref="_Ready"/>) so the lifecycle walk runs at one rate from end to
/// end; the season's own effect, and the winter stall the game ships with, are
/// asserted on bare systems where the date can be put where the assertion
/// needs it instead of waited for.
/// </summary>
public partial class CropSmokeTest : Node
{
    /// <summary>Size of the rectangle marked as the test's field.</summary>
    private const int FieldWidth = 3;
    private const int FieldHeight = 2;

    /// <summary>
    /// How far from the start the fertility search looks. Wide enough that the
    /// noise field has both a good and a poor patch inside it, and small enough
    /// that both are somewhere a player would plausibly farm.
    /// </summary>
    private const int SearchRadius = 24;

    private WorldGrid _world = null!;
    private Simulation _sim = null!;
    private CropSystem _crops = null!;
    private Field? _field;

    /// <summary>Every stage the field was seen in, in order — the sequence asserted at the end.</summary>
    private readonly List<CropStage> _walked = new();

    /// <summary>
    /// What the field promised at sowing. Kept so the number the player would
    /// have been shown then can be compared against the one that actually lands
    /// in the buffer days later — a projection that drifts is worse than none.
    /// </summary>
    private int _promised;

    private bool _done;
    private bool _failed;

    public override void _Ready()
    {
        Node main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        var world = main.GetNode<WorldGrid>("World");

        // Machines have nothing to do with crops yet (that is M5), and a world
        // without them keeps the hash checks below about the crop rows.
        world.MachineCount = 0;

        // Set before the world is in the tree, because _Ready is what builds
        // the crop system out of these. A flat season table takes the calendar
        // out of the lifecycle walk: the run below spans tens of game days, so
        // with the shipped table it would cross into autumn mid-crop and every
        // timing assertion would be measuring the date instead of the schedule.
        // Seasons get their own sections, on systems whose date is set outright.
        world.CropSeasonGrowth = [1f, 1f, 1f, 1f];
        AddChild(main);

        _world = world;
        _sim = main.GetNode<Simulation>("Sim");
        _crops = world.Crops;

        // Nothing may tick from the wall clock: from here every tick is one
        // this test asked for.
        _sim.SetSpeed(0);
    }

    public override void _Process(double delta)
    {
        if (_done)
        {
            return;
        }
        _done = true;

        CheckANewFieldStartsFallow();
        if (_field == null)
        {
            // Nothing below can say anything without a field to say it about.
            GD.Print("CROP SMOKE TEST FAILED");
            GetTree().Quit(1);
            return;
        }

        CheckFallowRefusesEverythingButPloughing();
        CheckPloughing();
        CheckSowing();
        CheckItGrowsOnSchedule();
        CheckRipeWaitsToBeHarvested();
        CheckTheYieldIsKnownBeforeTheCut();
        CheckHarvestLeavesStubble();
        CheckTheCycleCloses();
        CheckCropStateIsHashed();
        CheckAFullBufferRefusesTheHarvest();
        CheckTheOutputBufferIsHashed();
        CheckThePostHarvestStageIsTunable();
        CheckGrowthIsCountedInTicksNotSeconds();
        CheckBulldozingClosesTheRow();
        CheckTheSeasonIsWiredIntoTheWorld();
        CheckFertilityIsAveragedByChunk();
        CheckAFertileFieldOutgrowsAPoorOne();
        CheckAZeroFactorStallsTheCrop();

        GD.Print(_failed ? "CROP SMOKE TEST FAILED" : "CROP SMOKE TEST PASSED");
        GetTree().Quit(_failed ? 1 : 0);
    }

    /// <summary>
    /// Marking farmland opens a crop row for it, at the stage a field the
    /// player has not touched should be in.
    /// </summary>
    private void CheckANewFieldStartsFallow()
    {
        Vector2I? anchor = FindClearSoilRect();
        Check("the map has a clear patch of soil to mark", anchor != null);
        if (anchor == null)
        {
            return;
        }

        Vector2I far = anchor.Value + new Vector2I(FieldWidth - 1, FieldHeight - 1);
        Field? field = _world.MarkField(WorldGrid.RectCells(anchor.Value, far));
        Check("marking the patch creates a field", field != null);
        if (field == null)
        {
            return;
        }

        _field = field;
        Check("the field has a live crop row", _crops.IsAlive(field.Crop));
        Check("the row knows which field it belongs to",
            _crops.OwnerOf(field.Crop) == field.Id);
        Check("the cell under the cursor reaches the stage through the field",
            _crops.StageOf(_world.GetField(anchor.Value)!.Crop) == CropStage.Fallow);
        Record();
        Check("a field nobody has worked is fallow", Stage() == CropStage.Fallow);
        Check("and has grown nothing", _crops.GrowthOf(field.Crop) == 0f);
    }

    /// <summary>
    /// Sowing unploughed land and harvesting a field with nothing in it are the
    /// two orders M5 will get wrong first. Both have to be refused, and the
    /// refusal has to leave the field exactly where it was.
    /// </summary>
    private void CheckFallowRefusesEverythingButPloughing()
    {
        RefuseAllBut(CropOperation.Plough, "fallow");
        Check("the one thing fallow land is waiting for is the plough",
            _crops.NextOperation(Row) == CropOperation.Plough);

        // Time alone must not move it: an unsown field left running for a day
        // is still an unsown field.
        _sim.Step(_crops.TicksPerDay);
        Check("a fallow field left for a whole day is still fallow",
            Stage() == CropStage.Fallow && _crops.GrowthOf(Row) == 0f);
    }

    private void CheckPloughing()
    {
        Check("ploughing fallow land is accepted",
            _crops.Plough(Row) == CropOpResult.Ok);
        Record();
        Check("and leaves it ploughed", Stage() == CropStage.Ploughed);
        RefuseAllBut(CropOperation.Sow, "ploughed");

        _sim.Step(_crops.TicksPerDay);
        Check("ploughed land with nothing in it does not grow",
            Stage() == CropStage.Ploughed && _crops.GrowthOf(Row) == 0f);
    }

    private void CheckSowing()
    {
        Check("sowing ploughed land is accepted",
            _crops.Sow(Row) == CropOpResult.Ok);
        Record();
        Check("and puts the seed in the ground", Stage() == CropStage.Sown);

        _promised = _crops.ProjectedYield(Row);
        Check($"a sown field already promises what it will give: {_promised} "
            + $"{_crops.HarvestItem.Name}", _promised == ExpectedYield(_crops, Row));
        Check("which nothing has been paid yet — the buffer is empty",
            Output().IsEmpty && !Output().IsFull);

        // Nothing is legal here: the two timed transitions are the only way out
        // of a sown field, which is what makes the crop take real time.
        RefuseAllBut(null, "sown");
    }

    /// <summary>
    /// The two timed transitions. The rate is no longer 1, so the schedule is
    /// asserted twice over: as the invariant that cannot drift — the stage is
    /// exactly which side of its threshold the banked growth is on, checked
    /// every tick of the run — and as the tick count the field's own rate
    /// predicts, which is what catches a factor being quietly dropped from the
    /// product rather than merely misapplied.
    /// </summary>
    private void CheckItGrowsOnSchedule()
    {
        float rate = _crops.GrowthPerTick(Row);
        Check($"the field grows at {rate:F3} of a tick per tick — the product of "
            + $"base {_crops.BaseGrowthRate:F2}, soil {_crops.FertilityOf(Row):F3}, "
            + $"season {_crops.SeasonFactor:F2} and water {_crops.WaterGrowth:F2}",
            Near(rate, _crops.BaseGrowthRate * _crops.FertilityOf(Row)
                * _crops.SeasonFactor * _crops.WaterGrowth));
        Check("which is real ground, so it is slower than a perfect tick and not zero",
            rate > 0f && rate < 1f);

        int sprouted = -1;
        int ripened = -1;
        bool stageTracksGrowth = true;
        for (int tick = 1; tick <= TickBudget(rate) && ripened < 0; tick++)
        {
            _sim.Step();
            stageTracksGrowth &= StageMatchesBankedGrowth();
            if (sprouted < 0 && Stage() == CropStage.Growing)
            {
                sprouted = tick;
                Record();
                RefuseAllBut(null, "growing");
            }
            if (Stage() == CropStage.Harvestable)
            {
                ripened = tick;
                Record();
            }
        }

        Check("through the whole run, the stage is exactly which side of its "
            + "threshold the banked growth is on", stageTracksGrowth);
        Check($"it came up after {sprouted} ticks, the {_crops.DaysToSprout} grown "
            + "day(s) the schedule asks for at that rate",
            sprouted > 0 && Near(sprouted, _crops.TicksToSprout / rate));
        Check($"and ripened after {ripened}, the {_crops.DaysToRipen} grown day(s)",
            ripened > 0 && Near(ripened, _crops.TicksToRipen / rate));
        Check("having banked at least the growth the schedule asked for",
            _crops.GrowthOf(Row) >= _crops.TicksToRipen);
    }

    /// <summary>
    /// Ripe wheat waits. It is what makes the harvest pass something the player
    /// has to arrange rather than a deadline that punishes them for looking
    /// away — spoilage is M9's question, deliberately not this one's.
    /// </summary>
    private void CheckRipeWaitsToBeHarvested()
    {
        float ripe = _crops.GrowthOf(Row);
        _sim.Step(_crops.TicksPerDay * 2);
        Check("a ripe field left standing for two days is still just ripe",
            Stage() == CropStage.Harvestable && _crops.GrowthOf(Row) == ripe);
        RefuseAllBut(CropOperation.Harvest, "harvestable");
    }

    /// <summary>
    /// <b>The number the player is shown has to be the number they get.</b>
    /// #30's inspector displays the projection, so it is asserted here against
    /// the ground it is computed from, against what it said days earlier at
    /// sowing, and — in the section below — against what actually lands in the
    /// buffer. Two of those could agree with a second formula that had drifted
    /// from the first; all four cannot.
    /// </summary>
    private void CheckTheYieldIsKnownBeforeTheCut()
    {
        ItemBuffer output = Output();
        Check($"the field's yield is its ground: {_crops.YieldPerCell:F0} per cell x "
            + $"{_crops.AreaOf(Row)} cells x soil {_crops.FertilityOf(Row):F3} = "
            + $"{_crops.ProjectedYield(Row)}",
            _crops.ProjectedYield(Row) == ExpectedYield(_crops, Row));
        Check("and it is the same promise the field made when it was sown, "
            + "not a number that drifted while it grew", _crops.ProjectedYield(Row) == _promised);
        Check("the world pushed the field's own area down to the row, not a cell count of one",
            _crops.AreaOf(Row) == _field!.CellCount && _field.CellCount > 1);
        Check($"the buffer is sized to {_crops.OutputHarvests} perfect harvests of that "
            + $"field ({output.Capacity} units), so the first cut always fits",
            output.Capacity == (int)(_crops.YieldPerCell * _crops.AreaOf(Row))
                * _crops.OutputHarvests
            && output.HasRoomFor(_crops.ProjectedYield(Row)));
    }

    private void CheckHarvestLeavesStubble()
    {
        ItemBuffer output = Output();
        Check("harvesting a ripe field is accepted",
            _crops.Harvest(Row) == CropOpResult.Ok);
        Record();
        Check($"and puts exactly the {_promised} {_crops.HarvestItem.Name} it promised into "
            + "the field's own buffer — a stack of a typed good, not a global total",
            output.CountOf(ItemTypes.Grain) == _promised && output.Total == _promised);
        Check("as one stack, of grain and of nothing else",
            output.StackCount == 1 && output.StackAt(0) == new ItemStack(ItemTypes.Grain, _promised)
            && output.StackAt(0).Type == _crops.HarvestItem);
        Check("and there is still room, so this harvest was not the backpressure case",
            !output.IsFull && output.Free == output.Capacity - _promised);
        Check("and leaves the stage the tunable says it should",
            Stage() == _crops.PostHarvestStage);
        Check("which by default is stubble, not bare fallow ground",
            _world.StubbleNeedsPloughing && Stage() == CropStage.Stubble);
        Check("with the growth it had banked emptied out",
            _crops.GrowthOf(Row) == 0f);

        // The pacing decision, stated as an assertion: stubble cannot be sown
        // straight back into, so every cycle costs another ploughing pass.
        RefuseAllBut(CropOperation.Plough, "stubble");
    }

    private void CheckTheCycleCloses()
    {
        Check("ploughing stubble in is accepted",
            _crops.Plough(Row) == CropOpResult.Ok);
        Record();
        Check("and the field is ready to sow again", Stage() == CropStage.Ploughed);

        CropStage[] expected =
        [
            CropStage.Fallow, CropStage.Ploughed, CropStage.Sown, CropStage.Growing,
            CropStage.Harvestable, CropStage.Stubble, CropStage.Ploughed,
        ];
        Check($"the field walked {Describe(expected)}", Walked(expected));
    }

    /// <summary>
    /// Crop state has to be inside <see cref="SimStateHash"/>, or the
    /// determinism harness would pass a run whose crops diverged on tick one.
    /// </summary>
    private void CheckCropStateIsHashed()
    {
        Check("the crops are a hashed state source", HasState(_sim, CropSystem.StateSourceName));

        ulong before = SimStateHash.Of(_sim);
        Check("sowing moves the hash",
            _crops.Sow(Row) == CropOpResult.Ok && SimStateHash.Of(_sim) != before);

        ulong sown = SimStateHash.Of(_sim);
        _sim.Step();
        Check("and one tick of growth moves it again", SimStateHash.Of(_sim) != sown);

        // Put the field back where the cycle left it, so the bulldoze section
        // below is not quietly testing a different stage.
        int held = Output().Total;
        Check("the second crop ripened the same way the first did",
            Ripen() && _crops.Harvest(Row) == CropOpResult.Ok);
        Check($"and its {_promised} piled up on the {held} already in the buffer "
            + "rather than replacing them", Output().Total == held + _promised);
    }

    /// <summary>
    /// <b>The backpressure case, which is the reason the capacity exists at
    /// all.</b> Nothing collects from a field in M4, so a buffer that filled
    /// would be a dead end — the decision recorded here is that a harvest with
    /// nowhere to go is <i>refused whole</i>: the crop stays standing, ripe,
    /// and nothing is spilled or half-deposited. M5's collection is what
    /// unblocks it, and <see cref="ItemBuffer.Remove"/> stands in for that.
    ///
    /// On a bare system, because the point is the arithmetic of a full buffer
    /// and the world's real fields are deliberately sized never to hit one on
    /// their first cut.
    /// </summary>
    private void CheckAFullBufferRefusesTheHarvest()
    {
        // One harvest of room, so the second cut has nowhere to go.
        var tight = new CropSystem(
            _crops.TicksPerDay, 1, 2, stubbleNeedsPloughing: false, outputHarvests: 1);
        EntityId row = tight.Create(fieldId: 1, fertility: 1f, area: 2);
        ItemBuffer buffer = tight.OutputOf(row)!;
        int yield = ExpectedYield(tight, row);

        Check($"a field's buffer starts empty, holding {yield} of the "
            + $"{buffer.Capacity} units it has room for",
            buffer.IsEmpty && !buffer.IsFull && buffer.Capacity == yield);
        Check("the first harvest fills it exactly to the brim",
            Cycle(tight, row) == CropStage.Ploughed && buffer.Total == buffer.Capacity);
        Check("which is what being full means, and it is a state the buffer reports",
            buffer.IsFull && buffer.Free == 0);

        Check("a second crop grows on the field regardless — the field is not blocked, "
            + "only the cutting is", Ripen(tight, row) == CropStage.Harvestable);
        Check("but the harvest is refused, and named as full rather than out of stage",
            tight.Harvest(row) == CropOpResult.OutputFull);
        Check("nothing was half-deposited: the buffer holds exactly what it did",
            buffer.Total == buffer.Capacity && buffer.CountOf(ItemTypes.Grain) == yield);
        Check("the crop is left standing, ripe, for as long as that lasts",
            tight.StageOf(row) == CropStage.Harvestable);
        Check("and a scheduler asking whether it can be cut is told no",
            !tight.CanApply(row, CropOperation.Harvest));
        Check("though harvest is still the one thing the field is waiting for",
            tight.NextOperation(row) == CropOperation.Harvest);

        // Room for a unit is not room for a harvest: the yield goes in whole or
        // not at all, which is what keeps a part-cut field out of the state
        // machine.
        buffer.Remove(ItemTypes.Grain, 1);
        Check("one unit of room is not enough, because a harvest is all or nothing",
            !tight.CanApply(row, CropOperation.Harvest)
            && tight.Harvest(row) == CropOpResult.OutputFull);

        int collected = buffer.Remove(ItemTypes.Grain, buffer.Total);
        Check($"collecting the {collected + 1} units in the buffer empties it",
            buffer.IsEmpty && !buffer.IsFull && buffer.CountOf(ItemTypes.Grain) == 0);
        Check("and the crop that was standing all along can now be cut",
            tight.Harvest(row) == CropOpResult.Ok && buffer.Total == yield);
    }

    /// <summary>
    /// The buffer is sim state, so two worlds that harvested differently must
    /// not hash alike — otherwise the determinism harness would pass a run
    /// whose stores diverged. Checked on a pair of bare systems, which is the
    /// smallest thing that can differ by exactly one deposit.
    /// </summary>
    private void CheckTheOutputBufferIsHashed()
    {
        var left = new CropSystem(_crops.TicksPerDay, 1, 2);
        var right = new CropSystem(_crops.TicksPerDay, 1, 2);
        EntityId a = left.Create(1, 1f, 2);
        EntityId b = right.Create(1, 1f, 2);
        Check("two systems built and filled the same way hash the same",
            HashOf(left) == HashOf(right));

        left.OutputOf(a)!.Add(ItemTypes.Grain, 1);
        Check("one unit of grain in one of them moves its hash", HashOf(left) != HashOf(right));
        right.OutputOf(b)!.Add(ItemTypes.Grain, 1);
        Check("and the same unit in the other brings them back together",
            HashOf(left) == HashOf(right));
    }

    /// <summary>
    /// The other side of the pacing tunable, on a bare <see cref="CropSystem"/>
    /// — which is the point of it holding no world: a whole crop schedule can be
    /// run without a scene, a grid or a sim node in sight.
    /// </summary>
    private void CheckThePostHarvestStageIsTunable()
    {
        var loose = new CropSystem(
            _crops.TicksPerDay, daysToSprout: 1, daysToRipen: 2, stubbleNeedsPloughing: false);
        EntityId row = loose.Create(fieldId: 1);
        Check("harvest with re-ploughing switched off lands on ploughed",
            loose.PostHarvestStage == CropStage.Ploughed);
        Check("a bare crop system runs its own schedule",
            Cycle(loose, row) == CropStage.Ploughed);
        Check("so the field can be sown again without another ploughing pass",
            loose.Sow(row) == CropOpResult.Ok && loose.StageOf(row) == CropStage.Sown);
    }

    /// <summary>
    /// Growth is a count of ticks, not of seconds — so two systems fed wildly
    /// different <c>dt</c> values must reach the same stage on the same tick.
    /// Get this wrong and how long a crop takes depends on the tick rate the
    /// game happened to be built with.
    /// </summary>
    private void CheckGrowthIsCountedInTicksNotSeconds()
    {
        var fast = new CropSystem(_crops.TicksPerDay, 1, 2);
        var slow = new CropSystem(_crops.TicksPerDay, 1, 2);
        EntityId a = fast.Create(1);
        EntityId b = slow.Create(1);
        fast.Plough(a);
        fast.Sow(a);
        slow.Plough(b);
        slow.Sow(b);
        for (int tick = 0; tick < fast.TicksToRipen; tick++)
        {
            fast.Tick(1f / 20f);
            slow.Tick(1000f);
        }
        Check("the tick delta cannot change how long a crop takes",
            fast.StageOf(a) == CropStage.Harvestable && slow.StageOf(b) == fast.StageOf(a)
            && fast.GrowthOf(a) == slow.GrowthOf(b));
    }

    /// <summary>
    /// A field is its cells, so bulldozing the last one drops it — and the crop
    /// row has to go with it, or a stale handle would address whatever field is
    /// marked into the recycled slot next. Shrinking a field must not touch its
    /// crop at all.
    /// </summary>
    private void CheckBulldozingClosesTheRow()
    {
        EntityId row = Row;
        CropStage before = Stage();
        var cells = new List<Vector2I>(_field!.Cells);
        Check("the field has more than one cell to bulldoze", cells.Count > 1);

        _world.Clear(cells[0]);
        Check("clearing one cell shrinks the field without touching the crop",
            _crops.IsAlive(row) && _crops.StageOf(row) == before
            && _field.CellCount == cells.Count - 1);

        for (int i = 1; i < cells.Count; i++)
        {
            _world.Clear(cells[i]);
        }
        Check("clearing the last cell closes the crop row",
            !_crops.IsAlive(row) && _crops.Count == 0);
        Check("and every operation on the stale handle is refused",
            _crops.Plough(row) == CropOpResult.NoSuchField
            && _crops.Sow(row) == CropOpResult.NoSuchField
            && _crops.Harvest(row) == CropOpResult.NoSuchField);
        Check("reading a dead handle answers rather than crashing",
            _crops.StageOf(row) == CropStage.Fallow && _crops.GrowthOf(row) == 0f
            && _crops.NextOperation(row) == null && _crops.AreaOf(row) == 0
            && _crops.ProjectedYield(row) == 0);
        Check("and a bulldozed field has no buffer at all, rather than an empty one",
            _crops.OutputOf(row) == null);
        // A tick that walks a slot nobody lives in any more must be a no-op,
        // not an index into a column that was never rewritten.
        _sim.Step(_crops.TicksPerDay);
        Check("and a day of ticking over an empty system changes nothing",
            _crops.Count == 0 && !_crops.IsAlive(row));
    }

    /// <summary>
    /// The season factor comes off the live calendar, not a copy taken when
    /// the world was built — otherwise a date jump (M10's load, a scenario
    /// opening in autumn) would grow crops at last year's season forever.
    /// </summary>
    private void CheckTheSeasonIsWiredIntoTheWorld()
    {
        Check("the world hands its exported season table to the crops",
            SameTable(_crops.SeasonGrowth, _world.CropSeasonGrowth));
        Check("and the calendar, so growth is read against the date the sim is on",
            _crops.CurrentSeason == _sim.Calendar.Season);

        long was = _sim.Calendar.Ticks;
        _sim.Calendar.SetDate(1, Season.Autumn, 1);
        Check("which follows the date rather than a copy taken at startup",
            _crops.CurrentSeason == Season.Autumn
            && _crops.SeasonFactor == _crops.SeasonGrowth[(int)Season.Autumn]);
        _sim.Calendar.SetTicks(was);
    }

    /// <summary>
    /// A field is a region of ground, not a cell, so the number it grows at is
    /// an aggregate — the mean of its chunk means. The two properties worth
    /// pinning are the knob's off position (a chunk of one cell is the plain
    /// cell mean) and that the answer is one the ground could actually have
    /// produced, never outside the range of the cells it covered.
    /// </summary>
    private void CheckFertilityIsAveragedByChunk()
    {
        Vector2I? anchor = FindClearSoilRect();
        Check("there is still a clear patch to aggregate over", anchor != null);
        if (anchor == null)
        {
            return;
        }

        Vector2I far = anchor.Value + new Vector2I(FieldWidth - 1, FieldHeight - 1);
        List<Vector2I> cells = WorldGrid.RectCells(anchor.Value, far);
        int chunkSize = _world.FertilityChunkSize;

        float low = float.MaxValue;
        float high = float.MinValue;
        float total = 0f;
        foreach (Vector2I cell in cells)
        {
            float fertility = _world.GetFertility(cell);
            low = Math.Min(low, fertility);
            high = Math.Max(high, fertility);
            total += fertility;
        }

        _world.FertilityChunkSize = 1;
        Check("a chunk of one cell is the plain average of the cells",
            Near(_world.ChunkedFertility(cells), total / cells.Count));

        _world.FertilityChunkSize = 2;
        float chunked = _world.ChunkedFertility(cells);
        Check($"a {_world.FertilityChunkSize}-cell chunking stays inside the range of "
            + $"the ground it covered ({low:F3}..{high:F3})",
            chunked >= low - 0.001f && chunked <= high + 0.001f);

        _world.FertilityChunkSize = chunkSize;
        Field? field = _world.MarkField(cells);
        Check("a new field's crop row carries the aggregate of its ground",
            field != null
            && Near(_crops.FertilityOf(field.Crop), _world.ChunkedFertility(cells)));
        if (field == null)
        {
            return;
        }

        int store = _crops.OutputOf(field.Crop)!.Capacity;
        _world.Clear(cells[0]);
        Check("and it is taken again when bulldozing takes a cell off the field",
            Near(_crops.FertilityOf(field.Crop), _world.ChunkedFertility(field.Cells)));
        Check("along with the area, so a shrunken field yields less and stores less",
            _crops.AreaOf(field.Crop) == field.CellCount
            && _crops.OutputOf(field.Crop)!.Capacity < store);
        ClearAll(field);
    }

    /// <summary>
    /// <b>The milestone's question, as an assertion.</b> Two fields sown on the
    /// same tick and left for the same number of ticks: the one on better
    /// ground has to be further along, and by the ratio of the ground rather
    /// than by some flat bonus — which is what says fertility is a factor of
    /// the product and not an addend.
    ///
    /// Both patches are searched for by fertility, so a seed change moves where
    /// they are without turning this into a test of two identical fields.
    /// </summary>
    private void CheckAFertileFieldOutgrowsAPoorOne()
    {
        (Vector2I Rich, Vector2I Poor)? patches = FindRichAndPoorRects();
        Check("the map offers a fertile patch and a poor one to compare", patches != null);
        if (patches == null)
        {
            return;
        }

        Field? rich = MarkRect(patches.Value.Rich);
        Field? poor = MarkRect(patches.Value.Poor);
        Check("both are markable", rich != null && poor != null);
        if (rich == null || poor == null)
        {
            return;
        }

        float good = _crops.FertilityOf(rich.Crop);
        float bad = _crops.FertilityOf(poor.Crop);
        Check($"and they are meaningfully different ground: {good:F3} against {bad:F3}",
            good > bad + 0.1f && bad > 0f);

        foreach (Field field in new[] { rich, poor })
        {
            Check($"{field.Name} is ploughed and sown",
                _crops.Plough(field.Crop) == CropOpResult.Ok
                && _crops.Sow(field.Crop) == CropOpResult.Ok);
        }

        // One grown day for the better field, which leaves both still in the
        // ground: a comparison of banked growth means nothing once one of them
        // has ripened and stopped accumulating.
        int ticks = Math.Max(1, (int)(_crops.TicksToSprout / _crops.GrowthPerTick(rich.Crop)));
        _sim.Step(ticks);

        float grownRich = _crops.GrowthOf(rich.Crop);
        float grownPoor = _crops.GrowthOf(poor.Crop);
        Check($"over the same {ticks} ticks the fertile field outgrew the poor one "
            + $"({grownRich:F0} against {grownPoor:F0} grown ticks)", grownRich > grownPoor);
        Check("by the ratio of their soil, because fertility multiplies rather than adds",
            Near(grownRich / grownPoor, good / bad));
        Check("and neither has ripened, so it is growth being compared and not stages",
            _crops.StageOf(rich.Crop) != CropStage.Harvestable
            && _crops.StageOf(poor.Crop) != CropStage.Harvestable);

        // Fertility is paid twice — sooner and more — which is what makes
        // choosing where to farm a decision rather than a formality.
        int fromRich = _crops.ProjectedYield(rich.Crop);
        int fromPoor = _crops.ProjectedYield(poor.Crop);
        Check($"the fertile field also promises the bigger harvest of the two "
            + $"({fromRich} against {fromPoor}), off the same {_crops.AreaOf(rich.Crop)} cells",
            fromRich > fromPoor && _crops.AreaOf(rich.Crop) == _crops.AreaOf(poor.Crop));
        Check("and both promises are exactly what their ground says they are",
            fromRich == ExpectedYield(_crops, rich.Crop)
            && fromPoor == ExpectedYield(_crops, poor.Crop));

        // Area is the other half of the ground, and the only one the world can
        // change under a standing crop. A bare system says it without needing
        // two more patches of map.
        var wide = new CropSystem(_crops.TicksPerDay, 1, 2);
        EntityId one = wide.Create(1, fertility: 1f, area: 1);
        EntityId four = wide.Create(2, fertility: 1f, area: 4);
        wide.Plough(one);
        wide.Sow(one);
        wide.Plough(four);
        wide.Sow(four);
        Check("four times the ground is four times the harvest, and four times the store",
            wide.ProjectedYield(four) == wide.ProjectedYield(one) * 4
            && wide.OutputOf(four)!.Capacity == wide.OutputOf(one)!.Capacity * 4);

        ClearAll(rich);
        ClearAll(poor);
    }

    /// <summary>
    /// <b>The other half of the milestone's question.</b> The factors multiply,
    /// so any one of them at zero has to hold the crop at the stage it is in
    /// rather than merely slow it — an untended crop that finishes anyway would
    /// make M5's whole labour system optional. Checked on bare systems, one per
    /// factor, because the point is that it does not matter which one is zero.
    /// </summary>
    private void CheckAZeroFactorStallsTheCrop()
    {
        const int days = 10;
        Check("the season table the game ships with stops growth in winter outright",
            CropSystem.DefaultSeasonGrowth[(int)Season.Winter] == 0f);

        var dead = new CropSystem(_crops.TicksPerDay, 1, 2);
        Check($"a crop on dead ground is still where it was sown {days} days later",
            Stalls(dead, dead.Create(1, fertility: 0f), days));

        var dry = new CropSystem(_crops.TicksPerDay, 1, 2, true, waterGrowth: 0f);
        Check("and so is one with the water factor at zero — the seam nothing writes yet",
            Stalls(dry, dry.Create(1, fertility: 1f), days));

        var calendar = new GameCalendar(_crops.TicksPerDay);
        calendar.SetDate(1, Season.Winter, 1);
        var wintered = new CropSystem(
            _crops.TicksPerDay, 1, 2, true, 1f, CropSystem.DefaultSeasonGrowth, 1f, calendar);
        EntityId row = wintered.Create(1, fertility: 1f);
        Check("and so is perfect ground in winter, which is the product's whole point",
            Stalls(wintered, row, days));

        // Stalled, not dead: the same crop finishes once the season turns, so
        // an over-wintered field is a delay the player can plan around rather
        // than a loss they cannot see coming.
        calendar.SetDate(2, Season.Spring, 1);
        for (int tick = 0; tick < wintered.TicksToRipen; tick++)
        {
            wintered.Tick(1f / 20f);
        }
        Check("and it picks up again when the season turns, rather than being lost",
            wintered.StageOf(row) == CropStage.Harvestable);
    }

    /// <summary>
    /// Ploughs, sows and runs a bare system for <paramref name="days"/> days —
    /// true if the crop banked nothing at all and never left the sown stage.
    /// </summary>
    private static bool Stalls(CropSystem crops, EntityId row, int days)
    {
        crops.Plough(row);
        crops.Sow(row);
        for (int tick = 0; tick < days * crops.TicksPerDay; tick++)
        {
            crops.Tick(1f / 20f);
        }
        return crops.GrowthPerTick(row) == 0f && crops.GrowthOf(row) == 0f
            && crops.StageOf(row) == CropStage.Sown;
    }

    /// <summary>
    /// Whether the field's stage is exactly which side of its thresholds the
    /// banked growth is on. The schedule assertion that survives a fractional
    /// rate: a tick count can drift by a rounding, this cannot.
    /// </summary>
    private bool StageMatchesBankedGrowth()
    {
        float grown = _crops.GrowthOf(Row);
        return Stage() switch
        {
            CropStage.Sown => grown < _crops.TicksToSprout,
            CropStage.Growing => grown >= _crops.TicksToSprout && grown < _crops.TicksToRipen,
            CropStage.Harvestable => grown >= _crops.TicksToRipen,
            _ => true,
        };
    }

    /// <summary>The field's output buffer. Only called where the field is known live.</summary>
    private ItemBuffer Output() => _crops.OutputOf(Row)!;

    /// <summary>
    /// The yield the ground says the field owes, spelled out here rather than
    /// asked of the system — the assertion is that the two agree, so stating it
    /// twice is the point and not duplication.
    /// </summary>
    private static int ExpectedYield(CropSystem crops, EntityId row) =>
        Math.Max(1, (int)(crops.YieldPerCell * crops.AreaOf(row) * crops.FertilityOf(row)));

    /// <summary>One system's state as a number, for comparing two of them.</summary>
    private static ulong HashOf(CropSystem crops)
    {
        var hash = new StateHash();
        crops.HashState(hash);
        return hash.Value;
    }

    /// <summary>Sows a ploughed row on a bare system and runs it to ripe.</summary>
    private static CropStage Ripen(CropSystem crops, EntityId row)
    {
        crops.Sow(row);
        for (int tick = 0; tick < crops.TicksToRipen; tick++)
        {
            crops.Tick(1f / 20f);
        }
        return crops.StageOf(row);
    }

    /// <summary>Ticks the field needs to ripen at <paramref name="rate"/>, with slack.</summary>
    private int TickBudget(float rate) =>
        rate > 0f ? (int)(_crops.TicksToRipen / rate) + 16 : 0;

    /// <summary>Runs the field to ripe at whatever rate it grows at. False if it never got there.</summary>
    private bool Ripen()
    {
        int budget = TickBudget(_crops.GrowthPerTick(Row));
        for (int tick = 0; tick < budget && Stage() != CropStage.Harvestable; tick++)
        {
            _sim.Step();
        }
        return Stage() == CropStage.Harvestable;
    }

    /// <summary>Marks the standard rectangle anchored at the cell.</summary>
    private Field? MarkRect(Vector2I anchor) => _world.MarkField(WorldGrid.RectCells(
        anchor, anchor + new Vector2I(FieldWidth - 1, FieldHeight - 1)));

    /// <summary>Bulldozes a field away, so the next section searches a clean map.</summary>
    private void ClearAll(Field field)
    {
        foreach (Vector2I cell in new List<Vector2I>(field.Cells))
        {
            _world.Clear(cell);
        }
    }

    /// <summary>
    /// Within 1%, floored at a whole unit. Growth is accumulated one fractional
    /// multiplier at a time over thousands of ticks, so an exact comparison
    /// against <c>threshold / rate</c> would be asserting single-precision
    /// summation order rather than the schedule; 1% still catches a factor
    /// dropped from the product, which moves the answer by half or double.
    /// </summary>
    private static bool Near(float actual, float expected) =>
        Math.Abs(actual - expected) <= Math.Max(0.01f, Math.Abs(expected) * 0.01f);

    private static bool SameTable(IReadOnlyList<float> actual, IReadOnlyList<float> expected)
    {
        if (actual.Count != expected.Count)
        {
            return false;
        }
        for (int i = 0; i < actual.Count; i++)
        {
            if (actual[i] != expected[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Offers the field all three operations and requires every one but
    /// <paramref name="legal"/> to be refused with <see cref="CropOpResult.WrongStage"/>
    /// and to leave the stage untouched. Pass null where nothing is legal.
    /// </summary>
    private void RefuseAllBut(CropOperation? legal, string stage)
    {
        foreach (CropOperation operation in Enum.GetValues<CropOperation>())
        {
            if (operation == legal)
            {
                Check($"{operation.ToString().ToLowerInvariant()} is allowed on {stage} land",
                    _crops.CanApply(Row, operation));
                continue;
            }

            CropStage was = Stage();
            CropOpResult result = _crops.Apply(Row, operation);
            Check($"{operation.ToString().ToLowerInvariant()} is refused on {stage} land",
                result == CropOpResult.WrongStage && Stage() == was
                && !_crops.CanApply(Row, operation));
        }
    }

    /// <summary>Runs a bare system's crop from fallow to harvested, and reports where it landed.</summary>
    private static CropStage Cycle(CropSystem crops, EntityId row)
    {
        crops.Plough(row);
        crops.Sow(row);
        for (int tick = 0; tick < crops.TicksToRipen; tick++)
        {
            crops.Tick(1f / 20f);
        }
        crops.Harvest(row);
        return crops.StageOf(row);
    }

    private EntityId Row => _field!.Crop;

    private CropStage Stage() => _crops.StageOf(Row);

    /// <summary>Notes the stage the field is in now, for the sequence assertion.</summary>
    private void Record()
    {
        _walked.Add(Stage());
    }

    private bool Walked(IReadOnlyList<CropStage> expected)
    {
        if (_walked.Count != expected.Count)
        {
            return false;
        }
        for (int i = 0; i < expected.Count; i++)
        {
            if (_walked[i] != expected[i])
            {
                return false;
            }
        }
        return true;
    }

    private static string Describe(IReadOnlyList<CropStage> stages)
    {
        var names = new string[stages.Count];
        for (int i = 0; i < stages.Count; i++)
        {
            names[i] = CropSystem.Name(stages[i]);
        }
        return string.Join(" → ", names);
    }

    private static bool HasState(Simulation sim, string name)
    {
        foreach (IHashableState state in sim.States)
        {
            if (state.StateName == name)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The top-left corner of the nearest rectangle of clear soil big enough to
    /// mark, searched outward from the origin so the patch is near the starting
    /// area and the search is deterministic.
    /// </summary>
    private Vector2I? FindClearSoilRect()
    {
        for (int radius = 0; radius <= 40; radius++)
        {
            for (int y = -radius; y <= radius; y++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    if (Math.Max(Math.Abs(x), Math.Abs(y)) != radius)
                    {
                        continue;
                    }
                    var anchor = new Vector2I(x, y);
                    if (IsClearSoilRect(anchor))
                    {
                        return anchor;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The best and worst patches of ground within reach of the start, as two
    /// rectangles that do not overlap — the pair the fertility comparison needs.
    /// <b>Searched by fertility rather than written in</b>, and searched with
    /// the same aggregate a field would actually be given, so what the
    /// assertion compares is exactly what the sim would grow at. Null when the
    /// map has nowhere to put two fields.
    /// </summary>
    private (Vector2I Rich, Vector2I Poor)? FindRichAndPoorRects()
    {
        List<Vector2I> anchors = FindClearSoilRects(SearchRadius);
        if (anchors.Count < 2)
        {
            return null;
        }

        Vector2I rich = anchors[0];
        float best = Aggregate(rich);
        foreach (Vector2I anchor in anchors)
        {
            float fertility = Aggregate(anchor);
            if (fertility > best)
            {
                best = fertility;
                rich = anchor;
            }
        }

        // The poorest of the rectangles that do not overlap the best one:
        // two fields cannot share a cell, and the worst patch is often right
        // beside the best where a noise field is steep.
        var taken = new Rect2I(rich, new Vector2I(FieldWidth, FieldHeight));
        Vector2I? poor = null;
        float worst = float.MaxValue;
        foreach (Vector2I anchor in anchors)
        {
            if (taken.Intersects(new Rect2I(anchor, new Vector2I(FieldWidth, FieldHeight))))
            {
                continue;
            }
            float fertility = Aggregate(anchor);
            if (fertility < worst)
            {
                worst = fertility;
                poor = anchor;
            }
        }
        return poor == null ? null : (rich, poor.Value);
    }

    /// <summary>The fertility a field marked at this anchor would be given.</summary>
    private float Aggregate(Vector2I anchor) => _world.ChunkedFertility(WorldGrid.RectCells(
        anchor, anchor + new Vector2I(FieldWidth - 1, FieldHeight - 1)));

    /// <summary>Every markable anchor within the radius, in the ring order above.</summary>
    private List<Vector2I> FindClearSoilRects(int radius)
    {
        var found = new List<Vector2I>();
        for (int y = -radius; y <= radius; y++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                var anchor = new Vector2I(x, y);
                if (IsClearSoilRect(anchor))
                {
                    found.Add(anchor);
                }
            }
        }
        return found;
    }

    private bool IsClearSoilRect(Vector2I anchor)
    {
        for (int dy = 0; dy < FieldHeight; dy++)
        {
            for (int dx = 0; dx < FieldWidth; dx++)
            {
                var cell = anchor + new Vector2I(dx, dy);
                if (!_world.IsSoil(cell) || _world.GetTile(cell) != TileType.Empty)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private void Check(string what, bool ok)
    {
        GD.Print($"{(ok ? "PASS" : "FAIL")}: {what}");
        _failed |= !ok;
    }
}
