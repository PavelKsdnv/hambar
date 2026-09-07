using System;
using Godot;

namespace Arable;

/// <summary>
/// Headless determinism harness (M3's acceptance test): runs the same world
/// twice from one seed, hashes the whole sim after <b>every</b> tick, and
/// requires the two sequences of hashes to be identical — then runs a third
/// world from a different seed and requires it to differ, so a passing run is
/// evidence rather than a tautology. Run with:
/// godot --headless --path . res://scenes/dev/SimSmokeTest.tscn
/// Exits 0 on pass, 1 on failure.
///
/// <b>Per tick, not at the end.</b> A single hash at the last tick answers "did
/// they diverge" with a boolean, and hunting a determinism bug from a boolean
/// means bisecting the tick count by hand. Hashing every tick costs one walk
/// per tick and turns the failure into "they first differed on tick 412", which
/// is a breakpoint. That is the whole reason this harness exists, so the
/// reporting is the feature and not a nicety — see <see cref="Compare"/>.
///
/// <b>Stepped, not played.</b> Each run instances <c>Main.tscn</c>, pauses it
/// before the frame loop can schedule anything, and drives every tick itself
/// through <see cref="Simulation.Step"/>. So the whole comparison happens
/// inside one frame at CPU speed rather than over 30 real seconds, and nothing
/// about the result depends on how the frames fell — which is exactly the
/// property under test.
///
/// <b>One world at a time.</b> The runs are sequential, with a frame between
/// them for the previous scene to actually free: two live <c>Main.tscn</c>
/// instances mean two nodes in the <c>simulation</c> group, and
/// <see cref="Simulation.For"/> answers whichever it finds first — the second
/// world would silently wire itself to the first world's clock and streams.
/// </summary>
public partial class SimSmokeTest : Node
{
    /// <summary>
    /// Ticks each run is driven for: one in-game day, so the calendar rolls a
    /// day inside the window and machines plan several routes each.
    /// </summary>
    private const int Ticks = GameCalendar.DefaultTicksPerDay;

    /// <summary>
    /// Machines spawned into each run. More than one, because a single mover
    /// cannot show whether the slot walk or the RNG draws are order-dependent.
    /// </summary>
    private const int Machines = 3;

    /// <summary>Frames spent letting a freed world leave the tree before the next enters.</summary>
    private const int SettleFrames = 1;

    private int _stage;
    private int _settle;
    private bool _failed;
    private ulong[] _first = [];
    private ulong[] _second = [];
    private ulong[] _otherSeed = [];

    /// <summary>A state source with one number in it, for the order checks below.</summary>
    private sealed class Probe : IHashableState
    {
        public Probe(string name, int value)
        {
            StateName = name;
            Value = value;
        }

        public string StateName { get; }

        public int Value { get; set; }

        public void HashState(StateHash hash) => hash.Write(Value);
    }

    public override void _Process(double delta)
    {
        if (_settle > 0)
        {
            _settle--;
            return;
        }

        switch (_stage++)
        {
            case 0:
                CheckHashWalker();
                _settle = SettleFrames;
                break;
            case 1:
                CheckWorldTakesTheSimsStreams();
                _settle = SettleFrames;
                break;
            case 2:
                _first = Run("run 1", 0, probeWorldState: true);
                _settle = SettleFrames;
                break;
            case 3:
                _second = Run("run 2", 0);
                _settle = SettleFrames;
                break;
            case 4:
                _otherSeed = Run("run 3", 1);
                _settle = SettleFrames;
                break;
            default:
                Compare();
                GD.Print(_failed ? "SIM SMOKE TEST FAILED" : "SIM SMOKE TEST PASSED");
                GetTree().Quit(_failed ? 1 : 0);
                break;
        }
    }

    /// <summary>
    /// A world reads its randomness from the <c>Simulation</c>, even when
    /// something asked it for a seed before it was in the tree.
    ///
    /// The reason this is worth a check of its own: the answer to "which
    /// registry" decides whether the world's rolls are inside
    /// <see cref="SimStateHash"/> at all, and getting it wrong is invisible to
    /// every other test here. A world running on a private registry is still
    /// perfectly deterministic — both runs build the same private registry from
    /// the same seed — so the comparison below would go on passing while
    /// quietly hashing none of the world's randomness. A determinism harness
    /// that has stopped watching is worse than none.
    /// </summary>
    private void CheckWorldTakesTheSimsStreams()
    {
        Node main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        var sim = main.GetNode<Simulation>("Sim");
        var world = main.GetNode<WorldGrid>("World");

        // The access the lazy resolve exists to tolerate: there is no tree yet,
        // so there is no Simulation to find. Whatever it answers here must not
        // become the world's registry for the rest of the run.
        int seedWhileDetached = world.WorldSeed;
        sim.WorldSeed = seedWhileDetached + 4242;
        world.MachineCount = 0;
        AddChild(main);

        Check("a world asked for its seed before the tree still takes the sim's",
            world.WorldSeed == sim.WorldSeed && world.WorldSeed != seedWhileDetached);
        // _Ready opens both of the world's streams. They have to have been
        // opened on the sim's registry, which is the one the walker hashes.
        Check("and the streams it opened are on the hashed registry",
            sim.Streams.Has(WorldGrid.SpawnStreamName)
            && sim.Streams.Has(MachineSystem.StreamName));

        main.QueueFree();
    }

