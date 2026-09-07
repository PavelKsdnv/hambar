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
public partial class WorldGrid : Node3D, IHashableState, ISimView
{
    private const int StartRoadHalfExtent = 16; // the starting road spans cells -16..16

    // MeshLibrary item ids in assets/dev/tile_library.tres.
    private const int RoadItem = 0;
    private const int RockItem = 2;
    private const int WaterItem = 3;

    /// <summary>
    /// Farmland whose crop row cannot be reached — the one frame inside
    /// <see cref="MarkField"/> before the cell → field lookup is written, and
    /// any cell the unvalidated <see cref="SetTile"/> door makes farmland
    /// without an owning <see cref="Field"/>. Never what the player sees: a
    /// real field is drawn by its stage below.
    /// </summary>
    private const int FieldItem = 1;

    /// <summary>
    /// Soil is drawn in <see cref="SoilTiers"/> shades by fertility, as
    /// consecutive MeshLibrary items starting at this id.
    /// </summary>
    private const int SoilItemFirst = 4;
    private const int SoilTiers = 4;

    /// <summary>Dev art for a placed building: a tall box, so it reads as one.</summary>
    private const int StructureItem = 8;

    /// <summary>
    /// A field cell is drawn by <b>what is standing on it</b>, one MeshLibrary
    /// item per <see cref="CropStage"/>, consecutive from this id in enum
    /// order — the same trick <see cref="SoilItemFirst"/> plays with fertility
    /// tiers, and for the same reason: the mapping is arithmetic, so adding a
    /// stage is adding a mesh rather than editing a switch. Each item differs
    /// in <i>both</i> height and colour, because at the far end of the rig's
    /// zoom a 2 m cell is a few pixels tall and height alone stops carrying.
    /// </summary>
    private const int FieldStageItemFirst = 9;

    /// <summary>
    /// The stream machine spawn placement draws from. <b>The only randomness a
    /// vehicle is subject to</b>: where a bought one is parked is the world
    /// laying the game out, and everything a machine does afterwards is an
    /// order somebody typed. The machine system used to own a stream of its own
    /// for choosing destinations; that went with the wandering.
    /// </summary>
    public const string SpawnStreamName = "world.spawn";

    /// <summary>
    /// Cells on a side of a fertility chunk: 4, so a chunk is 8 m square — a
    /// patch of ground about the size of the smallest field worth marking, and
    /// small enough that a big field still spans several of them.
    /// </summary>
    public const int DefaultFertilityChunkSize = 4;

    /// <summary>Names the two terrain noise fields derive their seeds from.</summary>
    private const string FertilityNoiseName = "world.terrain.fertility";
    private const string MaskNoiseName = "world.terrain.mask";

    [Export] public PackedScene? MachineScene { get; set; }
    [Export] public int MachineCount { get; set; } = 0;

