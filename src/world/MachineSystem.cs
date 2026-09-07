using System;
using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Every machine's state and movement, in flat arrays indexed by
/// <see cref="EntityId.Index"/>. This is the <see cref="ISimSystem"/> half of
/// what <c>Machine</c> used to be: it is the only thing that writes a machine's
/// position, yaw, route or crew, and it does so for all of them in one tick
/// loop. The <c>Machine</c> node keeps only the <see cref="ISimView"/> half and
/// reads back through this class.
///
/// Why arrays rather than a node each — beyond tech.md asking for it: entity
/// state has to be walkable cheaply and in a fixed order, because the
/// determinism hash and save/load both have to visit all of it. A slot walk
/// gives that; a scene subtree gives an order that depends on spawn history and
/// a per-entity allocation to go with it.
///
/// <b>A machine with no route does not move.</b> Nothing in here decides where
/// a vehicle should go: the tick spends a travel budget along a route somebody
/// else put there, and an empty route is the ordinary state of an idle
/// vehicle rather than a fault. Until #34 and #36 give the player a way to type
/// an order, <i>every</i> machine is idle — which is the point. It used to
/// wander to a random road cell forever, and that scaffolding hid the one
/// thing M5 has to be able to show: that a farm only does what it was told to.
///
/// <b>Not a Node.</b> It has no tree lifecycle of its own — <c>WorldGrid</c>
/// constructs it, registers it with the <c>Simulation</c>, and is the only
/// thing that spawns into it. Keeping it a plain class is also what keeps
/// <c>src/sim/</c> free of any dependency on the world: the store and the hash
/// know nothing about roads.
/// </summary>
public sealed class MachineSystem : ISimSystem, IHashableState
{
    /// <summary>
    /// The name this system's rows are filed under in a state hash.
    ///
    /// It used to name an RNG stream as well, back when a machine chose its own
    /// destinations. Nothing here draws a random number any more — an authored
    /// order is not a roll — so the stream is gone and only the state name is
    /// left. Where a bought vehicle <i>parks</i> is still random, but that is
    /// the world laying the game out and comes off
    /// <c>WorldGrid.SpawnStreamName</c>.
    /// </summary>
    public const string StateSourceName = "machines";

    /// <summary>Slots the arrays start at; they grow by doubling from there.</summary>
    private const int InitialCapacity = 16;

    private readonly WorldGrid _world;
    private readonly EntityStore _entities = new();
    private readonly SpatialHash _occupancy = new();

    // Parallel component arrays, all sized to _entities.SlotCount. Kept
    // deliberately few — a column is added by the milestone that reads it, not
    // in advance of one.
    private Vector3[] _position = [];
    private Vector3[] _previousPosition = [];
    private float[] _yaw = [];
    private float[] _previousYaw = [];
    private float[] _speed = [];
    private float[] _turnSpeed = [];
    private MachineKind[] _kind = [];
    private Vector2I[] _cell = [];
    private int[] _routeNext = [];

    // Who drives it: the worker's handle, or EntityId.None for a vehicle
    // nobody was assigned to. The link is stored on this side rather than on
    // the worker because the question asked every tick from #36 onwards is
    // "does this vehicle have a driver", which has to be O(1) on the row; the
    // reverse lookup is a walk over a handful of slots and needs no index.
    // Fleet is the only thing that should write it — see SetCrew.
    private EntityId[] _crew = [];

    // What the vehicle is carrying. A reference-typed column like the crop
    // system's output buffers, and for the same reason: the contents are a
    // container with a capacity, not a number, and every carrier in the game
    // holds the same type so a haul can move between any two of them.
    private ItemBuffer[] _cargo = [];

    // The one reference-typed column left. The route list is allocated once per
    // slot and refilled in place, so a machine that plans a thousand routes
    // allocates one list, and recycling a slot reuses it. It is also derived
    // state — a route can be replanned from the order that produced it — so
    // nothing needs to save or hash it. Nothing fills one yet: the road
    // pathfinding and the travel budget below are kept whole for #36, which
    // supplies the orders rather than the driving.
    private List<Vector3>[] _route = [];

