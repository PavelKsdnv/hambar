using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Road-building tool, the palette's first entry: click a start cell, click an
/// end cell, get a straight Bresenham line of road between them (stair-stepped
/// through diagonals so the road stays 4-connected for machines).
///
/// Hovering, ghosting, validating and cancelling all live in
/// <see cref="BuildTool"/>. All this class says is "roads go on buildable
/// ground that nothing else occupies, and a drag covers the line between the
/// two cells".
/// </summary>
public partial class RoadBuildTool : BuildTool
{
    /// <summary>
    /// Road is the cheap thing you draw a lot of, so it is priced per cell and
    /// priced low. The number is a placeholder — money means nothing until M7 —
    /// and it is set here rather than as an initializer because
    /// <see cref="BuildTool.CostPerCell"/> is the base's export; Main.tscn
    /// overrides it, which is the point of exporting it.
    /// </summary>
    public RoadBuildTool()
    {
        DisplayName = "Road";
        CostPerCell = 5;
    }

    protected override TileType PlacedTile => TileType.Road;

    /// <summary>
    /// Roads need real ground (no rock, water or off-map cells) and may not be
    /// drawn over something else the player built. Road over road stays legal —
    /// that is how a new road is started from the existing network.
    /// </summary>
    protected override PlacementRule Rules =>
        PlacementRule.BuildableTerrain | PlacementRule.NoOverlap;

    protected override IReadOnlyList<Vector2I> Footprint(Vector2I anchor, Vector2I cell) =>
        WorldGrid.LineCells(anchor, cell);
}
