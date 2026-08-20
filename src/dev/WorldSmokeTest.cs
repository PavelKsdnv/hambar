using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Headless smoke test for the world grid and machines: instances Main.tscn
/// and asserts the starting road was generated with nothing else, then spawns
/// a machine via menu key 9 and asserts it drives the road, and exercises the
/// road-build tool (menu key 1: anchor click + place click → straight road
/// with diagonal steps). Run with:
/// godot --headless res://scenes/dev/WorldSmokeTest.tscn
/// Exits 0 on pass, 1 on failure.
/// </summary>
public partial class WorldSmokeTest : Node
{
    private WorldGrid _world = null!;
    private RoadBuildTool _roadTool = null!;
    private readonly Dictionary<Machine, Vector3> _startPositions = new();
    private int _frame;
    private bool _failed;

    public override void _Ready()
    {
        Node main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        AddChild(main);
        _world = main.GetNode<WorldGrid>("World");
        _roadTool = main.GetNode<RoadBuildTool>("RoadTool");
    }

    public override void _Process(double delta)
    {
        _frame++;
        if (_frame == 5)
        {
            var gridMap = _world.GetNode<GridMap>("GridMap");
            // The starting road is the single row z = 0, x = -16..16 (33 cells).
            Check("only the starting road was generated", gridMap.GetUsedCells().Count == 33);
            Check("origin cell is road", _world.IsRoad(Vector2I.Zero));
            Check("road spans to both ends", _world.IsRoad(new Vector2I(-16, 0))
                && _world.IsRoad(new Vector2I(16, 0)));
            Check("cell off the road is empty", _world.GetTile(new Vector2I(0, 1)) == TileType.Empty);
            Check("no machines at start", GetTree().GetNodesInGroup("machines").Count == 0);

            // Menu slot 9 spawns a machine.
            Input.ParseInputEvent(new InputEventAction { Action = "menu_9", Pressed = true });
        }
        else if (_frame == 10)
        {
            foreach (Node node in GetTree().GetNodesInGroup("machines"))
            {
                _startPositions[(Machine)node] = ((Machine)node).Position;
            }
            Check("menu key 9 spawned a machine", _startPositions.Count == 1);

            // Menu slot 1 toggles the road-build tool.
            Check("road tool starts inactive", !_roadTool.Active);
            Input.ParseInputEvent(new InputEventAction { Action = "menu_1", Pressed = true });
        }
        else if (_frame == 15)
        {
            Check("menu key 1 activates the road tool", _roadTool.Active);

            // First click anchors, second click places a diagonal road
            // (headless has no real cursor, so click the cells directly).
            _roadTool.ClickCell(new Vector2I(3, 2));
            Check("first click sets the anchor", _roadTool.Anchor == new Vector2I(3, 2));
            _roadTool.ClickCell(new Vector2I(7, 6));
            Check("second click clears the anchor", _roadTool.Anchor == null);
            Check("second click keeps the tool active", _roadTool.Active);
            Check("road line endpoints were placed",
                _world.IsRoad(new Vector2I(3, 2)) && _world.IsRoad(new Vector2I(7, 6)));
            // A diagonal is stair-stepped into 4-connected cells; the BFS may
            // cut those stair corners, so it walks the road in 4 diagonal
            // steps + the start cell = 5 path cells.
            List<Vector2I>? diagonal = _world.FindRoadPath(new Vector2I(3, 2), new Vector2I(7, 6));
            Check("diagonal road is machine-traversable", diagonal is { Count: 5 });
            // String pulling then collapses it to one straight run.
            Check("diagonal path smooths to a single segment",
                diagonal != null && _world.SmoothRoadPath(diagonal).Count == 2);
            Check("cell beside the new road is empty",
                _world.GetTile(new Vector2I(5, 2)) == TileType.Empty);

            Input.ParseInputEvent(new InputEventAction { Action = "menu_1", Pressed = true });
        }
        else if (_frame == 20)
        {
            Check("menu key 1 deactivates the road tool", !_roadTool.Active);
        }
        else if (_frame == 190)
        {
            foreach ((Machine machine, Vector3 start) in _startPositions)
            {
                Check($"{machine.Name} moved", machine.Position.DistanceTo(start) > 1f);
                Check($"{machine.Name} is on a road",
                    _world.IsRoad(_world.WorldToCell(machine.Position)));
            }
            GD.Print(_failed ? "SMOKE TEST FAILED" : "SMOKE TEST PASSED");
            GetTree().Quit(_failed ? 1 : 0);
        }
    }

    private void Check(string what, bool ok)
    {
        GD.Print($"{(ok ? "PASS" : "FAIL")}: {what}");
        _failed |= !ok;
    }
}
