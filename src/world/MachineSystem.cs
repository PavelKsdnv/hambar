using System;
using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Every machine's state and movement, in flat arrays indexed by
/// <see cref="EntityId.Index"/>. This is the <see cref="ISimSystem"/> half of
/// what <c>Machine</c> used to be: it is the only thing that writes a machine's
/// position, yaw or route, and it does so for all of them in one tick loop.
/// The <c>Machine</c> node keeps only the <see cref="ISimView"/> half and reads
/// back through this class.
///
/// Why arrays rather than a node each — beyond tech.md asking for it: entity
/// state has to be walkable cheaply and in a fixed order, because the
/// determinism hash and save/load both have to visit all of it. A slot walk
/// gives that; a scene subtree gives an order that depends on spawn history and
/// a per-entity allocation to go with it.
///
/// <b>Not a Node.</b> It has no tree lifecycle of its own — <c>WorldGrid</c>
/// constructs it, registers it with the <c>Simulation</c>, and is the only
/// thing that spawns into it. Keeping it a plain class is also what keeps
/// <c>src/sim/</c> free of any dependency on the world: the store and the hash
/// know nothing about roads.
/// </summary>
public sealed class MachineSystem : ISimSystem
{
    /// <summary>
    /// The system's named RNG stream. One stream for the whole system, not one
    /// per machine: the tick walks slots in a fixed order, so a single sequence
    /// is already reproducible, and #24's guarantee is about draws crossing
    /// <i>system</i> boundaries. It is also what removed the last reference-typed
    /// column here — a per-machine generator is a per-entity allocation and one
    /// more thing for a save to walk, buying nothing.
    /// </summary>
    public const string StreamName = "machines";

    /// <summary>Random destinations tried before a machine gives up for this tick.</summary>
    private const int RouteAttempts = 8;

    /// <summary>Slots the arrays start at; they grow by doubling from there.</summary>
    private const int InitialCapacity = 16;

    private readonly WorldGrid _world;
    private readonly RandomStream _rng;
    private readonly EntityStore _entities = new();
    private readonly SpatialHash _occupancy = new();

    // Parallel component arrays, all sized to _entities.SlotCount. Kept
    // deliberately few: M4/M5 decide what machines really carry, and inventing
    // components now would be guessing.
    private Vector3[] _position = [];
    private Vector3[] _previousPosition = [];
    private float[] _yaw = [];
    private float[] _previousYaw = [];
    private float[] _speed = [];
    private float[] _turnSpeed = [];
    private bool[] _parked = [];
    private Vector2I[] _cell = [];
    private int[] _routeNext = [];

    // The one reference-typed column left. The route list is allocated once per
    // slot and refilled in place, so a machine that plans a thousand routes
    // allocates one list, and recycling a slot reuses it. It is also derived
    // state — the route can be replanned from the world — so nothing needs to
    // save or hash it.
    private List<Vector3>[] _route = [];

    public MachineSystem(WorldGrid world, RandomStream rng)
    {
        _world = world;
        _rng = rng;
    }

    /// <summary>Machines alive right now.</summary>
    public int Count => _entities.Count;

    /// <summary>Slots ever used — the exclusive bound of a walk over the arrays.</summary>
    public int SlotCount => _entities.SlotCount;

    /// <summary>Which machines are on which cell. Derived from the arrays; do not save it.</summary>
    public SpatialHash Occupancy => _occupancy;

    public bool IsAlive(EntityId id) => _entities.IsAlive(id);

    /// <summary>The handle for a slot, for turning a walk or a query hit into an id.</summary>
    public EntityId IdAt(int slot) => _entities.IdAt(slot);

    public bool IsAliveSlot(int slot) => _entities.IsAliveSlot(slot);

    /// <summary>
    /// Adds a machine at a world position. Every component is written here,
    /// because a recycled slot still holds the previous occupant's values.
    /// </summary>
    public EntityId Spawn(Vector3 position, float speed, float turnSpeed)
    {
        EntityId id = _entities.Create();
        EnsureCapacity(_entities.SlotCount);
        int i = id.Index;

        _position[i] = _previousPosition[i] = position;
        _yaw[i] = _previousYaw[i] = 0f;
        _speed[i] = speed;
        _turnSpeed[i] = turnSpeed;
        _parked[i] = false;
        _route[i].Clear();
        _routeNext[i] = 0;
        _cell[i] = _world.WorldToCell(position);
        _occupancy.Insert(_cell[i], id);
        return id;
    }

    /// <summary>
    /// Removes a machine. Returns false for a stale handle, so despawning
    /// twice cannot free whatever moved into the slot in between.
    /// </summary>
    public bool Despawn(EntityId id)
    {
        if (!_entities.IsAlive(id))
        {
            return false;
        }
        _occupancy.Remove(_cell[id.Index], id);
        _route[id.Index].Clear();
        return _entities.Destroy(id);
    }

    /// <summary>Where the machine is as of the last tick. Sim truth.</summary>
    public Vector3 PositionOf(EntityId id) =>
        _entities.IsAlive(id) ? _position[id.Index] : Vector3.Zero;

    /// <summary>Where it was the tick before — the other end of the view's blend.</summary>
    public Vector3 PreviousPositionOf(EntityId id) =>
        _entities.IsAlive(id) ? _previousPosition[id.Index] : Vector3.Zero;

    public float YawOf(EntityId id) => _entities.IsAlive(id) ? _yaw[id.Index] : 0f;

    public float PreviousYawOf(EntityId id) =>
        _entities.IsAlive(id) ? _previousYaw[id.Index] : 0f;

