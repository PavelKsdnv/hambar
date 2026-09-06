using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Headless smoke test for the world grid and machines: instances Main.tscn and
/// asserts the generated terrain (seeded, bounded, varied, deterministic) and
/// the starting road, that terrain and placement are independent layers, then
/// spawns a machine via dev key 9 and asserts it drives the road, exercises
/// the road-build tool (armed from the build palette, then anchor click + place
/// click → straight road with diagonal steps), and drives known screen pixels
/// through the shared cell picker to check the hover readout (dev key 8). Run
/// with:
/// godot --headless res://scenes/dev/WorldSmokeTest.tscn
/// Exits 0 on pass, 1 on failure.
/// </summary>
public partial class WorldSmokeTest : Node
{
    /// <summary>The road tool's place on the build palette — its first entry.</summary>
    private const int RoadEntry = 0;

    private WorldGrid _world = null!;
    private GridMap _gridMap = null!;
    private RoadBuildTool _roadTool = null!;
    private BuildPalette _palette = null!;
    private CellInspector _inspector = null!;
    private Label _readout = null!;
    private readonly Dictionary<Machine, Vector3> _startPositions = new();
    private int _frame;
    private bool _failed;

    public override void _Ready()
    {
        Node main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        AddChild(main);
        _world = main.GetNode<WorldGrid>("World");
        _gridMap = _world.GetNode<GridMap>("GridMap");
        _roadTool = main.GetNode<RoadBuildTool>("RoadTool");
        _palette = main.GetNode<BuildPalette>("Hud/BuildPalette");
        _inspector = main.GetNode<CellInspector>("CellInspector");
        _readout = main.GetNode<Label>("Hud/CellReadout");
    }