    /// <summary>
    /// The people who can be in a cab, so order execution can tell a live
    /// driver from the handle a dismissed one left behind
    /// (<see cref="MachineSystem.CrewOf"/> is deliberately raw). Null means
    /// this scene has no labour at all, and a crew handle is then believed as
    /// written — the same "no such thing in this scene" reading a
    /// <c>BuildTool</c> with no <c>Economy</c> has.
    /// </summary>
    [Export] public LabourPool? Labour { get; set; }

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
        set
        {
            // Reading a detached world's seed is harmless; reseeding one is
            // not. The new seed lands on the throwaway registry of
            // <see cref="Streams"/> and is gone the moment the world enters
            // the tree and takes the Simulation's, so the world starts on a
            // seed nobody asked for. Guarded on _streams as well as the tree
            // because a world that already resolved keeps the sim's registry
            // after it leaves, and that reseed is kept.
            if (_streams == null && !IsInsideTree())
            {
                GD.PushWarning("WorldGrid: reseeded before entering the tree; "
                    + "the new seed is dropped when the world takes the "
                    + "Simulation's randomness. Set Simulation.WorldSeed.");
            }
            Streams.Reseed(value);
        }
    }

    /// <summary>Noise frequency (per cell) of the fertility field.</summary>
    [Export] public float FertilityFrequency { get; set; } = 0.04f;

    /// <summary>Noise frequency (per cell) of the rock/water mask.</summary>
    [Export] public float MaskFrequency { get; set; } = 0.06f;

    /// <summary>Mask values below this become water.</summary>
    [Export] public float WaterLevel { get; set; } = -0.3f;

    /// <summary>Mask values above this become rock.</summary>
    [Export] public float RockLevel { get; set; } = 0.34f;

    /// <summary>
    /// Grown days from sowing to the crop showing above ground. Exported with
    /// the two below because crop cadence is the tempo M4 is trying to find,
    /// and a playtest that wants to argue about it should not need a rebuild.
    /// See <see cref="CropSystem"/> for what the numbers mean.
    /// </summary>
    [Export] public int CropDaysToSprout { get; set; } = CropSystem.DefaultDaysToSprout;

    /// <summary>Grown days from sowing to ripe — cumulative, not on top of the sprout.</summary>
    [Export] public int CropDaysToRipen { get; set; } = CropSystem.DefaultDaysToRipen;

    /// <summary>
    /// Whether a harvested field has to be ploughed again before it can be
    /// sown. <b>The pacing decision M4 owns</b>: on (the default) harvest
    /// leaves stubble and every cycle costs a ploughing pass; off, harvest
    /// leaves the field ready to sow and ploughing is a once-per-field job.
    /// </summary>
    [Export] public bool StubbleNeedsPloughing { get; set; } = true;

    /// <summary>
    /// What a crop banks per tick before any factor is applied — the first term
    /// of <c>CropSystem</c>'s product, and the knob that rescales the whole
    /// game's crop tempo at once. Raising it shortens every cycle without
    /// touching what a "day of growth" means.
    /// </summary>
    [Export] public float CropBaseGrowthRate { get; set; } = CropSystem.DefaultBaseGrowthRate;

    /// <summary>
    /// The growth multiplier per season, in <see cref="Season"/> order:
    /// spring, summer, autumn, winter. Winter is 0 by default, which is what
    /// makes an over-wintered crop stall instead of ripening through the dead
    /// season — see <see cref="CropSystem.DefaultSeasonGrowth"/>.
    /// </summary>
    [Export] public float[] CropSeasonGrowth { get; set; } = CropSystem.DefaultSeasonGrowth;

    /// <summary>
    /// The water factor. <b>Pinned at 1 and inert</b>: nothing in the POC plan
    /// irrigates or rains, so this is the seam the system that eventually does
    /// writes into, not a knob with a meaning today. See
    /// <see cref="CropSystem.DefaultWaterGrowth"/>.
    /// </summary>
    [Export] public float CropWaterGrowth { get; set; } = CropSystem.DefaultWaterGrowth;

    /// <summary>
    /// Cells on a side of a fertility chunk — the granularity a field's ground
    /// is averaged at (<see cref="ChunkedFertility"/>). 1 makes it the plain
    /// per-cell mean; larger values weigh each patch of ground the field covers
    /// equally, however many of its cells landed in that patch.
    /// </summary>
    [Export] public int FertilityChunkSize { get; set; } = DefaultFertilityChunkSize;

    /// <summary>
    /// Units of produce a cell of perfect ground gives when it is cut. The
    /// other half of what fertility buys — good soil ripens sooner and yields
    /// more — and the knob that scales every harvest in the game at once.
    /// </summary>
    [Export] public float CropYieldPerCell { get; set; } = CropSystem.DefaultYieldPerCell;

    /// <summary>
    /// How many harvests off perfect ground a field's output buffer holds
    /// before it refuses the next one. <b>The backpressure knob</b>: the field
    /// stops itself when nothing has collected, and this says how much slack
    /// there is before it does. See <see cref="CropSystem.DefaultOutputHarvests"/>.
    /// </summary>
    [Export] public int FieldOutputHarvests { get; set; } = CropSystem.DefaultOutputHarvests;

    /// <summary>
    /// Units a building holds (<see cref="Structure.Storage"/>). An order of
    /// magnitude over a field's buffer, because a silo is where a farm's output
    /// piles up between sales while the fields keep cutting — a store that
    /// filled as fast as one field would make hauling a shuffle rather than a
    /// gain. One number for every building while there is one kind of building;
    /// M6's roster is what makes it per-kind.
    /// </summary>
    [Export] public int StructureStorageCapacity { get; set; } = 500;

    private GridMap _gridMap = null!;

    // Placement layer: sparse, the player owns it.
    private readonly Dictionary<Vector2I, TileType> _tiles = new();
    private readonly List<Vector2I> _roadCells = new();

    // Fields are entities, not just tiles: the tile layer says a cell is
    // farmland, these say *which* field it belongs to (see Field).
    private readonly List<Field> _fields = new();
    private readonly Dictionary<Vector2I, Field> _fieldOf = new();
    private int _fieldsCreated;

    // The stage each field's cells are currently *drawn* at, so the per-frame
    // sweep can spot the ones the sim moved and push only those. Pure view
    // bookkeeping — derived from the crop rows, never hashed, never saved, and
    // never read by anything that decides something.
    private readonly Dictionary<int, CropStage> _drawnStage = new();

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

    // Only ever used when there is no Simulation to ask; see Streams.
    private RandomStreams? _fallbackStreams;
    private RandomStream _spawnRng = null!;
    private int _machinesSpawned;

    // Machines are sim entities, so their state lives in flat arrays rather
    // than in the nodes that draw them (see MachineSystem). The registry lives
    // here for the same reason the field and structure registries do: this
    // class already owns the road queries the machines run on, and there is
    // exactly one world.
    private MachineSystem _machines = null!;

    // Crop state is entity rows too, for the reason machine state is: it is
    // walked and hashed every tick, and a List of Fields is not an order a
    // load has to reproduce. The registry of *which* cells a field covers stays
    // above; only what changes with time went down there.
    private CropSystem _crops = null!;
    private Simulation? _sim;

    public override void _Ready()
    {
        _gridMap = GetNode<GridMap>("GridMap");
        _sim = Simulation.For(this);

        // The streams are taken once and held: they are the same objects for
        // the life of the world, and a reseed rewrites them in place.
        _spawnRng = Streams.For(SpawnStreamName);
        _machines = new MachineSystem(this);
        _sim?.Register(_machines);

        // The crop schedule is read against the calendar's day, so the tempo
        // knob moves crop timing with it rather than quietly redefining how
        // long a wheat crop takes. A scene with no sim still gets a working
        // system, on the default day length.
        _crops = new CropSystem(
            _sim?.Calendar.TicksPerDay ?? GameCalendar.DefaultTicksPerDay,
            CropDaysToSprout, CropDaysToRipen, StubbleNeedsPloughing,
            CropBaseGrowthRate, CropSeasonGrowth, CropWaterGrowth, _sim?.Calendar,
            CropYieldPerCell, FieldOutputHarvests);
        _sim?.Register(_crops);

        // The world is state, not a system: it has nothing to tick, but a
        // determinism run and a save both have to see the map the player built.
        _sim?.RegisterState(this);

        // ...and it is a view as well, because a field's appearance is a
        // function of a crop stage the sim moves on its own. See Interpolate.
        _sim?.RegisterView(this);
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
        _sim?.Unregister(_crops);
        _sim?.UnregisterState(this);
        _sim?.UnregisterView(this);
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
                // Two situations arrive here and only one of them is a defect.
                // Out of the tree is the transient case this lazy resolve
                // exists for: WorldSeed is readable before the world is built,
                // nothing is wrong, and the registry below is dropped again on
                // entry — so the warning would be a false alarm, and its text
                // ("in the tree") untrue besides. In the tree with no
                // Simulation is the broken scene, and the only one worth a
                // word. The lossy half of a detached access — a reseed that
                // gets dropped — warns from the WorldSeed setter instead.
                if (IsInsideTree())
                {
                    GD.PushWarning("WorldGrid: no Simulation in the tree; "
                        + "world randomness falls back to a private registry.");
                }
                // Deliberately not cached into _streams: an access from outside
                // the tree must not decide the world's randomness for the rest
                // of the run. Caching it here would leave _Ready building the
                // world from a registry the Simulation does not own, and so
                // outside SimStateHash — a world whose rolls are invisible to
                // the determinism harness, which would still happily pass.
                // The caveat that remains: a reseed made before the world
                // enters the tree lands on this registry and is dropped.
                return _fallbackStreams ??= new RandomStreams();
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

    /// <summary>
    /// The one soil-quality number a whole field grows at: the mean of its
    /// <b>chunk</b> means, where a chunk is a
    /// <see cref="FertilityChunkSize"/>-cell square of the world.
    ///
    /// <b>Why chunks rather than the plain average of the cells.</b> A field is
    /// a region of ground, and what makes one worth farming is the patches it
    /// covers, not how many cells of each it happens to contain: averaging by
    /// cell lets a field lean its rate on whichever patch it clipped most of,
    /// so the same two patches give a different answer depending on where the
    /// drag started. Weighing each chunk equally makes the answer a property of
    /// the ground the field spans. It is also the granularity later work wants
    /// — #28's yield and M9's hazards are per patch, not per cell — and setting
    /// the size to 1 collapses it back to the exact cell mean, which is the
    /// knob's "off".
    ///
    /// Chunks are aligned to the <b>world origin</b>, not to the field's own
    /// corner, so the same ground always falls in the same chunks whoever marks
    /// it. The cells are sorted into ascending chunk order and summed in that
    /// order, never enumerated out of a dictionary: float addition is not
    /// associative, so an order that depended on insertion history would be a
    /// determinism bug that only showed up as a crop ripening a tick late. The
    /// sort key carries each cell's index in the input, so cells sharing a chunk
    /// keep the order they were handed in and no two keys can tie.
    ///
    /// <b>Sorted rather than bucketed into an array over the chunk bounding
    /// box</b>, which is how this read first. The cell list is unvalidated — dev
    /// and scenario code marks fields directly, and cells off the map are legal
    /// input that simply weigh 0 — so two cells far apart size that array by the
    /// <i>gap</i> between them rather than by how many cells there are: an
    /// allocation with no upper bound, reached through a width × height multiply
    /// that wraps to a negative length before it gets there. Sorting is bounded
    /// by the input, and the walk it produces is the same one, in the same
    /// order, to the bit.
    ///
    /// <b>Trap:</b> a row is given this number when the field is marked and
    /// when its cells change (see <c>ReleaseFieldCell</c>) — never per tick. So
    /// moving <see cref="FertilityChunkSize"/> at runtime re-aggregates the
    /// fields marked after it, not the ones already standing: a field re-measures
    /// its ground at the size it was marked at
    /// (<see cref="Field.FertilityChunkSize"/>), so bulldozing a corner off it
    /// cannot also silently re-chunk it at whatever the knob has moved to since.
    /// </summary>
    public float ChunkedFertility(IReadOnlyList<Vector2I> cells) =>
        ChunkedFertility(cells, EffectiveFertilityChunkSize);

    /// <summary>
    /// The aggregate at a chunk size <i>given</i> rather than read off the
    /// export — what a standing field re-measures its ground at. See the trap
    /// on <see cref="ChunkedFertility(IReadOnlyList{Vector2I})"/>.
    /// </summary>
    private float ChunkedFertility(IReadOnlyList<Vector2I> cells, int size)
    {
        if (cells.Count == 0)
        {
            return 0f;
        }

        // Chunk row, chunk column, then where the cell came in the input: the
        // first two group and order the chunks, the third breaks every tie, so
        // the summation order is a function of the cells alone and not of how
        // the sort happened to move equal keys around.
        var keys = new (int ChunkY, int ChunkX, int Index)[cells.Count];
        for (int i = 0; i < cells.Count; i++)
        {
            Vector2I chunk = ChunkOf(cells[i], size);
            keys[i] = (chunk.Y, chunk.X, i);
        }
        Array.Sort(keys);

        float total = 0f;
        int chunks = 0;
        int start = 0;
        while (start < keys.Length)
        {
            float sum = 0f;
            int end = start;
            while (end < keys.Length
                && keys[end].ChunkY == keys[start].ChunkY
                && keys[end].ChunkX == keys[start].ChunkX)
            {
                sum += GetFertility(cells[keys[end].Index]);
                end++;
            }
            total += sum / (end - start);
            chunks++;
            start = end;
        }
        // Every cell lands in exactly one chunk, so a non-empty region always
        // covered at least one of them.
        return total / chunks;
    }

    /// <summary>
    /// <see cref="FertilityChunkSize"/> as the aggregation may actually use it.
    /// An export is a number a playtest can leave at 0 or below; a chunk is at
    /// least one cell, and one cell is the knob's off position rather than an
    /// error worth refusing a field over.
    /// </summary>
    private int EffectiveFertilityChunkSize => Math.Max(1, FertilityChunkSize);

    /// <summary>
    /// The chunk a cell falls in. Floor division, not C#'s truncation, or the
    /// chunks either side of an axis would be half-width and the origin's
    /// would be double.
    /// </summary>
    private static Vector2I ChunkOf(Vector2I cell, int size) =>
        new(FloorDiv(cell.X, size), FloorDiv(cell.Y, size));

    // Corrected by the remainder rather than by negating a shifted dividend:
    // the shift overflows near int.MinValue, and this is handed raw cell
    // coordinates from callers that never validated them.
    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }

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
    /// The field with that <see cref="Field.Id"/>, or null once it has lost
    /// its last cell — how an order refers to a field without holding the
    /// object or a cell that might not be its any more. Linear, the same
    /// trade <see cref="GetStructure(int)"/> makes: fields are few.
    /// </summary>
    public Field? GetField(int id)
    {
        foreach (Field field in _fields)
        {
            if (field.Id == id)
            {
                return field;
            }
        }
        return null;
    }

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
        // The crop row is opened before the field so the handle can be
        // constructor input: a Field is never observable without one. The
        // ground it stands on — how good it is and how much of it there is —
        // is aggregated here and pushed down, because the sim has no idea what
        // a cell is.
        // Read once and carried on the field: the size the ground was measured
        // at is part of what the field is, not a knob it re-reads later.
        int chunkSize = EffectiveFertilityChunkSize;
        var field = new Field(
            _fieldsCreated, $"Field {_fieldsCreated}", cells,
            _crops.Create(_fieldsCreated, ChunkedFertility(cells, chunkSize), cells.Count),
            chunkSize);
        _fields.Add(field);
        foreach (Vector2I cell in cells)
        {
            ReleaseFieldCell(cell);
            SetTile(cell, TileType.Field);
            _fieldOf[cell] = field;
        }

        // The SetTile above ran before the cell → field lookup existed, so it
        // could only draw the "no crop row" tile. Now that the field is
        // reachable, draw it at the stage it opened on — a field must look
        // right the instant it is marked, not on the next rendered frame,
        // because a stepped headless run has no frames at all.
        DrawFieldStage(field);
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
            _structuresCreated, $"Structure {_structuresCreated}", cells,
            StructureStorageCapacity);
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
        if (field.CellCount > 0)
        {
            // The aggregate is only true of the cells it was taken over, so a
            // field that lost its best corner grows slower and yields less from
            // now on, and its output buffer holds less. Pushed on change rather
            // than read per tick: the ground itself never moves, and the sim
            // must be able to hash the numbers without asking the world.
            _crops.SetGround(
                field.Crop,
                ChunkedFertility(field.Cells, field.FertilityChunkSize),
                field.CellCount);
            return;
        }

        _fields.Remove(field);
        _drawnStage.Remove(field.Id);
        // The row goes with the field, which is what makes every handle to it
        // answer NoSuchField rather than address whatever field is marked into
        // the recycled slot next.
        _crops.Destroy(field.Crop);
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
                // The tile layer only says "farmland"; what is standing on it
                // is the crop row's business, so the view reads through to it.
                return GetField(cell) is { } field
                    ? FieldStageItemFirst + (int)_crops.StageOf(field.Crop)
                    : FieldItem;
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

    /// <summary>
    /// <b>How a crop stage the sim moved reaches the GridMap.</b> Every other
    /// tile changes because something called <see cref="SetTile"/>; a crop
    /// ripens because time passed, and nothing calls anything. So the world
    /// takes the <see cref="ISimView"/> half as well and sweeps its fields once
    /// per rendered frame, comparing each against the stage it last drew.
    ///
    /// A poll, and deliberately not a callback out of <c>CropSystem</c>: an
    /// event raised inside <see cref="ISimSystem.Tick"/> would run the view
    /// half-way through a tick, and it would point the dependency from the sim
    /// at the renderer — the exact direction the ownership rule forbids. This
    /// way the sim knows nothing, and the compare is one enum per field per
    /// frame, over a list a farm keeps in the dozens.
    ///
    /// <paramref name="alpha"/> is unused: a tile is a discrete state, so there
    /// is nothing between two of them to blend.
    /// </summary>
    public void Interpolate(float alpha)
    {
        for (int i = 0; i < _fields.Count; i++)
        {
            Field field = _fields[i];
            if (!_drawnStage.TryGetValue(field.Id, out CropStage drawn)
                || drawn != _crops.StageOf(field.Crop))
            {
                DrawFieldStage(field);
            }
        }
    }

    /// <summary>
    /// Pushes every cell of one field at its current stage and records what was
    /// drawn, so the sweep above has something to compare against.
    /// </summary>
    private void DrawFieldStage(Field field)
    {
        _drawnStage[field.Id] = _crops.StageOf(field.Crop);
        IReadOnlyList<Vector2I> cells = field.Cells;
        for (int i = 0; i < cells.Count; i++)
        {
            RefreshCell(cells[i]);
        }
    }

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
    /// Somewhere legal to leave a vehicle: a road cell with no machine already
    /// standing on it. False when there is no road at all, or when every cell
    /// of it is taken — a refusal a buyer has to be able to hear, since a farm
    /// with nowhere to park should not be charged for a truck it cannot
    /// receive.
    ///
    /// One random draw picks where to start looking and the search then walks
    /// the road list forward, wrapping. Retrying random cells instead would
    /// make the number of draws depend on how full the road is, which is a
    /// determinism trap: two runs that parked the same vehicles in the same
    /// places would leave the stream in different positions.
    /// </summary>
    public bool TryFindParking(RandomStream rng, out Vector2I cell)
    {
        cell = Vector2I.Zero;
        if (_roadCells.Count == 0)
        {
            return false;
        }

        int start = rng.NextInt(_roadCells.Count);
        for (int i = 0; i < _roadCells.Count; i++)
        {
            Vector2I candidate = _roadCells[(start + i) % _roadCells.Count];
            if (_machines.Occupancy.At(candidate).Count == 0)
            {
                cell = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The road cell an order parks a vehicle on to work a field or a
    /// building: the first cell of the footprint with a road neighbour,
    /// walked in footprint order so the answer never depends on a
    /// dictionary's enumeration. Null when nothing in the footprint touches
    /// road at all — <see cref="Field"/>s are legal without one
    /// (<c>## Fields</c>), so this is the ordinary shape of
    /// <see cref="OrderBlock.NoRoadAccess"/> rather than a bug.
    ///
    /// <b>The vehicle stops here, not on the footprint itself.</b> A
    /// structure's cell is never road and a field's is deliberately not
    /// required to be, so "arrived" for an order means reaching this cell —
    /// the same neighbour <see cref="PlacementRules.HasRoadAccess"/> already
    /// asks the structure tool to require, asked here for the actual cell
    /// rather than a yes/no. Entering the footprint itself is #36's to add.
    /// </summary>
    public Vector2I? FindRoadAccess(IReadOnlyList<Vector2I> cells)
    {
        foreach (Vector2I cell in cells)
        {
            foreach (Vector2I step in Steps4)
            {
                Vector2I neighbor = cell + step;
                if (IsRoad(neighbor))
                {
                    return neighbor;
                }
            }
        }
        return null;
    }

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

    // The same 4-neighbourhood PlacementRules.Neighbors uses for "touches a
    // road": sharing an edge, not a corner. A second array rather than a
    // slice of Steps above, so this rule cannot start including diagonals the
    // day somebody reorders that one.
    private static readonly Vector2I[] Steps4 =
        [Vector2I.Right, Vector2I.Left, Vector2I.Up, Vector2I.Down];

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
            // Which row the field's crop state lives in. Not derivable from
            // either side: the row knows the field id and the field knows the
            // slot, and a load that paired them differently would be a
            // different world however identical both registries looked.
            member.Write(field.Crop.Index);
            member.Write(field.Crop.Generation);
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
            // What it is holding. A building is not an entity row yet, so its
            // store is hashed here with the rest of it rather than by a system
            // walking slots — the line moves with the object when M5 or M6
            // gives buildings state worth ticking.
            structure.Storage.HashState(member);
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
    /// Every field's crop state, in flat arrays. Reached from a cell through
    /// <c>GetField(cell).Crop</c>; ticked by the <see cref="Simulation"/>.
    /// </summary>
    public CropSystem Crops => _crops;

    /// <summary>
    /// Puts one machine of the given kind on the road network: a row in
    /// <see cref="Machines"/> for the sim, and a node bound to it for the view.
    /// Where it parks comes off the world's spawn stream, so a given sequence
    /// of purchases is reproducible for a world seed. Null when there is no
    /// machine scene assigned or nowhere legal to leave it.
    ///
    /// <b>The unvalidated door</b>, the way <see cref="BuildRoadLine"/> is for
    /// road: it charges nothing and asks nobody, which is what a dev key, a
    /// scenario and a test all want. <c>Fleet.TryBuy</c> is the one the player
    /// goes through.
    /// </summary>
    public Machine? SpawnMachine(MachineKind kind)
    {
        if (MachineScene == null || !TryFindParking(_spawnRng, out Vector2I cell))
        {
            return null;
        }

        // The node is instanced first so the kind can be written onto its
        // exports and read back out of them: Speed, TurnSpeed and
        // CargoCapacity are spawn input to the arrays, and the sim never looks
        // at the node again.
        var machine = MachineScene.Instantiate<Machine>();
        machine.ApplyKind(kind);
        Vector3 spawn = CellToWorld(cell) + Vector3.Up * Machine.DeckHeight;
        EntityId entity = _machines.Spawn(
            spawn, machine.Kind, machine.Speed, machine.TurnSpeed, machine.CargoCapacity);
        machine.Setup(_machines, entity);
        machine.Position = spawn;
        machine.Name = $"{MachineKinds.Name(kind)}{_machinesSpawned}";
        AddChild(machine);
        _machinesSpawned++;
        return machine;
    }

    /// <summary>
    /// The next machine in the roster: tractor, harvester, truck, round again.
    /// What a dev key and a scenario's <see cref="MachineCount"/> want — a
    /// vehicle of each sort without having to say which — and it cycles off the
    /// hashed spawn counter, so the sequence is part of the world rather than
    /// of how the caller was written.
    /// </summary>
    public Machine? SpawnMachine() => SpawnMachine(MachineKinds.At(_machinesSpawned));
}