    /// <summary>Set once a machine strands off-road: it stays alive but stops moving.</summary>
    public bool IsParked(EntityId id) => _entities.IsAlive(id) && _parked[id.Index];

    /// <summary>The cell the machine is filed under in <see cref="Occupancy"/>.</summary>
    public Vector2I CellOf(EntityId id) =>
        _entities.IsAlive(id) ? _cell[id.Index] : Vector2I.Zero;

    /// <summary>Waypoints left in the machine's current route.</summary>
    public int RouteLengthOf(EntityId id) =>
        _entities.IsAlive(id) ? _route[id.Index].Count - _routeNext[id.Index] : 0;

    /// <summary>
    /// One fixed sim tick for every live machine, walked in ascending slot
    /// order so the result never depends on spawn history or on any hash-map
    /// enumeration. <paramref name="dt"/> never varies, so the travel budget is
    /// the same every tick and the drive is reproducible.
    /// </summary>
    public void Tick(float dt)
    {
        for (int i = 0; i < _entities.SlotCount; i++)
        {
            if (!_entities.IsAliveSlot(i))
            {
                continue;
            }

            // The previous pose is snapshotted for every live machine, parked
            // or not: the view blends from it every frame, and a stale one
            // would make a parked machine appear to drift.
            _previousPosition[i] = _position[i];
            _previousYaw[i] = _yaw[i];

            if (_parked[i])
            {
                continue;
            }
            if (_routeNext[i] >= _route[i].Count && !TryPlanRoute(i))
            {
                continue;
            }

            Drive(i, dt);
            Refile(i);
        }
    }

    /// <summary>
    /// Spends the tick's travel budget across waypoints, so a machine turning a
    /// corner loses no distance.
    /// </summary>
    private void Drive(int i, float dt)
    {
        float budget = _speed[i] * dt;
        List<Vector3> route = _route[i];
        while (budget > 0f && _routeNext[i] < route.Count)
        {
            Vector3 target = route[_routeNext[i]];
            Vector3 toTarget = target - _position[i];
            float distance = toTarget.Length();
            if (distance <= budget)
            {
                _position[i] = target;
                budget -= distance;
                _routeNext[i]++;
            }
            else
            {
                Vector3 direction = toTarget / distance;
                _position[i] += direction * budget;
                FaceDirection(i, direction, dt);
                budget = 0f;
            }
        }
    }

    /// <summary>Keeps the spatial index in step after the machine moved.</summary>
    private void Refile(int i)
    {
        Vector2I cell = _world.WorldToCell(_position[i]);
        if (cell != _cell[i])
        {
            _occupancy.Move(_cell[i], cell, _entities.IdAt(i));
            _cell[i] = cell;
        }
    }

    /// <summary>
    /// Wander: pick a random road cell, BFS to it, string-pull the result and
    /// queue the waypoints. The smoothing is what stops a stair-stepped
    /// diagonal road turning into a zigzag drive.
    /// </summary>
    private bool TryPlanRoute(int i)
    {
        Vector2I current = _world.WorldToCell(_position[i]);
        if (!_world.IsRoad(current))
        {
            GD.PushWarning($"Machine {_entities.IdAt(i)} at {_position[i]} "
                + "is stranded off-road; not moving.");
            _parked[i] = true;
            return false;
        }

        for (int attempt = 0; attempt < RouteAttempts; attempt++)
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
            path = _world.SmoothRoadPath(path);

            List<Vector3> route = _route[i];
            route.Clear();
            _routeNext[i] = 0;
            for (int j = 1; j < path.Count; j++)
            {
                route.Add(_world.CellToWorld(path[j]) + Vector3.Up * Machine.DeckHeight);
            }
            return true;
        }
        return false;
    }

    private void FaceDirection(int i, Vector3 direction, float dt)
    {
        // -Z is the machine's forward. Exponential smoothing, so the turn takes
        // the same wall-clock time whatever the tick rate is set to.
        float targetYaw = Mathf.Atan2(-direction.X, -direction.Z);
        float weight = 1f - Mathf.Exp(-_turnSpeed[i] * dt);
        _yaw[i] = Mathf.LerpAngle(_yaw[i], targetYaw, weight);
    }

    /// <summary>
    /// Grows every column at once, by doubling — <see cref="EntityStore"/>
    /// hands out one slot at a time, and resizing to the exact count would make
    /// spawning quadratic. Columns are only ever resized together: an index is
    /// valid in all of them or in none, which is the invariant that lets the
    /// tick loop index without bounds checks of its own.
    /// </summary>
    private void EnsureCapacity(int slots)
    {
        if (slots <= _position.Length)
        {
            return;
        }

        int old = _position.Length;
        int capacity = Math.Max(InitialCapacity, old);
        while (capacity < slots)
        {
            capacity *= 2;
        }

        Array.Resize(ref _position, capacity);
        Array.Resize(ref _previousPosition, capacity);
        Array.Resize(ref _yaw, capacity);
        Array.Resize(ref _previousYaw, capacity);
        Array.Resize(ref _speed, capacity);
        Array.Resize(ref _turnSpeed, capacity);
        Array.Resize(ref _parked, capacity);
        Array.Resize(ref _cell, capacity);
        Array.Resize(ref _routeNext, capacity);
        Array.Resize(ref _route, capacity);

        // An empty List allocates no backing array until the first Add, so
        // filling the new slots up front costs nothing and keeps the column
        // non-null everywhere.
        for (int i = old; i < capacity; i++)
        {
            _route[i] = new List<Vector3>();
        }
    }
}
