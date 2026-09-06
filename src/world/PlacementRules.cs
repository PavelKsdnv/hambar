using System;
using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// The legality rules a build tool can opt into. A tool declares the set it
/// cares about (see <see cref="BuildTool.Rules"/>) and
/// <see cref="PlacementRules.Check"/> applies exactly those — so "roads may not
/// cross water" and "a barn must touch a road" are one shared implementation
/// with different flags, never a rule re-coded per tool.
/// </summary>
[Flags]
public enum PlacementRule
{
    None = 0,

    /// <summary>The cell must be on the map and on buildable ground (soil).</summary>
    BuildableTerrain = 1 << 0,

    /// <summary>
    /// The cell must not already hold a *different* placement. Placing what is
    /// already there stays legal, which is what lets a road be extended from a
    /// cell of the existing network.
    /// </summary>
    NoOverlap = 1 << 1,

    /// <summary>
    /// The placement as a whole must border the road network — at least one
    /// footprint cell with a road neighbour outside the footprint. A
    /// footprint rule, not a per-cell one.
    /// </summary>
    TouchesRoad = 1 << 2,

    /// <summary>
    /// The cell must hold <i>nothing at all</i> — stricter than
    /// <see cref="NoOverlap"/>, which lets a tool place over its own tile.
    /// Fields opt into this because a cell belongs to exactly one
    /// <see cref="Field"/>: re-covering a field cell would mean two fields
    /// claiming it, so a new rectangle has to start on free ground.
    /// </summary>
    VacantCell = 1 << 3,

    /// <summary>
    /// The cell must be on the map, and nothing more — the weakest terrain
    /// rule there is. <see cref="BuildableTerrain"/> is its stricter form (on
    /// the map <i>and</i> soil), so a tool sets one or the other, never both.
    /// It exists for the bulldozer: the map edge still bounds where it may
    /// work, but "is this soil?" is not its business — what it may take off a
    /// cell has nothing to do with what could be built there.
    /// </summary>
    InBounds = 1 << 4,

    /// <summary>
    /// The cell must hold <i>something</i> — the exact inverse of
    /// <see cref="VacantCell"/>, and the rule the bulldozer is built on: an
    /// empty cell has nothing to clear, which is the one thing a removal can
    /// be refused for. Mutually exclusive with <see cref="VacantCell"/> and
    /// <see cref="NoOverlap"/>: a tool either wants the cell free or wants it
    /// taken, never both.
    /// </summary>
    OccupiedCell = 1 << 5,
}

/// <summary>Why a cell, or a whole placement, was refused.</summary>
public enum PlacementRefusal
{
    None = 0,
    OffMap,
    UnbuildableTerrain,
    Occupied,

    /// <summary>
    /// The mirror of <see cref="Occupied"/>, and a removal's own refusal: the
    /// cell holds nothing the player put there, so there is nothing to take
    /// off it (<see cref="PlacementRule.OccupiedCell"/>).
    /// </summary>
    NothingToClear,

    NoRoadAccess,

    /// <summary>
    /// The player cannot pay for it. A refusal like any other, and
    /// deliberately so: it is decided inside <see cref="PlacementRules.Check"/>
    /// from the <see cref="PlacementBudget"/> handed in, so the ghost shows
    /// "you cannot afford this" before the click exactly the way it shows
    /// "that is water". Like <see cref="NoRoadAccess"/> it is a property of the
    /// <i>whole</i> placement — no single cell is the offender — because a
    /// drag is bought outright or not at all.
    /// </summary>
    CannotAfford,

    /// <summary>Nothing to place: an empty footprint, or no world to place in.</summary>
    NothingToPlace,
}

