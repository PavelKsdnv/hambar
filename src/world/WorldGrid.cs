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
    private const int HalfExtent = 16; // the starting road spans cells -16..16

    // MeshLibrary item ids in assets/dev/tile_library.tres.
    private const int RoadItem = 0;
    private const int FieldItem = 1;

    [Export] public PackedScene? MachineScene { get; set; }
    [Export] public int MachineCount { get; set; } = 0;

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
    private readonly Random _spawnRng = new(1234);
    private int _machinesSpawned;

    public override void _Ready()
    {
        _gridMap = GetNode<GridMap>("GridMap");
        GenerateStartRoad();
        for (int i = 0; i < MachineCount; i++)
        {
            SpawnMachine();
        }
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

    /// <summary>Starting layout: a single straight road along x through the origin.</summary>
    private void GenerateStartRoad()
    {
        for (int x = -HalfExtent; x <= HalfExtent; x++)
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
