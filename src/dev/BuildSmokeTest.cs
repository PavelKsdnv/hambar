using System;
using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Headless smoke test for build mode (M2): the <see cref="BuildTool"/> base,
/// the <see cref="PlacementRules"/> it validates against, and the ghost preview
/// that shows a refusal before the click. Instances Main.tscn and drives the
/// tools through their public, cell-driven API — activate, hover, click,
/// cancel — because headless has no cursor. Run with:
/// godot --headless --path . res://scenes/dev/BuildSmokeTest.tscn
/// Exits 0 on pass, 1 on failure.
///
/// Two tools are covered, in order: the road tool first, then the field tool —
/// which therefore validates against a world that already holds this test's
/// roads, and is also where the "only one tool armed at a time" rule is
/// proven. The rest of M2 (structures, bulldoze, costs, the palette) adds its
/// assertions the same way: give each new tool a section like
/// <see cref="CheckLegalPlacement"/> and reuse the cell-finding helpers at the
/// bottom, which look terrain up at runtime instead of hard-coding coordinates
/// that a seed change would invalidate.
/// </summary>
public partial class BuildSmokeTest : Node
{
    /// <summary>Width and height of the rectangle the field checks mark.</summary>
    private const int FieldRectWidth = 3;
    private const int FieldRectHeight = 2;

    private WorldGrid _world = null!;
    private RoadBuildTool _tool = null!;
    private FieldBuildTool _fieldTool = null!;
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

    // The field section's cells, picked once the roads are down (see PickFieldCells).
    private Vector2I _rectFrom;
    private Vector2I _rectTo;
    private Vector2I _nextRectFrom;
    private Vector2I _nextRectTo;
    private Vector2I _belowRect;
    private Field? _markedField;

