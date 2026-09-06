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
/// from state it must treat as read-only. A node that is currently both (a
/// <c>Machine</c>) implements both halves and keeps them in separate methods.
///
/// The loop is fed from <c>_Process</c> — real frame time — and not from
/// <c>_PhysicsProcess</c>: a sim clocked off Godot's 60 Hz physics step would
/// be a divisor of the render loop, which is precisely the coupling this
/// removes.
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

    private SimClock _clock = new();
    private readonly List<ISimSystem> _systems = new();
    private readonly List<ISimView> _views = new();

    /// <summary>The sim node for a node's tree, or null if the scene has none.</summary>
    public static Simulation? For(Node node) =>
        node.IsInsideTree() ? node.GetTree().GetFirstNodeInGroup(GroupName) as Simulation : null;

    /// <summary>The tick schedule. Read it; the loop below is what advances it.</summary>
    public SimClock Clock => _clock;

    public long TickCount => _clock.TickCount;

    public float Alpha => _clock.Alpha;

    public int SystemCount => _systems.Count;

    public override void _EnterTree()
    {
        // In _EnterTree, not _Ready: the whole tree enters before any _Ready
        // runs, so entities spawned from another node's _Ready can still find
        // the sim.
        AddToGroup(GroupName);
        ProcessPriority = TicksFirst;
        _clock = new SimClock(TickRate, MaxTicksPerFrame);
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
    }

    public void Unregister(ISimSystem system) => _systems.Remove(system);

    public void RegisterView(ISimView view)
    {
        if (!_views.Contains(view))
        {
            _views.Add(view);
        }
    }

    public void UnregisterView(ISimView view) => _views.Remove(view);

    public override void _Process(double delta)
    {
        int ticks = _clock.Advance(delta);
        var dt = (float)_clock.TickDelta;
        for (int t = 0; t < ticks; t++)
        {
            for (int i = 0; i < _systems.Count; i++)
            {
                _systems[i].Tick(dt);
            }
        }

        // Every frame, ticks or not: the alpha moves even when no tick ran,
        // which is the whole point above the tick rate.
        float alpha = _clock.Alpha;
        for (int i = 0; i < _views.Count; i++)
        {
            _views[i].Interpolate(alpha);
        }
    }
}
