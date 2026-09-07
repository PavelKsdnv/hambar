namespace Arable;

/// <summary>
/// What kind of building a <see cref="Structure"/> is. One entry today — the
/// silo, #37's buffer with an in/out interface — and the seam #38's depot and
/// M6's roster (cleaner, mill, bakery) extend: a new kind is a new row here
/// plus a new <see cref="StructureBuildTool"/> node in Main.tscn, never a
/// second way to place or address a building.
/// </summary>
public enum StructureKind
{
    /// <summary>Holds whatever is hauled into it, up to a capacity. Nothing processes it.</summary>
    Silo,
}

/// <summary>
/// What differs between one <see cref="StructureKind"/> and another. Only a
/// default name and capacity live here — unlike <see cref="MachineKinds"/>'s
/// table, a placed building's real capacity is an export on the
/// <see cref="StructureBuildTool"/> that placed it
/// (<see cref="StructureBuildTool.StorageCapacity"/>), because the silo's
/// brief calls capacity out as a number a playtest argues about, the same
/// reason <see cref="BuildTool.CostPerCell"/> is an export and not content.
/// This table only backs the door that places a building without going
/// through a tool at all (dev fixtures, scenario code) — the same role
/// <see cref="ItemTypes"/> plays for a stack nothing priced.
/// </summary>
/// <param name="Name">The label a placed building of this kind defaults to,
/// capitalised like <see cref="Field"/>'s ("Silo 3", not "silo 3").</param>
/// <param name="DefaultStorageCapacity">Capacity used when nothing more
/// specific — a tool's own export — supplies one.</param>
public readonly record struct StructureSpec(string Name, int DefaultStorageCapacity);

/// <summary>The structure roster, as a static table — see <see cref="StructureSpec"/>.</summary>
public static class StructureKinds
{
    private static readonly StructureSpec[] Specs =
    [
        new("Silo", 500),
    ];

    /// <summary>
    /// The row for a kind. An out-of-range value answers the first row rather
    /// than throwing — the same rule <see cref="MachineKinds.Spec"/> takes
    /// with a corrupt or future save.
    /// </summary>
    public static StructureSpec Spec(StructureKind kind) =>
        Specs[(int)kind >= 0 && (int)kind < Specs.Length ? (int)kind : 0];

    /// <summary>The default label a placed building of this kind gets.</summary>
    public static string Name(StructureKind kind) => Spec(kind).Name;

    /// <summary>Capacity used when nothing placed the building through a tool.</summary>
    public static int DefaultStorageCapacity(StructureKind kind) => Spec(kind).DefaultStorageCapacity;
}
