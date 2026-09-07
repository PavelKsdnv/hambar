namespace Arable;

/// <summary>
/// The four actions a vehicle can be told to run. Four and no more, on
/// purpose: they are exactly the orders #7's tracker names — a tractor
/// ploughs or sows a field, a harvester cuts one, a truck hauls a good
/// between two buildings — and a fifth is a taxonomy nobody has asked for
/// yet, the same restraint <see cref="MachineKind"/> already took with the
/// vehicle roster.
/// </summary>
public enum OrderKind
{
    PloughField,
    SowField,
    HarvestField,
    HaulGoods,
}

/// <summary>
/// Which registry a haul's source resolves through. A field's own harvest
/// buffer (<see cref="CropSystem.OutputOf"/>) needed a haul endpoint too
/// (#39), and it has no id among <see cref="Structure"/>s, so the source
/// carries this alongside its id rather than the id alone growing a second,
/// incompatible meaning. The destination never needs the same treatment —
/// nothing sells or stores out of a field — so only the source has a kind.
/// </summary>
public enum HaulSourceKind
{
    Structure,
    Field,
}

/// <summary>
/// An action plus its targets — the whole of what a player types into a
/// vehicle. A value type on purpose: assigning one is a copy, comparing two
/// is <c>==</c>, and it serializes for M10's saves as the handful of ints and
/// enums it already is, with nothing to walk and nothing that can dangle.
///
/// <b>One shape for every kind, not a union.</b> A <see cref="HaulGoods"/>
/// order leaves <see cref="FieldId"/> at 0 and a field order leaves
/// <see cref="Good"/> at <see cref="ItemType.None"/>; the unused fields cost
/// a few ints of waste and buy a struct with no discriminated-union
/// boilerplate for four cases that will not grow far. Build one through the
/// factory methods below, not the constructor: they are what keeps a
/// <see cref="PloughField"/> order from being built with a stray
/// <see cref="Good"/> set on it by mistake.
///
/// <b>Existence is checked at execution, not at assignment.</b> Field and
/// structure ids are not resolved when the order is set — only whether the
/// order's <see cref="Kind"/> suits the vehicle is
/// (<see cref="MachineSystem.Accepts"/>). A field bulldozed out from under a
/// standing order, or a structure that never existed, is the ordinary case
/// of a blocked vehicle (<see cref="OrderBlock.NoSuchField"/>,
/// <see cref="OrderBlock.NoSuchStructure"/>), not a validation failure at the
/// point the player typed the order — which is also what lets an order be
/// authored before its target is even built.
/// </summary>
public readonly record struct Order
{
    public OrderKind Kind { get; private init; }

    /// <summary>The field to plough, sow or harvest. Unused by <see cref="HaulGoods"/>.</summary>
    public int FieldId { get; private init; }

    /// <summary>The good a haul moves. <see cref="ItemType.None"/> for a field order.</summary>
    public ItemType Good { get; private init; }

    /// <summary>Whether <see cref="FromId"/> names a <see cref="Structure"/> or a <see cref="Field"/>.</summary>
    public HaulSourceKind FromKind { get; private init; }

    /// <summary>Where a haul loads from — a structure id or a field id, per <see cref="FromKind"/>.</summary>
    public int FromId { get; private init; }

    /// <summary>Where a haul delivers to. Always a structure — nothing sells or stores into a field.</summary>
    public int ToStructureId { get; private init; }

    public static Order Plough(int fieldId) => new() { Kind = OrderKind.PloughField, FieldId = fieldId };

    public static Order Sow(int fieldId) => new() { Kind = OrderKind.SowField, FieldId = fieldId };

    public static Order Harvest(int fieldId) => new() { Kind = OrderKind.HarvestField, FieldId = fieldId };

    public static Order Haul(ItemType good, int fromStructureId, int toStructureId) => new()
    {
        Kind = OrderKind.HaulGoods,
        Good = good,
        FromKind = HaulSourceKind.Structure,
        FromId = fromStructureId,
        ToStructureId = toStructureId,
    };

    /// <summary>A haul whose source is a field's own harvest buffer rather than a building.</summary>
    public static Order HaulFromField(ItemType good, int fromFieldId, int toStructureId) => new()
    {
        Kind = OrderKind.HaulGoods,
        Good = good,
        FromKind = HaulSourceKind.Field,
        FromId = fromFieldId,
        ToStructureId = toStructureId,
    };

    /// <summary>
    /// Every field, in a fixed order — what a vehicle row hashes and, in
    /// M10, saves. Written here rather than by a walker reaching into the
    /// struct, the way <see cref="ItemBuffer.HashState"/> owns its own stacks.
    /// <see cref="FromKind"/> is written alongside <see cref="FromId"/>: two
    /// orders with the same id but different kinds (a field 3 and a structure
    /// 3) are different orders, and leaving it out would hash them alike.
    /// </summary>
    public void HashState(StateHash hash)
    {
        hash.Write((int)Kind);
        hash.Write(FieldId);
        hash.Write(Good.Id);
        hash.Write((int)FromKind);
        hash.Write(FromId);
        hash.Write(ToStructureId);
    }

    public override string ToString() => Kind switch
    {
        OrderKind.PloughField => $"plough field {FieldId}",
        OrderKind.SowField => $"sow field {FieldId}",
        OrderKind.HarvestField => $"harvest field {FieldId}",
        OrderKind.HaulGoods when FromKind == HaulSourceKind.Field =>
            $"haul {Good} from field {FromId} to {ToStructureId}",
        OrderKind.HaulGoods => $"haul {Good} from {FromId} to {ToStructureId}",
        _ => "order",
    };
}

