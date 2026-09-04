using System;
using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Headless smoke test for build mode (M2): the <see cref="BuildTool"/> base,
/// the <see cref="PlacementRules"/> it validates against, and the ghost preview
/// that shows a refusal before the click. Instances Main.tscn and drives the
/// road tool through its public, cell-driven API — activate, hover, click,
/// cancel — because headless has no cursor. Run with:
/// godot --headless --path . res://scenes/dev/BuildSmokeTest.tscn
/// Exits 0 on pass, 1 on failure.
///
/// This is where the rest of M2 (field rectangles, structures, bulldoze, costs,
/// the palette) adds its assertions: give each new tool a section like
/// <see cref="CheckLegalPlacement"/> and reuse the cell-finding helpers at the
/// bottom, which look terrain up at runtime instead of hard-coding coordinates
/// that a seed change would invalidate.
/// </summary>
public partial class BuildSmokeTest : Node
{
    private WorldGrid _world = null!;
    private RoadBuildTool _tool = null!;
    private int _frame;
    private bool _failed;

    // Cells chosen from the generated terrain in _Ready, not hard-coded.
    private Vector2I _soilFrom;
    private Vector2I _soilTo;
    private Vector2I _rock;
    private Vector2I _waterAnchor;
    private Vector2I _water;
    private Vector2I _field;
    private Vector2I _fieldAnchor;
    private Vector2I _offMap;

    public override void _Ready()
    {
        Node main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        AddChild(main);
        _world = main.GetNode<WorldGrid>("World");
        _tool = main.GetNode<RoadBuildTool>("RoadTool");
    }

