using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Structure-placing tool, the palette's third entry: one click drops one
/// building on the cell under the cursor — but only where the cell shares an
/// edge with the road network, which is the rule that makes roads
/// load-bearing instead of decorative.
///
/// <b>One tool, every kind.</b> <see cref="Kind"/> and
/// <see cref="StorageCapacity"/> are exports, so the silo (M5) and the depot
/// (#38) are two nodes in Main.tscn built from this one script — a different
/// kind, name, price and capacity — never a subclass or a second placement
/// path. What this tool commits to is placement and adjacency, and that a
/// placement creates a <see cref="Structure"/> — an entity with an identity —
/// rather than stamping a tile the game could only read back as an enum.
///
/// Hovering, the ghost, validation and cancelling all come from
/// <see cref="BuildTool"/>. This class says four things: buildings go on free
/// soil beside a road, they place on a single click, the footprint is the one
/// cell, and committing one registers the entity as this node's exported kind.
/// </summary>
public partial class StructureBuildTool : BuildTool
{
    /// <summary>
    /// Which <see cref="StructureKind"/> a click through this node places.
    /// Defaults to the only kind M5 has; #38's depot is a second node with
    /// this set instead of a second script.
    /// </summary>
    [Export] public StructureKind Kind { get; set; } = StructureKind.Silo;

    /// <summary>
    /// Units the placed building's <see cref="Structure.Storage"/> holds — the
    /// silo's one tunable, exported for the same reason
    /// <see cref="BuildTool.CostPerCell"/> is: a playtest argues about a
    /// number, not a rebuild. <see cref="WorldGrid.PlaceStructure"/> only
    /// falls back to <see cref="StructureKinds"/>' default when something
    /// places a building without going through a tool at all.
    /// </summary>
    [Export] public int StorageCapacity { get; set; }

    /// <summary>
    /// A building is the expensive decision, and its footprint is one cell, so
    /// <see cref="BuildTool.CostPerCell"/> is simply its price. The
    /// constructor's defaults name this instance after <see cref="Kind"/>;
    /// Main.tscn's exports win, so a second node placing a different kind
    /// overrides all three independently.
    /// </summary>
    public StructureBuildTool()
    {
        DisplayName = StructureKinds.Name(Kind);
        CostPerCell = 250;
        StorageCapacity = StructureKinds.DefaultStorageCapacity(Kind);
    }

    protected override TileType PlacedTile => TileType.Structure;

    /// <summary>
    /// Buildings need real ground, ground nothing else already holds
    /// (<see cref="PlacementRule.VacantCell"/> — a building may not be stacked
    /// on road, field or another building, and every cell has exactly one
    /// owner), and — the point of this tool —
    /// <see cref="PlacementRule.TouchesRoad"/>: the footprint must border a
    /// road cell outside itself, so vehicles can reach it in M5 and a building
    /// can never satisfy its own road requirement.
    /// </summary>
    protected override PlacementRule Rules =>
        PlacementRule.BuildableTerrain | PlacementRule.VacantCell | PlacementRule.TouchesRoad;

    /// <summary>
    /// A building is placed, not dragged: one click and it is down, and the
    /// ghost shows the verdict as soon as the cursor moves.
    /// </summary>
    public override bool NeedsAnchor => false;

    /// <summary>
    /// The cell under the cursor, on its own. This is the single place a
    /// multi-tile roster would change — a 2x2 is
    /// <c>WorldGrid.RectCells(cell, cell + Vector2I.One)</c> here, and nothing
    /// downstream of it (rules, ghost, registry, demolition) is written per
    /// cell for any other reason.
    /// </summary>
    protected override IReadOnlyList<Vector2I> Footprint(Vector2I anchor, Vector2I cell) =>
        [cell];

    /// <summary>
    /// One click makes one building of this node's <see cref="Kind"/>. The
    /// default <see cref="BuildTool.Apply"/> would write the tile and leave a
    /// building nothing could address; <see cref="WorldGrid.PlaceStructure"/>
    /// writes it <i>and</i> registers the entity that owns it, sized by
    /// <see cref="StorageCapacity"/>.
    /// </summary>
    protected override void Apply(PlacementPlan plan)
    {
        Structure? structure = World!.PlaceStructure(plan.Cells, Kind, StorageCapacity);
        if (structure != null)
        {
            GD.Print($"{Name}: placed {structure}");
        }
    }
}
