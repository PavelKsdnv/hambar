using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Road-building tool, toggled by menu key 1: click a start cell, click an end
/// cell, get a straight Bresenham line of road between them (stair-stepped
/// through diagonals so the road stays 4-connected for machines).
///
/// Hovering, ghosting, validating and cancelling all live in
/// <see cref="BuildTool"/>. All this class says is "roads go on buildable
/// ground that nothing else occupies, and a drag covers the line between the
/// two cells".
/// </summary>
public partial class RoadBuildTool : BuildTool
{
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
