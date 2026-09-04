using System;
using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Owns the logical world data and keeps the child GridMap view in sync.
///
/// Two independent layers live here:
/// <list type="bullet">
/// <item><b>terrain</b> — what the land is (<see cref="TerrainType"/> plus a
/// fertility scalar). Generated from <see cref="WorldSeed"/>, bounded by
/// <see cref="MapHalfExtent"/>, and never edited by the player.</item>
/// <item><b>placement</b> — what the player built (<see cref="TileType"/>).
/// Sparse; clearing it back to <see cref="TileType.Empty"/> leaves the terrain
/// underneath untouched.</item>
/// </list>
///
/// Also answers road-network queries (pathfinding, random road cells) for
/// machines. The GridMap is presentation only — game logic must always go
/// through this class, never read the GridMap back.
/// </summary>
public partial class WorldGrid : Node3D
{
    private const int StartRoadHalfExtent = 16; // the starting road spans cells -16..16

    // MeshLibrary item ids in assets/dev/tile_library.tres.
    private const int RoadItem = 0;
    private const int FieldItem = 1;
    private const int RockItem = 2;
    private const int WaterItem = 3;

    /// <summary>
    /// Soil is drawn in <see cref="SoilTiers"/> shades by fertility, as
    /// consecutive MeshLibrary items starting at this id.
    /// </summary>
    private const int SoilItemFirst = 4;
    private const int SoilTiers = 4;

    /// <summary>
    /// Offset that derives the rock/water mask seed from the world seed, so a
    /// single seed still describes the whole map.
    /// </summary>
    private const int MaskSeedOffset = 7919;

    [Export] public PackedScene? MachineScene { get; set; }
    [Export] public int MachineCount { get; set; } = 0;

    /// <summary>
    /// The map spans cells −<c>MapHalfExtent</c>..<c>MapHalfExtent</c> on both
    /// axes. 48 → a 97×97 grid = 194 m across at 2 m cells: inside the
    /// 200×200 ground plane, and a little larger than what the camera shows
    /// when fully zoomed out.
    /// </summary>
    [Export] public int MapHalfExtent { get; set; } = 48;

    /// <summary>Single seed for the whole map: both noise fields derive from it.</summary>
    [Export] public int WorldSeed { get; set; } = 20260904;

    /// <summary>Noise frequency (per cell) of the fertility field.</summary>
    [Export] public float FertilityFrequency { get; set; } = 0.04f;

    /// <summary>Noise frequency (per cell) of the rock/water mask.</summary>
    [Export] public float MaskFrequency { get; set; } = 0.06f;

    /// <summary>Mask values below this become water.</summary>
    [Export] public float WaterLevel { get; set; } = -0.3f;

    /// <summary>Mask values above this become rock.</summary>
    [Export] public float RockLevel { get; set; } = 0.34f;

    private static readonly Color[] MachineColors =
    [
        new(0.75f, 0.22f, 0.17f), // tractor red
        new(0.20f, 0.42f, 0.75f), // truck blue
        new(0.85f, 0.70f, 0.20f), // combine yellow
        new(0.25f, 0.55f, 0.30f), // harvester green
    ];

    private GridMap _gridMap = null!;

    // Placement layer: sparse, the player owns it.
    private readonly Dictionary<Vector2I, TileType> _tiles = new();
    private readonly List<Vector2I> _roadCells = new();

    // Terrain layer: dense flat arrays indexed by Index(cell). Every in-bounds
    // cell has a value, so a dictionary would only add overhead — this is the
    // first piece of state laid out the way the M3 sim core wants all of it.
    private TerrainType[] _terrain = [];
    private float[] _fertility = [];

    /// <summary>
    /// Half-extent the current terrain arrays were generated with; −1 until
    /// <see cref="GenerateTerrain"/> has run, which makes every cell out of
    /// bounds until then.
    /// </summary>
    private int _halfExtent = -1;

    private readonly Random _spawnRng = new(1234);
    private int _machinesSpawned;

    public override void _Ready()
    {
        _gridMap = GetNode<GridMap>("GridMap");
        GenerateTerrain();
        GenerateStartRoad();
        for (int i = 0; i < MachineCount; i++)
        {
            SpawnMachine();
        }
    }

    /// <summary>Side length of the generated map, in cells.</summary>
    public int MapSize => _halfExtent < 0 ? 0 : _halfExtent * 2 + 1;

    /// <summary>Number of generated terrain cells.</summary>
    public int CellCount => MapSize * MapSize;

    /// <summary>Whether the cell lies inside the generated map.</summary>
    public bool InBounds(Vector2I cell) =>
        cell.X >= -_halfExtent && cell.X <= _halfExtent
        && cell.Y >= -_halfExtent && cell.Y <= _halfExtent;

