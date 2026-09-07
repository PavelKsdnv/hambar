using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// One field: the named region of cells the player marked with the field tool,
/// and <b>the unit farmland is addressed by</b>. Cells carry
/// <see cref="TileType.Field"/> so the view can draw them, but nothing in the
/// game refers to "that field cell" — jobs, crops and yields all hang off this
/// object, one per marked rectangle. Crop state is one step further out: this
/// object holds a <see cref="Crop"/> handle into <see cref="CropSystem"/>'s
/// entity rows, which is where everything the sim ticks actually lives.
///
/// Consequences of that choice, all of them deliberate:
/// <list type="bullet">
/// <item>A cell belongs to <b>exactly one</b> field. The field tool opts into
/// <see cref="PlacementRule.VacantCell"/>, so a rectangle can never be drawn
/// over cells another field already owns.</item>
/// <item>Fields are <b>not merged</b> when they happen to touch: two adjacent
/// rectangles stay two fields, because each is separately named, worked and
/// harvested. Enlarging a field is therefore marking another one.</item>
/// <item>A field owns its cells rather than deriving them from the tile layer,
/// so it survives an irregular shape — the rectangle is how the player
/// <i>draws</i> one, not what a field is allowed to be. Clearing a cell (M2's
/// bulldoze) shrinks the field and drops it when the last cell goes.</item>
/// </list>
///
/// Created through <see cref="WorldGrid.MarkField"/>, which owns the registry
/// and the cell → field lookup.
/// </summary>
public sealed class Field
{
    private readonly List<Vector2I> _cells;
    private readonly IReadOnlyList<Vector2I> _readOnlyCells;

    public Field(int id, string name, IReadOnlyList<Vector2I> cells, EntityId crop,
        int fertilityChunkSize)
    {
        Id = id;
        Name = name;
        Crop = crop;
        FertilityChunkSize = Mathf.Max(1, fertilityChunkSize);
        _cells = new List<Vector2I>(cells);
        _readOnlyCells = _cells.AsReadOnly();
    }

    /// <summary>Stable identity, handed out in creation order and never reused.</summary>
    public int Id { get; }

    /// <summary>
    /// The field's row in <see cref="CropSystem"/> — its stage, and everything
    /// about it that changes with time. <b>Held, not owned</b>: this object is
    /// the cells and the name, while anything the sim ticks lives in the entity
    /// arrays, where a slot walk can hash and save it in an order that does not
    /// depend on the order fields were marked in.
    ///
    /// Opened by <see cref="WorldGrid.MarkField"/> and closed when the field
    /// loses its last cell, so it is alive for exactly as long as the field is.
    /// </summary>
    public EntityId Crop { get; }

    /// <summary>What the player calls it. Defaulted at creation, renameable later.</summary>
    public string Name { get; set; }

    /// <summary>
    /// Cells on a side of the chunks this field's ground was averaged over
    /// (<see cref="WorldGrid.ChunkedFertility(IReadOnlyList{Vector2I})"/>),
    /// <b>frozen when the field was marked</b>. Carried here rather than read
    /// off <see cref="WorldGrid.FertilityChunkSize"/> at use, because that one
    /// is an export a playtest moves mid-run: without it, bulldozing a single
    /// cell would re-measure a field that had been standing for seasons at a
    /// granularity it was never marked at, and two fields on identical ground
    /// would end up growing at different rates purely by which of them last
    /// lost a corner.
    /// </summary>
    public int FertilityChunkSize { get; }

    /// <summary>
    /// Cells the field covers, in the order they were marked. A read-only
    /// <i>wrapper</i>, not the backing list — it tracks the shrinking a
    /// <see cref="RemoveCell"/> does, but a caller cannot cast it back and take
    /// a cell out behind <see cref="WorldGrid"/>'s back, leaving the tile layer
    /// and the cell → field lookup pointing at a cell the field disowns.
    /// </summary>
    public IReadOnlyList<Vector2I> Cells => _readOnlyCells;

    public int CellCount => _cells.Count;

    /// <summary>
    /// Whether the cell belongs to this field. Linear — the fast lookup is
    /// <see cref="WorldGrid.GetField"/>, which indexes every field cell.
    /// </summary>
    public bool Contains(Vector2I cell) => _cells.Contains(cell);

    /// <summary>
    /// Axis-aligned bounds of the region (position = min corner, size in
    /// cells). Recomputed per call, because a field is small and its cells
    /// change only when the player edits it.
    /// </summary>
    public Rect2I Bounds
    {
        get
        {
            if (_cells.Count == 0)
            {
                return new Rect2I();
            }
            Vector2I min = _cells[0];
            Vector2I max = _cells[0];
            foreach (Vector2I cell in _cells)
            {
                min = new Vector2I(Mathf.Min(min.X, cell.X), Mathf.Min(min.Y, cell.Y));
                max = new Vector2I(Mathf.Max(max.X, cell.X), Mathf.Max(max.Y, cell.Y));
            }
            return new Rect2I(min, max - min + Vector2I.One);
        }
    }

    /// <summary>
    /// Drops one cell from the region. Internal: membership is a two-sided
    /// relationship and <see cref="WorldGrid"/> keeps both sides in step.
    /// </summary>
    internal bool RemoveCell(Vector2I cell) => _cells.Remove(cell);

    public override string ToString() => $"{Name} ({CellCount} cells)";
}
