using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Owns the fixed simulation tick and drives it from real frame time, so the
/// sim advances at its own steady rate no matter what the renderer manages.
/// This is tech.md's load-bearing decision; everything after M3 assumes it.
///
/// <b>Ownership rule — the view reads sim state, never writes it.</b> The same
/// rule <c>WorldGrid</c> holds over its <c>GridMap</c>, one level up: an
/// <see cref="ISimSystem"/> is the only thing allowed to change sim state, and
/// it may only do so inside <see cref="ISimSystem.Tick"/>. An
/// <see cref="ISimView"/> is handed the interpolation alpha and poses visuals
/// from state it must treat as read-only. A system owns component arrays for
/// many entities (<c>MachineSystem</c>); a view node draws exactly one row of
/// them and writes nothing back.
///
/// The loop is fed from <c>_Process</c> — real frame time — and not from
/// <c>_PhysicsProcess</c>: a sim clocked off Godot's 60 Hz physics step would
/// be a divisor of the render loop, which is precisely the coupling this
/// removes.
///
/// <b>Two clocks live here and they are not the same thing.</b>
/// <see cref="Clock"/> (<see cref="SimClock"/>) is the tick scheduler — real
/// seconds in, ticks out. <see cref="Calendar"/> (<see cref="GameCalendar"/>)
/// is the date — ticks in, days and seasons out. The player's speed control
/// moves the first and never the second, which is why 3× runs the game three
/// times as fast without a day becoming any shorter in ticks.
///
/// <b>Randomness is sim state, and it is derived, not shared.</b>
/// <see cref="Streams"/> hands every system its own named sequence off
/// <see cref="WorldSeed"/>, so a roll added in one system cannot shift what
/// another one draws — see <see cref="RandomStreams"/> for why that is the
/// whole point.
///
/// <b>Speed is not sim state.</b> It decides <i>when</i> ticks happen in real
/// time, never <i>what</i> a tick does, so it is deliberately outside anything
/// #25 hashes or a replay reproduces: the same run at 1× and at 3× is the same
/// sequence of ticks. Pausing simply stops scheduling them — the view and the
/// camera keep running on their own <c>_Process</c>, so the player can still
/// look around a stopped world.
/// </summary>
public partial class Simulation : Node
{
    /// <summary>
    /// Group the one simulation node joins. Machines and other entities are
    /// instanced at runtime, so an exported <c>NodePath</c> cannot reach them;
    /// the group is the single lookup, and each entity does it once in
    /// <c>_Ready</c>.
    /// </summary>
    public const string GroupName = "simulation";

    /// <summary>
    /// Runs before every other node's <c>_Process</c>, so anything reading a
    /// machine's transform this frame reads the pose the sim just produced
    /// rather than last frame's.
    /// </summary>
    private const int TicksFirst = -100;

    [Export] public int TickRate { get; set; } = SimClock.DefaultTickRate;
    [Export] public int MaxTicksPerFrame { get; set; } = SimClock.DefaultMaxTicksPerFrame;

    /// <summary>
    /// Ticks that make one in-game day. Exported because M4 calls crop cadence
    /// the tempo of the entire game, and a playtest that wants to argue about
    /// it should not need a rebuild to. See
    /// <see cref="GameCalendar.DefaultTicksPerDay"/> for why 600.
    /// </summary>
    [Export] public int TicksPerDay { get; set; } = GameCalendar.DefaultTicksPerDay;

    /// <summary>Days that make one season — the other half of the tempo knob.</summary>
    [Export] public int DaysPerSeason { get; set; } = GameCalendar.DefaultDaysPerSeason;

    /// <summary>
    /// <b>The one seed the whole world is derived from.</b> It lives on the sim
    /// rather than on <c>WorldGrid</c> because terrain is only its loudest
    /// consumer, not its owner: M7's prices and M9's hazards draw from the same
    /// seed and have no business reaching into the world grid for it. Changing
    /// it is starting a different world — see <see cref="RandomStreams.Reseed"/>.
    /// </summary>
    [Export] public int WorldSeed { get; set; } = RandomStreams.DefaultWorldSeed;

    /// <summary>
    /// The speed ladder the player steps through, as multipliers on real time.
    /// The UI builds one button per entry, in this order, so adding a 5× for a
    /// playtest is editing this list — the same trick <c>BuildPalette</c> plays
    /// with its tool list.
    ///
    /// <b>The first entry is pause.</b> A ladder whose first step is not 0 is
    /// repaired at load (see <see cref="_EnterTree"/>): the list is otherwise
    /// free-form, but the code has to be able to find the stop.
    /// </summary>
    [Export] public float[] SpeedSteps { get; set; } = [0f, 1f, 2f, 3f];