    public override void _Process(double delta)
    {
        _frame++;
        if (_frame == 5)
        {
            CheckTerrain();
            CheckLayersAreIndependent();
            CheckStartRoad();

            // Dev key 9 spawns a machine — one of the two shortcuts left over
            // from the number-key menu the build palette replaced.
            Input.ParseInputEvent(new InputEventAction { Action = "menu_9", Pressed = true });
        }
        else if (_frame == 10)
        {
            foreach (Node node in GetTree().GetNodesInGroup("machines"))
            {
                _startPositions[(Machine)node] = ((Machine)node).Position;
            }
            Check("dev key 9 spawned a machine", _startPositions.Count == 1);

            // The build palette is how a tool is armed: the first entry is the
            // road tool, and Select is the same call its button makes.
            Check("road tool starts inactive", !_roadTool.Active);
            _palette.Select(RoadEntry);
        }
        else if (_frame == 15)
        {
            Check("the palette arms the road tool", _roadTool.Active);

            // First click anchors, second click places a diagonal road
            // (headless has no real cursor, so click the cells directly). The
            // tool validates placement now, so this diagonal runs over cells
            // this seed generates as empty soil; refusal itself is
            // BuildSmokeTest's subject.
            _roadTool.ClickCell(new Vector2I(2, 2));
            Check("first click sets the anchor", _roadTool.Anchor == new Vector2I(2, 2));
            _roadTool.ClickCell(new Vector2I(6, 6));
            Check("second click clears the anchor", _roadTool.Anchor == null);
            Check("second click keeps the tool active", _roadTool.Active);
            Check("road line endpoints were placed",
                _world.IsRoad(new Vector2I(2, 2)) && _world.IsRoad(new Vector2I(6, 6)));
            // A diagonal is stair-stepped into 4-connected cells; the BFS may
            // cut those stair corners, so it walks the road in 4 diagonal
            // steps + the start cell = 5 path cells.
            List<Vector2I>? diagonal = _world.FindRoadPath(new Vector2I(2, 2), new Vector2I(6, 6));
            Check("diagonal road is machine-traversable", diagonal is { Count: 5 });
            // String pulling then collapses it to one straight run.
            Check("diagonal path smooths to a single segment",
                diagonal != null && _world.SmoothRoadPath(diagonal).Count == 2);
            Check("cell beside the new road is empty",
                _world.GetTile(new Vector2I(4, 2)) == TileType.Empty);

            _palette.Toggle(RoadEntry);
        }
        else if (_frame == 20)
        {
            Check("the palette puts the road tool down again", !_roadTool.Active);
            Check("inspector is wired to the world and its label",
                _inspector.World == _world && _inspector.Readout == _readout);
            Check("the readout starts switched on", _inspector.Enabled && _readout.Visible);

            CheckHoverReadout();

            // Dev key 8 toggles the readout (so it can be off for screenshots).
            // It moved off key 2 when the palette took the low number keys for
            // its entries; the readout is a dev instrument, not a build tool.
            Input.ParseInputEvent(new InputEventAction { Action = "menu_8", Pressed = true });
        }
        else if (_frame == 25)
        {
            Check("dev key 8 switches the readout off",
                !_inspector.Enabled && !_readout.Visible);
            Input.ParseInputEvent(new InputEventAction { Action = "menu_8", Pressed = true });
        }
        else if (_frame == 30)
        {
            Check("dev key 8 switches the readout back on",
                _inspector.Enabled && _readout.Visible);
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

    /// <summary>
    /// Hover readout and the cell picking behind it. Headless has no cursor, so
    /// instead of moving a mouse the test projects a known cell center to its
    /// pixel and drives that pixel back through the picker — a round trip that
    /// fails if either half of screen ↔ cell drifts.
    /// </summary>
    private void CheckHoverReadout()
    {
        Camera3D? camera = GetViewport().GetCamera3D();
        Check("a camera is available to pick through", camera != null);
        if (camera == null)
        {
            return;
        }

        // Cells on screen at the default camera pose, plus one off the map.
        var probes = new[] { Vector2I.Zero, new Vector2I(4, -3), new Vector2I(-6, 5) };
        foreach (Vector2I cell in probes)
        {
            Vector2 pixel = camera.UnprojectPosition(_world.CellToWorld(cell));
            Check($"picker turns the pixel of cell {cell.X},{cell.Y} back into it",
                CellPicker.CellAt(_world, camera, pixel) == cell);
            Check($"inspector reads cell {cell.X},{cell.Y} at that pixel",
                _inspector.Inspect(pixel) == cell);
            Check($"road tool picks cell {cell.X},{cell.Y} at the same pixel",
                _roadTool.PickCell(pixel) == cell);
            Check($"the label shows what the inspector read for {cell.X},{cell.Y}",
                _readout.Text == _inspector.Text && _readout.Text.Length > 0);
        }

        // The readout names all four facts: coords, terrain, fertility, tile.
        // The origin is on the starting road, and generation carved that strip
        // to soil, so its expected content is known.
        string origin = _inspector.Describe(Vector2I.Zero);
        GD.Print("readout at 0,0: " + origin.Replace('\n', ' '));
        Check("readout names the cell coordinates", origin.Contains("cell: 0, 0"));
        Check("readout names the terrain", origin.Contains("terrain: soil"));
        Check("readout names the fertility", origin.Contains("fertility: "
            + _world.GetFertility(Vector2I.Zero)
                .ToString("F2", System.Globalization.CultureInfo.InvariantCulture)));
        Check("readout names the placed tile", origin.Contains("tile: road"));

        // Moving the cursor changes the readout: a neighbouring cell off the
        // road reads as empty rather than repeating the road cell.
        Check("readout follows the cursor to another cell",
            _inspector.Describe(new Vector2I(0, 3)).Contains("cell: 0, 3"));
        Check("an unbuilt cell reads as empty",
            _inspector.Describe(new Vector2I(0, 3)).Contains("tile: empty"));

        // Off the map edge: still a cell, but the terrain layer says so.
        var beyond = new Vector2I(_world.MapSize / 2 + 5, 0);
        Vector2 beyondPixel = camera.UnprojectPosition(_world.CellToWorld(beyond));
        Check("picking past the map edge still resolves the cell",
            _inspector.Inspect(beyondPixel) == beyond);
        Check("readout reports off-map cells sensibly",
            _inspector.Text.Contains($"cell: {beyond.X}, 0")
            && _inspector.Text.Contains("off the map")
            && _inspector.Text.Contains("fertility: -"));
    }

    /// <summary>
    /// The generated terrain layer: bounded, varied (soil + rock + water),
    /// fertility only on soil, and reproducible from the world seed.
    /// </summary>
    private void CheckTerrain()
    {
        int half = _world.MapSize / 2;
        Check("map generated with an odd, non-empty extent",
            _world.MapSize > 1 && _world.MapSize % 2 == 1);
        Check("terrain fills every cell of the map",
            _gridMap.GetUsedCells().Count == _world.CellCount);

        // Bounds: in-bounds cells carry terrain, out-of-bounds ones are
        // distinguishable without a second call.
        Check("map corner is in bounds", _world.InBounds(new Vector2I(half, half))
            && _world.GetTerrain(new Vector2I(half, half)) != TerrainType.OutOfBounds);
        var beyond = new Vector2I(half + 1, 0);
        Check("cell past the edge is out of bounds", !_world.InBounds(beyond)
            && _world.GetTerrain(beyond) == TerrainType.OutOfBounds
            && _world.GetFertility(beyond) == 0f);

        int soil = 0, rock = 0, water = 0;
        float minFertility = float.MaxValue, maxFertility = float.MinValue;
        bool fertilityInRange = true, barrenGroundHasNoFertility = true;
        for (int z = -half; z <= half; z++)
        {
            for (int x = -half; x <= half; x++)
            {
                var cell = new Vector2I(x, z);
                float fertility = _world.GetFertility(cell);
                fertilityInRange &= fertility is >= 0f and <= 1f;
                switch (_world.GetTerrain(cell))
                {
                    case TerrainType.Soil:
                        soil++;
                        minFertility = Mathf.Min(minFertility, fertility);
                        maxFertility = Mathf.Max(maxFertility, fertility);
                        break;
                    case TerrainType.Rock:
                        rock++;
                        barrenGroundHasNoFertility &= fertility == 0f;
                        break;
                    case TerrainType.Water:
                        water++;
                        barrenGroundHasNoFertility &= fertility == 0f;
                        break;
                    default:
                        Check($"cell {cell} has terrain", false);
                        return;
                }
            }
        }

        GD.Print($"terrain: {soil} soil, {rock} rock, {water} water "
            + $"of {_world.CellCount} cells; fertility {minFertility:F3}..{maxFertility:F3}");
        Check("rock cells exist", rock > 0);
        Check("water cells exist", water > 0);
        Check("most of the map is soil", soil > rock + water);
        Check("fertility stays within 0..1", fertilityInRange);
        Check("fertility varies across the map", maxFertility - minFertility > 0.2f);
        Check("rock and water carry no fertility", barrenGroundHasNoFertility);

        // The starting road has to sit on legal ground.
        bool roadStripIsSoil = true;
        for (int x = -16; x <= 16; x++)
        {
            roadStripIsSoil &= _world.IsSoil(new Vector2I(x, 0));
        }
        Check("the starting road strip is soil", roadStripIsSoil);

        CheckSeedDeterminism();
    }

    /// <summary>
    /// Same seed in, same map out — including after the world has generated a
    /// different seed in between.
    /// </summary>
    private void CheckSeedDeterminism()
    {
        (TerrainType[] terrain, float[] fertility) first = Snapshot();

        _world.GenerateTerrain();
        (TerrainType[] terrain, float[] fertility) second = Snapshot();
        Check("regenerating the same seed yields identical terrain",
            SameTerrain(first.terrain, second.terrain));
        Check("regenerating the same seed yields identical fertility",
            SameFertility(first.fertility, second.fertility));

        int seed = _world.WorldSeed;
        _world.WorldSeed = seed + 1;
        _world.GenerateTerrain();
        (TerrainType[] terrain, float[] fertility) other = Snapshot();
        Check("a different seed yields a different map",
            !SameTerrain(first.terrain, other.terrain)
            || !SameFertility(first.fertility, other.fertility));

        _world.WorldSeed = seed;
        _world.GenerateTerrain();
        (TerrainType[] terrain, float[] fertility) again = Snapshot();
        Check("returning to the seed restores the same terrain",
            SameTerrain(first.terrain, again.terrain));
        Check("returning to the seed restores the same fertility",
            SameFertility(first.fertility, again.fertility));
    }

    /// <summary>
    /// Terrain and placement are separate layers: building on a cell and then
    /// clearing it leaves the terrain — and the terrain the view draws —
    /// exactly as generated.
    /// </summary>
    private void CheckLayersAreIndependent()
    {
        var cell = new Vector2I(5, 5);
        var mapCell = new Vector3I(cell.X, 0, cell.Y);
        TerrainType terrain = _world.GetTerrain(cell);
        float fertility = _world.GetFertility(cell);
        int terrainItem = _gridMap.GetCellItem(mapCell);
        Check("test cell starts unbuilt", _world.GetTile(cell) == TileType.Empty
            && terrain != TerrainType.OutOfBounds
            && terrainItem != GridMap.InvalidCellItem);

        _world.SetTile(cell, TileType.Road);
        Check("placing a road does not change the terrain under it",
            _world.GetTerrain(cell) == terrain && _world.GetFertility(cell) == fertility);
        Check("a placed road hides the terrain in the view",
            _gridMap.GetCellItem(mapCell) != terrainItem);

        _world.SetTile(cell, TileType.Empty);
        Check("clearing the tile leaves the terrain intact",
            _world.GetTerrain(cell) == terrain && _world.GetFertility(cell) == fertility);
        Check("clearing the tile shows the terrain again",
            _gridMap.GetCellItem(mapCell) == terrainItem);
    }

    /// <summary>The starting road, on top of the generated terrain.</summary>
    private void CheckStartRoad()
    {
        // The starting road is the single row z = 0, x = -16..16 (33 cells).
        Check("only the starting road was placed", _world.RoadCellCount == 33);
        Check("origin cell is road", _world.IsRoad(Vector2I.Zero));
        Check("road spans to both ends", _world.IsRoad(new Vector2I(-16, 0))
            && _world.IsRoad(new Vector2I(16, 0)));
        Check("cell off the road is empty", _world.GetTile(new Vector2I(0, 1)) == TileType.Empty);
        Check("no machines at start", GetTree().GetNodesInGroup("machines").Count == 0);
    }

    private (TerrainType[] Terrain, float[] Fertility) Snapshot()
    {
        int half = _world.MapSize / 2;
        var terrain = new TerrainType[_world.CellCount];
        var fertility = new float[_world.CellCount];
        int i = 0;
        for (int z = -half; z <= half; z++)
        {
            for (int x = -half; x <= half; x++)
            {
                var cell = new Vector2I(x, z);
                terrain[i] = _world.GetTerrain(cell);
                fertility[i] = _world.GetFertility(cell);
                i++;
            }
        }
        return (terrain, fertility);
    }

    private static bool SameTerrain(TerrainType[] a, TerrainType[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    // Bit-exact: the same seed runs the same noise through the same arithmetic,
    // so anything less than equality would hide a determinism bug.
    private static bool SameFertility(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    private void Check(string what, bool ok)
    {
        GD.Print($"{(ok ? "PASS" : "FAIL")}: {what}");
        _failed |= !ok;
    }
}