/// <summary>
/// What a placement costs and what the player has to pay with — the pair
/// <see cref="PlacementRules.Check"/> needs to answer "can this be afforded?"
/// <b>as part of the verdict</b> rather than after it.
///
/// It is a <i>value</i>, not a handle on the account, and that is the whole of
/// the design: <see cref="PlacementRules"/> stays a pure evaluator with no node
/// state, so money reaches it the same way the world does — passed in. A tool
/// reads its own price (<see cref="BuildTool.CostPerCell"/>) and the
/// <see cref="Economy"/>'s balance and hands over the pair; nothing in the
/// rules ever reaches out for either, and nothing here can spend anything.
/// </summary>
public readonly struct PlacementBudget
{
    /// <summary>
    /// A placement nothing charges for, and which is therefore always
    /// affordable — the default, so every caller that has no money to talk
    /// about (dev code, the tests that check a rule on its own, a tool with no
    /// account wired) keeps asking the same question it always did.
    /// </summary>
    public static PlacementBudget Free => default;

    public PlacementBudget(int costPerCell, int balance)
    {
        CostPerCell = costPerCell;
        Balance = balance;
    }

    /// <summary>What one charged cell costs. Zero for a tool that is free.</summary>
    public int CostPerCell { get; }

    /// <summary>The money that is there to pay with.</summary>
    public int Balance { get; }

    /// <summary>
    /// What <paramref name="chargedCells"/> cells come to. Affordability is a
    /// whole-placement property, like <see cref="PlacementRule.TouchesRoad"/>:
    /// a ten-cell road at five each costs fifty, and the player either buys all
    /// of it or none of it — never "the cells that were individually
    /// affordable".
    /// </summary>
    public int Price(int chargedCells) => CostPerCell * chargedCells;

    /// <summary>Whether that total is within the balance.</summary>
    public bool CanAfford(int cost) => cost <= Balance;
}

/// <summary>
/// How the per-cell verdicts on a footprint add up to the verdict on the whole
/// thing. Two policies, because building and clearing genuinely want opposite
/// answers on a mixed region — not because a tool might like one better.
/// </summary>
public enum FootprintPolicy
{
    /// <summary>
    /// <b>Every cell must pass.</b> One illegal cell refuses the whole
    /// placement and nothing lands on "the legal part" of the drag — a half
    /// road or a field with a bite out of it is not what the player asked for.
    /// Every building tool uses this.
    /// </summary>
    EveryCell,

    /// <summary>
    /// <b>One passing cell is enough</b>, and the cells that fail are simply
    /// skipped. The bulldozer's policy: a drag across a farm crosses empty
    /// ground as a matter of course, so refusing the whole region over it
    /// would make the tool unusable. The drag is refused only when there is
    /// nothing at all in it to remove.
    /// </summary>
    AnyCell,
}

/// <summary>
/// The verdict on one candidate placement: which cells it would cover, which
/// of them are individually illegal, and whether the placement as a whole is
/// allowed. Build tools show it as the ghost preview and refuse the click when
/// <see cref="Legal"/> is false, so the player always sees the refusal before
/// committing to it.
/// </summary>
public sealed class PlacementPlan
{
    /// <summary>The verdict on "nothing at all" — refused, covering no cells.</summary>
    public static readonly PlacementPlan Nothing =
        new([], [], PlacementRefusal.NothingToPlace, 0);

    public PlacementPlan(
        IReadOnlyList<Vector2I> cells,
        IReadOnlyList<PlacementRefusal> cellRefusals,
        PlacementRefusal refusal,
        int cost)
    {
        Cells = cells;
        CellRefusals = cellRefusals;
        Refusal = refusal;
        Cost = cost;
    }

    /// <summary>Cells the placement would write, in footprint order.</summary>
    public IReadOnlyList<Vector2I> Cells { get; }

    /// <summary>
    /// Per-cell verdict, parallel to <see cref="Cells"/>;
    /// <see cref="PlacementRefusal.None"/> where the cell itself is fine.
    /// </summary>
    public IReadOnlyList<PlacementRefusal> CellRefusals { get; }

    /// <summary>Why the whole placement is refused, or None when it is allowed.</summary>
    public PlacementRefusal Refusal { get; }

