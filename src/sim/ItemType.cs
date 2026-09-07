namespace Arable;

/// <summary>
/// What a thing <i>is</i>, as a number the sim can hash: an id into
/// <see cref="ItemTypes"/>'s catalogue and nothing else.
///
/// <b>Items are typed data, never a per-commodity counter.</b> tech.md's rule
/// for the whole production chain is that goods are simulated rather than
/// tallied, and the shape that keeps that honest is a type plus a quantity
/// (<see cref="ItemStack"/>) sitting in a container with a capacity
/// (<see cref="ItemBuffer"/>). A field that added to a global grain total would
/// have no place for M5's trucks to collect from and no way for M6 to tell
/// grain from the flour it becomes — the type id is what makes those milestones
/// additive instead of a rewrite.
///
/// <b>A struct wrapping an int, not an enum.</b> An enum would read better at
/// the two call sites that exist today and would have to be edited — with every
/// switch over it — each time a recipe adds a good; an int id is a row in a
/// catalogue, which is what M6's clean grain, flour and bread and M7's prices
/// actually want. It also hashes as one word with no conversion, and the
/// wrapper keeps a type from being passed where a quantity belongs.
/// </summary>
public readonly record struct ItemType(int Id)
{
    /// <summary>
    /// The type that is not a type — id 0, so <c>default(ItemType)</c> is
    /// nothing at all rather than the first real good. Nothing accepts it: an
    /// empty buffer slot holds no stack rather than a stack of nothing.
    /// </summary>
    public static ItemType None => default;

    public bool IsNone => Id == 0;

    /// <summary>What the UI calls it — lower case, like every other readout.</summary>
    public string Name => ItemTypes.NameOf(this);

    public override string ToString() => Name;
}

/// <summary>
/// The catalogue of goods that exist. <b>Data, not a hard-coded crop</b>: grain
/// is the only entry M4 needs, and adding the rest of the chain is adding rows
/// here rather than touching anything that carries an item.
///
/// Held as parallel arrays indexed by <see cref="ItemType.Id"/>, the way the
/// entity systems hold their columns: M6 giving items a mass, or M7 a base
/// price, is another array beside this one and not a new mechanism. Ids are
/// positions in it and are therefore <b>stable</b> — a save writes the number,
/// so rows are appended, never reordered or removed.
/// </summary>
public static class ItemTypes
{
    // Index 0 is ItemType.None, so an id can be used as an index without a
    // subtraction and the unset value names itself in a log line.
    private static readonly string[] Names = ["nothing", "grain"];

    /// <summary>Threshed wheat, straight off the field. The first good in the chain.</summary>
    public static ItemType Grain => new(1);

    /// <summary>Rows in the catalogue, counting <see cref="ItemType.None"/>.</summary>
    public static int Count => Names.Length;

    /// <summary>Whether the id names a real good rather than nothing or a gap.</summary>
    public static bool IsKnown(ItemType type) => type.Id > 0 && type.Id < Names.Length;

    /// <summary>
    /// The good's name, or a legible placeholder for an id this build has no
    /// row for — a save from a later build is a thing to report, not to crash
    /// over.
    /// </summary>
    public static string NameOf(ItemType type) =>
        IsKnown(type) ? Names[type.Id] : type.Id == 0 ? Names[0] : $"item {type.Id}";
}

/// <summary>
/// A countable pile of one good: what a harvest produces and what M5's trucks
/// will carry. Quantities are <b>whole units</b> — a stack is a count of
/// things, and half a sack of grain is not a thing.
/// </summary>
public readonly record struct ItemStack(ItemType Type, int Quantity)
{
    public bool IsEmpty => Quantity <= 0 || Type.IsNone;

    public override string ToString() => $"{Quantity} {Type.Name}";
}