    /// <summary>
    /// The walker's own claims, on a bare <see cref="Simulation"/> with nothing
    /// in it: that the hash covers the clock, the calendar and every RNG
    /// stream's position, that it ignores what is not sim state, and that it
    /// does not depend on the order state sources registered in. A bare sim is
    /// used rather than the game because each claim needs exactly one thing to
    /// change, which a running world will not hold still for.
    /// </summary>
    private void CheckHashWalker()
    {
        var sim = new Simulation();
        AddChild(sim);
        sim.SetSpeed(0);

        ulong empty = SimStateHash.Of(sim);
        Check("hashing the same state twice gives the same number",
            SimStateHash.Of(sim) == empty);

        // The RNG, which is the state a shared-pool bug moves and nothing else
        // notices. This is also M10's case: a stream saved and restored has to
        // bring the hash back with it.
        RandomStream stream = sim.Streams.For("probe");
        ulong opened = SimStateHash.Of(sim);
        Check("opening a stream changes the hash", opened != empty);
        ulong saved = stream.State;
        stream.NextUInt();
        ulong drawn = SimStateHash.Of(sim);
        Check("one random draw moves the hash", drawn != opened);
        stream.State = saved;
        Check("restoring the stream's state restores the hash",
            SimStateHash.Of(sim) == opened);

        // The clock and the calendar.
        sim.Step();
        ulong ticked = SimStateHash.Of(sim);
        Check("a tick moves the hash", ticked != opened);
        Check("the step ran without the wall clock",
            sim.TickCount == 1 && sim.Calendar.Ticks == 1 && sim.Alpha == 0f
            && sim.Clock.DroppedTicks == 0);

        // ...and what is deliberately outside it. Speed decides when ticks
        // happen, never what one does, so two players watching the same world
        // at different speeds must hash the same.
        sim.SetSpeed(2);
        Check("the speed setting is not sim state", SimStateHash.Of(sim) == ticked);
        sim.SetSpeed(0);

        // A speed that is not a number at all. Infinity is the dangerous one:
        // it is neither NaN nor negative, so it slips past a bare NaN check,
        // and it does not merely run fast — it makes the accumulator infinite
        // for good, since taking any finite backlog off infinity leaves
        // infinity. The clock would then tick the per-frame cap every frame
        // forever and overflow DroppedTicks through a saturating cast.
        var poisoned = new SimClock();
        foreach (double bad in new[]
                 { double.PositiveInfinity, double.NegativeInfinity, double.NaN, -1.0 })
        {
            poisoned.Speed = bad;
            Check($"a speed of {bad} is refused", poisoned.Speed == 0.0);
        }

        poisoned.Speed = double.PositiveInfinity;
        poisoned.Advance(1.0);
        poisoned.Speed = 1.0;
        Check("and having been refused, it left the clock usable",
            poisoned.Advance(poisoned.TickDelta) == 1 && poisoned.DroppedTicks == 0);

        // The other door into the same arithmetic.
        var deltaPoisoned = new SimClock { Speed = 1.0 };
        deltaPoisoned.Advance(double.PositiveInfinity);
        Check("an infinite frame delta is ignored rather than banked",
            deltaPoisoned.TickCount == 0 && deltaPoisoned.DroppedTicks == 0
            && deltaPoisoned.Advance(deltaPoisoned.TickDelta) == 1);

        // Registration order. Two sources, registered both ways round: the walk
        // over them is a fold, so the order cannot reach the result. This is
        // the same guarantee that keeps a Dictionary of placed tiles honest.
        var alpha = new Probe("alpha", 1);
        var beta = new Probe("beta", 2);
        sim.RegisterState(alpha);
        sim.RegisterState(beta);
        ulong forward = SimStateHash.Of(sim);
        Check("registered state is in the hash", forward != ticked);
        sim.UnregisterState(alpha);
        sim.UnregisterState(beta);
        sim.RegisterState(beta);
        sim.RegisterState(alpha);
        Check("the hash does not depend on the order state registered in",
            SimStateHash.Of(sim) == forward);
        alpha.Value = 9;
        Check("changing one source's state changes the hash",
            SimStateHash.Of(sim) != forward);
        Check("two sources cannot swap their values unnoticed",
            Fold(new Probe("alpha", 1), new Probe("beta", 2))
                != Fold(new Probe("alpha", 2), new Probe("beta", 1)));

        // The fold itself, stated directly: members in any order, same number.
        ulong ab = StateHash.Fold(StateHash.Fold(0UL, 11UL), 22UL);
        ulong ba = StateHash.Fold(StateHash.Fold(0UL, 22UL), 11UL);
        Check("the unordered fold is commutative", ab == ba && ab != 0UL);

        sim.QueueFree();
    }