    /// <summary>Which step play opens on. 1 is the first running speed, i.e. 1×.</summary>
    [Export] public int StartSpeedIndex { get; set; } = 1;

    private SimClock _clock = new();
    private GameCalendar _calendar = new();
    private RandomStreams _streams = new();
    private readonly List<ISimSystem> _systems = new();
    private readonly List<ISimView> _views = new();
    private readonly List<IHashableState> _states = new();
    private float[] _speeds = [0f, 1f];
    private int _speedIndex = 1;

    /// <summary>
    /// The step <see cref="TogglePause"/> comes back to. Pause is a round trip
    /// for the player, so the game has to remember what it was doing — and this
    /// is a UI convenience, not sim state: it changes nothing a tick does.
    /// </summary>
    private int _lastRunningIndex = 1;

    /// <summary>The sim node for a node's tree, or null if the scene has none.</summary>
    public static Simulation? For(Node node) =>
        node.IsInsideTree() ? node.GetTree().GetFirstNodeInGroup(GroupName) as Simulation : null;

    /// <summary>The tick schedule. Read it; the loop below is what advances it.</summary>
    public SimClock Clock => _clock;

    /// <summary>
    /// The date. Ticks in, days and seasons out — see
    /// <see cref="GameCalendar"/>, and do not confuse it with
    /// <see cref="Clock"/>.
    /// </summary>
    public GameCalendar Calendar => _calendar;

    /// <summary>
    /// The world's randomness: one seed, a named stream per system. Sim state —
    /// a stream advances only inside a tick, and #25 hashes it with the rest.
    /// </summary>
    public RandomStreams Streams => _streams;

    public long TickCount => _clock.TickCount;

    public float Alpha => _clock.Alpha;

    public int SystemCount => _systems.Count;

    /// <summary>
    /// Everything that can write itself into a state hash — see
    /// <see cref="SimStateHash"/>. A system that implements
    /// <see cref="IHashableState"/> lands here by registering; state that is
    /// not a system says so with <see cref="RegisterState"/>. The walk over
    /// this list is order-independent, so nothing here has to be kept in any
    /// particular sequence.
    /// </summary>
    public IReadOnlyList<IHashableState> States => _states;

    /// <summary>The speed ladder actually in force, after the export was vetted.</summary>
    public IReadOnlyList<float> Speeds => _speeds;

    /// <summary>Which step of <see cref="Speeds"/> is selected. Step 0 is pause.</summary>
    public int SpeedIndex => _speedIndex;

    /// <summary>The multiplier in force: how much faster than real time the sim is scheduled.</summary>
    public double Speed => _clock.Speed;

    /// <summary>Whether sim time is stopped. The view and the camera are not.</summary>
    public bool IsPaused => _clock.IsPaused;

    public override void _EnterTree()
    {
        // In _EnterTree, not _Ready: the whole tree enters before any _Ready
        // runs, so entities spawned from another node's _Ready can still find
        // the sim.
        AddToGroup(GroupName);
        ProcessPriority = TicksFirst;
        _clock = new SimClock(TickRate, MaxTicksPerFrame);
        _calendar = new GameCalendar(TicksPerDay, DaysPerSeason);
        _streams = new RandomStreams(WorldSeed);
        _speeds = VetSpeeds(SpeedSteps);
        _lastRunningIndex = FirstRunningIndex();
        if (!SetSpeed(StartSpeedIndex))
        {
            SetSpeed(_lastRunningIndex);
        }
    }

    /// <summary>
    /// <b>The one path a speed changes through</b> — the button, a key and a
    /// test all take it, so nothing can end up with the clock running at a
    /// speed the UI is not showing. Returns false, changing nothing, for an
    /// index off the ladder.
    /// </summary>
    public bool SetSpeed(int index)
    {
        if (index < 0 || index >= _speeds.Length)
        {
            return false;
        }

        _speedIndex = index;
        _clock.Speed = _speeds[index];
        if (_speeds[index] > 0f)
        {
            _lastRunningIndex = index;
        }
        return true;
    }

    /// <summary>
    /// Stops sim time, or starts it again at whatever speed it was last
    /// running. What a pause key (or a menu opening, later) calls.
    /// </summary>
    public void TogglePause() => SetSpeed(IsPaused ? _lastRunningIndex : 0);

