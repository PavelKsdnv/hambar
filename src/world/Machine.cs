using System;
using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// A vehicle (tractor, combine, truck, ...) that drives the road network.
/// For now it wanders: pick a random road cell, BFS a path along roads, drive
/// waypoint to waypoint, repeat. Movement runs on the fixed physics tick.
/// </summary>
public partial class Machine : Node3D
{
    /// <summary>Top surface of road tiles — machines drive at this height.</summary>
    public const float DeckHeight = 0.1f;

    [Export] public float Speed { get; set; } = 6f;
    [Export] public float TurnSpeed { get; set; } = 8f;
    [Export] public Color BodyColor { get; set; } = new(0.75f, 0.22f, 0.17f);

    private const int RouteAttempts = 8;

    private WorldGrid? _world;
    private Random _rng = new();
    private readonly Queue<Vector3> _waypoints = new();

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
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_world == null)
        {
            return;
        }
        if (_waypoints.Count == 0 && !TryPlanRoute())
        {
            return;
        }

        // Spend the tick's travel budget across waypoints so corners don't
        // lose distance.
        float budget = Speed * (float)delta;
        while (budget > 0f && _waypoints.Count > 0)
        {
            Vector3 target = _waypoints.Peek();
            Vector3 toTarget = target - Position;
            float distance = toTarget.Length();
            if (distance <= budget)
            {
                Position = target;
                budget -= distance;
                _waypoints.Dequeue();
            }
            else
            {
                Vector3 direction = toTarget / distance;
                Position += direction * budget;
                FaceDirection(direction, (float)delta);
                budget = 0f;
            }
        }
    }

    private bool TryPlanRoute()
    {
        Vector2I current = _world!.WorldToCell(Position);
        if (!_world.IsRoad(current))
        {
            GD.PushWarning($"Machine at {Position} is stranded off-road; not moving.");
            SetPhysicsProcess(false);
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

    private void FaceDirection(Vector3 direction, float delta)
    {
        // -Z is the machine's forward.
        float targetYaw = Mathf.Atan2(-direction.X, -direction.Z);
        float weight = 1f - Mathf.Exp(-TurnSpeed * delta);
        Rotation = new Vector3(0f, Mathf.LerpAngle(Rotation.Y, targetYaw, weight), 0f);
    }
}