    public override void _Process(double delta)
    {
        _frame++;
        if (_frame == 5)
        {
            PickTestCells();
            CheckStartState();

            // Menu slot 1 arms the road tool — the same path the player takes.
            Input.ParseInputEvent(new InputEventAction { Action = "menu_1", Pressed = true });
        }
        else if (_frame == 10)
        {
            Check("menu key 1 activates the build tool", _tool.Active);

            CheckHoverPreview();
            // Before anything is placed: the road-access rule is asserted
            // against a world whose only road is the starting one.
            CheckRoadAccessRule();
            CheckLegalPlacement();
            CheckIllegalTerrainIsRefused();
            CheckOccupiedCellIsRefused();
            CheckOffMapIsRefused();

            // Esc drops a pending anchor.
            _tool.ClickCell(_soilFrom);
            Check("a fresh anchor is pending before the cancel", _tool.Anchor == _soilFrom);
            Input.ParseInputEvent(new InputEventAction { Action = "ui_cancel", Pressed = true });
        }
        else if (_frame == 15)
        {
            Check("Esc drops the pending anchor", _tool.Anchor == null);
            Check("Esc with an anchor keeps the tool active", _tool.Active);
            Check("cancelling clears the ghost", _tool.GhostCellCount == 0);

            // Right click does the same two steps: anchor first, tool second.
            _tool.ClickCell(_soilFrom);
            Input.ParseInputEvent(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Right,
                Pressed = true,
                Position = Vector2.Zero,
            });
        }
        else if (_frame == 20)
        {
            Check("right click drops the pending anchor", _tool.Anchor == null);
            Check("right click with an anchor keeps the tool active", _tool.Active);

            Input.ParseInputEvent(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Right,
                Pressed = true,
                Position = Vector2.Zero,
            });
        }
        else if (_frame == 25)
        {
            Check("right click with no anchor leaves the tool", !_tool.Active);
            Check("leaving the tool clears the ghost", _tool.GhostCellCount == 0);
            Check("leaving the tool clears the preview", _tool.Preview == null);

            GD.Print(_failed ? "BUILD SMOKE TEST FAILED" : "BUILD SMOKE TEST PASSED");
            GetTree().Quit(_failed ? 1 : 0);
        }
    }

    /// <summary>The tool is inert until it is armed.</summary>
    private void CheckStartState()
    {
        Check("build tool starts inactive", !_tool.Active);
        Check("build tool starts with no anchor", _tool.Anchor == null);
        Check("build tool starts with no ghost", _tool.GhostCellCount == 0);
        Check("build tool starts with no preview", _tool.Preview == null);
        Check("build tool is wired to the world", _tool.World == _world);
    }

    /// <summary>
    /// Hovering before the anchor: no ghost line yet, but the hover square is
    /// already tinted by the verdict on the cell under it.
    /// </summary>
    private void CheckHoverPreview()
    {
        _tool.HoverAt(_soilFrom);
        Check("hovering buildable ground previews a legal placement", _tool.PreviewLegal);
        Check("no ghost line before the anchor", _tool.GhostCellCount == 0);
        Check("the hover square reads legal on buildable ground",
            _tool.CursorColor.IsEqualApprox(BuildTool.CursorLegal));

        _tool.HoverAt(_rock);
        Check("hovering rock previews a refusal", !_tool.PreviewLegal);
        Check("the refusal names the terrain",
            _tool.Preview?.Refusal == PlacementRefusal.UnbuildableTerrain);
        Check("the hover square reads refused on rock",
            _tool.CursorColor.IsEqualApprox(BuildTool.CursorRefused));

        // A click that cannot legally anchor must not anchor at all.
        Check("a click on rock is refused", !_tool.ClickCell(_rock));
        Check("a refused click sets no anchor", _tool.Anchor == null);
        Check("rock stayed unbuilt", _world.GetTile(_rock) == TileType.Empty);
    }

    /// <summary>Anchor, ghost the whole line as legal, place it.</summary>
    private void CheckLegalPlacement()
    {
        int roadsBefore = _world.RoadCellCount;
        int lineLength = WorldGrid.LineCells(_soilFrom, _soilTo).Count;

        Check("clicking buildable ground anchors", _tool.ClickCell(_soilFrom));
        Check("the anchor is the clicked cell", _tool.Anchor == _soilFrom);

        _tool.HoverAt(_soilTo);
        Check("the ghost covers the whole pending line", _tool.GhostCellCount == lineLength);
        Check("a legal drag previews as legal", _tool.PreviewLegal);
        Check("every ghost cell of a legal drag is drawn legal",
            CountGhost(BuildTool.GhostLegal) == lineLength);

        Check("the second click places the line", _tool.ClickCell(_soilTo));
        Check("placing clears the anchor", _tool.Anchor == null);
        Check("placing keeps the tool armed for the next road", _tool.Active);
        Check("every cell of the line became road", AllRoad(_soilFrom, _soilTo));
        Check("exactly the line was placed", _world.RoadCellCount == roadsBefore + lineLength);
        Check("placing clears the ghost", _tool.GhostCellCount == 0);

        // Building over the road just built is legal — that is how a branch is
        // started from the existing network — and places nothing new.
        Check("a road may be started from an existing road cell",
            _tool.PlanFor(_soilFrom).Legal);
    }

    /// <summary>
    /// A drag whose far end runs into water: refused before the click, shown as
    /// refused in the ghost, and — the partial-legality rule — the legal cells
    /// of that drag are not built either.
    /// </summary>
    private void CheckIllegalTerrainIsRefused()
    {
        int roadsBefore = _world.RoadCellCount;
        List<Vector2I> line = WorldGrid.LineCells(_waterAnchor, _water);

        Check("the drag anchors on legal ground", _tool.ClickCell(_waterAnchor));
        _tool.HoverAt(_water);
        Check("a drag into water previews as refused", !_tool.PreviewLegal);
        Check("the refusal names the terrain",
            _tool.Preview?.Refusal == PlacementRefusal.UnbuildableTerrain);
        Check("the ghost still covers the whole drag", _tool.GhostCellCount == line.Count);
        Check("the ghost marks the water cell as the offender",
            CountGhost(BuildTool.GhostIllegalCell) == 1);
        Check("no cell of a refused drag is drawn legal",
            CountGhost(BuildTool.GhostLegal) == 0);

        Check("the click on a refused drag is rejected", !_tool.ClickCell(_water));
        Check("water stayed unbuilt", _world.GetTile(_water) == TileType.Empty);
        // Partial legality is all-or-nothing: one illegal cell refuses the
        // whole line, and none of its legal cells are built either.
        Check("one illegal cell refuses the whole line",
            _world.RoadCellCount == roadsBefore);
        Check("a refused placement keeps the anchor so the player can re-aim",
            _tool.Anchor == _waterAnchor);

        _tool.Cancel();
        Check("cancel drops that anchor", _tool.Anchor == null);
    }

    /// <summary>A cell holding a different placement refuses the build.</summary>
    private void CheckOccupiedCellIsRefused()
    {
        _world.SetTile(_field, TileType.Field);
        Check("the test field was placed", _world.GetTile(_field) == TileType.Field);

        Check("the drag anchors beside the field", _tool.ClickCell(_fieldAnchor));
        _tool.HoverAt(_field);
        Check("a drag onto a built cell previews as refused", !_tool.PreviewLegal);
        Check("the refusal names the occupied cell",
            _tool.Preview?.Refusal == PlacementRefusal.Occupied);
        Check("the click onto a built cell is rejected", !_tool.ClickCell(_field));
        Check("the field survived the refused road", _world.GetTile(_field) == TileType.Field);
        Check("the anchor cell was not built either",
            _world.GetTile(_fieldAnchor) == TileType.Empty);

        _tool.Cancel();
        _world.SetTile(_field, TileType.Empty);
    }

    /// <summary>Off the map is refused, and says so distinctly.</summary>
    private void CheckOffMapIsRefused()
    {
        Check("a cell past the map edge is refused",
            _tool.PlanFor(_offMap).Refusal == PlacementRefusal.OffMap);
        Check("a click past the map edge is rejected", !_tool.ClickCell(_offMap));
        Check("nothing was built past the map edge",
            _world.GetTile(_offMap) == TileType.Empty);
    }

    /// <summary>
    /// The must-touch-a-road rule. No tool opts into it yet — the structure
    /// tool will — so it is checked straight through <see cref="PlacementRules"/>
    /// to keep it live and proven until then.
    /// </summary>
    private void CheckRoadAccessRule()
    {
        var besideRoad = new Vector2I(0, 1); // the starting road runs along z = 0
        Check("the road-access rule accepts a cell beside a road",
            PlacementRules.Check(_world, [besideRoad], PlacementRule.TouchesRoad, TileType.Field)
                .Legal);

        Vector2I? isolated = FindCell(cell =>
            !_world.IsRoad(cell) && !PlacementRules.HasRoadAccess(_world, [cell]), 6, 40);
        Check("an isolated cell exists to test the road-access rule", isolated != null);
        if (isolated is { } far)
        {
            Check("the road-access rule refuses a cell away from the network",
                PlacementRules.Check(_world, [far], PlacementRule.TouchesRoad, TileType.Field)
                    .Refusal == PlacementRefusal.NoRoadAccess);
        }

        // A placement cannot satisfy its own road requirement: a footprint made
        // only of road cells still has to border a road outside itself.
        Check("the road-access rule ignores roads inside the footprint",
            !PlacementRules.HasRoadAccess(
                _world, WorldGrid.LineCells(new Vector2I(-16, 0), new Vector2I(16, 0))));
    }

    /// <summary>Ghost cells currently drawn in that colour.</summary>
    private int CountGhost(Color color)
    {
        int count = 0;
        for (int i = 0; i < _tool.GhostCellCount; i++)
        {
            if (_tool.GhostColor(i).IsEqualApprox(color))
            {
                count++;
            }
        }
        return count;
    }

    private bool AllRoad(Vector2I from, Vector2I to)
    {
        foreach (Vector2I cell in WorldGrid.LineCells(from, to))
        {
            if (!_world.IsRoad(cell))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Picks the cells the assertions use out of the *generated* terrain rather
    /// than hard-coding coordinates, so a changed seed or noise tuning cannot
    /// silently turn a "legal ground" cell into water.
    /// </summary>
    private void PickTestCells()
    {
        Vector2I? soil = FindCell(cell => IsFreeSoilLine(cell, cell + new Vector2I(3, 0)));
        Vector2I? rock = FindCell(cell =>
            _world.GetTerrain(cell) == TerrainType.Rock && _world.GetTile(cell) == TileType.Empty);

        // The overlap check needs a pair of cells the road line will not have
        // covered by the time it runs.
        var roadLine = new HashSet<Vector2I>(soil is { } start
            ? WorldGrid.LineCells(start, start + new Vector2I(3, 0))
            : []);
        Vector2I? field = FindCell(cell =>
            IsFreeSoil(cell) && IsFreeSoil(cell + Vector2I.Right)
            && !roadLine.Contains(cell) && !roadLine.Contains(cell + Vector2I.Right));

        Check("found a clear soil line to build on", soil != null);
        Check("found a rock cell to be refused on", rock != null);
        Check("found a clear soil pair for the overlap check", field != null);
        Check("found a soil-to-water line for the terrain check",
            TryFindLineIntoWater(out _waterAnchor, out _water));

        _soilFrom = soil ?? Vector2I.Zero;
        _soilTo = _soilFrom + new Vector2I(3, 0);
        _rock = rock ?? Vector2I.Zero;
        _field = field ?? Vector2I.Zero;
        _fieldAnchor = _field + Vector2I.Right;
        _offMap = new Vector2I(_world.MapSize / 2 + 3, 0);

        GD.Print($"build test cells: line {_soilFrom} to {_soilTo}, rock {_rock}, "
            + $"water {_water} from {_waterAnchor}, field {_field}, off-map {_offMap}");
    }

    private bool IsFreeSoil(Vector2I cell) =>
        _world.IsSoil(cell) && _world.GetTile(cell) == TileType.Empty;

    private bool IsFreeSoilLine(Vector2I from, Vector2I to)
    {
        foreach (Vector2I cell in WorldGrid.LineCells(from, to))
        {
            if (!IsFreeSoil(cell))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// A straight run of clear soil ending one cell short of open water, so a
    /// drag from the run's far end into the water is legal everywhere but its
    /// last cell.
    /// </summary>
    private bool TryFindLineIntoWater(out Vector2I anchor, out Vector2I water)
    {
        Vector2I[] directions = [Vector2I.Right, Vector2I.Left, Vector2I.Up, Vector2I.Down];
        for (int radius = 1; radius <= 40; radius++)
        {
            foreach (Vector2I cell in Ring(radius))
            {
                if (_world.GetTerrain(cell) != TerrainType.Water
                    || _world.GetTile(cell) != TileType.Empty)
                {
                    continue;
                }
                foreach (Vector2I direction in directions)
                {
                    Vector2I candidate = cell + direction * 3;
                    if (IsFreeSoilLine(candidate, cell + direction))
                    {
                        anchor = candidate;
                        water = cell;
                        return true;
                    }
                }
            }
        }
        anchor = water = Vector2I.Zero;
        return false;
    }

    /// <summary>
    /// First cell matching the predicate, searched outward from the origin in
    /// square rings — so the cells the test uses are the ones nearest the
    /// starting area, and the search is deterministic.
    /// </summary>
    private Vector2I? FindCell(Func<Vector2I, bool> match, int minRadius = 0, int maxRadius = 40)
    {
        for (int radius = minRadius; radius <= maxRadius; radius++)
        {
            foreach (Vector2I cell in Ring(radius))
            {
                if (_world.InBounds(cell) && match(cell))
                {
                    return cell;
                }
            }
        }
        return null;
    }

    /// <summary>Cells exactly <paramref name="radius"/> steps away in Chebyshev distance.</summary>
    private static IEnumerable<Vector2I> Ring(int radius)
    {
        for (int y = -radius; y <= radius; y++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                if (Math.Max(Math.Abs(x), Math.Abs(y)) == radius)
                {
                    yield return new Vector2I(x, y);
                }
            }
        }
    }

    private void Check(string what, bool ok)
    {
        GD.Print($"{(ok ? "PASS" : "FAIL")}: {what}");
        _failed |= !ok;
    }
}