    /// <summary>Folds two state sources the way the walker does, for the check above.</summary>
    private static ulong Fold(IHashableState first, IHashableState second)
    {
        var member = new StateHash();
        ulong fold = 0UL;
        foreach (IHashableState source in new[] { first, second })
        {
            member.Reset();
            member.Write(source.StateName);
            source.HashState(member);
            fold = StateHash.Fold(fold, member.Value);
        }
        return fold;
    }

    /// <summary>
    /// One run: a fresh <c>Main.tscn</c>, paused on arrival, stepped
    /// <see cref="Ticks"/> ticks, hashed after each one. Index 0 of the result
    /// is the world before any tick ran, so a divergence reported at 0 means
    /// the two worlds were never the same to begin with.
    ///
    /// The seed is taken from the scene and shifted, rather than written in
    /// here, so the runs follow whatever <c>Main.tscn</c> is authored with.
    /// Exports are set before <c>AddChild</c> because the sim builds its
    /// streams in <c>_EnterTree</c> and the world generates terrain in
    /// <c>_Ready</c> — both inside that call.
    /// </summary>
    private ulong[] Run(string label, int seedShift, bool probeWorldState = false)
    {
        Check($"{label} starts with no other world in the tree",
            GetTree().GetNodesInGroup(Simulation.GroupName).Count == 0);

        Node main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        var sim = main.GetNode<Simulation>("Sim");
        var world = main.GetNode<WorldGrid>("World");
        int seed = sim.WorldSeed + seedShift;
        sim.WorldSeed = seed;
        world.MachineCount = Machines;
        AddChild(main);

        // Nothing may tick from the wall clock: from here every tick is one
        // this method asked for.
        sim.SetSpeed(0);

        var hashes = new ulong[Ticks + 1];
        ulong startedMs = Time.GetTicksMsec();
        hashes[0] = SimStateHash.Of(sim);
        for (int tick = 1; tick <= Ticks; tick++)
        {
            sim.Step();
            hashes[tick] = SimStateHash.Of(sim);
        }

        GD.Print($"{label}: seed {seed}, {world.Machines.Count} machines, "
            + $"{sim.TickCount} ticks in {Time.GetTicksMsec() - startedMs} ms, "
            + $"{sim.Calendar.TotalDays} day(s), tick 0 {hashes[0]:X16}, "
            + $"tick {Ticks} {hashes[Ticks]:X16}");

        Check($"{label} ran exactly the ticks it was asked for",
            sim.TickCount == Ticks && sim.Calendar.Ticks == Ticks
            && sim.Clock.DroppedTicks == 0);
        Check($"{label} spawned the machines it was asked for",
            world.Machines.Count == Machines);
        Check($"{label} hashes the world, the machines and the balance",
            HasState(sim, "world") && HasState(sim, MachineSystem.StreamName)
            && HasState(sim, "economy"));
        Check($"{label} moved: the hash is not what it started as",
            hashes[Ticks] != hashes[0]);

        if (probeWorldState)
        {
            CheckPlacementIsHashed(sim, world);
            CheckCarrierInventoriesAreHashed(sim, world);
        }

        main.QueueFree();
        return hashes;
    }

    /// <summary>
    /// The placement layer is the <c>Dictionary</c> in all this, so it is worth
    /// showing it reaches the hash at all — an unordered fold that quietly
    /// folded nothing would pass every other check here. Run after the run's
    /// hashes are recorded, on a world that is about to be freed.
    /// </summary>
    private void CheckPlacementIsHashed(Simulation sim, WorldGrid world)
    {
        ulong before = SimStateHash.Of(sim);
        Vector2I? free = FindEmptyCell(world);
        Check("an unbuilt cell exists to place on", free != null);
        if (free == null)
        {
            return;
        }

        world.SetTile(free.Value, TileType.Road);
        Check("placing a tile changes the hash", SimStateHash.Of(sim) != before);
        world.SetTile(free.Value, TileType.Empty);
        Check("clearing it again brings the hash back", SimStateHash.Of(sim) == before);
    }