    /// <summary>
    /// What committing this placement charges: the tool's price times the
    /// cells it would actually act on (see <see cref="PlacementRules.Check"/>).
    /// Priced <i>here</i>, with the rest of the verdict, so the amount the
    /// player is charged on the click is the amount the ghost was validated
    /// against — the click never prices anything itself. Zero for a free tool,
    /// and for a plan checked without a <see cref="PlacementBudget"/>.
    /// </summary>
    public int Cost { get; }

    public bool Legal => Refusal == PlacementRefusal.None;

    public int Count => Cells.Count;

    /// <summary>Whether that one cell passes the per-cell rules.</summary>
    public bool CellLegal(int index) => CellRefusals[index] == PlacementRefusal.None;

    public override string ToString() =>
        Legal
            ? $"{Count} cell(s), legal"
            : $"{Count} cell(s), refused: {PlacementRules.Explain(Refusal)}";
}

/// <summary>
/// Evaluates <see cref="PlacementRule"/>s against the world. Pure queries over
/// <see cref="WorldGrid"/> — no node state, no side effects — so tools, the
/// HUD and the headless tests all ask the same code the same question.
/// </summary>
public static class PlacementRules
{
    // 4-neighbourhood: road access means sharing an edge with a road, not a corner.
    private static readonly Vector2I[] Neighbors =
        [Vector2I.Right, Vector2I.Left, Vector2I.Up, Vector2I.Down];

    /// <summary>Ground a player may build on. Rock and water are not.</summary>
    public static bool IsBuildableTerrain(TerrainType terrain) => terrain == TerrainType.Soil;

    /// <summary>
    /// The verdict on placing <paramref name="placing"/> over
    /// <paramref name="cells"/> under <paramref name="rules"/>.
    ///
    /// <b>Partial legality is all-or-nothing</b> under the default
    /// <see cref="FootprintPolicy.EveryCell"/>: if any single cell of a
    /// multi-cell placement is illegal, the whole placement is refused — no
    /// build ever lands on "the legal part" of a drag. Removal inverts that
    /// with <see cref="FootprintPolicy.AnyCell"/>, where the failing cells are
    /// skipped instead. Either way the per-cell verdicts survive in
    /// <see cref="PlacementPlan.CellRefusals"/>, so the ghost can point at the
    /// cells that caused the refusal — or, under
    /// <see cref="FootprintPolicy.AnyCell"/>, at the cells nothing will happen
    /// to.
    ///
    /// <b>Cost is one of the rules.</b> <paramref name="budget"/> carries the
    /// tool's price and the player's balance, and the plan comes back priced
    /// (<see cref="PlacementPlan.Cost"/>) and refused with
    /// <see cref="PlacementRefusal.CannotAfford"/> when the total is out of
    /// reach — so "you cannot afford this" is in the ghost with every other
    /// refusal instead of being discovered on the click. Omit it and the
    /// placement is free, which is what every caller that has no money to talk
    /// about wants.
    /// </summary>
    public static PlacementPlan Check(
        WorldGrid? world,
        IReadOnlyList<Vector2I> cells,
        PlacementRule rules,
        TileType placing,
        FootprintPolicy policy = FootprintPolicy.EveryCell,
        PlacementBudget budget = default)
    {
        if (world == null || cells.Count == 0)
        {
            return PlacementPlan.Nothing;
        }

        var refusals = new PlacementRefusal[cells.Count];
        PlacementRefusal firstRefusal = PlacementRefusal.None;
        int legalCells = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            refusals[i] = CheckCell(world, cells[i], rules, placing);
            if (refusals[i] == PlacementRefusal.None)
            {
                legalCells++;
            }
            else if (firstRefusal == PlacementRefusal.None)
            {
                firstRefusal = refusals[i];
            }
        }

        // The two policies read the same per-cell verdicts in opposite
        // directions: a build wants no refusal anywhere, a removal wants at
        // least one cell it can act on. When an AnyCell footprint has none,
        // the first cell's refusal is still what to tell the player.
        PlacementRefusal verdict = policy == FootprintPolicy.AnyCell
            ? legalCells > 0 ? PlacementRefusal.None : firstRefusal
            : firstRefusal;

