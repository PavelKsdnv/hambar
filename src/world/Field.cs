using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// One field: the named region of cells the player marked with the field tool,
/// and <b>the unit farmland is addressed by</b>. Cells carry
/// <see cref="TileType.Field"/> so the view can draw them, but nothing in the
/// game refers to "that field cell" — jobs, crops, yields and the output buffer
/// M4 adds all hang off this object, one per marked rectangle.
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

    public Field(int id, string name, IReadOnlyList<Vector2I> cells)
    {
        Id = id;
        Name = name;
        _cells = new List<Vector2I>(cells);
        _readOnlyCells = _cells.AsReadOnly();
    }

    /// <summary>Stable identity, handed out in creation order and never reused.</summary>
    public int Id { get; }

    /// <summary>What the player calls it. Defaulted at creation, renameable later.</summary>
    public string Name { get; set; }

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
