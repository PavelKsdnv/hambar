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
}

/// <summary>Why a cell, or a whole placement, was refused.</summary>
public enum PlacementRefusal
{
    None = 0,
    OffMap,
    UnbuildableTerrain,
    Occupied,
    NoRoadAccess,

    /// <summary>Nothing to place: an empty footprint, or no world to place in.</summary>
    NothingToPlace,
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
        new([], [], PlacementRefusal.NothingToPlace);

    public PlacementPlan(
        IReadOnlyList<Vector2I> cells,
        IReadOnlyList<PlacementRefusal> cellRefusals,
        PlacementRefusal refusal)
    {
        Cells = cells;
        CellRefusals = cellRefusals;
        Refusal = refusal;
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
    /// <b>Partial legality is all-or-nothing:</b> if any single cell of a
    /// multi-cell placement is illegal, the whole placement is refused — no
    /// build ever lands on "the legal part" of a drag. The per-cell verdicts
    /// survive in <see cref="PlacementPlan.CellRefusals"/> only so the ghost
    /// can point at the cells that caused the refusal.
    /// </summary>
    public static PlacementPlan Check(
        WorldGrid? world, IReadOnlyList<Vector2I> cells, PlacementRule rules, TileType placing)
    {
        if (world == null || cells.Count == 0)
        {
            return PlacementPlan.Nothing;
        }

        var refusals = new PlacementRefusal[cells.Count];
        PlacementRefusal verdict = PlacementRefusal.None;
        for (int i = 0; i < cells.Count; i++)
        {
            refusals[i] = CheckCell(world, cells[i], rules, placing);
            if (verdict == PlacementRefusal.None)
            {
                verdict = refusals[i];
            }
        }

        if (verdict == PlacementRefusal.None
            && rules.HasFlag(PlacementRule.TouchesRoad)
            && !HasRoadAccess(world, cells))
        {
            verdict = PlacementRefusal.NoRoadAccess;
        }

        return new PlacementPlan(cells, refusals, verdict);
    }

    /// <summary>The per-cell rules, applied to one cell on its own.</summary>
    public static PlacementRefusal CheckCell(
        WorldGrid world, Vector2I cell, PlacementRule rules, TileType placing)
    {
        if (rules.HasFlag(PlacementRule.BuildableTerrain))
        {
            TerrainType terrain = world.GetTerrain(cell);
            if (terrain == TerrainType.OutOfBounds)
            {
                return PlacementRefusal.OffMap;
            }
            if (!IsBuildableTerrain(terrain))
            {
                return PlacementRefusal.UnbuildableTerrain;
            }
        }

        if (rules.HasFlag(PlacementRule.VacantCell))
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
        PlacementRefusal.NoRoadAccess => "must touch a road",
        _ => "nothing to place",
    };
}
