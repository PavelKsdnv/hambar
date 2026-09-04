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
/// Four tools are covered, in that order: the road tool first, then the field
/// tool — which therefore validates against a world that already holds this
/// test's roads, and is also where the "only one tool armed at a time" rule is
/// proven — then the structure tool, whose must-touch-a-road rule only means
/// anything once there is a road network to touch, and last the bulldozer,
/// which needs one of each of those on the map before it can take them off
/// again. The rest of M2 (costs, the palette) adds its assertions the same way:
/// give each new tool a section like <see cref="CheckLegalPlacement"/> and
/// reuse the cell-finding helpers at the bottom, which look terrain up at
/// runtime instead of hard-coding coordinates that a seed change would
/// invalidate.
/// </summary>
public partial class BuildSmokeTest : Node
{
    /// <summary>Width and height of the rectangle the field checks mark.</summary>
    private const int FieldRectWidth = 3;
    private const int FieldRectHeight = 2;

    private WorldGrid _world = null!;
    private RoadBuildTool _tool = null!;
    private FieldBuildTool _fieldTool = null!;
    private StructureBuildTool _structureTool = null!;
    private BulldozeTool _bulldozeTool = null!;
    private CellInspector _inspector = null!;
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

    // The structure section's cell and the buildings it places. Like the field
    // section's cells these are found (or, for the road-access checks, built)
    // at the point of use, once this test's roads and fields are on the map.
    private Vector2I _structureCell;
    private Structure? _structure;
    private Structure? _secondStructure;

    // The terrain the bulldoze section builds on, remembered cell by cell
    // *before* anything is placed on it — the headline the whole tool has to
    // survive: clearing a placement must leave the ground it stood on exactly
    // as it was found (see RememberTerrain / TerrainUnchanged).
    private readonly List<Vector2I> _rememberedCells = new();
    private readonly List<TerrainType> _rememberedTerrain = new();
    private readonly List<float> _rememberedFertility = new();