    private int Index(Vector2I cell) => (cell.Y + _halfExtent) * MapSize + (cell.X + _halfExtent);

    /// <summary>
    /// Terrain under the cell, or <see cref="TerrainType.OutOfBounds"/> when the
    /// cell is off the map. Never affected by what the player placed on top.
    /// </summary>
    public TerrainType GetTerrain(Vector2I cell) =>
        InBounds(cell) ? _terrain[Index(cell)] : TerrainType.OutOfBounds;

    /// <summary>
    /// Soil quality of the cell, 0..1. Rock, water and out-of-bounds cells have
    /// no soil and answer 0.
    /// </summary>
    public float GetFertility(Vector2I cell) => InBounds(cell) ? _fertility[Index(cell)] : 0f;

    /// <summary>Whether the terrain is workable ground.</summary>
    public bool IsSoil(Vector2I cell) => GetTerrain(cell) == TerrainType.Soil;

    public TileType GetTile(Vector2I cell) => _tiles.GetValueOrDefault(cell, TileType.Empty);

    public bool IsRoad(Vector2I cell) => GetTile(cell) == TileType.Road;

    /// <summary>How many cells currently hold a road tile.</summary>
    public int RoadCellCount => _roadCells.Count;

    public void SetTile(Vector2I cell, TileType type)
    {
        TileType previous = GetTile(cell);
        if (previous == type)
        {
            return;
        }

        if (previous == TileType.Road)
        {
            _roadCells.Remove(cell);
        }
        if (type == TileType.Road)
        {
            _roadCells.Add(cell);
        }

        if (type == TileType.Empty)
        {
            _tiles.Remove(cell);
        }
        else
        {
            _tiles[cell] = type;
        }

        RefreshCell(cell);
    }

    /// <summary>
    /// MeshLibrary item a cell should show: the placed tile when there is one,
    /// otherwise the terrain underneath. One GridMap draws both layers, so
    /// placement hides terrain visually without ever overwriting it — clearing
    /// the tile brings the same terrain back.
    /// </summary>
    private int ViewItem(Vector2I cell)
    {
        switch (GetTile(cell))
        {
            case TileType.Road:
                return RoadItem;
            case TileType.Field:
                return FieldItem;
        }

        return GetTerrain(cell) switch
        {
            TerrainType.Soil => SoilItemFirst
                + Math.Clamp((int)(GetFertility(cell) * SoilTiers), 0, SoilTiers - 1),
            TerrainType.Rock => RockItem,
            TerrainType.Water => WaterItem,
            _ => (int)GridMap.InvalidCellItem,
        };
    }

    /// <summary>Pushes one cell's current state to the GridMap view.</summary>
    private void RefreshCell(Vector2I cell) =>
        _gridMap.SetCellItem(new Vector3I(cell.X, 0, cell.Y), ViewItem(cell));

    /// <summary>Center of the cell on the ground plane (y = 0).</summary>
    public Vector3 CellToWorld(Vector2I cell) =>
        _gridMap.ToGlobal(_gridMap.MapToLocal(new Vector3I(cell.X, 0, cell.Y)));

    public Vector2I WorldToCell(Vector3 position)
    {
        Vector3I cell = _gridMap.LocalToMap(_gridMap.ToLocal(position));
        return new Vector2I(cell.X, cell.Z);
    }

    public Vector2I RandomRoadCell(Random rng) => _roadCells[rng.Next(_roadCells.Count)];

    /// <summary>
    /// Breadth-first shortest path over road cells, including both endpoints.
    /// Steps to all 8 neighbors; a diagonal step is only allowed past a road
    /// corner (see <see cref="CanCutCorner"/>), so a stair-stepped diagonal
    /// road is walked diagonally but paths never squeeze through the point gap
    /// between two unconnected road strips.
    /// Returns null when either end is off-road or unreachable.
    /// </summary>
    public List<Vector2I>? FindRoadPath(Vector2I from, Vector2I to)
    {
        if (!IsRoad(from) || !IsRoad(to))
        {
            return null;
        }

        var cameFrom = new Dictionary<Vector2I, Vector2I> { [from] = from };
        var frontier = new Queue<Vector2I>();
        frontier.Enqueue(from);
        while (frontier.Count > 0 && !cameFrom.ContainsKey(to))
        {
            Vector2I current = frontier.Dequeue();
            foreach (Vector2I step in Steps)
            {
                Vector2I next = current + step;
                if (!IsRoad(next) || cameFrom.ContainsKey(next))
                {
                    continue;
                }
                if (step.X != 0 && step.Y != 0 && !CanCutCorner(current, step))
                {
                    continue;
                }
                cameFrom[next] = current;
                frontier.Enqueue(next);
            }
        }

        if (!cameFrom.ContainsKey(to))
        {
            return null;
        }

        var path = new List<Vector2I> { to };
        while (path[^1] != from)
        {
            path.Add(cameFrom[path[^1]]);
        }
        path.Reverse();
        return path;
    }

