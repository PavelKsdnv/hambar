using System;
using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Every machine's state and movement, in flat arrays indexed by
/// <see cref="EntityId.Index"/>. This is the <see cref="ISimSystem"/> half of
/// what <c>Machine</c> used to be: it is the only thing that writes a machine's
/// position, yaw, route, crew or order, and it does so for all of them in one
/// tick loop. The <c>Machine</c> node keeps only the <see cref="ISimView"/> half
/// and reads back through this class.
///
/// Why arrays rather than a node each — beyond tech.md asking for it: entity
/// state has to be walkable cheaply and in a fixed order, because the
/// determinism hash and save/load both have to visit all of it. A slot walk
/// gives that; a scene subtree gives an order that depends on spawn history and
/// a per-entity allocation to go with it.
///
/// <b>A machine with no order does not move on its own.</b> #34's order is the
/// only thing that ever fills the route below — see
/// <see cref="AdvanceOrder"/> — so an unordered, or undriven, vehicle sitting
/// exactly where it was left is still the ordinary state rather than a fault.
/// It used to wander to a random road cell forever, and that scaffolding hid
/// the one thing M5 has to be able to show: that a farm only does what it was
/// told to. This system calls <c>WorldGrid.FindRoadPath</c> and stops at the
/// road cell beside a target's footprint — deliberately, per #36, rather than
/// entering it; lane discipline and several vehicles queuing for the same
/// door stay out of scope.
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
    // the worker because the question asked every tick, from #34's order
    // execution on, is "does this vehicle have a driver", which has to be
    // O(1) on the row; the reverse lookup is a walk over a handful of slots
    // and needs no index. Fleet is the only thing that should write it — see
    // SetCrew.
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
    // nothing needs to save or hash it. Filled by AdvanceOrder (below) from
    // WorldGrid.FindRoadPath, smoothed by WorldGrid.SmoothRoadPath — #36's
    // wiring of an order's target into that call, and its NoRoadAccess block
    // when no path exists.
    private List<Vector3>[] _route = [];

    // The order a vehicle is running, and the one queued to replace it once
    // the current cycle is not mid-commitment (see AdvanceOrder). Two columns
    // rather than one nullable, because "no change queued" and "queued to go
    // idle" are different things and both have to be representable —
    // Order?[] alone cannot tell "nothing pending" from "pending is null".
    private Order?[] _order = [];
    private bool[] _hasPendingOrder = [];
    private Order?[] _pendingOrder = [];

    // What StepOf/BlockOf read back. Written once per vehicle per tick, from
    // the order and the world alone — never saved or hashed, the way the
    // route above is not: a load can always re-derive "what am I doing" from
    // the order it restores, so nothing here is information a save would lose
    // by leaving it out.
    private OrderStep[] _step = [];
    private OrderBlock[] _block = [];

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
        // A recycled slot must never arrive already running whoever held it
        // before's order, the same reason it must never arrive already
        // crewed by them.
        _order[i] = null;
        _hasPendingOrder[i] = false;
        _pendingOrder[i] = null;
        _step[i] = OrderStep.Idle;
        _block[i] = OrderBlock.None;
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
    /// <see cref="ItemBuffer.Transfer"/>. A haul order is what loads and
    /// unloads it, inside <see cref="RunHaulOrder"/>; reading it from outside
    /// a tick (a HUD, a test) is safe, writing to it is not.
    /// </summary>
    public ItemBuffer? CargoOf(EntityId id) =>
        _entities.IsAlive(id) ? _cargo[id.Index] : null;

    /// <summary>Waypoints left in the machine's current route.</summary>
    public int RouteLengthOf(EntityId id) =>
        _entities.IsAlive(id) ? _route[id.Index].Count - _routeNext[id.Index] : 0;

    /// <summary>
    /// Whether this kind of vehicle can be given this kind of order — the
    /// whole of "validate an order's action against the vehicle kind". Reads
    /// only the two facts <see cref="MachineSpec"/> exposes for exactly this
    /// (see its doc comment): a haul needs somewhere to put the load, and
    /// every other order needs a vehicle that works fields at all. Nothing
    /// finer — a tractor ploughing is no more or less "allowed" here than a
    /// harvester ploughing, because the roster does not say otherwise and a
    /// half-guessed rule here is exactly the taxonomy <see cref="MachineKind"/>
    /// was kept free of.
    /// </summary>
    public static bool Accepts(MachineKind kind, OrderKind orderKind)
    {
        MachineSpec spec = MachineKinds.Spec(kind);
        return orderKind == OrderKind.HaulGoods ? spec.CargoCapacity > 0 : spec.WorksFields;
    }

    /// <summary>
    /// Gives a vehicle an order to run, repeating until this is called again.
    /// Pass null to send it back to idle once its current cycle allows it.
    ///
    /// <b>Never applied mid-commitment.</b> The order lands in a queue of one
    /// and only takes over once the vehicle is not actively driving and
    /// (for a haul) not holding a load — see <see cref="AdvanceOrder"/> — so a
    /// vehicle that already has a cycle running finishes it on the order it
    /// started with. A vehicle with nothing running (no order, or standing
    /// idle already) starts the new one on its very next tick, because there
    /// is no cycle there to protect.
    /// </summary>
    public SetOrderResult SetOrder(EntityId vehicle, Order? order)
    {
        if (!_entities.IsAlive(vehicle))
        {
            return SetOrderResult.NoSuchVehicle;
        }
        if (order is { } o && !Accepts(_kind[vehicle.Index], o.Kind))
        {
            return SetOrderResult.WrongVehicleKind;
        }
        _pendingOrder[vehicle.Index] = order;
        _hasPendingOrder[vehicle.Index] = true;
        return SetOrderResult.Ok;
    }

    /// <summary>The order actually running, or null for an idle vehicle.</summary>
    public Order? OrderOf(EntityId id) => _entities.IsAlive(id) ? _order[id.Index] : null;

    /// <summary>
    /// What <see cref="SetOrder"/> queued and has not taken over yet, or null
    /// when nothing is queued — distinct from "queued to go idle", which
    /// reads as a non-null pending value of <c>null</c>. Mostly for a test or
    /// a HUD to show "this will change once the current run finishes".
    /// </summary>
    public Order? PendingOrderOf(EntityId id) =>
        _entities.IsAlive(id) && _hasPendingOrder[id.Index] ? _pendingOrder[id.Index] : null;

    /// <summary>What the vehicle is doing right now, in words — see <see cref="OrderText"/>.</summary>
    public OrderStep StepOf(EntityId id) => _entities.IsAlive(id) ? _step[id.Index] : OrderStep.Idle;

    /// <summary>
    /// Why it is not making progress, or <see cref="OrderBlock.None"/> when it
    /// is. <b>Not a fault</b> — a blocked vehicle is waiting on the world or
    /// on a crew, not misbehaving, which is the whole of what M5's tracker
    /// means by "the player can tell why an idle vehicle is idle".
    /// </summary>
    public OrderBlock BlockOf(EntityId id) => _entities.IsAlive(id) ? _block[id.Index] : OrderBlock.None;

    public bool IsBlocked(EntityId id) => BlockOf(id) != OrderBlock.None;

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

            AdvanceOrder(i);

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

            // The order and what is queued behind it: two farms running the
            // same fleet on different standing orders are different farms,
            // and a re-point that has not taken over yet is still a fact
            // about this vehicle a save has to carry. Step and block are not
            // here — see their fields' doc comment.
            hash.Write(_order[i].HasValue);
            _order[i]?.HashState(hash);
            hash.Write(_hasPendingOrder[i]);
            if (_hasPendingOrder[i])
            {
                hash.Write(_pendingOrder[i].HasValue);
                _pendingOrder[i]?.HashState(hash);
            }
        }
    }

    /// <summary>
    /// Spends the tick's travel budget across waypoints, so a machine turning a
    /// corner loses no distance. Kept whole through the removal of wandering:
    /// order execution supplies the route, and this is what drives it.
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
    /// The order half of a tick: promotes a queued order if this is a safe
    /// point to, then runs whatever is now active and leaves
    /// <see cref="_step"/>/<see cref="_block"/> saying what happened. Nothing
    /// here moves the machine directly — it only ever sets a route, which the
    /// ordinary drive below then spends the tick's travel budget along, so a
    /// vehicle that just got a fresh route this tick still moves this tick.
    ///
    /// <b>The promotion test is the whole of "next cycle, not mid-cycle".</b>
    /// A vehicle counts as mid-commitment while a route is still in flight or
    /// while it is holding cargo — the two ways a vehicle can be partway
    /// through doing something physical — and a queued order waits behind
    /// either. Neither is true of a vehicle that arrived and is simply
    /// blocked (wrong stage, nothing to load, no driver): that is not a
    /// commitment, it is standing still, so a re-point there takes over on
    /// the very next tick rather than waiting for a stage the standing order
    /// might never reach again.
    /// </summary>
    private void AdvanceOrder(int i)
    {
        bool committed = _routeNext[i] < _route[i].Count || !_cargo[i].IsEmpty;
        if (_hasPendingOrder[i] && !committed)
        {
            _order[i] = _pendingOrder[i];
            _hasPendingOrder[i] = false;
            _pendingOrder[i] = null;
        }

        if (_order[i] is not { } order)
        {
            _step[i] = OrderStep.Idle;
            _block[i] = OrderBlock.None;
            return;
        }

        // No driver, no work — the same rule Fleet states about an idle
        // worker and an idle vehicle, applied to whichever one this vehicle
        // is currently missing.
        if (!HasDriver(i))
        {
            _step[i] = OrderStep.Idle;
            _block[i] = OrderBlock.NoDriver;
            return;
        }

        switch (order.Kind)
        {
            case OrderKind.PloughField:
                RunFieldOrder(i, order.FieldId, CropOperation.Plough, OrderStep.Ploughing);
                break;
            case OrderKind.SowField:
                RunFieldOrder(i, order.FieldId, CropOperation.Sow, OrderStep.Sowing);
                break;
            case OrderKind.HarvestField:
                RunFieldOrder(i, order.FieldId, CropOperation.Harvest, OrderStep.Harvesting);
                break;
            case OrderKind.HaulGoods:
                RunHaulOrder(i, order);
                break;
        }
    }

    /// <summary>
    /// Whether somebody is actually in the cab — the <b>resolved</b> answer,
    /// not the raw handle. <see cref="CrewOf"/> can hand back a driver who has
    /// since been let go, and believing it would leave a dismissed worker's
    /// tractor ploughing on by itself while <c>Fleet.IsCrewed</c> reports the
    /// cab empty: the sim doing work nobody is paying a wage for, which is the
    /// inverse of the rule this milestone exists to demonstrate. Resolving on
    /// read is what <c>Fleet.DriverOf</c> already does, and for the same reason
    /// — sweeping the stale handle would be a state write triggered by a read.
    ///
    /// A scene with no <see cref="WorldGrid.Labour"/> believes the handle as
    /// written, so a dev scene that puts a fabricated crew in a cab still runs
    /// its orders.
    /// </summary>
    private bool HasDriver(int i)
    {
        EntityId crew = _crew[i];
        if (crew == EntityId.None)
        {
            return false;
        }
        LabourPool? labour = _world.Labour;
        return labour == null || labour.IsAlive(crew);
    }

    /// <summary>
    /// Drives to a field and, once there, ploughs, sows or harvests it —
    /// whichever <paramref name="operation"/> the order named — every tick,
    /// so the moment the field becomes ready the very next tick applies it.
    /// Calls straight into <see cref="CropSystem"/>'s own door
    /// (<see cref="CropSystem.Apply"/> through <see cref="CropSystem.CanApply"/>);
    /// this is a caller of that state machine, never a second copy of it.
    /// </summary>
    private void RunFieldOrder(int i, int fieldId, CropOperation operation, OrderStep workStep)
    {
        Field? field = _world.GetField(fieldId);
        if (field == null)
        {
            _step[i] = OrderStep.DrivingToField;
            _block[i] = OrderBlock.NoSuchField;
            return;
        }

        Vector2I? access = _world.FindRoadAccess(field.Cells);
        if (access == null)
        {
            _step[i] = OrderStep.DrivingToField;
            _block[i] = OrderBlock.NoRoadAccess;
            return;
        }

        if (!HasArrived(i, access.Value))
        {
            _step[i] = OrderStep.DrivingToField;
            _block[i] = DriveTowards(i, access.Value) ? OrderBlock.None : OrderBlock.NoRoadAccess;
            return;
        }

        _step[i] = workStep;
        EntityId crop = field.Crop;
        if (!CropSystem.Allows(_world.Crops.StageOf(crop), operation))
        {
            _block[i] = OrderBlock.WrongStage;
            return;
        }
        if (!_world.Crops.CanApply(crop, operation))
        {
            // The stage allows it, so the only other reason CanApply refuses
            // is the room test a harvest carries and the other two do not.
            _block[i] = OrderBlock.OutputFull;
            return;
        }

        _world.Crops.Apply(crop, operation);
        _block[i] = OrderBlock.None;
    }

    /// <summary>
    /// Drives to the source, loads, drives to the destination, unloads,
    /// repeat — a haul between two <see cref="Structure"/>s named by id.
    /// <b>Which leg is current is read off the cargo, not stored</b>: empty
    /// means "go load", carrying means "go deliver", so there is nothing
    /// beyond <see cref="Order"/> itself for a save to have to restore.
    ///
    /// A load or unload takes whatever a single <see cref="ItemBuffer.Transfer"/>
    /// moves in one call — bounded by the source's stock, the destination's
    /// room and the vehicle's own capacity — rather than topping up over
    /// several ticks first: nothing in this milestone produces goods fast
    /// enough for a second tick's worth to matter, and picking up a partial
    /// load and going is simpler than a policy for when a fuller one is worth
    /// the wait.
    /// </summary>
    private void RunHaulOrder(int i, Order order)
    {
        ItemBuffer cargo = _cargo[i];
        bool loaded = !cargo.IsEmpty;
        OrderStep travelStep = loaded ? OrderStep.DrivingToDestination : OrderStep.DrivingToSource;
        int structureId = loaded ? order.ToStructureId : order.FromStructureId;

        Structure? structure = _world.GetStructure(structureId);
        if (structure == null)
        {
            _step[i] = travelStep;
            _block[i] = OrderBlock.NoSuchStructure;
            return;
        }

        Vector2I? access = _world.FindRoadAccess(structure.Cells);
        if (access == null)
        {
            _step[i] = travelStep;
            _block[i] = OrderBlock.NoRoadAccess;
            return;
        }

        if (!HasArrived(i, access.Value))
        {
            _step[i] = travelStep;
            _block[i] = DriveTowards(i, access.Value) ? OrderBlock.None : OrderBlock.NoRoadAccess;
            return;
        }

        if (!loaded)
        {
            _step[i] = OrderStep.Loading;
            int moved = ItemBuffer.Transfer(structure.Storage, cargo, order.Good, cargo.Free);
            _block[i] = moved > 0 ? OrderBlock.None : OrderBlock.SourceEmpty;
        }
        else
        {
            _step[i] = OrderStep.Unloading;
            int amount = cargo.CountOf(order.Good);
            int moved = ItemBuffer.Transfer(cargo, structure.Storage, order.Good, amount);
            _block[i] = moved >= amount ? OrderBlock.None : OrderBlock.DestinationFull;
        }
    }

    /// <summary>
    /// Whether the machine is done travelling to <paramref name="target"/> —
    /// <b>route-empty, not merely cell-equal.</b> <c>_cell[i]</c> is a coarse,
    /// discrete reading of a continuous position, and a vehicle enters the
    /// target cell slightly before it reaches the exact point <see cref="Drive"/>
    /// is steering it to, which can leave one short final waypoint
    /// unconsumed. Working off the cell alone would call that "arrived" a
    /// tick or two early — the route would still read as in flight, which is
    /// exactly the signal a re-point mid-cycle is waiting on, so this is not
    /// a rounding nicety but the difference between promoting a queued order
    /// on time and never promoting it at all while it keeps re-arriving.
    /// </summary>
    private bool HasArrived(int i, Vector2I target) =>
        _routeNext[i] >= _route[i].Count && _cell[i] == target;

    /// <summary>
    /// Makes sure the machine is on its way to <paramref name="target"/>,
    /// which must be a road cell. A route already in flight is left alone —
    /// re-planning it every tick would run <c>FindRoadPath</c>'s search for no
    /// reason, since nothing about the road network changes while a vehicle
    /// is mid-drive. False for an unreachable target (no path at all), which
    /// the caller reports as <see cref="OrderBlock.NoRoadAccess"/>: from where
    /// the vehicle sits, a frontage nothing connects to and a frontage that
    /// does not exist are the same "nowhere to go".
    /// </summary>
    private bool DriveTowards(int i, Vector2I target)
    {
        if (_routeNext[i] < _route[i].Count)
        {
            return true;
        }
        List<Vector2I>? path = _world.FindRoadPath(_cell[i], target);
        if (path == null)
        {
            return false;
        }
        SetRoute(i, _world.SmoothRoadPath(path));
        return true;
    }

    /// <summary>
    /// Refills the route in place from a cell path — the same reused-list
    /// discipline <see cref="Spawn"/> keeps, so planning a thousand routes
    /// over a vehicle's life allocates the one list its slot started with.
    /// </summary>
    private void SetRoute(int i, List<Vector2I> cells)
    {
        _route[i].Clear();
        foreach (Vector2I cell in cells)
        {
            _route[i].Add(_world.CellToWorld(cell) + Vector3.Up * Machine.DeckHeight);
        }
        _routeNext[i] = 0;
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
        Array.Resize(ref _order, capacity);
        Array.Resize(ref _hasPendingOrder, capacity);
        Array.Resize(ref _pendingOrder, capacity);
        Array.Resize(ref _step, capacity);
        Array.Resize(ref _block, capacity);

        // An empty List allocates no backing array until the first Add, so
        // filling the new slots up front costs nothing and keeps the column
        // non-null everywhere.
        for (int i = old; i < capacity; i++)
        {
            _route[i] = new List<Vector3>();
        }
    }
}
