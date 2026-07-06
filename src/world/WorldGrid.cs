using System;
using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Owns the logical tile data (roads, fields) and keeps the child GridMap view
/// in sync. Also answers road-network queries (pathfinding, random road cells)
/// for machines. The GridMap is presentation only — game logic must always go
/// through this class, never read the GridMap back.
/// </summary>
public partial class WorldGrid : Node3D
{
    private const int HalfExtent = 16; // world spans cells -16..16 on both axes
    private const int BlockSize = 8;   // road every N cells, both axes

    // MeshLibrary item ids in assets/dev/tile_library.tres.
    private const int RoadItem = 0;
    private const int FieldItem = 1;

    [Export] public PackedScene? MachineScene { get; set; }
    [Export] public int MachineCount { get; set; } = 4;

    private static readonly Color[] MachineColors =
    [
        new(0.75f, 0.22f, 0.17f), // tractor red
        new(0.20f, 0.42f, 0.75f), // truck blue
        new(0.85f, 0.70f, 0.20f), // combine yellow
        new(0.25f, 0.55f, 0.30f), // harvester green
    ];

    private GridMap _gridMap = null!;
    private readonly Dictionary<Vector2I, TileType> _tiles = new();
    private readonly List<Vector2I> _roadCells = new();

    public override void _Ready()
    {
        _gridMap = GetNode<GridMap>("GridMap");
        GenerateTestFarm();
        SpawnMachines();
    }

    public TileType GetTile(Vector2I cell) => _tiles.GetValueOrDefault(cell, TileType.Empty);

    public bool IsRoad(Vector2I cell) => GetTile(cell) == TileType.Road;

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

        int item = type switch
        {
            TileType.Road => RoadItem,
            TileType.Field => FieldItem,
            _ => (int)GridMap.InvalidCellItem,
        };
        _gridMap.SetCellItem(new Vector3I(cell.X, 0, cell.Y), item);
    }

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
            foreach (Vector2I next in Neighbors(current))
            {
                if (IsRoad(next) && !cameFrom.ContainsKey(next))
                {
                    cameFrom[next] = current;
                    frontier.Enqueue(next);
                }
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

    private static Vector2I[] Neighbors(Vector2I cell) =>
    [
        cell + Vector2I.Right,
        cell + Vector2I.Left,
        cell + Vector2I.Up,
        cell + Vector2I.Down,
    ];

    /// <summary>
    /// Deterministic dev layout: a road lattice every BlockSize cells with most
    /// blocks in between filled with field, some left as grass.
    /// </summary>
    private void GenerateTestFarm()
    {
        for (int x = -HalfExtent; x <= HalfExtent; x++)
        {
            for (int z = -HalfExtent; z <= HalfExtent; z++)
            {
                if (FloorMod(x, BlockSize) == 0 || FloorMod(z, BlockSize) == 0)
                {
                    SetTile(new Vector2I(x, z), TileType.Road);
                }
                else if (FloorMod(FloorDiv(x, BlockSize) + FloorDiv(z, BlockSize), 3) != 0)
                {
                    SetTile(new Vector2I(x, z), TileType.Field);
                }
            }
        }
    }

    private void SpawnMachines()
    {
        if (MachineScene == null)
        {
            return;
        }

        var rng = new Random(1234);
        for (int i = 0; i < MachineCount; i++)
        {
            var machine = MachineScene.Instantiate<Machine>();
            machine.Setup(this, new Random(1000 + i), MachineColors[i % MachineColors.Length]);
            machine.Position = CellToWorld(RandomRoadCell(rng)) + Vector3.Up * Machine.DeckHeight;
            AddChild(machine);
        }
    }

    private static int FloorMod(int value, int modulus) => ((value % modulus) + modulus) % modulus;

    private static int FloorDiv(int value, int divisor) => (value - FloorMod(value, divisor)) / divisor;
}