    /// <summary>
    /// Drops every waypoint that a machine can skip by driving straight
    /// (string pulling): keeps a cell only when the direct segment to the next
    /// kept cell would leave the road. The result has the same endpoints but
    /// far fewer waypoints — long runs at any angle instead of cell-by-cell
    /// zigzag — which is what machines actually drive.
    /// </summary>
    public List<Vector2I> SmoothRoadPath(List<Vector2I> path)
    {
        var smoothed = new List<Vector2I> { path[0] };
        int i = 0;
        while (i < path.Count - 1)
        {
            int j = path.Count - 1;
            while (j > i + 1 && !HasRoadLineOfSight(path[i], path[j]))
            {
                j--;
            }
            smoothed.Add(path[j]);
            i = j;
        }
        return smoothed;
    }

    /// <summary>
    /// Whether the straight segment between two cell centers stays on road:
    /// every cell it crosses must be road, and where it passes exactly through
    /// a cell corner the same rule as <see cref="CanCutCorner"/> applies.
    /// Integer supercover walk, so it is exact and deterministic.
    /// </summary>
    public bool HasRoadLineOfSight(Vector2I from, Vector2I to)
    {
        if (!IsRoad(from))
        {
            return false;
        }
        int nx = Math.Abs(to.X - from.X);
        int ny = Math.Abs(to.Y - from.Y);
        int sx = Math.Sign(to.X - from.X);
        int sy = Math.Sign(to.Y - from.Y);
        Vector2I cell = from;
        int ix = 0, iy = 0;
        while (ix < nx || iy < ny)
        {
            // Which grid line does the segment cross next: the vertical one at
            // x-fraction (1+2ix)/(2nx) or the horizontal one at (1+2iy)/(2ny)?
            long crossing = (long)(1 + 2 * ix) * ny - (long)(1 + 2 * iy) * nx;
            if (crossing == 0)
            {
                if (!CanCutCorner(cell, new Vector2I(sx, sy)))
                {
                    return false;
                }
                cell += new Vector2I(sx, sy);
                ix++;
                iy++;
            }
            else if (crossing < 0)
            {
                cell.X += sx;
                ix++;
            }
            else
            {
                cell.Y += sy;
                iy++;
            }
            if (!IsRoad(cell))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// A diagonal move out of <paramref name="cell"/> passes exactly through a
    /// cell corner; it is allowed when at least one of the two cells sharing
    /// that corner is road (true along a stair-stepped diagonal road), and
    /// refused when both are empty — that would mean cutting through the point
    /// gap between two roads that aren't actually connected.
    /// </summary>
    private bool CanCutCorner(Vector2I cell, Vector2I diagonalStep) =>
        IsRoad(new Vector2I(cell.X + diagonalStep.X, cell.Y))
        || IsRoad(new Vector2I(cell.X, cell.Y + diagonalStep.Y));

    /// <summary>
    /// Cells of the straight line between two cells (Bresenham), both endpoints
    /// included. Where the line calls for a diagonal step, it is split into two
    /// orthogonal steps (a staircase), so consecutive cells always share an
    /// edge and the footprint stays 4-connected. The connector cell alternates
    /// sides on successive diagonal steps, keeping the staircase centered on
    /// the true line — so the center-to-center segment machines drive runs
    /// down the middle of the road band instead of hugging one edge.
    /// </summary>
    public static List<Vector2I> LineCells(Vector2I from, Vector2I to)
    {
        var cells = new List<Vector2I> { from };
        int dx = Math.Abs(to.X - from.X);
        int dy = Math.Abs(to.Y - from.Y);
        int sx = from.X < to.X ? 1 : -1;
        int sy = from.Y < to.Y ? 1 : -1;
        int error = dx - dy;
        bool connectorOnX = true;
        Vector2I cell = from;
        while (cell != to)
        {
            int e2 = 2 * error;
            bool stepX = e2 > -dy;
            bool stepY = e2 < dx;
            if (stepX)
            {
                error -= dy;
            }
            if (stepY)
            {
                error += dx;
            }
            if (stepX && stepY)
            {
                cells.Add(connectorOnX
                    ? new Vector2I(cell.X + sx, cell.Y)
                    : new Vector2I(cell.X, cell.Y + sy));
                connectorOnX = !connectorOnX;
                cell += new Vector2I(sx, sy);
            }
            else if (stepX)
            {
                cell.X += sx;
            }
            else
            {
                cell.Y += sy;
            }
            cells.Add(cell);
        }
        return cells;
    }

    /// <summary>Places road tiles along the straight line from..to.</summary>
    public void BuildRoadLine(Vector2I from, Vector2I to)
    {
        foreach (Vector2I cell in LineCells(from, to))
        {
            SetTile(cell, TileType.Road);
        }
    }

    // Orthogonals first so BFS prefers them on ties; order fixes which of the
    // equal-length paths is found, keeping routes deterministic.
    private static readonly Vector2I[] Steps =
    [
        Vector2I.Right,
        Vector2I.Left,
        Vector2I.Up,
        Vector2I.Down,
        new(1, 1),
        new(1, -1),
        new(-1, 1),
        new(-1, -1),
    ];

    /// <summary>
    /// Generates the terrain layer from <see cref="WorldSeed"/> and redraws the
    /// view. Two <c>FastNoiseLite</c> fields, both derived from that one seed:
    /// fertility, and a rock/water mask that reads like a coarse elevation —
    /// its low ground becomes water and its high ground bare rock, so the two
    /// unusable kinds never border each other. The placement layer is untouched,
    /// so regenerating leaves roads and fields exactly where they were.
    /// Deterministic: the same seed and extent always produce the same arrays.
    /// </summary>
    public void GenerateTerrain()
    {
        _halfExtent = Mathf.Max(0, MapHalfExtent);
        int cells = CellCount;
        _terrain = new TerrainType[cells];
        _fertility = new float[cells];

        var fertilityNoise = new FastNoiseLite
        {
            Seed = WorldSeed,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = FertilityFrequency,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 4,
        };
        var maskNoise = new FastNoiseLite
        {
            Seed = WorldSeed + MaskSeedOffset,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = MaskFrequency,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 3,
        };

        for (int z = -_halfExtent; z <= _halfExtent; z++)
        {
            for (int x = -_halfExtent; x <= _halfExtent; x++)
            {
                var cell = new Vector2I(x, z);
                float mask = maskNoise.GetNoise2D(x, z);
                TerrainType terrain =
                    mask < WaterLevel ? TerrainType.Water
                    : mask > RockLevel ? TerrainType.Rock
                    : TerrainType.Soil;

                // The starting road has to land on legal ground: carve its strip
                // back to soil rather than biasing the noise, so the seed still
                // owns every other cell.
                if (IsStartRoadCell(cell))
                {
                    terrain = TerrainType.Soil;
                }

                int i = Index(cell);
                _terrain[i] = terrain;
                _fertility[i] = terrain == TerrainType.Soil
                    ? Mathf.Clamp(fertilityNoise.GetNoise2D(x, z) * 0.5f + 0.5f, 0f, 1f)
                    : 0f;
            }
        }

        RedrawAllCells();
    }

    /// <summary>Redraws the whole GridMap from both layers.</summary>
    private void RedrawAllCells()
    {
        _gridMap.Clear();
        for (int z = -_halfExtent; z <= _halfExtent; z++)
        {
            for (int x = -_halfExtent; x <= _halfExtent; x++)
            {
                RefreshCell(new Vector2I(x, z));
            }
        }

        // Placement outside the generated map is still allowed (build
        // validation is a later milestone), so those cells need drawing too.
        foreach (Vector2I cell in _tiles.Keys)
        {
            if (!InBounds(cell))
            {
                RefreshCell(cell);
            }
        }
    }

    /// <summary>Cells the starting road occupies — kept soil by generation.</summary>
    private static bool IsStartRoadCell(Vector2I cell) =>
        cell.Y == 0 && cell.X >= -StartRoadHalfExtent && cell.X <= StartRoadHalfExtent;

    /// <summary>Starting layout: a single straight road along x through the origin.</summary>
    private void GenerateStartRoad()
    {
        for (int x = -StartRoadHalfExtent; x <= StartRoadHalfExtent; x++)
        {
            SetTile(new Vector2I(x, 0), TileType.Road);
        }
    }

    /// <summary>
    /// Spawns one machine at a random road cell. Position and behavior seeds
    /// come from a fixed-seed spawn counter, so a given spawn sequence is
    /// reproducible. Returns null when no machine scene is assigned.
    /// </summary>
    public Machine? SpawnMachine()
    {
        if (MachineScene == null)
        {
            return null;
        }

        var machine = MachineScene.Instantiate<Machine>();
        machine.Setup(this, new Random(1000 + _machinesSpawned),
            MachineColors[_machinesSpawned % MachineColors.Length]);
        machine.Position = CellToWorld(RandomRoadCell(_spawnRng)) + Vector3.Up * Machine.DeckHeight;
        AddChild(machine);
        _machinesSpawned++;
        return machine;
    }
}
