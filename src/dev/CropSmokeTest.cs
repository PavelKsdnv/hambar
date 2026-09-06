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
/// turn an assertion into a test of something else.
/// </summary>
public partial class CropSmokeTest : Node
{
    /// <summary>Size of the rectangle marked as the test's field.</summary>
    private const int FieldWidth = 3;
    private const int FieldHeight = 2;

    private WorldGrid _world = null!;
    private Simulation _sim = null!;
    private CropSystem _crops = null!;
    private Field? _field;

    /// <summary>Every stage the field was seen in, in order — the sequence asserted at the end.</summary>
    private readonly List<CropStage> _walked = new();

    private bool _done;
    private bool _failed;

    public override void _Ready()
    {
        Node main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        var world = main.GetNode<WorldGrid>("World");

        // Machines have nothing to do with crops yet (that is M5), and a world
        // without them keeps the hash checks below about the crop rows.
        world.MachineCount = 0;
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
        CheckHarvestLeavesStubble();
        CheckTheCycleCloses();
        CheckCropStateIsHashed();
        CheckThePostHarvestStageIsTunable();
        CheckGrowthIsCountedInTicksNotSeconds();
        CheckBulldozingClosesTheRow();

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

        // Nothing is legal here: the two timed transitions are the only way out
        // of a sown field, which is what makes the crop take real time.
        RefuseAllBut(null, "sown");
    }

    /// <summary>
    /// The two timed transitions, each checked on the tick either side of its
    /// threshold — an off-by-one here is a crop that ripens a day early for the
    /// rest of the game, and no other assertion would catch it.
    /// </summary>
    private void CheckItGrowsOnSchedule()
    {
        _sim.Step(_crops.TicksToSprout - 1);
        Check("a sown field has not come up the tick before it is due",
            Stage() == CropStage.Sown);
        _sim.Step();
        Record();
        Check($"and is growing after {_crops.DaysToSprout} day(s)", Stage() == CropStage.Growing);

        _sim.Step(_crops.TicksToRipen - _crops.TicksToSprout - 1);
        Check("a growing crop is not ripe the tick before it is due",
            Stage() == CropStage.Growing);
        RefuseAllBut(null, "growing");
        _sim.Step();
        Record();
        Check($"and is harvestable after {_crops.DaysToRipen} day(s)",
            Stage() == CropStage.Harvestable);
        // Exactly, not approximately: a full-rate tick adds a whole 1, so a
        // crop that ripened a tick early or late would show up here as a number
        // that is off by one rather than as a rounding difference.
        Check("having banked exactly the growth the schedule asked for",
            _crops.GrowthOf(Row) == _crops.TicksToRipen);
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

    private void CheckHarvestLeavesStubble()
    {
        Check("harvesting a ripe field is accepted",
            _crops.Harvest(Row) == CropOpResult.Ok);
        Record();
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
        _sim.Step(_crops.TicksToRipen);
        Check("the second crop ripened the same way the first did",
            Stage() == CropStage.Harvestable
            && _crops.Harvest(Row) == CropOpResult.Ok);
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
            && _crops.NextOperation(row) == null);
        // A tick that walks a slot nobody lives in any more must be a no-op,
        // not an index into a column that was never rewritten.
        _sim.Step(_crops.TicksPerDay);
        Check("and a day of ticking over an empty system changes nothing",
            _crops.Count == 0 && !_crops.IsAlive(row));
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
