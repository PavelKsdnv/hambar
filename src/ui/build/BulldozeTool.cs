using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Bulldoze tool, toggled by menu key 5: drag a rectangle and everything the
/// player built inside it comes off, leaving the terrain underneath exactly as
/// it was. The fourth <see cref="BuildTool"/>, and the first that removes
/// instead of places — which is why it is the one that had to bend the base
/// rather than just fill in the contract.
///
/// <b>What legality means for a bulldozer.</b> None of the building rules
/// describe it. <see cref="PlacementRule.BuildableTerrain"/> asks whether soil
/// could be built on, which has nothing to do with whether something can be
/// taken off; <see cref="PlacementRule.VacantCell"/> demands the exact
/// opposite of what this tool is for. So it opts into the two rules that do
/// describe removal — <see cref="PlacementRule.InBounds"/> (the map edge still
/// bounds where the player works) and
/// <see cref="PlacementRule.OccupiedCell"/> (a cell with nothing on it has
/// nothing to clear) — and there is no third one: what a cell holds never
/// makes it un-removable. A road under a vehicle is the one case that will
/// want re-checking, and it is M5's; see <see cref="WorldGrid.Clear"/>.
///
/// <b>A mixed drag clears what is there and skips what is not.</b> Building is
/// all-or-nothing, because half a road is not what the player asked for. A
/// bulldoze rectangle crossing empty ground <i>is</i> what the player asked
/// for — clearing a farmyard means dragging over the gaps between its
/// buildings — so this tool takes <see cref="FootprintPolicy.AnyCell"/>: the
/// drag is refused only when there is nothing in it at all, and the ghost
/// keeps that honest by drawing the cells it will take in the legal colour and
/// dimming the ones it will not. What the player sees is exactly what the
/// click does. Starting the drag is the same rule applied to the one cell
/// under the cursor, so an anchor still has to land on something removable.
///
/// <b>The registries differ on removal, deliberately, and this tool inherits
/// that.</b> Every cell goes through <see cref="WorldGrid.Clear"/>: a field
/// shrinks cell by cell and is dropped when its last one goes, while clipping
/// any single cell of a building demolishes the whole building. Half a mill is
/// not a mill.
/// </summary>
public partial class BulldozeTool : BuildTool
{
    private readonly List<Removal> _removals = new();

    /// <summary>
    /// The one tool that writes nothing. <see cref="TileType.Empty"/> is only
    /// here because the base hands <see cref="PlacedTile"/> to the rules as
    /// "what is being placed", and the only rule that reads it
    /// (<see cref="PlacementRule.NoOverlap"/>) is one this tool does not use.
    /// </summary>
    protected override TileType PlacedTile => TileType.Empty;

    /// <summary>
    /// On the map, and holding something. That is the whole of it — see the
    /// class summary for why none of the building rules apply.
    /// </summary>
    protected override PlacementRule Rules =>
        PlacementRule.InBounds | PlacementRule.OccupiedCell;

    /// <summary>
    /// The bulldozer's inversion of the all-or-nothing rule: empty cells in the
    /// drag are skipped, not fatal.
    /// </summary>
    protected override FootprintPolicy Policy => FootprintPolicy.AnyCell;

    /// <summary>Same rectangle drag as the field tool: two opposite corners.</summary>
    protected override IReadOnlyList<Vector2I> Footprint(Vector2I anchor, Vector2I cell) =>
        WorldGrid.RectCells(anchor, cell);

    /// <summary>
    /// The ghost shows the cells that will actually be cleared, not the verdict
    /// on the drag: legal where there is something to take, dimmed where there
    /// is not. The base's version would paint a legal drag's every cell legal —
    /// true for a build, a lie for a removal that skips half of them.
    /// </summary>
    protected override Color GhostColorFor(PlacementPlan plan, int index) =>
        plan.CellLegal(index) ? GhostLegal : GhostRefused;

    /// <summary>
    /// Everything this tool has taken off the map, in removal order — one
    /// entry per <i>removal</i>, not per cleared cell, so a demolished building
    /// appears once however many of its cells the drag covered. It is the
    /// ledger of what went through <see cref="RefundFor"/>; a HUD (or the
    /// smoke test) reads it without the tool having to log.
    /// </summary>
    public IReadOnlyList<Removal> Removals => _removals;

    /// <summary>
    /// What the player has been refunded, in whatever money M7 ends up using.
    /// Zero, and it stays zero until there is a build cost to give a fraction
    /// of — see <see cref="RefundFor"/>. It is also the placeholder for the
    /// account: the next issue's money counter takes over the one line in
    /// <see cref="Apply"/> that adds to it.
    /// </summary>
    public int RefundTotal { get; private set; }

    /// <summary>
    /// Clears every cell of the drag that has something on it, skipping the
    /// rest. Each cell is re-read at the moment it is cleared rather than
    /// trusted from the plan, which is what stops a drag that clipped two cells
    /// of the same building from removing (and refunding) it twice: by then the
    /// second cell is already empty and <see cref="WorldGrid.Clear"/> answers
    /// null.
    /// </summary>
    protected override void Apply(PlacementPlan plan)
    {
        foreach (Vector2I cell in plan.Cells)
        {
            if (World!.Clear(cell) is not { } removed)
            {
                continue;
            }

            // Every removal, and nothing else, comes through here: priced by
            // the seam, credited on the line after it — which is the line the
            // money counter takes over — and recorded, so what the bulldozer
            // took can be read back without parsing a log.
            int refund = RefundFor(removed);
            _removals.Add(removed);
            RefundTotal += refund;
            GD.Print($"{Name}: cleared {removed}, refund {refund}");
        }
    }

    /// <summary>
    /// <b>The refund seam — an M7 stub, and the single place a removal is
    /// priced.</b> Every removal the player makes passes through here exactly
    /// once, carrying the thing that came off (kind, entity, footprint) and
    /// where it stood — everything a real rule would need to answer "how much
    /// of the build cost does the player get back".
    ///
    /// It pays nothing today, and that is deliberate rather than unfinished:
    /// nothing has a build cost yet, so no fraction of one exists to return,
    /// and the refund <i>economics</i> — what fraction, whether it varies by
    /// building, whether a bulldozed field returns anything at all — are M7's
    /// to design, not this tool's to guess at. When there are costs, this
    /// becomes one expression — <c>fraction * BuildCost(removed)</c> — and
    /// nothing that calls it has to change; the money counter that comes next
    /// credits what it returns.
    /// </summary>
    private static int RefundFor(Removal removed) => 0;
}