    public MachineSystem(WorldGrid world) => _world = world;

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
    /// because a recycled slot still holds the previous occupant's values —
    /// including, load-bearingly, the crew: a new vehicle must never arrive
    /// already driven by whoever drove the one whose slot it took.
    /// </summary>
    public EntityId Spawn(
        Vector3 position, MachineKind kind, float speed, float turnSpeed, int cargoCapacity)
    {
        EntityId id = _entities.Create();
        EnsureCapacity(_entities.SlotCount);
        int i = id.Index;

        _position[i] = _previousPosition[i] = position;
        _yaw[i] = _previousYaw[i] = 0f;
        _speed[i] = speed;
        _turnSpeed[i] = turnSpeed;
        _kind[i] = kind;
        _crew[i] = EntityId.None;
        // A fresh buffer, not the recycled slot's one emptied out — the same
        // rule CropSystem.Create keeps. A generation bump invalidates a stale
        // *handle*, and nothing invalidates a stale reference to the object
        // behind it: reusing the instance would show whoever still held the
        // dead machine's buffer the new machine's load.
        _cargo[i] = new ItemBuffer(cargoCapacity);
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

    /// <summary>What sort of vehicle it is: the row's copy of its identity.</summary>
    public MachineKind KindOf(EntityId id) =>
        _entities.IsAlive(id) ? _kind[id.Index] : MachineKind.Tractor;

    /// <summary>The cell the machine is filed under in <see cref="Occupancy"/>.</summary>
    public Vector2I CellOf(EntityId id) =>
        _entities.IsAlive(id) ? _cell[id.Index] : Vector2I.Zero;

    /// <summary>
    /// Whether the machine has nothing left to drive. <b>The ordinary state of
    /// a vehicle</b>, not a fault: it sits exactly where it is until an order
    /// gives it a route.
    /// </summary>
    public bool IsIdle(EntityId id) =>
        !_entities.IsAlive(id) || _routeNext[id.Index] >= _route[id.Index].Count;

    /// <summary>
    /// The worker assigned to this vehicle, raw. It can be a <b>stale</b>
    /// handle — a driver who was let go leaves theirs behind — so ask the
    /// labour pool whether it is still alive before believing it, which is what
    /// <c>Fleet.DriverOf</c> is for.
    /// </summary>
    public EntityId CrewOf(EntityId id) =>
        _entities.IsAlive(id) ? _crew[id.Index] : EntityId.None;

    /// <summary>
    /// The vehicle this worker is assigned to, or <see cref="EntityId.None"/>.
    /// A walk over the live slots rather than a second index: handles carry
    /// their generation, so a re-hired worker cannot match the handle their
    /// predecessor left on a vehicle, and a farm never has enough machines for
    /// the scan to be worth an index that a save would then have to rebuild.
    /// </summary>
    public EntityId VehicleCrewedBy(EntityId worker)
    {
        if (worker == EntityId.None)
        {
            return EntityId.None;
        }
        for (int i = 0; i < _entities.SlotCount; i++)
        {
            if (_entities.IsAliveSlot(i) && _crew[i] == worker)
            {
                return _entities.IdAt(i);
            }
        }
        return EntityId.None;
    }

    /// <summary>
    /// Writes the link, unvalidated — the door <c>Fleet</c> goes through after
    /// it has checked that both ends exist and that neither is spoken for. The
    /// split is the one <c>WorldGrid.SetTile</c> keeps against the build tools:
    /// one place that writes, one place that decides. Nothing else should call
    /// this, because nothing else enforces one worker to one vehicle.
    /// </summary>
    public bool SetCrew(EntityId vehicle, EntityId worker)
    {
        if (!_entities.IsAlive(vehicle))
        {
            return false;
        }
        _crew[vehicle.Index] = worker;
        return true;
    }

    /// <summary>
    /// What the machine is carrying, or null for a dead handle — the buffer
    /// itself, so a caller can hand it straight to
    /// <see cref="ItemBuffer.Transfer"/>. Writing to it is a sim state write
    /// and belongs inside a tick; nothing loads or unloads a machine yet, which
    /// is #34's job.
    /// </summary>
    public ItemBuffer? CargoOf(EntityId id) =>
        _entities.IsAlive(id) ? _cargo[id.Index] : null;

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

            // The previous pose is snapshotted for every live machine, moving
            // or not: the view blends from it every frame, and a stale one
            // would make a parked machine appear to drift.
            _previousPosition[i] = _position[i];
            _previousYaw[i] = _yaw[i];

            // No route, no movement, and nothing that goes looking for one.
            if (_routeNext[i] >= _route[i].Count)
            {
                continue;
            }

            Drive(i, dt);
            Refile(i);
        }
    }

    /// <summary>The name this system's rows are filed under in a state hash.</summary>
    public string StateName => StateSourceName;

    /// <summary>
    /// Every column, walked in ascending slot order. The route and the
    /// occupancy index are left out because both are derived — a route can be
    /// replanned from the order behind it and the index rebuilt from the
    /// cells — and hashing derived state is what makes a save/load comparison
    /// fail over a correct rebuild.
    /// </summary>
    public void HashState(StateHash hash)
    {
        _entities.HashState(hash);
        for (int i = 0; i < _entities.SlotCount; i++)
        {
            if (!_entities.IsAliveSlot(i))
            {
                continue;
            }

            hash.Write(i);
            hash.Write(_position[i]);
            hash.Write(_previousPosition[i]);
            hash.Write(_yaw[i]);
            hash.Write(_previousYaw[i]);
            hash.Write(_speed[i]);
            hash.Write(_turnSpeed[i]);
            // What it is and who drives it are both state a save has to carry:
            // two farms that spent the same money on different vehicles, or
            // put their one worker in a different cab, are different farms.
            hash.Write((int)_kind[i]);
            hash.Write(_crew[i].Index);
            hash.Write(_crew[i].Generation);
            hash.Write(_cell[i]);
            // The load is state a tick moves and a save writes; two runs that
            // hauled differently must not hash alike. The buffer's own stack
            // order is canonical, so this walk needs no fold.
            _cargo[i].HashState(hash);
        }
    }

    /// <summary>
    /// Spends the tick's travel budget across waypoints, so a machine turning a
    /// corner loses no distance. Kept whole through the removal of wandering:
    /// #36 supplies the route, and this is what drives it.
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
        Array.Resize(ref _kind, capacity);
        Array.Resize(ref _crew, capacity);
        Array.Resize(ref _cell, capacity);
        Array.Resize(ref _routeNext, capacity);
        Array.Resize(ref _cargo, capacity);
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