    public override void _Ready()
    {
        Node main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        AddChild(main);
        _world = main.GetNode<WorldGrid>("World");
        _tool = main.GetNode<RoadBuildTool>("RoadTool");
        _fieldTool = main.GetNode<FieldBuildTool>("FieldTool");
        _structureTool = main.GetNode<StructureBuildTool>("StructureTool");
        _bulldozeTool = main.GetNode<BulldozeTool>("BulldozeTool");
        _inspector = main.GetNode<CellInspector>("CellInspector");
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

            // Menu slot 4 arms the structure tool — the player's path again.
            Input.ParseInputEvent(new InputEventAction { Action = "menu_4", Pressed = true });
        }
        else if (_frame == 40)
        {
            CheckStructureToolArmed();
            CheckStructurePlacement();
            CheckStructureNeedsRoadAccess();
            CheckStructureOnRoughGroundIsRefused();
            CheckStructureOnOccupiedGroundIsRefused();
            CheckClearingDemolishesAStructure();

            // Menu slot 5 arms the bulldozer — the player's path, once more.
            Input.ParseInputEvent(new InputEventAction { Action = "menu_5", Pressed = true });
        }
        else if (_frame == 45)
        {
            CheckBulldozeToolArmed();
            CheckBulldozeClearsARoad();
            CheckBulldozeClearsAField();
            CheckBulldozeClearsAStructure();
            CheckTerrainSurvivedTheBulldozer();
            CheckBulldozeShrinksAndDropsAField();
            CheckBulldozeTakesAWholeStructure();
            CheckBulldozeSkipsEmptyGround();
            CheckNothingToClearIsRefused();
            CheckRefundSeam();

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
        Check("the structure tool starts inactive too", !_structureTool.Active);
        Check("the structure tool is wired to the world", _structureTool.World == _world);
        Check("the bulldozer starts inactive too", !_bulldozeTool.Active);
        Check("the bulldozer is wired to the world", _bulldozeTool.World == _world);
        Check("the bulldozer has removed nothing before it is used",
            _bulldozeTool.Removals.Count == 0 && _bulldozeTool.RefundTotal == 0);
        Check("no field is registered before one is marked",
            _world.Fields.Count == 0 && _world.FieldCellCount == 0);
        Check("no structure is registered before one is placed",
            _world.Structures.Count == 0 && _world.StructureCellCount == 0);
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
        Check("the structure tool is not armed either", !_structureTool.Active);
        Check("the bulldozer is not armed either", !_bulldozeTool.Active);
        Check("every tool joined the build-tool group",
            GetTree().GetNodesInGroup(BuildTool.ToolGroup).Count == 4);
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

    // --- structures --------------------------------------------------------

    /// <summary>
    /// The structure tool comes last, so the road network it has to touch is
    /// already on the map. Arming it also re-proves the tool-group rule with a
    /// third member.
    /// </summary>
    private void CheckStructureToolArmed()
    {
        Check("menu key 4 activates the structure tool", _structureTool.Active);
        Check("arming the structure tool disarms the field tool", !_fieldTool.Active);
        Check("arming the structure tool leaves the road tool disarmed", !_tool.Active);
        // No anchor to check for — a single-click tool never takes one, and it
        // ghosts the cell under the cursor from the moment it is armed.
        Check("arming the structure tool leaves no anchor pending",
            _structureTool.Anchor == null);
        Check("no structure was registered by the road or field sections",
            _world.Structures.Count == 0 && _world.StructureCellCount == 0);
    }

    /// <summary>
    /// The accepted case, and what the issue is really about: one click on free
    /// soil that shares an edge with the road network puts a building down, and
    /// what lands is a <see cref="Structure"/> with an id — not a tile the game
    /// could only ever read back as an enum value.
    /// </summary>
    private void CheckStructurePlacement()
    {
        // Searched here rather than up front: the cell beside the road has to
        // still be clear now that this test's roads and fields are on the map.
        Vector2I? found = FindCell(cell => IsFreeSoil(cell) && TouchesRoad(cell));
        Check("found clear soil beside the road network", found != null);
        if (found is not { } beside)
        {
            return;
        }

        _structureCell = beside;
        int structuresBefore = _world.Structures.Count;

        _structureTool.HoverAt(beside);
        Check("a cell beside a road previews as legal", _structureTool.PreviewLegal);
        Check("a single-click tool ghosts before any anchor",
            _structureTool.GhostCellCount == 1 && _structureTool.Anchor == null);
        Check("a structure footprint is one cell", _structureTool.Preview?.Count == 1);
        Check("the ghost cell of a legal placement is drawn legal",
            CountGhost(_structureTool, BuildTool.GhostLegal) == 1);
        Check("the hover square reads legal beside a road",
            _structureTool.CursorColor.IsEqualApprox(BuildTool.CursorLegal));
        Check("the preview itself builds nothing",
            _world.Structures.Count == structuresBefore);

        Check("one click places the structure", _structureTool.ClickCell(beside));
        Check("placing needs no second click", _structureTool.Anchor == null);
        Check("placing keeps the tool armed for the next building", _structureTool.Active);
        Check("the cell became a structure tile",
            _world.GetTile(beside) == TileType.Structure);
        Check("exactly one structure was registered",
            _world.Structures.Count == structuresBefore + 1);
        Check("the building covers exactly one cell", _world.StructureCellCount == 1);
        Check("the cell it stands on now previews as occupied",
            _structureTool.Preview?.Refusal == PlacementRefusal.Occupied);

        // The point of the whole feature: a building is an entity with an
        // identity, reachable by cell *and* by id, not a bare tile value.
        _structure = _world.GetStructure(beside);
        Check("the placed cell addresses a structure", _structure != null);
        if (_structure is not { } structure)
        {
            return;
        }
        Check("the structure has an id", structure.Id > 0);
        Check("the structure is addressable by id, not just by tile",
            _world.GetStructure(structure.Id) == structure);
        Check("an id nothing was ever handed resolves to nothing",
            _world.GetStructure(structure.Id + 1) == null);
        Check("the structure knows the cell it stands on",
            structure.CellCount == 1 && structure.Cells[0] == beside
            && structure.Contains(beside) && structure.Origin == beside);
        Check("the structure footprint is a 1x1 rectangle",
            structure.Bounds == new Rect2I(beside, Vector2I.One));
        Check("the structure is the one the world just registered",
            _world.Structures[^1] == structure);
        Check("the structure is named", !string.IsNullOrEmpty(structure.Name));
        Check("the hover readout names the building on the cell",
            _inspector.Describe(beside).Contains($"tile: structure ({structure.Name})"));
    }

    /// <summary>
    /// The rule that makes the road network load-bearing, taken through all
    /// three of its verdicts on <b>one</b> cell: refused with no road near it,
    /// still refused when the nearest road only touches its corner (access is
    /// 4-neighbour, not 8), and accepted the moment a road shares an edge with
    /// it. The roads are laid rather than searched for, so nothing but the road
    /// changes between the three answers.
    /// </summary>
    private void CheckStructureNeedsRoadAccess()
    {
        Vector2I? found = FindCell(cell =>
            IsFreeSoil(cell) && !TouchesRoad(cell)
            && IsFreeSoil(cell + Vector2I.Right) && IsFreeSoil(cell + Vector2I.One));
        Check("found clear soil away from every road", found != null);
        if (found is not { } isolated)
        {
            return;
        }

        int structuresBefore = _world.Structures.Count;
        int cellsBefore = _world.StructureCellCount;

        _structureTool.HoverAt(isolated);
        Check("a cell with no road beside it previews as refused",
            !_structureTool.PreviewLegal);
        Check("the refusal names the missing road",
            _structureTool.Preview?.Refusal == PlacementRefusal.NoRoadAccess);
        Check("the ghost still covers the cell", _structureTool.GhostCellCount == 1);
        Check("no cell of a refused placement is drawn legal",
            CountGhost(_structureTool, BuildTool.GhostLegal) == 0);
        // Road access is a footprint rule, so no single cell is the offender:
        // the ghost dims the whole placement instead of pointing at a cell.
        Check("the whole placement reads refused, with no cell blamed",
            CountGhost(_structureTool, BuildTool.GhostRefused) == 1
            && CountGhost(_structureTool, BuildTool.GhostIllegalCell) == 0);
        Check("the hover square reads refused away from the road",
            _structureTool.CursorColor.IsEqualApprox(BuildTool.CursorRefused));

        Check("the click away from the road is rejected",
            !_structureTool.ClickCell(isolated));
        Check("nothing was built away from the road",
            _world.GetTile(isolated) == TileType.Empty);
        Check("the refused click registered no structure",
            _world.Structures.Count == structuresBefore
            && _world.StructureCellCount == cellsBefore);
        Check("the refused cell addresses no structure",
            _world.GetStructure(isolated) == null);

        // A road on the diagonal touches the cell's corner, not its edge.
        _world.SetTile(isolated + Vector2I.One, TileType.Road);
        _structureTool.HoverAt(isolated);
        Check("a road touching only the corner is not road access",
            _structureTool.Preview?.Refusal == PlacementRefusal.NoRoadAccess);
        Check("the click beside a diagonal-only road is rejected",
            !_structureTool.ClickCell(isolated));
        Check("nothing was built beside the diagonal road",
            _world.GetTile(isolated) == TileType.Empty);

        // One road sharing an edge, and the very same cell becomes legal.
        _world.SetTile(isolated + Vector2I.Right, TileType.Road);
        _structureTool.HoverAt(isolated);
        Check("a road sharing an edge grants access", _structureTool.PreviewLegal);
        Check("the ghost turns legal with the road",
            CountGhost(_structureTool, BuildTool.GhostLegal) == 1);
        Check("the click is accepted once a road touches the cell",
            _structureTool.ClickCell(isolated));

        _secondStructure = _world.GetStructure(isolated);
        Check("the second building is registered",
            _secondStructure != null && _world.Structures.Count == structuresBefore + 1);
        if (_secondStructure is not { } second || _structure is not { } first)
        {
            return;
        }
        Check("ids are handed out in creation order", second.Id > first.Id);
        Check("each cell addresses its own building",
            second != first && _world.GetStructure(_structureCell) == first);
        Check("each id resolves to its own building",
            _world.GetStructure(second.Id) == second
            && _world.GetStructure(first.Id) == first);
    }

    /// <summary>
    /// Rock and water refuse a building the way they refuse a road. To prove it
    /// is the <i>terrain</i> talking and not the road rule, the check lays a
    /// road beside the rough cell first — so road access is satisfied and
    /// cannot be the reason — and takes it up again afterwards.
    /// </summary>
    private void CheckStructureOnRoughGroundIsRefused()
    {
        bool found = TryFindRoughCellWithSoilBeside(out Vector2I rough, out Vector2I beside);
        Check("found rock or water with clear soil beside it", found);
        if (!found)
        {
            return;
        }

        int structuresBefore = _world.Structures.Count;
        _world.SetTile(beside, TileType.Road);
        Check("the rough cell now has road access",
            PlacementRules.HasRoadAccess(_world, [rough]));

        _structureTool.HoverAt(rough);
        Check("rock or water is refused even with a road beside it",
            _structureTool.Preview?.Refusal == PlacementRefusal.UnbuildableTerrain);
        Check("the ghost marks the rough cell as the offender",
            CountGhost(_structureTool, BuildTool.GhostIllegalCell) == 1);
        Check("the click on rough ground is rejected", !_structureTool.ClickCell(rough));
        Check("the rough cell stayed unbuilt", _world.GetTile(rough) == TileType.Empty);
        Check("no structure was registered on rough ground",
            _world.Structures.Count == structuresBefore);

        _world.SetTile(beside, TileType.Empty);
    }

    /// <summary>
    /// <see cref="PlacementRule.VacantCell"/> again, from the building side: a
    /// structure may not be stacked on a road, a field or another building —
    /// and all three of those cells have road access, so nothing but occupancy
    /// is refusing them.
    /// </summary>
    private void CheckStructureOnOccupiedGroundIsRefused()
    {
        int structuresBefore = _world.Structures.Count;

        Check("a cell that already holds a building is refused",
            _structureTool.PlanFor(_structureCell).Refusal == PlacementRefusal.Occupied);
        Check("the click on a building is rejected",
            !_structureTool.ClickCell(_structureCell));
        Check("the building that was there survived",
            _world.GetStructure(_structureCell) == _structure);
        Check("the refused click registered no second building",
            _world.Structures.Count == structuresBefore);

        Vector2I? road = FindCell(cell =>
            _world.IsRoad(cell) && _world.IsRoad(cell + Vector2I.Right));
        Check("found a road cell with another road beside it", road != null);
        if (road is { } onRoad)
        {
            Check("a road cell is refused as occupied, not accepted for touching one",
                _structureTool.PlanFor(onRoad).Refusal == PlacementRefusal.Occupied);
            Check("the click on a road is rejected", !_structureTool.ClickCell(onRoad));
            Check("the road survived the refused building", _world.IsRoad(onRoad));
        }

        Check("a field cell is refused as occupied",
            _structureTool.PlanFor(_rectFrom).Refusal == PlacementRefusal.Occupied);
        Check("the field survived the refused building",
            _world.GetField(_rectFrom) == _markedField);
    }

    /// <summary>
    /// Clearing a structure cell takes the whole building with it — tile, cell
    /// lookup, registry entry and id. This is the path M2's bulldoze will take,
    /// and it is where a building parts company with a <see cref="Field"/>: a
    /// field shrinks cell by cell, a building is demolished whole, because half
    /// a mill is not a mill.
    /// </summary>
    private void CheckClearingDemolishesAStructure()
    {
        if (_structure is not { } structure)
        {
            return;
        }

        int structuresBefore = _world.Structures.Count;
        int cellsBefore = _world.StructureCellCount;
        int id = structure.Id;

        _world.SetTile(_structureCell, TileType.Empty);
        Check("clearing a structure cell empties the tile",
            _world.GetTile(_structureCell) == TileType.Empty);
        Check("the cleared cell addresses no building",
            _world.GetStructure(_structureCell) == null);
        Check("the building is gone from the registry",
            _world.Structures.Count == structuresBefore - 1);
        Check("its id resolves to nothing once it is demolished",
            _world.GetStructure(id) == null);
        Check("the world lost exactly that one structure cell",
            _world.StructureCellCount == cellsBefore - 1);
        Check("the terrain under a demolished building is untouched",
            _world.IsSoil(_structureCell));
        Check("the other building is untouched",
            _secondStructure != null
            && _world.GetStructure(_secondStructure.Origin) == _secondStructure);
        Check("the other building's id still resolves",
            _secondStructure != null
            && _world.GetStructure(_secondStructure.Id) == _secondStructure);
    }

    // --- bulldoze ----------------------------------------------------------

    /// <summary>
    /// The bulldozer comes last, because it needs one of everything on the map
    /// before it can take anything off again. Arming it re-proves the
    /// tool-group rule with a fourth member.
    /// </summary>
    private void CheckBulldozeToolArmed()
    {
        Check("menu key 5 activates the bulldozer", _bulldozeTool.Active);
        Check("arming the bulldozer disarms the structure tool", !_structureTool.Active);
        Check("arming the bulldozer leaves the road and field tools disarmed",
            !_tool.Active && !_fieldTool.Active);
        Check("the bulldozer takes no anchor from being armed",
            _bulldozeTool.Anchor == null);
        Check("nothing has gone through the refund seam yet",
            _bulldozeTool.Removals.Count == 0 && _bulldozeTool.RefundTotal == 0);
    }

    /// <summary>
    /// A road, laid with the road tool and taken away with the bulldozer. The
    /// whole drag previews legal, every cell of it is drawn legal, and the
    /// second click leaves empty cells behind — one refund-seam entry per cell,
    /// because a road is nothing but its cells.
    /// </summary>
    private void CheckBulldozeClearsARoad()
    {
        Vector2I? run = FindCell(cell => IsFreeSoilLine(cell, cell + new Vector2I(2, 0)));
        Check("found clear soil to lay a road for the bulldozer", run != null);
        if (run is not { } from)
        {
            return;
        }

        Vector2I to = from + new Vector2I(2, 0);
        List<Vector2I> cells = WorldGrid.RectCells(from, to);
        RememberTerrain(cells);

        Check("the road for the bulldozer goes down", Drag(_tool, from, to));
        Check("it is road before the bulldozer touches it", AllRoad(from, to));

        _bulldozeTool.SetActive(true);
        int roadsBefore = _world.RoadCellCount;
        int removalsBefore = _bulldozeTool.Removals.Count;

        _bulldozeTool.HoverAt(from);
        Check("a cell holding a road previews as clearable", _bulldozeTool.PreviewLegal);
        Check("the hover square reads legal over something removable",
            _bulldozeTool.CursorColor.IsEqualApprox(BuildTool.CursorLegal));

        Check("clicking a built cell anchors the bulldoze drag",
            _bulldozeTool.ClickCell(from));
        _bulldozeTool.HoverAt(to);
        Check("the ghost covers the whole pending rectangle",
            _bulldozeTool.GhostCellCount == cells.Count);
        Check("every cell of an all-built drag is drawn legal",
            CountGhost(_bulldozeTool, BuildTool.GhostLegal) == cells.Count);
        Check("the preview itself removes nothing", _world.RoadCellCount == roadsBefore);

        Check("the second click clears the road", _bulldozeTool.ClickCell(to));
        Check("every cell of the road is empty again", AllTile(cells, TileType.Empty));
        Check("exactly those road cells were taken",
            _world.RoadCellCount == roadsBefore - cells.Count);
        Check("clearing keeps the bulldozer armed for the next drag", _bulldozeTool.Active);
        Check("clearing clears the ghost", _bulldozeTool.GhostCellCount == 0);
        Check("the cleared cell now previews as nothing to clear",
            _bulldozeTool.PlanFor(from).Refusal == PlacementRefusal.NothingToClear);
        Check("terrain and fertility under the bulldozed road are unchanged",
            TerrainUnchanged(cells) && FertilityUnchanged(cells));
        Check("each road cell is its own removal",
            _bulldozeTool.Removals.Count == removalsBefore + cells.Count);
        Check("the refund seam was told it was road, one cell of it",
            LastRemoval()?.Tile == TileType.Road && LastRemoval()?.CellCount == 1);
    }

    /// <summary>
    /// A field, marked with the field tool and bulldozed whole: the tiles go,
    /// and the entity is dropped with its last cell. Each cell is its own
    /// removal and names the field it was taken out of — what a per-cell refund
    /// would need to price.
    /// </summary>
    private void CheckBulldozeClearsAField()
    {
        Vector2I? block = FindCell(cell => IsFreeSoilRect(cell, cell + Vector2I.One));
        Check("found a clear soil block to mark a field for the bulldozer",
            block != null);
        if (block is not { } from)
        {
            return;
        }

        Vector2I to = from + Vector2I.One;
        List<Vector2I> cells = WorldGrid.RectCells(from, to);
        RememberTerrain(cells);

        Check("the field for the bulldozer is marked", Drag(_fieldTool, from, to));
        Field? marked = _world.GetField(from);
        Check("it is a field before the bulldozer touches it",
            marked != null && AllTile(cells, TileType.Field));
        if (marked is not { } field)
        {
            return;
        }

        _bulldozeTool.SetActive(true);
        int fieldsBefore = _world.Fields.Count;
        int fieldCellsBefore = _world.FieldCellCount;
        int removalsBefore = _bulldozeTool.Removals.Count;

        Check("the bulldoze drag anchors on a field cell", _bulldozeTool.ClickCell(from));
        _bulldozeTool.HoverAt(to);
        Check("the ghost covers the whole field rectangle",
            _bulldozeTool.GhostCellCount == cells.Count);
        Check("the second click clears the field", _bulldozeTool.ClickCell(to));

        Check("every cell of the field is empty again", AllTile(cells, TileType.Empty));
        Check("the field is gone from the registry",
            !WorldHasField(field) && _world.Fields.Count == fieldsBefore - 1);
        Check("the world lost exactly that field's cells",
            _world.FieldCellCount == fieldCellsBefore - cells.Count);
        Check("no cell of it addresses a field any more", _world.GetField(from) == null);
        Check("terrain and fertility under the bulldozed field are unchanged",
            TerrainUnchanged(cells) && FertilityUnchanged(cells));
        Check("each field cell is its own removal",
            _bulldozeTool.Removals.Count == removalsBefore + cells.Count);
        Check("the refund seam was told which field it took from",
            LastRemoval()?.Tile == TileType.Field && LastRemoval()?.Field == field);
    }

    /// <summary>
    /// A building, placed with the structure tool and bulldozed away: tile,
    /// cell lookup, registry entry and id all gone, and one removal — a
    /// building is priced as a building, not per cell.
    /// </summary>
    private void CheckBulldozeClearsAStructure()
    {
        Vector2I? found = FindCell(cell => IsFreeSoil(cell) && TouchesRoad(cell));
        Check("found clear soil beside a road for the bulldozer's building",
            found != null);
        if (found is not { } cell)
        {
            return;
        }

        RememberTerrain([cell]);
        _structureTool.SetActive(true);
        Check("the building for the bulldozer goes down", _structureTool.ClickCell(cell));
        Structure? placed = _world.GetStructure(cell);
        Check("it is a building before the bulldozer touches it", placed != null);
        if (placed is not { } structure)
        {
            return;
        }

        int id = structure.Id;
        _bulldozeTool.SetActive(true);
        int structuresBefore = _world.Structures.Count;
        int removalsBefore = _bulldozeTool.Removals.Count;

        Check("the bulldoze drag anchors on the building", _bulldozeTool.ClickCell(cell));
        Check("a drag that never left its cell clears just that cell",
            _bulldozeTool.ClickCell(cell));
        Check("the building's cell is empty again", _world.GetTile(cell) == TileType.Empty);
        Check("the cell addresses no building any more", _world.GetStructure(cell) == null);
        Check("the building is gone from the registry",
            _world.Structures.Count == structuresBefore - 1);
        Check("its id resolves to nothing once the bulldozer has been",
            _world.GetStructure(id) == null);
        Check("terrain and fertility under the bulldozed building are unchanged",
            TerrainUnchanged([cell]) && FertilityUnchanged([cell]));
        Check("a building is one removal", _bulldozeTool.Removals.Count == removalsBefore + 1);
        Check("the refund seam was told which building it took",
            LastRemoval()?.Tile == TileType.Structure && LastRemoval()?.Structure == structure);
    }

    /// <summary>
    /// The headline, over every cell the bulldozer has worked on so far: the
    /// terrain layer is read *before* anything is placed on it and compared
    /// after everything has been taken off again — type and fertility both.
    /// Clearing a placement uncovers the ground it was hiding; it never edits
    /// it.
    /// </summary>
    private void CheckTerrainSurvivedTheBulldozer()
    {
        Check("the bulldoze checks remembered ground to compare against",
            _rememberedCells.Count >= 8);
        Check("all of it was remembered as soil, so the comparison is a real one",
            RememberedCellsWithTerrain(TerrainType.Soil) == _rememberedCells.Count);
        Check("some of it carries fertility, so that comparison means something too",
            RememberedFertileCells() > 0);
        Check("terrain type under everything bulldozed so far is unchanged",
            TerrainUnchanged(_rememberedCells));
        Check("fertility under everything bulldozed so far is unchanged",
            FertilityUnchanged(_rememberedCells));
        Check("and none of what was built on it is left",
            AllTile(_rememberedCells, TileType.Empty));
    }

    /// <summary>
    /// A field shrinks under the bulldozer and only disappears with its last
    /// cell — the asymmetry with a building, from the player's side: take a
    /// bite out of a field and what is left is still that field, under the same
    /// name. The drag that finishes it off runs back across the bitten cell, so
    /// it is also a mixed drag: already-empty ground in the middle of a
    /// removal changes nothing.
    /// </summary>
    private void CheckBulldozeShrinksAndDropsAField()
    {
        Vector2I? strip = FindCell(cell => IsFreeSoilRect(cell, cell + new Vector2I(2, 0)));
        Check("found a clear soil strip to mark a field to bite into", strip != null);
        if (strip is not { } from)
        {
            return;
        }

        Vector2I to = from + new Vector2I(2, 0);
        Vector2I middle = from + Vector2I.Right;
        List<Vector2I> cells = WorldGrid.RectCells(from, to);
        RememberTerrain(cells);

        Check("the field to bite into is marked", Drag(_fieldTool, from, to));
        Field? marked = _world.GetField(from);
        Check("the strip is one field", marked != null && marked.CellCount == cells.Count);
        if (marked is not { } field)
        {
            return;
        }

        _bulldozeTool.SetActive(true);
        int fieldsBefore = _world.Fields.Count;
        int removalsBefore = _bulldozeTool.Removals.Count;

        Check("a one-cell bulldoze anchors in the middle of the field",
            _bulldozeTool.ClickCell(middle));
        Check("the second click takes just that cell", _bulldozeTool.ClickCell(middle));
        Check("the bitten cell is empty", _world.GetTile(middle) == TileType.Empty);
        Check("the field shrank instead of disappearing",
            field.CellCount == cells.Count - 1 && WorldHasField(field));
        Check("the rest of the field still addresses the same entity",
            _world.GetField(from) == field && _world.GetField(to) == field);
        Check("the bitten cell belongs to no field", _world.GetField(middle) == null);
        Check("the world still lists it", _world.Fields.Count == fieldsBefore);
        Check("one cell taken is one removal, and it names its field",
            _bulldozeTool.Removals.Count == removalsBefore + 1
            && LastRemoval()?.Field == field && LastRemoval()?.CellCount == 1);

        // The finishing drag runs the length of the strip, straight across the
        // cell that is already gone.
        Check("the finishing drag anchors on what is left", _bulldozeTool.ClickCell(from));
        _bulldozeTool.HoverAt(to);
        Check("the drag across the bitten cell is still legal", _bulldozeTool.PreviewLegal);
        Check("the ghost skips the cell that is already empty",
            CountGhost(_bulldozeTool, BuildTool.GhostLegal) == cells.Count - 1
            && CountGhost(_bulldozeTool, BuildTool.GhostRefused) == 1);
        Check("the second click clears the rest", _bulldozeTool.ClickCell(to));
        Check("a field that lost its last cell is gone",
            !WorldHasField(field) && _world.Fields.Count == fieldsBefore - 1);
        Check("the whole strip is empty", AllTile(cells, TileType.Empty));
        Check("the already-empty cell was not removed twice",
            _bulldozeTool.Removals.Count == removalsBefore + cells.Count);
        Check("terrain and fertility under the whole strip are unchanged",
            TerrainUnchanged(cells) && FertilityUnchanged(cells));
    }

    /// <summary>
    /// Where a building parts company with a field: clipping <b>one edge</b> of
    /// a 2x2 takes the whole building — tile, footprint, registry entry and id
    /// — because half a mill is not a mill. The building is placed through
    /// <see cref="WorldGrid.PlaceStructure"/>, the dev entry point (the way
    /// <see cref="WorldGrid.MarkField"/> and <see cref="WorldGrid.BuildRoadLine"/>
    /// are), because the tool still only offers 1x1 footprints and a 1x1 cannot
    /// be clipped.
    ///
    /// The drag deliberately covers two of the four cells: the second one is
    /// already gone by the time the removal reaches it, so the building goes
    /// through the refund seam exactly once.
    /// </summary>
    private void CheckBulldozeTakesAWholeStructure()
    {
        Vector2I? block = FindCell(cell => IsFreeSoilRect(cell, cell + Vector2I.One));
        Check("found a clear soil block for a 2x2 building", block != null);
        if (block is not { } origin)
        {
            return;
        }

        List<Vector2I> footprint = WorldGrid.RectCells(origin, origin + Vector2I.One);
        RememberTerrain(footprint);
        Structure? placed = _world.PlaceStructure(footprint);
        Check("the 2x2 building is registered",
            placed != null && _world.GetStructure(origin) == placed);
        if (placed is not { } building)
        {
            return;
        }

        int id = building.Id;
        Check("it covers four cells",
            building.CellCount == 4 && AllTile(footprint, TileType.Structure));

        _bulldozeTool.SetActive(true);
        int structuresBefore = _world.Structures.Count;
        int structureCellsBefore = _world.StructureCellCount;
        int removalsBefore = _bulldozeTool.Removals.Count;

        Vector2I clipTo = origin + Vector2I.Down;
        Check("the clipping drag anchors on a corner of the building",
            _bulldozeTool.ClickCell(origin));
        _bulldozeTool.HoverAt(clipTo);
        Check("the clipping drag covers two of its four cells",
            _bulldozeTool.GhostCellCount == 2);
        Check("both clipped cells are drawn legal",
            CountGhost(_bulldozeTool, BuildTool.GhostLegal) == 2);
        Check("the second click clips the building", _bulldozeTool.ClickCell(clipTo));

        Check("clipping a building takes its whole footprint",
            AllTile(footprint, TileType.Empty));
        Check("no cell of it addresses a building any more",
            _world.GetStructure(origin) == null
            && _world.GetStructure(origin + Vector2I.One) == null);
        Check("the building is gone from the registry",
            _world.Structures.Count == structuresBefore - 1);
        Check("its id resolves to nothing", _world.GetStructure(id) == null);
        Check("the world lost all four of its cells",
            _world.StructureCellCount == structureCellsBefore - footprint.Count);
        Check("a clipped building is one removal, not one per cell clipped",
            _bulldozeTool.Removals.Count == removalsBefore + 1);
        Check("the removal carries the whole footprint that went, not the drag",
            LastRemoval()?.CellCount == footprint.Count
            && LastRemoval()?.Structure == building);
        Check("terrain and fertility under the whole footprint are unchanged",
            TerrainUnchanged(footprint) && FertilityUnchanged(footprint));
    }

    /// <summary>
    /// The deliberate difference from every building tool: a bulldoze drag over
    /// a region that is only <i>partly</i> built is not refused. It clears what
    /// is there and skips what is not — dragging across a farmyard crosses
    /// empty ground as a matter of course — and the ghost says so before the
    /// click, drawing only the cells it will actually take in the legal colour.
    /// </summary>
    private void CheckBulldozeSkipsEmptyGround()
    {
        Vector2I? block = FindCell(cell => IsFreeSoilRect(cell, cell + new Vector2I(2, 1)));
        Check("found a clear soil block for the mixed drag", block != null);
        if (block is not { } origin)
        {
            return;
        }

        Vector2I roadEnd = origin + new Vector2I(2, 0);
        Vector2I far = origin + new Vector2I(2, 1);
        List<Vector2I> road = WorldGrid.RectCells(origin, roadEnd);
        List<Vector2I> bare = WorldGrid.RectCells(origin + Vector2I.Down, far);
        _world.BuildRoadLine(origin, roadEnd);
        Check("the region is half built and half bare",
            AllTile(road, TileType.Road) && AllTile(bare, TileType.Empty));

        _bulldozeTool.SetActive(true);
        int roadsBefore = _world.RoadCellCount;
        int removalsBefore = _bulldozeTool.Removals.Count;

        Check("the mixed drag anchors on the built half", _bulldozeTool.ClickCell(origin));
        _bulldozeTool.HoverAt(far);
        Check("a partly empty drag is not refused", _bulldozeTool.PreviewLegal);
        Check("the ghost covers the whole rectangle",
            _bulldozeTool.GhostCellCount == road.Count + bare.Count);
        Check("the ghost draws only the cells it will take as legal",
            CountGhost(_bulldozeTool, BuildTool.GhostLegal) == road.Count);
        Check("the ghost dims the cells it will skip",
            CountGhost(_bulldozeTool, BuildTool.GhostRefused) == bare.Count);
        Check("no cell of a removal drag is blamed as an offender",
            CountGhost(_bulldozeTool, BuildTool.GhostIllegalCell) == 0);
        Check("the skipped cells say why they are skipped",
            _bulldozeTool.Preview?.CellRefusals[road.Count] == PlacementRefusal.NothingToClear);

        Check("the second click clears the mixed drag", _bulldozeTool.ClickCell(far));
        Check("the built half is gone", AllTile(road, TileType.Empty));
        Check("exactly the built cells were taken",
            _world.RoadCellCount == roadsBefore - road.Count);
        Check("the bare half was skipped, not turned into anything",
            AllTile(bare, TileType.Empty));
        Check("only the cells that held something reached the refund seam",
            _bulldozeTool.Removals.Count == removalsBefore + road.Count);
    }

    /// <summary>
    /// What a bulldozer <i>can</i> be refused for, which is two things: a cell
    /// with nothing on it, and the map edge. Note what is not a reason — rock
    /// refuses a build, but bare rock is refused here for holding nothing, not
    /// for being rock. The building rules describe what may go down; none of
    /// them describes what may come off, which is why this tool has its own
    /// pair (<see cref="PlacementRule.InBounds"/>,
    /// <see cref="PlacementRule.OccupiedCell"/>).
    /// </summary>
    private void CheckNothingToClearIsRefused()
    {
        Vector2I? free = FindCell(IsFreeSoil);
        Check("found empty soil for the bulldozer to refuse", free != null);
        if (free is not { } empty)
        {
            return;
        }

        int removalsBefore = _bulldozeTool.Removals.Count;
        _bulldozeTool.SetActive(true);
        _bulldozeTool.HoverAt(empty);
        Check("empty ground previews as refused", !_bulldozeTool.PreviewLegal);
        Check("the refusal says there is nothing to clear",
            _bulldozeTool.Preview?.Refusal == PlacementRefusal.NothingToClear);
        Check("the wording is the bulldozer's own",
            PlacementRules.Explain(PlacementRefusal.NothingToClear) == "nothing here to clear");
        Check("the hover square reads refused over empty ground",
            _bulldozeTool.CursorColor.IsEqualApprox(BuildTool.CursorRefused));
        Check("a click on empty ground is rejected", !_bulldozeTool.ClickCell(empty));
        Check("a refused bulldoze click does not even anchor",
            _bulldozeTool.Anchor == null);
        Check("empty ground stayed empty", _world.GetTile(empty) == TileType.Empty);

        Check("bare rock is refused for holding nothing, not for being rock",
            _bulldozeTool.PlanFor(_rock).Refusal == PlacementRefusal.NothingToClear);
        Check("the click on bare rock is rejected", !_bulldozeTool.ClickCell(_rock));
        Check("the rock is still rock", _world.GetTerrain(_rock) == TerrainType.Rock);

        Check("a cell past the map edge is refused",
            _bulldozeTool.PlanFor(_offMap).Refusal == PlacementRefusal.OffMap);
        Check("the click past the map edge is rejected", !_bulldozeTool.ClickCell(_offMap));

        Check("no refused click reached the refund seam",
            _bulldozeTool.Removals.Count == removalsBefore);
    }

    /// <summary>
    /// The seam itself: every removal went through it exactly once, each one
    /// describing what came off, how much ground it held and where — the
    /// information a real refund rule needs. What it pays is still zero, and
    /// deliberately so: pricing is M7's, and <c>BulldozeTool.RefundFor</c> is the
    /// one place it changes.
    /// </summary>
    private void CheckRefundSeam()
    {
        IReadOnlyList<Removal> removals = _bulldozeTool.Removals;
        Check("the bulldozer put its removals through the seam", removals.Count > 0);

        bool described = true;
        int roads = 0, fields = 0, structures = 0;
        foreach (Removal removed in removals)
        {
            described &= removed.Tile != TileType.Empty
                && removed.CellCount > 0
                && Covers(removed.Cells, removed.Cell)
                && !string.IsNullOrEmpty(removed.Name);
            switch (removed.Tile)
            {
                case TileType.Road:
                    roads++;
                    described &= removed.Field == null && removed.Structure == null;
                    break;
                case TileType.Field:
                    fields++;
                    described &= removed.Field != null && removed.Structure == null;
                    break;
                case TileType.Structure:
                    structures++;
                    described &= removed.Structure != null && removed.Field == null;
                    break;
            }
        }

        Check("each removal names what came off, where, and how much ground", described);
        Check("the seam saw all three kinds of thing the player can build",
            roads > 0 && fields > 0 && structures > 0);
        Check("nothing is refunded yet - the seam is an M7 stub",
            _bulldozeTool.RefundTotal == 0);
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

    /// <summary>Whether the list holds that cell.</summary>
    private static bool Covers(IReadOnlyList<Vector2I> cells, Vector2I cell)
    {
        foreach (Vector2I candidate in cells)
        {
            if (candidate == cell)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Arms a drag tool and runs one whole placement through it: anchor, hover
    /// the far corner, commit. Arming disarms whatever was armed before, so a
    /// bulldoze check that places something has to re-arm the bulldozer after.
    /// </summary>
    private static bool Drag(BuildTool tool, Vector2I from, Vector2I to)
    {
        tool.SetActive(true);
        if (!tool.ClickCell(from))
        {
            return false;
        }
        tool.HoverAt(to);
        return tool.ClickCell(to);
    }

    /// <summary>
    /// Records the terrain under cells *before* anything is built on them, so
    /// the bulldoze checks can compare against what the ground actually was
    /// rather than against what it reads as afterwards.
    /// </summary>
    private void RememberTerrain(IReadOnlyList<Vector2I> cells)
    {
        foreach (Vector2I cell in cells)
        {
            _rememberedCells.Add(cell);
            _rememberedTerrain.Add(_world.GetTerrain(cell));
            _rememberedFertility.Add(_world.GetFertility(cell));
        }
    }

    /// <summary>
    /// Whether every one of those cells still reads the terrain type it was
    /// remembered with. A cell that was never remembered fails, so a check
    /// cannot pass by comparing nothing.
    /// </summary>
    private bool TerrainUnchanged(IReadOnlyList<Vector2I> cells)
    {
        foreach (Vector2I cell in cells)
        {
            int i = _rememberedCells.IndexOf(cell);
            if (i < 0 || _world.GetTerrain(cell) != _rememberedTerrain[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The same for fertility, compared exactly: nothing in the placement layer
    /// is allowed to nudge it, so "close enough" would be the wrong test.
    /// </summary>
    private bool FertilityUnchanged(IReadOnlyList<Vector2I> cells)
    {
        foreach (Vector2I cell in cells)
        {
            int i = _rememberedCells.IndexOf(cell);
            if (i < 0 || _world.GetFertility(cell) != _rememberedFertility[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>How many remembered cells were that terrain when they were read.</summary>
    private int RememberedCellsWithTerrain(TerrainType terrain)
    {
        int count = 0;
        foreach (TerrainType remembered in _rememberedTerrain)
        {
            if (remembered == terrain)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>How many remembered cells had fertility on them to lose.</summary>
    private int RememberedFertileCells()
    {
        int count = 0;
        foreach (float fertility in _rememberedFertility)
        {
            if (fertility > 0f)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>The last thing the bulldozer put through the refund seam.</summary>
    private Removal? LastRemoval() =>
        _bulldozeTool.Removals.Count > 0
            ? _bulldozeTool.Removals[^1]
            : null;

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
    /// Whether the cell borders the road network the way
    /// <see cref="PlacementRule.TouchesRoad"/> means it — asked through the
    /// rule itself, so the test's idea of adjacency can never drift from the
    /// game's.
    /// </summary>
    private bool TouchesRoad(Vector2I cell) => PlacementRules.HasRoadAccess(_world, [cell]);

    /// <summary>
    /// A rock or water cell with clear soil beside it, so a road can be laid on
    /// that neighbour and the rough cell then judged with its road requirement
    /// already satisfied.
    /// </summary>
    private bool TryFindRoughCellWithSoilBeside(out Vector2I rough, out Vector2I beside)
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
                    if (IsFreeSoil(cell + direction))
                    {
                        rough = cell;
                        beside = cell + direction;
                        return true;
                    }
                }
            }
        }
        rough = beside = Vector2I.Zero;
        return false;
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
