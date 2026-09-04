using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Field-marking tool, toggled by menu key 3: click one corner, click the
/// opposite one, and the filled rectangle between them becomes a single
/// <see cref="Field"/> — a named region, not a pile of field tiles (see
/// <see cref="Field"/> for what that buys and costs).
///
/// Hovering, the live rectangle ghost, validation and cancelling all come from
/// <see cref="BuildTool"/>. This class says only three things: fields go on
/// free soil, a drag covers the rectangle between the two corners, and
/// committing one creates the field entity rather than stamping tiles.
/// </summary>
public partial class FieldBuildTool : BuildTool
{
    protected override TileType PlacedTile => TileType.Field;

    /// <summary>
    /// Fields need real ground, and — unlike roads, which may be drawn over
    /// road — every cell must be completely free:
    /// <see cref="PlacementRule.VacantCell"/> is what keeps a cell owned by
    /// exactly one field. Road access is deliberately not required; nothing
    /// works a field until machines get jobs in M4, and that is when the rule
    /// (if any) should be decided.
    /// </summary>
    protected override PlacementRule Rules =>
        PlacementRule.BuildableTerrain | PlacementRule.VacantCell;

    protected override IReadOnlyList<Vector2I> Footprint(Vector2I anchor, Vector2I cell) =>
        WorldGrid.RectCells(anchor, cell);

    /// <summary>
    /// One drag makes one field. The default <see cref="BuildTool.Apply"/>
    /// would only write the tiles, which would leave farmland nothing could
    /// address; <see cref="WorldGrid.MarkField"/> writes them <i>and</i>
    /// registers the region that owns them.
    /// </summary>
    protected override void Apply(PlacementPlan plan)
    {
        Field? field = World!.MarkField(plan.Cells);
        if (field != null)
        {
            GD.Print($"{Name}: marked {field} at {field.Bounds}");
        }
    }
}