    /// <summary>
    /// Vets the exported ladder the way <c>Economy</c> vets its starting
    /// balance: repair and complain, never take the scene down over a number
    /// typed in the inspector. Negative and NaN steps are dropped by
    /// <see cref="SimClock.Speed"/> anyway, so all this has to guarantee is
    /// that the list is non-empty and that step 0 is the stop.
    /// </summary>
    private static float[] VetSpeeds(float[]? steps)
    {
        if (steps == null || steps.Length == 0)
        {
            GD.PushWarning("Simulation: SpeedSteps is empty; falling back to pause/1x.");
            return [0f, 1f];
        }

        if (steps[0] != 0f)
        {
            GD.PushWarning("Simulation: SpeedSteps must start with 0 (pause); prepending it.");
            var repaired = new float[steps.Length + 1];
            steps.CopyTo(repaired, 1);
            return repaired;
        }

        return (float[])steps.Clone();
    }

    /// <summary>The first step that actually runs, so pause always has somewhere to come back to.</summary>
    private int FirstRunningIndex()
    {
        for (int i = 0; i < _speeds.Length; i++)
        {
            if (_speeds[i] > 0f)
            {
                return i;
            }
        }
        return 0;
    }

    /// <summary>
    /// Registers a writer of sim state. Register and unregister outside a tick
    /// (<c>_Ready</c>/<c>_ExitTree</c>); an entity that wants to stop moving
    /// should go idle in its own <c>Tick</c> rather than leave mid-loop.
    /// </summary>
    public void Register(ISimSystem system)
    {
        if (!_systems.Contains(system))
        {
            _systems.Add(system);
        }

        // A system that can hash itself is hashed: opting in separately is one
        // more thing a new system can forget, and a hash that quietly stopped
        // covering a system is a determinism check that passes for the wrong
        // reason.
        if (system is IHashableState hashable)
        {
            RegisterState(hashable);
        }
    }

    public void Unregister(ISimSystem system)
    {
        _systems.Remove(system);
        if (system is IHashableState hashable)
        {
            UnregisterState(hashable);
        }
    }

    /// <summary>
    /// Registers state that is not a system — the world grid and the balance
    /// are the two — so the hash covers it. Unregister on the way out: hashing
    /// a freed node's state is a crash, not a wrong number.
    /// </summary>
    public void RegisterState(IHashableState state)
    {
        if (!_states.Contains(state))
        {
            _states.Add(state);
        }
    }

    public void UnregisterState(IHashableState state) => _states.Remove(state);

    public void RegisterView(ISimView view)
    {
        if (!_views.Contains(view))
        {
            _views.Add(view);
        }
    }

    public void UnregisterView(ISimView view) => _views.Remove(view);

    /// <summary>
    /// Runs exactly <paramref name="ticks"/> ticks <b>now</b>, ignoring the
    /// wall clock and the speed setting: the same ticks a real-time run would
    /// have produced, as fast as the CPU can produce them.
    ///
    /// This is how a headless harness drives the sim — #25's determinism runs,
    /// M10's replay after a load. Driving <c>_Process</c> with a synthetic
    /// delta instead would put <see cref="MaxTicksPerFrame"/> and the leftover
    /// accumulator between the caller and the tick count, so a run would depend
    /// on the frame pattern it was fed. Nothing about a tick changes here: the
    /// step is <see cref="SimClock.TickDelta"/> either way, which is what makes
    /// a stepped run and a played run the same world.
    ///
    /// Views are not interpolated — they belong to the frame loop, and a
    /// stepped run has no frames to draw.
    /// </summary>
    public void Step(int ticks = 1)
    {
        if (ticks <= 0)
        {
            return;
        }
        _clock.Step(ticks);
        RunTicks(ticks);
    }

    public override void _Process(double delta)
    {
        RunTicks(_clock.Advance(delta));

        // Every frame, ticks or not: the alpha moves even when no tick ran,
        // which is the whole point above the tick rate.
        float alpha = _clock.Alpha;
        for (int i = 0; i < _views.Count; i++)
        {
            _views[i].Interpolate(alpha);
        }
    }

    /// <summary>The tick loop itself, shared by the frame clock and <see cref="Step"/>.</summary>
    private void RunTicks(int ticks)
    {
        var dt = (float)_clock.TickDelta;
        for (int t = 0; t < ticks; t++)
        {
            // The date moves first, so a system reading it inside its own Tick
            // sees the date of the tick it is running rather than the last one.
            _calendar.Advance();
            for (int i = 0; i < _systems.Count; i++)
            {
                _systems[i].Tick(dt);
            }
        }
    }
}
