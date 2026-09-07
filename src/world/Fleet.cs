using Godot;

namespace Arable;

/// <summary>
/// What came of trying to buy a vehicle. Anything but <see cref="Ok"/> means
/// <b>nothing was placed and nothing was charged</b> — the same all-or-nothing
/// promise <see cref="HireResult"/> makes about a hire.
/// </summary>
public enum BuyResult
{
    /// <summary>Bought. The price has left the balance and the vehicle is parked.</summary>
    Ok,

    /// <summary>The price is more than the balance.</summary>
    CannotAfford,

    /// <summary>Nowhere legal to leave it: no road at all, or every road cell taken.</summary>
    NoRoom,

    /// <summary>No world, or no vehicle scene wired to it. A broken scene, not a refusal.</summary>
    Unavailable,
}

/// <summary>
/// What came of trying to put a worker in a cab. Anything but <see cref="Ok"/>
/// leaves both of them exactly as they were.
/// </summary>
public enum AssignResult
{
    /// <summary>Assigned. The worker drives that vehicle and no other.</summary>
    Ok,

    /// <summary>Nobody by that handle is on the books.</summary>
    NoSuchWorker,

    /// <summary>No such vehicle — never bought, or sold since.</summary>
    NoSuchVehicle,

    /// <summary>That worker is already driving something. Take them off it first.</summary>
    WorkerHasAVehicle,

    /// <summary>That vehicle already has a driver. One cab, one person.</summary>
    VehicleHasADriver,
}

/// <summary>
/// <b>The vehicles the farm owns and who drives them.</b> Two acts, both of
/// which the player performs and neither of which the sim ever performs for
/// them: buying a machine, and assigning one of
/// <see cref="LabourPool"/>'s workers to it.
///
/// <b>Nothing here looks for work.</b> A vehicle with no driver and a worker
/// with no vehicle are both ordinary, indefinite states — the farm simply does
/// less. M5's rule is that automation is authored: the moment this node paired
/// an idle worker with an idle truck "helpfully", the player would stop being
/// able to tell what they had asked for from what the game had decided, and
/// every later issue in the milestone would be arguing with it.
///
/// <b>Why a node of its own</b> rather than methods on <c>WorldGrid</c>. The
/// world places things and owns no money; the two acts here each need an
/// account, and assignment needs the labour pool as well. That is the shape a
/// <c>BuildTool</c> already has — world plus economy plus a price — and the
/// same reason <see cref="LabourPool"/> is a node: the wiring is scene wiring,
/// and a null reference means "this scene has no such thing", not an error. A
/// fleet with no <see cref="Economy"/> buys for free, exactly as a tool with
/// none builds for free.
///
/// <b>It holds no state.</b> The link lives on the machine row
/// (<c>MachineSystem.SetCrew</c>), so it is saved, hashed and invalidated with
/// the vehicle; this node is only the place that decides. Storing pairs here
/// instead would have meant a third registry to keep level with two entity
/// stores that both recycle their slots.
///
/// <b>Dangling handles are resolved on read, never swept.</b> Letting a driver
/// go leaves their handle on the vehicle they drove; nothing goes looking for
/// it. <see cref="DriverOf"/> asks the pool whether that worker is still alive
/// and answers nobody if they are not, so the vehicle is immediately free to
/// crew again — and a re-hired worker gets a fresh generation, so the handle
/// they left behind can never match them. A cleanup pass would be a sim state
/// write triggered by a read, which is the one thing the tick loop's ownership
/// rules forbid.
/// </summary>
public partial class Fleet : Node
{
    /// <summary>The world the vehicle is placed in. Null means this scene has no farm.</summary>
    [Export] public WorldGrid? World { get; set; }

    /// <summary>
    /// The account the price comes out of. Null is legal and means this scene
    /// has no money, not that the player is broke.
    /// </summary>
    [Export] public Economy? Economy { get; set; }

    /// <summary>The people who can be put in a cab. Null means nobody can be assigned.</summary>
    [Export] public LabourPool? Labour { get; set; }

    /// <summary>What one of those costs. The roster's number, not a per-scene one.</summary>
    public static int PriceOf(MachineKind kind) => MachineKinds.PriceOf(kind);

    /// <summary>Whether the farm could buy one of those right now.</summary>
    public bool CanAfford(MachineKind kind) =>
        Economy == null || Economy.CanAfford(PriceOf(kind));

