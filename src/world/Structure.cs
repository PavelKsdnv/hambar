using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// One placed building — silo, cleaner, mill, bakery — and <b>the unit
/// buildings are addressed by</b>. Its cells carry
/// <see cref="TileType.Structure"/> so the view can draw them and the placement
/// rules can call them occupied, but nothing in the game refers to "that
/// structure cell": the tile enum has no room for identity, and a building
/// needs one long before it needs a mesh. M5's vehicles deliver *to a silo*,
/// and M6 hangs a recipe, an input buffer and an output buffer off *this*
/// object — so it exists from the first placement, not from the milestone that
/// finally fills it in.
///
/// Consequences of that choice, all of them deliberate:
/// <list type="bullet">
/// <item>A structure owns a <b>list of cells</b>, exactly like
/// <see cref="Field"/>, even though the tool places one cell today. A 2x2
/// footprint is then a wider list out of the tool's <c>Footprint</c> and
/// nothing else: the registry, the cell → structure lookup, the road rule and
/// the demolition path are all already written per cell.</item>
/// <item>A cell belongs to <b>exactly one</b> structure. The structure tool
/// opts into <see cref="PlacementRule.VacantCell"/>, so a building can never
/// be dropped on ground that is already road, field or building.</item>
/// <item>A building is <b>atomic</b>, which is where it parts company with a
/// field: clearing any one of its cells demolishes the whole thing rather than
/// shrinking it (see <see cref="WorldGrid.SetTile"/>). Half a mill is not a
/// mill.</item>
/// </list>
///
/// Created through <see cref="WorldGrid.PlaceStructure"/>, which owns the
/// registry and the cell → structure lookup.
/// </summary>
public sealed class Structure
{
    private readonly List<Vector2I> _cells;

    public Structure(int id, string name, IReadOnlyList<Vector2I> cells)
    {
        Id = id;
        Name = name;
        _cells = new List<Vector2I>(cells);
    }

    /// <summary>
    /// Stable identity, handed out in creation order and never reused — what a
    /// delivery order, a save file or a production recipe refers to, so none of
    /// them has to hold the object or re-derive it from a cell.
    /// </summary>
    public int Id { get; }

    /// <summary>What the player calls it. Defaulted at creation, renameable later.</summary>
    public string Name { get; set; }

    /// <summary>
    /// Cells the building covers, in footprint order — one today, four for a
    /// 2x2. The first is <see cref="Origin"/>.
    /// </summary>
    public IReadOnlyList<Vector2I> Cells => _cells;

    /// <summary>
    /// Anchor cell: the first cell of the footprint, which for the rectangular
    /// footprints <see cref="WorldGrid.RectCells"/> produces is its minimum
    /// corner. Where the model is pivoted and where a vehicle is sent.
    /// </summary>
    public Vector2I Origin => _cells.Count > 0 ? _cells[0] : Vector2I.Zero;

    public int CellCount => _cells.Count;

    /// <summary>
    /// Whether the cell belongs to this building. Linear — the fast lookup is
    /// <see cref="WorldGrid.GetStructure(Vector2I)"/>, which indexes every
    /// structure cell.
    /// </summary>
    public bool Contains(Vector2I cell) => _cells.Contains(cell);

    /// <summary>
    /// Axis-aligned bounds of the footprint (position = min corner, size in
    /// cells): 1x1 today, 2x2 when the roster grows. Recomputed per call,
    /// because a building is a handful of cells and they never change after
    /// placement.
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

    public override string ToString() => $"{Name} ({CellCount} cells at {Origin})";
}