    public override void _Ready()
    {
        Node main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        AddChild(main);
        _world = main.GetNode<WorldGrid>("World");
        _tool = main.GetNode<RoadBuildTool>("RoadTool");
        _fieldTool = main.GetNode<FieldBuildTool>("FieldTool");
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

            // Re-arm the road tool, so the next frame can prove that arming the
            // field tool is what disarms it.
            Input.ParseInputEvent(new InputEventAction { Action = "menu_1", Pressed = true });
        }
        else if (_frame == 30)
        {
            Check("menu key 1 re-arms the road tool", _tool.Active);

            // Menu slot 3 arms the field tool — again, the player's path.
            Input.ParseInputEvent(new InputEventAction { Action = "menu_3", Pressed = true });
        }
        else if (_frame == 35)
        {
            CheckOneToolAtATime();
            CheckRectFootprint();

            PickFieldCells();
            CheckFieldPlacement();
            CheckFieldsStayDistinct();
            CheckFieldOverlapIsRefused();
            CheckFieldOverRoadIsRefused();
            CheckFieldIntoRoughTerrainIsRefused();
            CheckClearingShrinksAndDropsAField();

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
        Check("the field tool starts inactive too", !_fieldTool.Active);
        Check("the field tool is wired to the world", _fieldTool.World == _world);
        Check("no field is registered before one is marked",
            _world.Fields.Count == 0 && _world.FieldCellCount == 0);
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
            CountGhost(_tool, BuildTool.GhostLegal) == lineLength);

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
            CountGhost(_tool, BuildTool.GhostIllegalCell) == 1);
        Check("no cell of a refused drag is drawn legal",
            CountGhost(_tool, BuildTool.GhostLegal) == 0);

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

    // --- field marking -----------------------------------------------------

    /// <summary>
    /// Build mode has exactly one armed tool: arming the field tool through
    /// menu key 3 disarms the road tool, and a disarmed tool shows nothing.
    /// Neither tool knows about the other — the <see cref="BuildTool.ToolGroup"/>
    /// scene group carries it, so a future tool gets the same for free.
    /// </summary>
    private void CheckOneToolAtATime()
    {
        Check("menu key 3 activates the field tool", _fieldTool.Active);
        Check("arming the field tool disarms the road tool", !_tool.Active);
        Check("the disarmed tool clears its ghost", _tool.GhostCellCount == 0);
        Check("the disarmed tool clears its preview", _tool.Preview == null);
        Check("the disarmed tool drops any anchor", _tool.Anchor == null);
        Check("both tools joined the build-tool group",
            GetTree().GetNodesInGroup(BuildTool.ToolGroup).Count == 2);
    }

    /// <summary>
    /// <see cref="WorldGrid.RectCells"/> on its own: a filled rectangle
    /// inclusive of both corners, in a fixed row-major order, and the *same*
    /// list whichever of the four corners the player started the drag from.
    /// The coordinates here are deliberately literal — this is pure geometry
    /// that never touches the map, so there is no terrain to search.
    /// </summary>
    private void CheckRectFootprint()
    {
        var min = new Vector2I(2, 3);
        var max = new Vector2I(6, 5);
        var topRight = new Vector2I(max.X, min.Y);
        var bottomLeft = new Vector2I(min.X, max.Y);
        List<Vector2I> cells = WorldGrid.RectCells(min, max);

        Check("a rectangle covers width x height cells", cells.Count == 5 * 3);
        Check("the rectangle holds no cell twice",
            new HashSet<Vector2I>(cells).Count == cells.Count);
        Check("the rectangle includes all four corners",
            cells.Contains(min) && cells.Contains(max)
            && cells.Contains(topRight) && cells.Contains(bottomLeft));
        Check("the rectangle starts at the minimum corner", cells[0] == min);
        Check("the rectangle ends at the maximum corner", cells[^1] == max);
        Check("the rectangle stays inside its corners", InsideRect(cells, min, max));

        Check("dragging the opposite way covers the same cells",
            SameCells(cells, WorldGrid.RectCells(max, min)));
        Check("dragging from the top-right corner covers the same cells",
            SameCells(cells, WorldGrid.RectCells(topRight, bottomLeft)));
        Check("dragging from the bottom-left corner covers the same cells",
            SameCells(cells, WorldGrid.RectCells(bottomLeft, topRight)));

        List<Vector2I> single = WorldGrid.RectCells(min, min);
        Check("a drag that never left its cell is that one cell",
            single.Count == 1 && single[0] == min);
    }

    /// <summary>
    /// One drag, one field. The rectangle is dragged from its *far* corner, to
    /// prove the committed region does not depend on which corner started it,
    /// and the preview is asserted before the second click: the whole rectangle
    /// is ghosted, and nothing is written until the commit.
    /// </summary>
    private void CheckFieldPlacement()
    {
        List<Vector2I> rect = WorldGrid.RectCells(_rectFrom, _rectTo);
        int fieldsBefore = _world.Fields.Count;
        int cellsBefore = _world.FieldCellCount;

        Check("clicking clear soil anchors the field drag", _fieldTool.ClickCell(_rectTo));
        Check("the field anchor is the clicked corner", _fieldTool.Anchor == _rectTo);

        _fieldTool.HoverAt(_rectFrom);
        Check("a legal rectangle previews as legal", _fieldTool.PreviewLegal);
        Check("the preview covers the whole rectangle before the commit",
            _fieldTool.Preview?.Count == rect.Count);
        Check("the ghost covers the whole pending rectangle",
            _fieldTool.GhostCellCount == rect.Count);
        Check("every ghost cell of a legal rectangle is drawn legal",
            CountGhost(_fieldTool, BuildTool.GhostLegal) == rect.Count);
        Check("the preview itself writes nothing",
            _world.Fields.Count == fieldsBefore && _world.FieldCellCount == cellsBefore);

        Check("the second click marks the field", _fieldTool.ClickCell(_rectFrom));
        Check("marking clears the anchor", _fieldTool.Anchor == null);
        Check("marking keeps the tool armed for the next field", _fieldTool.Active);
        Check("marking clears the ghost", _fieldTool.GhostCellCount == 0);
        Check("every cell of the rectangle became a field tile",
            AllTile(rect, TileType.Field));
        Check("exactly the rectangle was marked",
            _world.FieldCellCount == cellsBefore + rect.Count);

        // The point of the whole feature: the rectangle is one addressable
        // entity, not a pile of field tiles.
        Check("the drag created exactly one field", _world.Fields.Count == fieldsBefore + 1);
        _markedField = _world.GetField(_rectFrom);
        Check("the marked cells belong to a field", _markedField != null);
        if (_markedField is not { } field)
        {
            return;
        }
        Check("every cell of the rectangle addresses the same field",
            AllSameField(rect, field));
        Check("the field owns exactly the rectangle's cells", field.CellCount == rect.Count);
        Check("the field knows the cells it owns", field.Contains(_rectTo));
        Check("the field bounds are the dragged rectangle",
            field.Bounds == new Rect2I(_rectFrom, _rectTo - _rectFrom + Vector2I.One));
        Check("the field is the one the world just registered", _world.Fields[^1] == field);
        Check("the field is named", !string.IsNullOrEmpty(field.Name));
    }

    /// <summary>
    /// Two rectangles that share an edge stay two fields: touching regions are
    /// deliberately never merged, because each is separately named and worked.
    /// </summary>
    private void CheckFieldsStayDistinct()
    {
        List<Vector2I> rect = WorldGrid.RectCells(_nextRectFrom, _nextRectTo);
        int fieldsBefore = _world.Fields.Count;
        int cellsBefore = _world.FieldCellCount;

        Check("the second rectangle anchors beside the first",
            _fieldTool.ClickCell(_nextRectFrom));
        _fieldTool.HoverAt(_nextRectTo);
        Check("a rectangle bordering a field previews as legal", _fieldTool.PreviewLegal);
        Check("the second click marks it", _fieldTool.ClickCell(_nextRectTo));

        Check("the second rectangle is a second field",
            _world.Fields.Count == fieldsBefore + 1);
        Check("both rectangles are owned cell for cell",
            _world.FieldCellCount == cellsBefore + rect.Count);

        Field? neighbor = _world.GetField(_nextRectFrom);
        Check("the neighbouring rectangle addresses a field", neighbor != null);
        Check("touching rectangles are not merged",
            neighbor != null && neighbor != _markedField);
        Check("the two fields really do share an edge",
            _world.GetField(_nextRectFrom + Vector2I.Left) == _markedField);
        Check("the first field did not grow",
            _markedField?.CellCount == WorldGrid.RectCells(_rectFrom, _rectTo).Count);
        Check("the neighbour owns exactly its own rectangle",
            neighbor != null && neighbor.CellCount == rect.Count
            && AllSameField(rect, neighbor));
    }

    /// <summary>
    /// <see cref="PlacementRule.VacantCell"/>, the rule fields exist for: a
    /// rectangle may not be drawn over cells a field already owns, so a cell
    /// always has exactly one owner. It is stricter than
    /// <see cref="PlacementRule.NoOverlap"/>, which — the tool placing the same
    /// tile that is already there — would have allowed it.
    /// </summary>
    private void CheckFieldOverlapIsRefused()
    {
        int fieldsBefore = _world.Fields.Count;
        int cellsBefore = _world.FieldCellCount;

        Check("NoOverlap would have let a field cover a field",
            PlacementRules.Check(_world, [_rectFrom], PlacementRule.NoOverlap, TileType.Field)
                .Legal);
        Check("VacantCell refuses that same cell",
            PlacementRules.Check(_world, [_rectFrom], PlacementRule.VacantCell, TileType.Field)
                .Refusal == PlacementRefusal.Occupied);

        Check("a field cell cannot even be anchored on", !_fieldTool.ClickCell(_rectFrom));
        Check("the refused anchor click set no anchor", _fieldTool.Anchor == null);

        Check("the drag anchors on clear soil below the field",
            _fieldTool.ClickCell(_belowRect));
        _fieldTool.HoverAt(_rectFrom);
        Check("a rectangle over an existing field previews as refused",
            !_fieldTool.PreviewLegal);
        Check("the refusal names the occupied cell",
            _fieldTool.Preview?.Refusal == PlacementRefusal.Occupied);
        Check("the ghost marks the owned cells as the offenders",
            CountGhost(_fieldTool, BuildTool.GhostIllegalCell) == FieldRectHeight);
        Check("no cell of the overlapping rectangle is drawn legal",
            CountGhost(_fieldTool, BuildTool.GhostLegal) == 0);

        Check("the click over an existing field is rejected", !_fieldTool.ClickCell(_rectFrom));
        Check("the overlapping rectangle created no field",
            _world.Fields.Count == fieldsBefore);
        Check("the overlapping rectangle marked no cell",
            _world.FieldCellCount == cellsBefore);
        Check("the existing field still owns its cells",
            _world.GetField(_rectFrom) == _markedField);
        Check("the legal part of the overlapping rectangle was not marked either",
            _world.GetTile(_belowRect) == TileType.Empty);

        _fieldTool.Cancel();
        Check("cancel drops that anchor", _fieldTool.Anchor == null);
    }

    /// <summary>Vacancy is not only about fields: a road refuses a rectangle too.</summary>
    private void CheckFieldOverRoadIsRefused()
    {
        // Searched here rather than up front: the cell beside the road has to
        // still be clear now that this test's roads and fields are on the map.
        Vector2I? found = FindCell(cell => _world.IsRoad(cell) && IsFreeSoil(cell + Vector2I.Up));
        Check("found a road cell with clear soil beside it", found != null);
        if (found is not { } road)
        {
            return;
        }

        Vector2I beside = road + Vector2I.Up;
        int fieldsBefore = _world.Fields.Count;

        Check("the drag anchors beside the road", _fieldTool.ClickCell(beside));
        _fieldTool.HoverAt(road);
        Check("a rectangle covering a road previews as refused", !_fieldTool.PreviewLegal);
        Check("the road refusal names the occupied cell",
            _fieldTool.Preview?.Refusal == PlacementRefusal.Occupied);
        Check("the click over a road is rejected", !_fieldTool.ClickCell(road));
        Check("the road survived the refused rectangle", _world.IsRoad(road));
        Check("the cell beside it was not marked either",
            _world.GetTile(beside) == TileType.Empty);
        Check("the rectangle over a road created no field",
            _world.Fields.Count == fieldsBefore);

        _fieldTool.Cancel();
    }

    /// <summary>
    /// A rectangle straddling rock or water is refused whole, exactly like the
    /// road tool's drag into water: the base tool's partial-legality rule is
    /// all-or-nothing, so none of the rectangle's legal cells are marked.
    /// </summary>
    private void CheckFieldIntoRoughTerrainIsRefused()
    {
        bool found = TryFindRectIntoRoughTerrain(out Vector2I anchor, out Vector2I rough);
        Check("found a clear soil strip running into rock or water", found);
        if (!found)
        {
            return;
        }

        List<Vector2I> rect = WorldGrid.RectCells(anchor, rough);
        int fieldsBefore = _world.Fields.Count;
        int cellsBefore = _world.FieldCellCount;

        Check("the rough-ground drag anchors on soil", _fieldTool.ClickCell(anchor));
        _fieldTool.HoverAt(rough);
        Check("a rectangle straddling rock or water previews as refused",
            !_fieldTool.PreviewLegal);
        Check("the rough-ground refusal names the terrain",
            _fieldTool.Preview?.Refusal == PlacementRefusal.UnbuildableTerrain);
        Check("the ghost still covers the whole rectangle",
            _fieldTool.GhostCellCount == rect.Count);
        Check("the ghost marks the unbuildable cell as the offender",
            CountGhost(_fieldTool, BuildTool.GhostIllegalCell) == 1);
        Check("no cell of the straddling rectangle is drawn legal",
            CountGhost(_fieldTool, BuildTool.GhostLegal) == 0);

        Check("the click on a straddling rectangle is rejected", !_fieldTool.ClickCell(rough));
        Check("the rough cell stayed unmarked", _world.GetTile(rough) == TileType.Empty);
        Check("one unbuildable cell refuses the whole rectangle",
            _world.FieldCellCount == cellsBefore && _world.Fields.Count == fieldsBefore);
        Check("not even the soil cells of that rectangle were marked",
            _world.GetTile(anchor) == TileType.Empty);
        Check("a refused rectangle keeps the anchor so the player can re-aim",
            _fieldTool.Anchor == anchor);

        _fieldTool.Cancel();
    }

    /// <summary>
    /// A field <i>is</i> its cells: clearing one shrinks the field, and
    /// clearing the last one drops the field entirely. This is the path M2's
    /// bulldoze will take, which is why cell-to-field ownership is maintained
    /// inside <see cref="WorldGrid.SetTile"/> rather than beside it.
    /// </summary>
    private void CheckClearingShrinksAndDropsAField()
    {
        Field? doomed = _world.GetField(_nextRectFrom);
        Check("the neighbouring field is still there to be cleared", doomed != null);
        if (doomed is not { } field)
        {
            return;
        }

        int fieldsBefore = _world.Fields.Count;
        int cellsBefore = _world.FieldCellCount;
        int sizeBefore = field.CellCount;

        _world.SetTile(_nextRectFrom, TileType.Empty);
        Check("clearing a field cell shrinks its field", field.CellCount == sizeBefore - 1);
        Check("the cleared cell belongs to no field", _world.GetField(_nextRectFrom) == null);
        Check("the world lost exactly that one field cell",
            _world.FieldCellCount == cellsBefore - 1);
        Check("shrinking does not drop the field",
            WorldHasField(field) && _world.Fields.Count == fieldsBefore);
        Check("the field's other cells still address it",
            _world.GetField(_nextRectTo) == field);

        foreach (Vector2I cell in new List<Vector2I>(field.Cells))
        {
            _world.SetTile(cell, TileType.Empty);
        }
        Check("a field that lost its last cell is gone", !WorldHasField(field));
        Check("dropping it leaves the other field alone",
            _world.Fields.Count == fieldsBefore - 1);
        Check("the surviving field is untouched", _world.GetField(_rectFrom) == _markedField);
        Check("only the surviving field's cells are left",
            _world.FieldCellCount == WorldGrid.RectCells(_rectFrom, _rectTo).Count);
    }

    /// <summary>Cells the tool's ghost currently draws in that colour.</summary>
    private static int CountGhost(BuildTool tool, Color color)
    {
        int count = 0;
        for (int i = 0; i < tool.GhostCellCount; i++)
        {
            if (tool.GhostColor(i).IsEqualApprox(color))
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

    private bool AllTile(IReadOnlyList<Vector2I> cells, TileType type)
    {
        foreach (Vector2I cell in cells)
        {
            if (_world.GetTile(cell) != type)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether every cell is owned by that one field object.</summary>
    private bool AllSameField(IReadOnlyList<Vector2I> cells, Field field)
    {
        foreach (Vector2I cell in cells)
        {
            if (_world.GetField(cell) != field)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether the world still lists that field.</summary>
    private bool WorldHasField(Field field)
    {
        foreach (Field known in _world.Fields)
        {
            if (known == field)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Same cells in the same order — footprints are order-defined.</summary>
    private static bool SameCells(IReadOnlyList<Vector2I> a, IReadOnlyList<Vector2I> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    private static bool InsideRect(IReadOnlyList<Vector2I> cells, Vector2I min, Vector2I max)
    {
        foreach (Vector2I cell in cells)
        {
            if (cell.X < min.X || cell.X > max.X || cell.Y < min.Y || cell.Y > max.Y)
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

    /// <summary>
    /// Picks the field section's cells out of the world *as it is by then* —
    /// searched here rather than in <see cref="PickTestCells"/> because the
    /// road checks have laid road since, and a field rectangle has to land on
    /// ground that is still clear.
    ///
    /// One block of free soil, 2·<see cref="FieldRectWidth"/> wide and
    /// <see cref="FieldRectHeight"/> + 1 tall: its top half is the pair of
    /// touching rectangles the checks mark, and the spare row underneath is
    /// clear ground to anchor a drag that then runs onto them.
    /// </summary>
    private void PickFieldCells()
    {
        var span = new Vector2I(FieldRectWidth * 2 - 1, FieldRectHeight);
        Vector2I? block = FindCell(cell => IsFreeSoilRect(cell, cell + span));
        Check("found a clear soil block for the field rectangles", block != null);

        Vector2I origin = block ?? Vector2I.Zero;
        _rectFrom = origin;
        _rectTo = origin + new Vector2I(FieldRectWidth - 1, FieldRectHeight - 1);
        _nextRectFrom = origin + new Vector2I(FieldRectWidth, 0);
        _nextRectTo = origin + new Vector2I(FieldRectWidth * 2 - 1, FieldRectHeight - 1);
        _belowRect = origin + new Vector2I(0, FieldRectHeight);

        GD.Print($"field test cells: rectangle {_rectFrom} to {_rectTo}, neighbour "
            + $"{_nextRectFrom} to {_nextRectTo}, clear cell below {_belowRect}");
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

    private bool IsFreeSoilRect(Vector2I from, Vector2I to)
    {
        foreach (Vector2I cell in WorldGrid.RectCells(from, to))
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
    /// A short straight strip of clear soil ending on rock or open water, so
    /// the rectangle spanned by its two ends straddles ground no field may
    /// cover — the field-tool counterpart of
    /// <see cref="TryFindLineIntoWater"/>. Searched at the point of use, so
    /// the soil cells are guaranteed still clear of this test's own fields.
    /// </summary>
    private bool TryFindRectIntoRoughTerrain(out Vector2I anchor, out Vector2I rough)
    {
        Vector2I[] directions = [Vector2I.Right, Vector2I.Left, Vector2I.Up, Vector2I.Down];
        for (int radius = 1; radius <= 40; radius++)
        {
            foreach (Vector2I cell in Ring(radius))
            {
                TerrainType terrain = _world.GetTerrain(cell);
                if ((terrain != TerrainType.Rock && terrain != TerrainType.Water)
                    || _world.GetTile(cell) != TileType.Empty)
                {
                    continue;
                }
                foreach (Vector2I direction in directions)
                {
                    if (IsFreeSoil(cell + direction) && IsFreeSoil(cell + direction * 2))
                    {
                        anchor = cell + direction * 2;
                        rough = cell;
                        return true;
                    }
                }
            }
        }
        anchor = rough = Vector2I.Zero;
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
