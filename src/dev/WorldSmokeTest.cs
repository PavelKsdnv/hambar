using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Headless smoke test for the world grid and machines: instances Main.tscn
/// and asserts tiles were generated, machines spawned, and that after a few
/// seconds every machine has moved and is still on a road. Run with:
/// godot --headless res://scenes/dev/WorldSmokeTest.tscn
/// Exits 0 on pass, 1 on failure.
/// </summary>
public partial class WorldSmokeTest : Node
{
    private WorldGrid _world = null!;
    private readonly Dictionary<Machine, Vector3> _startPositions = new();
    private int _frame;
    private bool _failed;

    public override void _Ready()
    {
        Node main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        AddChild(main);
        _world = main.GetNode<WorldGrid>("World");
    }

    public override void _Process(double delta)
    {
        _frame++;
        if (_frame == 5)
        {
            var gridMap = _world.GetNode<GridMap>("GridMap");
            Check("tiles were generated", gridMap.GetUsedCells().Count > 100);
            Check("origin cell is road", _world.IsRoad(Vector2I.Zero));
            // Block (0,0) is a grass gap ((bx+bz) % 3 == 0); block (1,0) is field.
            Check("grass-gap block is empty", _world.GetTile(new Vector2I(2, 2)) == TileType.Empty);
            Check("block interior is field", _world.GetTile(new Vector2I(10, 2)) == TileType.Field);

            foreach (Node node in GetTree().GetNodesInGroup("machines"))
            {
                _startPositions[(Machine)node] = ((Machine)node).Position;
            }
            Check("machines spawned", _startPositions.Count > 0);
        }
        else if (_frame == 185)
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
