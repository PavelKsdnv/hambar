using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// One thing the player took back off the map: the result of a single
/// <see cref="WorldGrid.Clear"/>, and <b>the subject of the refund seam</b>
/// (<c>BulldozeTool.RefundFor</c>). It describes the removal the way a price
/// would have to see it — what kind of thing came off, which entity it was,
/// how much ground it covered and where the player hit it — so M7 can write a
/// real refund rule against this object without changing anything that
/// produces one.
///
/// A removal is not the same as a cleared cell, and the difference is the
/// point of the type:
/// <list type="bullet">
/// <item>Clearing a road cell removes one cell of road.</item>
/// <item>Clearing a field cell removes one cell <i>from</i> a
/// <see cref="Arable.Field"/> that may well survive it — <see cref="Field"/>
/// names the field it was taken from, and <see cref="Cells"/> is just that one
/// cell.</item>
/// <item>Clearing any cell of a building removes the <b>whole</b> building:
/// <see cref="Structure"/> names it and <see cref="Cells"/> is its entire
/// footprint, however many cells the drag actually covered. Half a mill is not
/// a mill, so a refund for one is not a refund per cell either.</item>
/// </list>
/// </summary>
public sealed class Removal
{
    public Removal(
        TileType tile,
        Vector2I cell,
        IReadOnlyList<Vector2I> cells,
        Field? field,
        Structure? structure)
    {
        Tile = tile;
        Cell = cell;
        Cells = cells;
        Field = field;
        Structure = structure;
    }

    /// <summary>What was on the cell: road, field or structure. Never Empty.</summary>
    public TileType Tile { get; }

    /// <summary>The cell the player cleared — where the removal happened.</summary>
    public Vector2I Cell { get; }

    /// <summary>
    /// Every cell this one removal actually freed: <see cref="Cell"/> alone for
    /// a road or a field cell, the building's whole footprint when a structure
    /// came off. A per-cell refund counts these, not the drag.
    /// </summary>
    public IReadOnlyList<Vector2I> Cells { get; }

    public int CellCount => Cells.Count;

    /// <summary>
    /// The field the cell was taken from, when it was field. Still populated
    /// when the field survived the loss — a refund cares which field it was,
    /// not whether it is still there.
    /// </summary>
    public Field? Field { get; }

    /// <summary>
    /// The building that was demolished, when the cell held one. Kept as the
    /// entity rather than a name because the refund M7 writes will price a
    /// building by <i>what</i> it was, and that lives on the entity — the
    /// roster arrives in M5/M6, and this signature does not have to move for it.
    /// </summary>
    public Structure? Structure { get; }

    /// <summary>What to call the thing that was removed, in logs and the HUD.</summary>
    public string Name =>
        Structure?.Name ?? Field?.Name ?? Tile.ToString();

    public override string ToString() =>
        $"{Name} ({Tile}, {CellCount} cell(s) at {Cell})";
}