/// <summary>
/// What came of trying to give a vehicle an order. Anything but
/// <see cref="Ok"/> means the vehicle's order is exactly what it was before —
/// the same all-or-nothing promise <see cref="AssignResult"/> makes about a
/// crew change, and the concrete shape of the brief's "validate an order's
/// action against the vehicle kind and refuse mismatches".
/// </summary>
public enum SetOrderResult
{
    Ok,
    NoSuchVehicle,

    /// <summary>
    /// This kind of vehicle cannot run this kind of order — a truck asked to
    /// plough, or a tractor asked to haul. See
    /// <see cref="MachineSystem.Accepts"/> for exactly which pairs that is.
    /// </summary>
    WrongVehicleKind,
}

/// <summary>
/// Where a vehicle is in its order, readable without knowing anything about
/// orders — the "driving to field", "ploughing" the brief asks every vehicle
/// to expose. <see cref="Idle"/> covers both "no order" and "no driver": from
/// outside, a vehicle nobody can act through is indistinguishable from a
/// vehicle with nothing to do, and <see cref="OrderBlock.NoDriver"/> is where
/// the difference actually lives.
/// </summary>
public enum OrderStep
{
    Idle,
    DrivingToField,
    Ploughing,
    Sowing,
    Harvesting,
    DrivingToSource,
    Loading,
    DrivingToDestination,
    Unloading,
}

/// <summary>
/// Why a vehicle running an order is not making progress right now.
/// <see cref="None"/> is not a refusal, it is the ordinary state of a vehicle
/// that is on its way or working — <b>the governing rule of M5, typed</b>: a
/// blocked vehicle waits and says why, and never goes looking for other work.
/// </summary>
public enum OrderBlock
{
    None,

    /// <summary>Nobody is driving it. An order sits and waits for a crew, same as a route would.</summary>
    NoDriver,

    NoSuchField,
    NoSuchStructure,

    /// <summary>
    /// The target has no road frontage to reach, or the road network does not
    /// connect to it from here — the two ways "get there" fails, folded into
    /// one reading, because from the seat of the vehicle they look the same:
    /// there is nowhere to go.
    /// </summary>
    NoRoadAccess,

    /// <summary>Arrived, but the field is not at a stage this operation accepts.</summary>
    WrongStage,

    /// <summary>The field's own buffer has no room for what a harvest would add to it.</summary>
    OutputFull,

    /// <summary>At the source, but it holds none of the good being hauled.</summary>
    SourceEmpty,

    /// <summary>At the destination, but it has no room left for the load.</summary>
    DestinationFull,
}

/// <summary>Player-facing wording for <see cref="OrderStep"/> and <see cref="OrderBlock"/>.</summary>
public static class OrderText
{
    public static string Describe(OrderStep step) => step switch
    {
        OrderStep.Idle => "idle",
        OrderStep.DrivingToField => "driving to field",
        OrderStep.Ploughing => "ploughing",
        OrderStep.Sowing => "sowing",
        OrderStep.Harvesting => "harvesting",
        OrderStep.DrivingToSource => "driving to pick-up",
        OrderStep.Loading => "loading",
        OrderStep.DrivingToDestination => "driving to drop-off",
        OrderStep.Unloading => "unloading",
        _ => "idle",
    };

    /// <summary>Empty string for <see cref="OrderBlock.None"/>: there is nothing to report.</summary>
    public static string Describe(OrderBlock block) => block switch
    {
        OrderBlock.None => "",
        OrderBlock.NoDriver => "waiting: no driver",
        OrderBlock.NoSuchField => "waiting: field no longer exists",
        OrderBlock.NoSuchStructure => "waiting: building no longer exists",
        OrderBlock.NoRoadAccess => "waiting: no road access",
        OrderBlock.WrongStage => "waiting: field not ready",
        OrderBlock.OutputFull => "waiting: field's store is full",
        OrderBlock.SourceEmpty => "waiting: nothing to load",
        OrderBlock.DestinationFull => "waiting: destination full",
        _ => "",
    };
}