        if (verdict == PlacementRefusal.None
            && rules.HasFlag(PlacementRule.TouchesRoad)
            && !HasRoadAccess(world, cells))
        {
            verdict = PlacementRefusal.NoRoadAccess;
        }

        // The price is the price of the cells the placement will act on. For a
        // build that is the whole footprint — all-or-nothing, so there is
        // nothing else it could be. For an AnyCell footprint it is only the
        // cells that pass: a bulldoze drag crosses empty ground as a matter of
        // course, and charging for cells nothing happens to would be charging
        // for nothing. Affordability is judged last, after every reason that
        // is not about money, so the player is told the more useful of two
        // refusals.
        int chargedCells = policy == FootprintPolicy.AnyCell ? legalCells : cells.Count;
        int cost = budget.Price(chargedCells);
        if (verdict == PlacementRefusal.None && !budget.CanAfford(cost))
        {
            verdict = PlacementRefusal.CannotAfford;
        }

        return new PlacementPlan(cells, refusals, verdict, cost);
    }

    /// <summary>The per-cell rules, applied to one cell on its own.</summary>
    public static PlacementRefusal CheckCell(
        WorldGrid world, Vector2I cell, PlacementRule rules, TileType placing)
    {
        // BuildableTerrain is InBounds plus "and it must be soil", so the map
        // bound is checked once for either flag and the terrain kind only for
        // the stricter one.
        if (rules.HasFlag(PlacementRule.BuildableTerrain) || rules.HasFlag(PlacementRule.InBounds))
        {
            if (!world.InBounds(cell))
            {
                return PlacementRefusal.OffMap;
            }
        }

        if (rules.HasFlag(PlacementRule.BuildableTerrain)
            && !IsBuildableTerrain(world.GetTerrain(cell)))
        {
            return PlacementRefusal.UnbuildableTerrain;
        }

        if (rules.HasFlag(PlacementRule.OccupiedCell))
        {
            // The inverse rule, and the whole of a removal's per-cell legality:
            // it is only ever refused for having nothing to take.
            if (world.GetTile(cell) == TileType.Empty)
            {
                return PlacementRefusal.NothingToClear;
            }
        }
        else if (rules.HasFlag(PlacementRule.VacantCell))
        {
            if (world.GetTile(cell) != TileType.Empty)
            {
                return PlacementRefusal.Occupied;
            }
        }
        else if (rules.HasFlag(PlacementRule.NoOverlap))
        {
            TileType existing = world.GetTile(cell);
            if (existing != TileType.Empty && existing != placing)
            {
                return PlacementRefusal.Occupied;
            }
        }

        return PlacementRefusal.None;
    }

    /// <summary>
    /// Whether the footprint borders the road network: some cell of it shares
    /// an edge with a road cell that is not itself part of the footprint (a
    /// placement can't satisfy its own road requirement).
    /// </summary>
    public static bool HasRoadAccess(WorldGrid world, IReadOnlyList<Vector2I> cells)
    {
        var footprint = new HashSet<Vector2I>(cells);
        foreach (Vector2I cell in cells)
        {
            foreach (Vector2I step in Neighbors)
            {
                Vector2I neighbor = cell + step;
                if (!footprint.Contains(neighbor) && world.IsRoad(neighbor))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Player-facing wording for a refusal (HUD text, logs, tests).</summary>
    public static string Explain(PlacementRefusal refusal) => refusal switch
    {
        PlacementRefusal.None => "ok",
        PlacementRefusal.OffMap => "off the map",
        PlacementRefusal.UnbuildableTerrain => "cannot build on rock or water",
        PlacementRefusal.Occupied => "something is already built here",
        PlacementRefusal.NothingToClear => "nothing here to clear",
        PlacementRefusal.NoRoadAccess => "must touch a road",
        PlacementRefusal.CannotAfford => "not enough money",
        _ => "nothing to place",
    };
}
