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
/// fertility scalar). Derived from <see cref="WorldSeed"/>, bounded by
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
public partial class WorldGrid : Node3D, IHashableState
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

    /// <summary>Dev art for a placed building: a tall box, so it reads as one.</summary>
    private const int StructureItem = 8;

    /// <summary>
    /// The stream machine spawn placement draws from. Separate from
    /// <c>MachineSystem.StreamName</c> on purpose: spawning is the world laying
    /// the game out, wandering is a machine deciding what to do, and a debug
    /// session that spawns an extra tractor should not reroute the ones already
    /// driving.
    /// </summary>
    public const string SpawnStreamName = "world.spawn";

    /// <summary>Names the two terrain noise fields derive their seeds from.</summary>
    private const string FertilityNoiseName = "world.terrain.fertility";
    private const string MaskNoiseName = "world.terrain.mask";

    [Export] public PackedScene? MachineScene { get; set; }
    [Export] public int MachineCount { get; set; } = 0;

    /// <summary>
    /// The map spans cells −<c>MapHalfExtent</c>..<c>MapHalfExtent</c> on both
    /// axes. 48 → a 97×97 grid = 194 m across at 2 m cells: inside the
    /// 200×200 ground plane, and a little larger than what the camera shows
    /// when fully zoomed out.
    /// </summary>
    [Export] public int MapHalfExtent { get; set; } = 48;

    /// <summary>
    /// The world seed, which the <c>Simulation</c> owns (<see cref="Streams"/>)
    /// — this is the door terrain code and the smoke tests already knew about,
    /// kept so there is still exactly one number and not a world copy of it.
    /// Setting it starts a different world: every stream jumps back to the
    /// beginning of its sequence. It deliberately does <b>not</b> regenerate the
    /// map — <see cref="GenerateTerrain"/> is a separate, explicit act.
    /// </summary>
    public int WorldSeed
    {
        get => Streams.WorldSeed;
        set => Streams.Reseed(value);
    }

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

    // Fields are entities, not just tiles: the tile layer says a cell is
    // farmland, these say *which* field it belongs to (see Field).
    private readonly List<Field> _fields = new();
    private readonly Dictionary<Vector2I, Field> _fieldOf = new();
    private int _fieldsCreated;

    // Buildings are entities too, and for a stronger reason: a tile enum has
    // no room for the identity M5 delivers to and M6 hangs state off (see
    // Structure). Same shape as the field registry — a list in creation order
    // plus a cell -> entity index — so both kinds of placed thing are looked
    // up the same way.
    private readonly List<Structure> _structures = new();
    private readonly Dictionary<Vector2I, Structure> _structureOf = new();
    private int _structuresCreated;

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

    private RandomStreams? _streams;
    private RandomStream _spawnRng = null!;
    private int _machinesSpawned;

    // Machines are sim entities, so their state lives in flat arrays rather
    // than in the nodes that draw them (see MachineSystem). The registry lives
    // here for the same reason the field and structure registries do: this
    // class already owns the road queries the machines run on, and there is
    // exactly one world.
    private MachineSystem _machines = null!;
    private Simulation? _sim;

    public override void _Ready()
    {
        _gridMap = GetNode<GridMap>("GridMap");
        _sim = Simulation.For(this);

        // The streams are taken once and held: they are the same objects for
        // the life of the world, and a reseed rewrites them in place.
        _spawnRng = Streams.For(SpawnStreamName);
        _machines = new MachineSystem(this, Streams.For(MachineSystem.StreamName));
        _sim?.Register(_machines);

        // The world is state, not a system: it has nothing to tick, but a
        // determinism run and a save both have to see the map the player built.
        _sim?.RegisterState(this);
        GenerateTerrain();
        GenerateStartRoad();
        for (int i = 0; i < MachineCount; i++)
        {
            SpawnMachine();
        }
    }

    // The machine system holds this world; leaving it registered would tick it
    // against a freed WorldGrid — and leaving the world registered as state
    // would hash a freed node.
    public override void _ExitTree()
    {
        _sim?.Unregister(_machines);
        _sim?.UnregisterState(this);
    }

    /// <summary>
    /// The world's randomness, which the <c>Simulation</c> owns. Resolved
    /// lazily rather than in <c>_Ready</c> because <see cref="WorldSeed"/> is
    /// readable before the world is built; a scene with no sim node gets a
    /// private registry and a warning, the way the rest of the codebase repairs
    /// a bad scene instead of taking it down.
    /// </summary>
    private RandomStreams Streams
    {
        get
        {
            if (_streams != null)
            {
                return _streams;
            }

            RandomStreams? fromSim = Simulation.For(this)?.Streams;
            if (fromSim == null)
            {
                GD.PushWarning("WorldGrid: no Simulation in the tree; "
                    + "world randomness falls back to a private registry.");
                fromSim = new RandomStreams();
            }
            return _streams = fromSim;
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

    /// <summary>Every field the player has marked, in creation order.</summary>
    public IReadOnlyList<Field> Fields => _fields;

    /// <summary>How many cells belong to a field.</summary>
    public int FieldCellCount => _fieldOf.Count;

    /// <summary>
    /// The field that owns the cell, or null when no field does. This is the
    /// lookup the rest of the game uses to go from "the cell under the cursor"
    /// to the entity that actually holds crop, jobs and yield.
    /// </summary>
    public Field? GetField(Vector2I cell) => _fieldOf.GetValueOrDefault(cell);

    /// <summary>
    /// Marks the cells as <b>one new field</b> — the addressable unit farmland
    /// comes in (see <see cref="Field"/>) — and returns it, or null for an
    /// empty region. Cells another field owned are transferred to the new one,
    /// so the cell to field map can never disagree with the tile layer; the
    /// field tool never places over an occupied cell in the first place, but
    /// dev and scenario code calls this directly, the way
    /// <see cref="BuildRoadLine"/> is the unvalidated way to lay road.
    /// </summary>
    public Field? MarkField(IReadOnlyList<Vector2I> cells)
    {
        if (cells.Count == 0)
        {
            return null;
        }

        _fieldsCreated++;
        var field = new Field(_fieldsCreated, $"Field {_fieldsCreated}", cells);
        _fields.Add(field);
        foreach (Vector2I cell in cells)
        {
            ReleaseFieldCell(cell);
            SetTile(cell, TileType.Field);
            _fieldOf[cell] = field;
        }
        return field;
    }

    /// <summary>Every building the player has placed, in creation order.</summary>
    public IReadOnlyList<Structure> Structures => _structures;

    /// <summary>How many cells are covered by a building.</summary>
    public int StructureCellCount => _structureOf.Count;

    /// <summary>
    /// The building that occupies the cell, or null when none does — the
    /// lookup that turns "the cell under the cursor" into the entity that
    /// holds the recipe and the buffers.
    /// </summary>
    public Structure? GetStructure(Vector2I cell) => _structureOf.GetValueOrDefault(cell);

    /// <summary>
    /// The building with that <see cref="Structure.Id"/>, or null when it has
    /// been demolished — how a saved order, a job or a recipe refers to a
    /// building without holding the object or knowing a cell. Linear over the
    /// registry: buildings are few, and an index can be added the day one is
    /// worth keeping in step.
    /// </summary>
    public Structure? GetStructure(int id)
    {
        foreach (Structure structure in _structures)
        {
            if (structure.Id == id)
            {
                return structure;
            }
        }
        return null;
    }

    /// <summary>
    /// Places the cells as <b>one new building</b> — the addressable unit
    /// buildings come in (see <see cref="Structure"/>) — and returns it, or
    /// null for an empty footprint. Whatever held those cells is cleared
    /// first, so the cell → structure map can never disagree with the tile
    /// layer; the structure tool refuses an occupied cell in the first place,
    /// but dev and scenario code calls this directly, the way
    /// <see cref="BuildRoadLine"/> is the unvalidated way to lay road and
    /// <see cref="MarkField"/> is for farmland.
    ///
    /// The footprint is a list, so a multi-cell building needs nothing here
    /// beyond a wider list from the tool.
    /// </summary>
    public Structure? PlaceStructure(IReadOnlyList<Vector2I> cells)
    {
        if (cells.Count == 0)
        {
            return null;
        }

        _structuresCreated++;
        var structure = new Structure(
            _structuresCreated, $"Structure {_structuresCreated}", cells);
        foreach (Vector2I cell in cells)
        {
            DemolishStructureAt(cell);
        }
        _structures.Add(structure);
        foreach (Vector2I cell in cells)
        {
            SetTile(cell, TileType.Structure);
            _structureOf[cell] = structure;
        }
        return structure;
    }

    /// <summary>
    /// Demolishes the building that owns the cell, if one does — <b>the whole
    /// building</b>, clearing every other cell of its footprint too. That is
    /// where a building parts company with a <see cref="Field"/>, which shrinks
    /// cell by cell instead: half a mill is not a mill, so bulldozing one
    /// corner of a 2x2 takes the mill with it.
    ///
    /// Every cell is detached from the registry before any tile is written, so
    /// the <see cref="SetTile"/> calls that clear the rest cannot re-enter.
    /// </summary>
    private void DemolishStructureAt(Vector2I cell)
    {
        if (!_structureOf.Remove(cell, out Structure? structure))
        {
            return;
        }

        _structures.Remove(structure);
        var footprint = new List<Vector2I>(structure.Cells);
        foreach (Vector2I owned in footprint)
        {
            _structureOf.Remove(owned);
        }
        foreach (Vector2I owned in footprint)
        {
            if (owned != cell)
            {
                SetTile(owned, TileType.Empty);
            }
        }
    }

    /// <summary>
    /// Detaches a cell from the field that owns it, and drops that field
    /// entirely once it has lost its last cell — a field is its cells, so an
    /// empty one is not a field the player can still address.
    /// </summary>
    private void ReleaseFieldCell(Vector2I cell)
    {
        if (!_fieldOf.Remove(cell, out Field? field))
        {
            return;
        }
        field.RemoveCell(cell);
        if (field.CellCount == 0)
        {
            _fields.Remove(field);
        }
    }

    /// <summary>
    /// Takes whatever the player placed off the cell and reports what came off,
    /// or null when the cell held nothing. <b>The terrain underneath is never
    /// touched</b> — the two layers are stored separately, so clearing the
    /// placement simply uncovers the ground that was always there, fertility
    /// and all.
    ///
    /// This is the one removal path (<c>BulldozeTool</c> is its only player-
    /// facing caller), and it reports rather than counts because a removal is
    /// not a cell: clearing a field cell shrinks a <see cref="Field"/> that may
    /// survive it, while clearing any cell of a building demolishes the
    /// <b>whole</b> building (see <see cref="SetTile"/>) — the returned
    /// <see cref="Removal"/> carries the footprint that actually went. Calling
    /// it again on a cell of that same building answers null, because the cell
    /// is already empty, which is what keeps one building from being refunded
    /// once per cell a drag clipped.
    ///
    /// <b>Note for M5 (vehicles) — road removal is not a safe operation.</b>
    /// From the milestone that gives machines routes onward, a road cell can be
    /// cleared out from under a vehicle that is driving over it or has it in a
    /// path it already computed. Nothing here stops that today and nothing is
    /// re-validated: machines currently pick their own random road walks and
    /// would simply fail to path next time. It is a real case, not an
    /// impossible one — when M5 lands, the vehicles must handle a route whose
    /// cells stopped being road (re-path, or refuse the removal), and this is
    /// the function that will hand them the news.
    /// </summary>
    public Removal? Clear(Vector2I cell)
    {
        TileType tile = GetTile(cell);
        if (tile == TileType.Empty)
        {
            return null;
        }

        // Read the entities *before* the write: SetTile drops an emptied field
        // and demolishes a structure whole, so afterwards neither lookup can
        // still name what was removed.
        Field? field = GetField(cell);
        Structure? structure = GetStructure(cell);
        IReadOnlyList<Vector2I> freed = structure != null
            ? new List<Vector2I>(structure.Cells)
            : [cell];

        SetTile(cell, TileType.Empty);
        return new Removal(tile, cell, freed, field, structure);
    }

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
        if (previous == TileType.Field)
        {
            ReleaseFieldCell(cell);
        }
        // Buildings are atomic: overwriting one of their cells takes the whole
        // building, and with it the rest of its footprint.
        if (previous == TileType.Structure)
        {
            DemolishStructureAt(cell);
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
            case TileType.Structure:
                return StructureItem;
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

    public Vector2I RandomRoadCell(RandomStream rng) => _roadCells[rng.NextInt(_roadCells.Count)];

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

    /// <summary>
    /// Cells of the filled axis-aligned rectangle spanned by two opposite
    /// corners, both corners included — the footprint of a field drag, the way
    /// <see cref="LineCells"/> is the footprint of a road drag. Row-major from
    /// the minimum corner, so the order does not depend on which corner the
    /// player started from and the same rectangle always yields the same list.
    /// </summary>
    public static List<Vector2I> RectCells(Vector2I from, Vector2I to)
    {
        int minX = Math.Min(from.X, to.X);
        int maxX = Math.Max(from.X, to.X);
        int minY = Math.Min(from.Y, to.Y);
        int maxY = Math.Max(from.Y, to.Y);

        var cells = new List<Vector2I>((maxX - minX + 1) * (maxY - minY + 1));
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                cells.Add(new Vector2I(x, y));
            }
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
            Seed = Streams.DeriveSeed(FertilityNoiseName),
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = FertilityFrequency,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 4,
        };
        var maskNoise = new FastNoiseLite
        {
            Seed = Streams.DeriveSeed(MaskNoiseName),
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

        // SetTile itself places anywhere, including outside the generated map
        // (legality is a build-tool concern, see PlacementRules), so those
        // cells need drawing too.
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

    /// <summary>The name the world's state is filed under in a state hash.</summary>
    public string StateName => "world";

    /// <summary>
    /// Both layers of the map, plus the registries and the id counters that say
    /// what the next placement will be called.
    ///
    /// <b>The terrain layer goes in even though it is regenerable from the
    /// seed</b> — it is what makes "two runs from different seeds hash
    /// differently" true on tick zero rather than whenever the divergence
    /// happens to reach a machine, and re-running generation is bit-exact, so a
    /// load that regenerates rather than restores still matches.
    ///
    /// <b>The placement layer is folded unordered</b>, because it is a
    /// <c>Dictionary</c>: its enumeration order depends on insertion history
    /// and capacity, so hashing that walk in order would report divergences
    /// between two identical maps. The cell-to-owner lookups and the road-cell
    /// list are left out entirely — all three are indexes derived from what is
    /// hashed here.
    /// </summary>
    public void HashState(StateHash hash)
    {
        hash.Write(_halfExtent);
        for (int i = 0; i < _terrain.Length; i++)
        {
            hash.Write((int)_terrain[i]);
            hash.Write(_fertility[i]);
        }

        var member = new StateHash();
        ulong fold = 0UL;
        foreach (KeyValuePair<Vector2I, TileType> tile in _tiles)
        {
            member.Reset();
            member.Write(tile.Key);
            member.Write((int)tile.Value);
            fold = StateHash.Fold(fold, member.Value);
        }
        hash.WriteUnordered(fold, _tiles.Count);

        // Fields and structures are folded by identity for the same reason:
        // their registries are lists today, but a load will not rebuild them in
        // creation order and the hash has no business caring.
        fold = 0UL;
        foreach (Field field in _fields)
        {
            member.Reset();
            member.Write(field.Id);
            member.Write(field.Name);
            foreach (Vector2I cell in field.Cells)
            {
                member.Write(cell);
            }
            fold = StateHash.Fold(fold, member.Value);
        }
        hash.WriteUnordered(fold, _fields.Count);

        fold = 0UL;
        foreach (Structure structure in _structures)
        {
            member.Reset();
            member.Write(structure.Id);
            member.Write(structure.Name);
            foreach (Vector2I cell in structure.Cells)
            {
                member.Write(cell);
            }
            fold = StateHash.Fold(fold, member.Value);
        }
        hash.WriteUnordered(fold, _structures.Count);

        // Counters, not derivable from the registries: a demolished building
        // does not give its id back, and the next machine's colour depends on
        // how many have ever spawned.
        hash.Write(_fieldsCreated);
        hash.Write(_structuresCreated);
        hash.Write(_machinesSpawned);
    }

    /// <summary>
    /// Every machine's sim state, in flat arrays. Ticked by the
    /// <see cref="Simulation"/>; the <see cref="Machine"/> nodes only draw it.
    /// </summary>
    public MachineSystem Machines => _machines;

    /// <summary>
    /// Spawns one machine at a random road cell: a row in
    /// <see cref="Machines"/> for the sim, and a node bound to it for the view.
    /// The spot comes off the world's spawn stream, so a given spawn sequence is
    /// reproducible for a world seed. Returns null when no machine scene is
    /// assigned.
    /// </summary>
    public Machine? SpawnMachine()
    {
        if (MachineScene == null)
        {
            return null;
        }

        // The node is instanced first only to read the exports the scene
        // carries — Speed and TurnSpeed are spawn input to the arrays, and the
        // sim never looks at the node again.
        var machine = MachineScene.Instantiate<Machine>();
        Vector3 spawn = CellToWorld(RandomRoadCell(_spawnRng)) + Vector3.Up * Machine.DeckHeight;
        EntityId entity = _machines.Spawn(spawn, machine.Speed, machine.TurnSpeed);
        machine.Setup(_machines, entity, MachineColors[_machinesSpawned % MachineColors.Length]);
        machine.Position = spawn;
        AddChild(machine);
        _machinesSpawned++;
        return machine;
    }
}