    /// <summary>
    /// Buys a vehicle and parks it somewhere legal on the road network.
    ///
    /// The order is deliberate: affordability is read first, the machine is
    /// placed second, and the money moves last. Placing before charging is what
    /// makes "nowhere to park" a free refusal — the alternative, charging first
    /// and handing the money back when placement fails, would put a refund path
    /// into a purchase for a case that is not an error. The read-then-spend
    /// pair <see cref="LabourPool"/> warns against is safe here for the one
    /// reason that makes such a pair safe anywhere: nothing between the two
    /// calls can move the balance.
    /// </summary>
    public BuyResult TryBuy(MachineKind kind, out Machine? machine)
    {
        machine = null;
        if (World == null || World.MachineScene == null)
        {
            return BuyResult.Unavailable;
        }

        int price = PriceOf(kind);
        if (Economy != null && !Economy.CanAfford(price))
        {
            return BuyResult.CannotAfford;
        }

        machine = World.SpawnMachine(kind);
        if (machine == null)
        {
            return BuyResult.NoRoom;
        }

        if (Economy != null && !Economy.TrySpend(price))
        {
            // Unreachable while nothing can spend between the check above and
            // here, and cheap insurance against the day something can: a
            // vehicle nobody paid for must not survive the refusal.
            machine.QueueFree();
            machine = null;
            return BuyResult.CannotAfford;
        }
        return BuyResult.Ok;
    }

    /// <summary>
    /// The worker driving this vehicle, or <see cref="EntityId.None"/>. The
    /// resolved answer: a handle left behind by somebody who has since been let
    /// go reads as nobody.
    /// </summary>
    public EntityId DriverOf(EntityId vehicle)
    {
        MachineSystem? machines = World?.Machines;
        if (machines == null)
        {
            return EntityId.None;
        }

        EntityId crew = machines.CrewOf(vehicle);
        return Labour != null && Labour.IsAlive(crew) ? crew : EntityId.None;
    }

    /// <summary>The vehicle this worker drives, or <see cref="EntityId.None"/>.</summary>
    public EntityId VehicleOf(EntityId worker)
    {
        MachineSystem? machines = World?.Machines;
        if (machines == null || Labour == null || !Labour.IsAlive(worker))
        {
            return EntityId.None;
        }
        return machines.VehicleCrewedBy(worker);
    }

    /// <summary>Whether somebody who still works here is driving it.</summary>
    public bool IsCrewed(EntityId vehicle) => DriverOf(vehicle) != EntityId.None;

    /// <summary>
    /// Puts a worker in a vehicle, one to one in both directions. Assigning
    /// somebody to the vehicle they already drive is accepted and changes
    /// nothing, so a UI that re-confirms a selection cannot make a refusal out
    /// of it.
    /// </summary>
    public AssignResult TryAssign(EntityId worker, EntityId vehicle)
    {
        MachineSystem? machines = World?.Machines;
        if (Labour == null || !Labour.IsAlive(worker))
        {
            return AssignResult.NoSuchWorker;
        }
        if (machines == null || !machines.IsAlive(vehicle))
        {
            return AssignResult.NoSuchVehicle;
        }

        EntityId already = machines.VehicleCrewedBy(worker);
        if (already == vehicle)
        {
            return AssignResult.Ok;
        }
        if (already != EntityId.None)
        {
            return AssignResult.WorkerHasAVehicle;
        }
        if (IsCrewed(vehicle))
        {
            return AssignResult.VehicleHasADriver;
        }

        machines.SetCrew(vehicle, worker);
        return AssignResult.Ok;
    }

    /// <summary>
    /// Takes whoever is driving out of the cab. True when there was somebody to
    /// take out — false is "it was already idle", not a failure. A vehicle
    /// whose driver was dismissed answers false, because as far as everything
    /// that reads the link is concerned it is already unassigned.
    /// </summary>
    public bool Unassign(EntityId vehicle)
    {
        MachineSystem? machines = World?.Machines;
        if (machines == null || !IsCrewed(vehicle))
        {
            return false;
        }
        machines.SetCrew(vehicle, EntityId.None);
        return true;
    }

    /// <summary>
    /// Takes a worker off whatever they were driving. The other half of
    /// <see cref="Unassign(EntityId)"/>, for a caller holding the person rather
    /// than the machine.
    /// </summary>
    public bool UnassignWorker(EntityId worker)
    {
        EntityId vehicle = VehicleOf(worker);
        return vehicle != EntityId.None && Unassign(vehicle);
    }
}