    /// <summary>
    /// What a carrier is holding is sim state, so a run whose truck came home
    /// loaded must not hash like one whose truck came home empty. Worth its own
    /// check for the reason the placement layer is: a column left out of a
    /// <c>HashState</c> is invisible to every other assertion here — both runs
    /// would agree perfectly while the harness watched none of the cargo.
    ///
    /// Run on a world about to be freed, after its hashes are recorded, so
    /// writing to sim state outside a tick cannot reach the comparison.
    /// </summary>
    private void CheckCarrierInventoriesAreHashed(Simulation sim, WorldGrid world)
    {
        ulong before = SimStateHash.Of(sim);
        ItemBuffer? cargo = world.Machines.CargoOf(world.Machines.IdAt(0));
        Check("a spawned machine has a cargo hold with room in it",
            cargo != null && cargo.IsEmpty && cargo.Capacity > 0);
        if (cargo == null)
        {
            return;
        }

        cargo.Add(ItemTypes.Grain, 1);
        Check("one unit in a machine's hold moves the hash", SimStateHash.Of(sim) != before);
        cargo.Remove(ItemTypes.Grain, 1);
        Check("unloading it again brings the hash back", SimStateHash.Of(sim) == before);

        Vector2I? free = FindEmptyCell(world);
        if (free == null)
        {
            return;
        }

        Structure? store = world.PlaceStructure([free.Value]);
        ulong placed = SimStateHash.Of(sim);
        Check("a placed building has a store sized by the world's tunable",
            store != null && store.Storage.IsEmpty
            && store.Storage.Capacity == world.StructureStorageCapacity);
        if (store == null)
        {
            return;
        }

        store.Storage.Add(ItemTypes.Grain, 1);
        Check("one unit in a building's store moves the hash", SimStateHash.Of(sim) != placed);
        store.Storage.Remove(ItemTypes.Grain, 1);
        Check("taking it out again brings the hash back", SimStateHash.Of(sim) == placed);
        world.SetTile(free.Value, TileType.Empty);
    }

    /// <summary>
    /// The comparison this test exists for. The same seed must agree on every
    /// tick; a different seed must not.
    /// </summary>
    private void Compare()
    {
        int diverged = FirstDivergence(_first, _second);
        Report("same seed", _first, _second, diverged);
        Check($"two {Ticks}-tick runs from the same seed hash identically",
            diverged < 0);

        int seedDiverged = FirstDivergence(_first, _otherSeed);
        Report("other seed", _first, _otherSeed, seedDiverged);
        Check("a different seed hashes differently", seedDiverged >= 0);

        // The reporting is the point of hashing per tick, and its path is the
        // one a passing run never takes — so it is taken here, on a copy with
        // one tick knocked out of it.
        var perturbed = (ulong[])_first.Clone();
        int at = Ticks / 3;
        perturbed[at] ^= 1UL;
        Check("a divergence is reported at the tick it happened",
            FirstDivergence(_first, perturbed) == at);
        Check("a run that agrees everywhere reports no divergence",
            FirstDivergence(_first, (ulong[])_first.Clone()) < 0);
    }

    /// <summary>
    /// The first tick the two runs disagree on, or −1 when they never do.
    /// Index 0 is the state before tick 1 ran.
    /// </summary>
    private static int FirstDivergence(ulong[] a, ulong[] b)
    {
        int shared = Math.Min(a.Length, b.Length);
        for (int i = 0; i < shared; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }
        return a.Length == b.Length ? -1 : shared;
    }

    private static void Report(string what, ulong[] a, ulong[] b, int at)
    {
        if (at < 0)
        {
            GD.Print($"{what}: {a.Length - 1} ticks, no divergence (final {a[^1]:X16})");
            return;
        }

        string agreed = at == 0 ? "never" : $"through tick {at - 1}";
        GD.Print($"{what}: agreed {agreed}, first differ at tick {at} — "
            + $"{a[at]:X16} vs {b[at]:X16}");
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
    /// A cell with nothing placed on it, searched rather than written in — the
    /// convention the other smoke tests keep, so a seed change cannot turn this
    /// into an assertion about something else.
    /// </summary>
    private static Vector2I? FindEmptyCell(WorldGrid world)
    {
        int half = world.MapSize / 2;
        for (int z = -half; z <= half; z++)
        {
            for (int x = -half; x <= half; x++)
            {
                var cell = new Vector2I(x, z);
                if (world.GetTile(cell) == TileType.Empty)
                {
                    return cell;
                }
            }
        }
        return null;
    }

    private void Check(string what, bool ok)
    {
        GD.Print($"{(ok ? "PASS" : "FAIL")}: {what}");
        _failed |= !ok;
    }
}
