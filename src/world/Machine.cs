using System;
using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// A vehicle (tractor, combine, truck, ...) that drives the road network.
/// For now it wanders: pick a random road cell, BFS a path along roads, drive
/// waypoint to waypoint, repeat.
///
/// One node, two halves, kept apart on purpose. <see cref="Tick"/> is the sim:
/// it advances <see cref="SimPosition"/> and the yaw on the fixed tick and is
/// the only thing allowed to write them. <see cref="Interpolate"/> is the view:
/// it blends the last two sim poses into the node transform every rendered
/// frame and writes nothing else. The transform is therefore a *drawing* of the
/// machine, not where the machine is — read <see cref="SimPosition"/> for that.
/// </summary>
public partial class Machine : Node3D, ISimSystem, ISimView
{
    /// <summary>Top surface of road tiles — machines drive at this height.</summary>
    public const float DeckHeight = 0.1f;

    [Export] public float Speed { get; set; } = 6f;
    [Export] public float TurnSpeed { get; set; } = 8f;
    [Export] public Color BodyColor { get; set; } = new(0.75f, 0.22f, 0.17f);

    private const int RouteAttempts = 8;

    private WorldGrid? _world;
    private Simulation? _sim;
    private Random _rng = new();
    private readonly Queue<Vector3> _waypoints = new();

    // Sim state, plus the previous tick's copy of it for the view to blend from.
    private Vector3 _position;
    private Vector3 _previousPosition;
    private float _yaw;
    private float _previousYaw;
    private bool _parked;

    /// <summary>Where the machine is as of the last tick. Sim truth.</summary>
    public Vector3 SimPosition => _position;

    /// <summary>Where it was the tick before — the other end of the view's blend.</summary>
    public Vector3 PreviousSimPosition => _previousPosition;

    /// <summary>Set once it strands off-road: it stops ticking but stays registered.</summary>
    public bool Parked => _parked;

    public void Setup(WorldGrid world, Random rng, Color color)
    {
        _world = world;
        _rng = rng;
        BodyColor = color;
    }

    public override void _Ready()
    {
        AddToGroup("machines");
        GetNode<MeshInstance3D>("Model/Body").MaterialOverride =
            new StandardMaterial3D { AlbedoColor = BodyColor };

        // The transform is spawn *input* up to this point — whoever placed the
        // node chose it. From here on the sim owns the pose and the view writes
        // the transform, so snapshot it once and never read it back.
        _position = _previousPosition = Position;
        _yaw = _previousYaw = Rotation.Y;

        _sim = Simulation.For(this);
        if (_sim == null)
        {
            GD.PushWarning($"{Name} found no Simulation node; it will not move.");
            return;
        }
        _sim.Register(this);
        _sim.RegisterView(this);
    }

    public override void _ExitTree()
    {
        _sim?.Unregister(this);
        _sim?.UnregisterView(this);
    }

    /// <summary>
    /// One fixed sim tick. <paramref name="dt"/> never varies, so the travel
    /// budget below is the same every tick and the drive is reproducible.
    /// </summary>
    public void Tick(float dt)
    {
        _previousPosition = _position;
        _previousYaw = _yaw;

        if (_world == null || _parked)
        {
            return;
        }
        if (_waypoints.Count == 0 && !TryPlanRoute())
        {
            return;
        }

        // Spend the tick's travel budget across waypoints so corners don't
        // lose distance.
        float budget = Speed * dt;
        while (budget > 0f && _waypoints.Count > 0)
        {
            Vector3 target = _waypoints.Peek();
            Vector3 toTarget = target - _position;
            float distance = toTarget.Length();
            if (distance <= budget)
            {
                _position = target;
                budget -= distance;
                _waypoints.Dequeue();
            }
            else
            {
                Vector3 direction = toTarget / distance;
                _position += direction * budget;
                FaceDirection(direction, dt);
                budget = 0f;
            }
        }
    }

    /// <summary>
    /// View half: pose the node between the last two sim states. Nothing here
    /// may touch sim state — this runs at frame rate, which is exactly the
    /// coupling the sim loop exists to remove.
    /// </summary>
    public void Interpolate(float alpha)
    {
        Position = _previousPosition.Lerp(_position, alpha);
        Rotation = new Vector3(0f, Mathf.LerpAngle(_previousYaw, _yaw, alpha), 0f);
    }

    private bool TryPlanRoute()
    {
        Vector2I current = _world!.WorldToCell(_position);
        if (!_world.IsRoad(current))
        {
            GD.PushWarning($"Machine at {_position} is stranded off-road; not moving.");
            _parked = true;
            return false;
        }

        for (int i = 0; i < RouteAttempts; i++)
        {
            Vector2I destination = _world.RandomRoadCell(_rng);
            if (destination == current)
            {
                continue;
            }
            List<Vector2I>? path = _world.FindRoadPath(current, destination);
            if (path == null)
            {
                continue;
            }
            // String-pull the cell path into long straight runs so the drive
            // is smooth instead of turning at every cell.
            path = _world.SmoothRoadPath(path);
            for (int j = 1; j < path.Count; j++)
            {
                _waypoints.Enqueue(_world.CellToWorld(path[j]) + Vector3.Up * DeckHeight);
            }
            return true;
        }
        return false;
    }

    private void FaceDirection(Vector3 direction, float dt)
    {
        // -Z is the machine's forward. Exponential smoothing, so the turn takes
        // the same wall-clock time whatever the tick rate is set to.
        float targetYaw = Mathf.Atan2(-direction.X, -direction.Z);
        float weight = 1f - Mathf.Exp(-TurnSpeed * dt);
        _yaw = Mathf.LerpAngle(_yaw, targetYaw, weight);
    }
}
