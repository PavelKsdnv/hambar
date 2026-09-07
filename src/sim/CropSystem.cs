using System;
using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// What is standing on a field right now. The stage is the whole of a field's
/// crop state that anything outside the sim asks about: the view draws it, the
/// inspector names it, and every operation's precondition is a test on it.
///
/// <see cref="Stubble"/> is the post-harvest stage rather than a return to
/// <see cref="Fallow"/>, which is a pacing choice — see
/// <see cref="CropSystem"/> for why, and for the tunable that undoes it.
/// </summary>
public enum CropStage
{
    /// <summary>Marked farmland nobody has worked yet. Where a new field starts.</summary>
    Fallow,

    /// <summary>Turned over and ready to take seed.</summary>
    Ploughed,

    /// <summary>Seed is in the ground and has not come up yet.</summary>
    Sown,

    /// <summary>Up, and putting on growth.</summary>
    Growing,

    /// <summary>Ripe. It stays here until somebody takes it off.</summary>
    Harvestable,

    /// <summary>Cut, and not sowable again until it has been ploughed back in.</summary>
    Stubble,
}

/// <summary>
/// The three things that can be <i>done</i> to a field, as opposed to the two
/// transitions that merely happen with time.
///
/// This enum is the vocabulary M5's machine orders are written in: an order
/// names an operation and a field, and <see cref="CropSystem.Apply"/> is the
/// single door it goes through — the dev key and any test take the same one, so
/// there is no path into the state machine that skips a precondition.
/// </summary>
public enum CropOperation
{
    Plough,
    Sow,
    Harvest,
}

/// <summary>
/// What came of asking for a <see cref="CropOperation"/>. Anything but
/// <see cref="Ok"/> means <b>nothing changed</b>.
///
/// A refusal is a return value and not a warning on purpose: M5 will ask "can
/// this field be sown" far more often than it sows, and a log line per refusal
/// would bury the log the first time a scheduler polls. The caller that wanted
/// noise makes it — the dev key prints its result.
/// </summary>
public enum CropOpResult
{
    Ok,

    /// <summary>The handle does not address a live field — bulldozed, or stale.</summary>
    NoSuchField,

    /// <summary>The field is not at a stage this operation is legal from.</summary>
    WrongStage,

    /// <summary>
    /// The stage was right, but the yield would not fit in the field's output
    /// buffer, so the crop is <b>still standing</b>. Harvest only. This is
    /// backpressure, not an error: it clears the moment something collects.
    /// </summary>
    OutputFull,
}

/// <summary>
/// Every field's crop state, in flat arrays indexed by
/// <see cref="EntityId.Index"/> — one row per <c>Field</c>. This is where a
/// field stopped being a named region and became something the sim ticks.
///
/// <b>Fields are entity rows now, not fields on the <c>Field</c> object.</b>
/// Crop state is walked every tick and hashed every tick, and both want the
/// ascending, history-independent slot order <see cref="EntityStore"/> gives;
/// the alternative — an enum and a float on <c>Field</c>, folded into
/// <c>WorldGrid</c>'s registry hash — would have worked today and put the
/// milestone that adds growth factors, output buffers and jobs back on a walk
/// over a <c>List</c> whose order a load does not have to reproduce.
/// <c>Field</c> keeps the cells and the name and holds a <c>Crop</c> handle;
/// everything that changes with time is here.
///
/// <b>The state machine.</b> Two of the transitions are <i>work</i> and three
/// are <i>time</i>:
/// <code>
///   Fallow ──plough──▸ Ploughed ──sow──▸ Sown ┄sprout┄▸ Growing ┄ripen┄▸ Harvestable
///      ▲                                                                      │
///      └──────────────────── Stubble ◂──────────harvest───────────────────────┘
/// </code>
/// The work transitions are <see cref="Apply"/>; the timed ones happen in
/// <see cref="Tick"/> once <see cref="GrowthOf"/> passes a threshold. An
/// operation asked for from the wrong stage is <b>refused</b> and changes
/// nothing — that is the point of the enum result, because M5's whole job is
/// issuing these out of order until it learns not to, and an operation that
/// silently succeeded would leave a field in a state no sequence of real work
/// could have produced.
///
/// <b>Harvest leaves stubble, and that needs ploughing again.</b> The
/// alternative — harvest dropping the field straight back to a sowable state —
/// makes ploughing a thing the player does once per field ever, and M5 exists
/// to program a <i>repeating</i> round of machine work; a cycle with only one
/// recurring job in it is not much of a program. It also costs a real pass of
/// machine time per cycle, which is the pacing lever the milestone is trying to
/// find. Set <c>stubbleNeedsPloughing</c> false and harvest lands on
/// <see cref="CropStage.Ploughed"/> instead, so a playtest that finds the extra
/// pass tedious can drop it without a rebuild.
///
/// <b>Growth is counted in ticks, never in <c>dt</c> seconds</b>, for the
/// reason <see cref="GameCalendar"/> counts a day in ticks: a rate multiplied
/// by a real-time delta would make how long a crop took depend on the tick rate
/// it was watched at. So <see cref="GrowthOf"/> is in <b>grown ticks</b> and a
/// full-rate tick adds exactly 1 — which is also why the accumulator is ticks
/// and not the day the tunables are authored in. A day's worth of adding
/// 1/600 lands a hair either side of 1.0 in single precision, and a crop that
/// ripens a tick early or late depending on which way the last rounding fell is
/// a bug nobody would find; adding whole numbers is exact until 2^24 ticks,
/// which is far past the point a crop was harvested. It is a float because the
/// growth factors below are fractional multipliers on that 1.
///
/// <b>Growth per tick is a product, and that is the whole design.</b> A tick
/// adds <c>base x fertility x season x water</c>, so a factor at zero
/// <i>stalls</i> the crop where it stands instead of slowing it: an untended
/// field on dead ground, or a winter one, never quietly finishes, which is what
/// keeps M5's labour worth programming. Summing the factors, or averaging them,
/// would let a good one carry a zero — the one behaviour the milestone asked
/// for. Every factor is a constructor input off an <c>[Export]</c>
/// (<c>WorldGrid</c>), because M4's real question is the cadence and no answer
/// to it should need a rebuild.
///
/// <b>Not a Node</b> and, unlike <c>MachineSystem</c>, it holds no world
/// either: nothing here knows what a cell is. <b>Fertility is a column, handed
/// in at <see cref="Create"/></b> — the world aggregates it over the field's
/// cells (<c>WorldGrid.ChunkedFertility</c>) and pushes the number down, rather
/// than this class reaching up for a cell it has no business knowing about. The
/// season is the exception it is allowed: <see cref="GameCalendar"/> is sim
/// state, not world state, so the date can be read here directly.
///
/// <b>A harvest deposits a stack; it does not add to a total.</b> The yield
/// goes into the field's own <see cref="ItemBuffer"/> as so many units of
/// <see cref="HarvestItem"/>, and if it does not fit the harvest is
/// <b>refused</b> and the crop stays standing (<see cref="CropOpResult.OutputFull"/>).
/// Refused rather than spilled, because grain that evaporates when nobody
/// collects it is the logistics game deleting itself; and rather than
/// part-filled, because a partly cut field is a state the machine above does
/// not have and inventing one to model a case nothing can yet reach is guessing
/// at M5's shape. See <see cref="ItemBuffer"/> for why the capacity exists
/// before anything consumes from it.
///
/// <b>What a field yields is a property of its ground</b>, not of the growth it
/// happened to bank: <see cref="YieldPerCell"/> x area x fertility, floored.
/// Banked growth is the wrong number — it stops accumulating at ripe, so it is
/// the same for every field that finished — while the ground gives fertility a
/// second, visible consequence: good soil ripens sooner <i>and</i> yields more.
/// The season deliberately stays out of it: winter makes a crop take longer, not
/// come up short, which keeps "when to sow" a scheduling decision rather than a
/// yield penalty the player cannot see coming. <see cref="ProjectedYield"/> is
/// the same computation the harvest itself runs, so the number #30's inspector
/// shows before the cut cannot drift from the one that lands in the buffer.
///
/// <b>Area is a column, pushed down like fertility</b> — a count of cells, not
/// a set of them, so nothing here learns what a cell is.
/// </summary>
public sealed class CropSystem : ISimSystem, IHashableState
{
    /// <summary>The name this system's rows are filed under in a state hash.</summary>
    public const string StateSourceName = "crops";

    /// <summary>
    /// Days from sowing to the crop showing above ground. One, so the sown
    /// stage is short enough to read as an event rather than a phase.
    /// </summary>
    public const int DefaultDaysToSprout = 1;

    /// <summary>
    /// Days from sowing to ripe — cumulative, not on top of
    /// <see cref="DefaultDaysToSprout"/>, so "days remaining" is one
    /// subtraction. Five of a twelve-day season leaves room for two full wheat
    /// cycles inside one season with the ploughing and sowing passes in
    /// between, which is the cadence the milestone is trying to feel out.
    /// </summary>
    public const int DefaultDaysToRipen = 5;

    /// <summary>
    /// Grown ticks a perfect tick banks: 1, so the schedule above reads in
    /// plain days and every other factor is a fraction of a day's work. Moving
    /// it rescales the whole game's crop tempo without touching the stage
    /// thresholds — which is the knob to reach for first when a playtest says
    /// the cycle is too slow.
    /// </summary>
    public const float DefaultBaseGrowthRate = 1f;

    /// <summary>
    /// <b>Water is not modelled.</b> There is no irrigation system anywhere in
    /// the POC plan, so the factor is present, pinned at 1 and deliberately
    /// inert: the alternative was leaving it out and having whoever adds
    /// rainfall re-open the product, its exports and its hash. Costs one
    /// multiply by a constant; buys the seam.
    /// </summary>
    public const float DefaultWaterGrowth = 1f;

    /// <summary>
    /// Growth multiplier per season, indexed by <see cref="Arable.Season"/>:
    /// spring and summer full, autumn half, <b>winter nothing</b>. A fresh
    /// array each read, because an <c>[Export]</c> default the inspector can
    /// edit must not be a shared instance.
    ///
    /// Winter at 0 is the product's point made in the default configuration —
    /// a crop left in the ground over winter stalls until spring rather than
    /// ripening through it, so the sowing date is a decision. It is also the
    /// number to soften first if playtests find the dead season too long.
    /// </summary>
    public static float[] DefaultSeasonGrowth => [1f, 1f, 0.5f, 0f];

    /// <summary>
    /// Units a cell of perfect ground yields when it is cut. Ten, so a modest
    /// field is tens of units rather than a number a player has to read as a
    /// fraction, and so halving the soil is visibly half the harvest.
    /// </summary>
    public const float DefaultYieldPerCell = 10f;

    /// <summary>
    /// How many <b>perfect</b> harvests a field's output buffer holds — the
    /// capacity, in units of the field's own best cut. Two, so the first
    /// harvest always fits however good the ground turned out to be, and the
    /// field stops itself only when a second (or, on poor ground, a fourth)
    /// has piled up with nobody collecting. Sizing it against the field rather
    /// than fixing one number for every field is what stops a large field being
    /// unharvestable and a small one being effectively unlimited.
    /// </summary>
    public const int DefaultOutputHarvests = 2;

    /// <summary>Slots the arrays start at; they grow by doubling from there.</summary>
    private const int InitialCapacity = 16;

    private readonly EntityStore _entities = new();

    private readonly CropStage _postHarvest;
    private readonly float _baseGrowthRate;
    private readonly float[] _seasonGrowth;
    private readonly float _waterGrowth;
    private readonly float _yieldPerCell;
    private readonly int _outputHarvests;

    // What comes off a field. Configuration and not a column, because there is
    // no crop-kind column either: one crop, one product. It becomes a column on
    // the tick the game grows a second crop, and every caller here already
    // reads it per row.
    private readonly ItemType _harvestItem;

    // The date, for the season factor only. Held rather than copied per tick
    // because a cached season is a second copy that a date jump would leave
    // stale; null is a system with no calendar (a bare test one), which grows
    // as though every season were full.
    private readonly GameCalendar? _calendar;

    // Parallel component arrays, all sized to _entities.SlotCount. Fertility
    // is a column and not a lookup because src/sim/ knows nothing of cells;
    // #28's output buffer joins these the same way.
    private CropStage[] _stage = [];
    private float[] _growth = [];
    private float[] _fertility = [];
    private int[] _area = [];
    private ItemBuffer[] _output = [];
    private int[] _owner = [];

    /// <param name="ticksPerDay">
    /// The day length the schedule below is read against — the calendar's, so
    /// moving the tempo knob moves crop timing with it instead of silently
    /// changing how many days a wheat crop takes.
    /// </param>
    /// <param name="stubbleNeedsPloughing">
    /// False makes harvest leave the field ready to sow again. See the class
    /// note for why the default is true.
    /// </param>
    /// <param name="baseGrowthRate">
    /// The first factor of the product: what a tick banks on perfect ground in
    /// a perfect season.
    /// </param>
    /// <param name="seasonGrowth">
    /// One multiplier per <see cref="Arable.Season"/>, in enum order. Null
    /// takes <see cref="DefaultSeasonGrowth"/>; a wrong-length array is
    /// repaired rather than thrown over, because it arrives from an inspector.
    /// </param>
    /// <param name="waterGrowth">Pinned at 1 — see <see cref="DefaultWaterGrowth"/>.</param>
    /// <param name="calendar">
    /// The date the season factor is read off, per tick. Null means no season
    /// at all — every season full — which is what a system built without a sim
    /// (a test, a tool) gets, rather than an arbitrary date to grow against.
    /// </param>
    /// <param name="yieldPerCell">Units a cell of perfect ground gives when cut.</param>
    /// <param name="outputHarvests">
    /// Perfect harvests a field's output buffer holds — see
    /// <see cref="DefaultOutputHarvests"/>. Zero is a field that can never be
    /// harvested, which is a legal thing to configure and an obvious one to
    /// notice.
    /// </param>
    /// <param name="harvestItem">
    /// The good a cut field produces. Null takes grain; it is an argument at
    /// all so that a second crop is a second system rather than a rewrite.
    /// </param>
    public CropSystem(
        int ticksPerDay,
        int daysToSprout = DefaultDaysToSprout,
        int daysToRipen = DefaultDaysToRipen,
        bool stubbleNeedsPloughing = true,
        float baseGrowthRate = DefaultBaseGrowthRate,
        float[]? seasonGrowth = null,
        float waterGrowth = DefaultWaterGrowth,
        GameCalendar? calendar = null,
        float yieldPerCell = DefaultYieldPerCell,
        int outputHarvests = DefaultOutputHarvests,
        ItemType? harvestItem = null)
    {
        TicksPerDay = Math.Max(1, ticksPerDay);
        DaysToSprout = Math.Max(0, daysToSprout);
        DaysToRipen = Math.Max(DaysToSprout, daysToRipen);
        if (daysToRipen < daysToSprout)
        {
            // Inspector input, so repair and complain rather than take the
            // scene down — but say so, because a ripen date before the sprout
            // date means the crop skips a stage inside one tick and nobody
            // watching the field would know why.
            GD.PushWarning($"CropSystem: DaysToRipen ({daysToRipen}) is before DaysToSprout "
                + $"({daysToSprout}); clamped to {DaysToRipen}.");
        }
        _postHarvest = stubbleNeedsPloughing ? CropStage.Stubble : CropStage.Ploughed;

        // A negative factor would run a crop backwards through its own stages;
        // clamping at zero keeps the worst any factor can do a stall.
        _baseGrowthRate = Math.Max(0f, baseGrowthRate);
        _waterGrowth = Math.Max(0f, waterGrowth);
        _seasonGrowth = VetSeasonGrowth(seasonGrowth);
        _calendar = calendar;

        // A negative yield or capacity is meaningless rather than merely
        // extreme, so both clamp at nothing: an unharvestable field is a
        // configuration, an inverted one is not a state anything downstream
        // could read.
        _yieldPerCell = Math.Max(0f, yieldPerCell);
        _outputHarvests = Math.Max(0, outputHarvests);
        _harvestItem = harvestItem ?? ItemTypes.Grain;
    }

    /// <summary>
    /// Sizes the season table to the seasons that exist and clamps it, the way
    /// <c>Simulation</c> vets its speed ladder: repair and complain, never take
    /// the scene down over a number typed into an inspector. A short array is
    /// padded with full growth so a half-filled one grows rather than stalls —
    /// a silent world-wide stall is the harder bug of the two to spot.
    /// </summary>
    private static float[] VetSeasonGrowth(float[]? rates)
    {
        float[] source = rates ?? DefaultSeasonGrowth;
        if (source.Length != GameCalendar.SeasonsPerYear && rates != null)
        {
            GD.PushWarning($"CropSystem: SeasonGrowth has {source.Length} entries, expected "
                + $"{GameCalendar.SeasonsPerYear}; resized, padding any gap with full growth.");
        }

        var vetted = new float[GameCalendar.SeasonsPerYear];
        for (int i = 0; i < vetted.Length; i++)
        {
            vetted[i] = i < source.Length ? Math.Max(0f, source[i]) : 1f;
        }
        return vetted;
    }

    /// <summary>Ticks that make one day here — the calendar's, copied at construction.</summary>
    public int TicksPerDay { get; }

    /// <summary>Days of full-rate growth from sowing to the crop coming up.</summary>
    public int DaysToSprout { get; }

    /// <summary>Days of full-rate growth from sowing to ripe. Cumulative, and never less than <see cref="DaysToSprout"/>.</summary>
    public int DaysToRipen { get; }

    /// <summary>The sprout threshold in the unit <see cref="GrowthOf"/> is measured in.</summary>
    public int TicksToSprout => DaysToSprout * TicksPerDay;

    /// <summary>The ripe threshold in the unit <see cref="GrowthOf"/> is measured in.</summary>
    public int TicksToRipen => DaysToRipen * TicksPerDay;

    /// <summary>The stage a harvest leaves behind — the pacing tunable.</summary>
    public CropStage PostHarvestStage => _postHarvest;

    /// <summary>Grown ticks a tick banks with every factor at full — the first term of the product.</summary>
    public float BaseGrowthRate => _baseGrowthRate;

    /// <summary>The season table, indexed by <see cref="Arable.Season"/>. Always four long.</summary>
    public IReadOnlyList<float> SeasonGrowth => _seasonGrowth;

    /// <summary>The water factor. 1, and inert — see <see cref="DefaultWaterGrowth"/>.</summary>
    public float WaterGrowth => _waterGrowth;

    /// <summary>Units a cell of perfect ground yields when it is cut.</summary>
    public float YieldPerCell => _yieldPerCell;

    /// <summary>Perfect harvests a field's output buffer holds.</summary>
    public int OutputHarvests => _outputHarvests;

    /// <summary>The good a harvest deposits. Grain, until there is a second crop.</summary>
    public ItemType HarvestItem => _harvestItem;

    /// <summary>The season the growth rate is currently read against, or null with no calendar.</summary>
    public Season? CurrentSeason => _calendar?.Season;

    /// <summary>
    /// The season's multiplier right now — 1 for a system with no calendar,
    /// which is the whole of what "no season" means here.
    /// </summary>
    public float SeasonFactor =>
        _calendar == null ? 1f : _seasonGrowth[(int)_calendar.Season];

    /// <summary>Fields with a crop row right now.</summary>
    public int Count => _entities.Count;

    /// <summary>Slots ever used — the exclusive bound of a walk over the arrays.</summary>
    public int SlotCount => _entities.SlotCount;

    public bool IsAlive(EntityId id) => _entities.IsAlive(id);

    public bool IsAliveSlot(int slot) => _entities.IsAliveSlot(slot);

    /// <summary>The handle for a slot, for turning a walk into an id.</summary>
    public EntityId IdAt(int slot) => _entities.IdAt(slot);

    /// <summary>
    /// Opens a crop row for a field, at <see cref="CropStage.Fallow"/>.
    /// <paramref name="fieldId"/> is the <c>Field.Id</c> that owns it, kept so a
    /// walk over the rows can name the field it is drawing or reporting on
    /// without a reverse index.
    ///
    /// <paramref name="fertility"/> (0..1) and <paramref name="area"/> are
    /// <b>input, not a lookup</b>: the world has already averaged the ground
    /// the field covers and counted its cells, and hands both answers down, so
    /// this class never learns what a cell is. They default to one cell of
    /// perfect ground, which is what a system built without a world grows on.
    /// </summary>
    public EntityId Create(int fieldId, float fertility = 1f, int area = 1)
    {
        EntityId id = _entities.Create();
        EnsureCapacity(_entities.SlotCount);
        int i = id.Index;

        // Every column, because a recycled slot still holds the last field's.
        _stage[i] = CropStage.Fallow;
        _growth[i] = 0f;
        _fertility[i] = Math.Clamp(fertility, 0f, 1f);
        _area[i] = Math.Max(0, area);
        // A fresh buffer, never the recycled slot's one cleared out: an
        // EntityId's generation invalidates a stale *handle*, and nothing
        // invalidates a stale reference to the object behind it. Reusing the
        // instance would quietly show the last field's holder this field's
        // grain. One allocation per field marked is nothing.
        _output[i] = new ItemBuffer(CapacityFor(_area[i]));
        _owner[i] = fieldId;
        return id;
    }

    /// <summary>
    /// Closes a field's crop row. False for a stale handle, so closing twice
    /// cannot free whatever moved into the slot in between.
    /// </summary>
    public bool Destroy(EntityId id) => _entities.Destroy(id);

    /// <summary>The field's stage, or <see cref="CropStage.Fallow"/> for a dead handle.</summary>
    public CropStage StageOf(EntityId id) =>
        _entities.IsAlive(id) ? _stage[id.Index] : CropStage.Fallow;

    /// <summary>
    /// Growth banked since sowing, in <b>grown ticks</b> — compare it against
    /// <see cref="TicksToSprout"/> and <see cref="TicksToRipen"/>, or divide by
    /// <see cref="TicksPerDay"/> for something to show a player. Zero outside
    /// the growing stages: every operation resets it, because a field only ever
    /// grows the crop currently in it.
    /// </summary>
    public float GrowthOf(EntityId id) => _entities.IsAlive(id) ? _growth[id.Index] : 0f;

    /// <summary>
    /// The soil quality the field grows on, 0..1 — the aggregate the world
    /// handed in, not a cell's. Zero is a stall, not slow going.
    /// </summary>
    public float FertilityOf(EntityId id) => _entities.IsAlive(id) ? _fertility[id.Index] : 0f;

    /// <summary>
    /// How much ground the field covers, in cells — a <i>count</i>, never the
    /// cells themselves. It is what makes a big field worth more than a small
    /// one at the same soil, and it sizes the output buffer.
    /// </summary>
    public int AreaOf(EntityId id) => _entities.IsAlive(id) ? _area[id.Index] : 0;

    /// <summary>
    /// The field's output buffer — where its harvests land and where M5 comes
    /// to collect. Null for a dead handle, which is the honest answer: there is
    /// no buffer, as opposed to an empty one.
    /// </summary>
    public ItemBuffer? OutputOf(EntityId id) =>
        _entities.IsAlive(id) ? _output[id.Index] : null;

    /// <summary>
    /// What harvesting this field would put in its buffer, or 0 while there is
    /// nothing in the ground. <b>The same computation the harvest runs</b>, so
    /// the number the inspector shows before the cut is the number that lands
    /// in the buffer after it — two formulas that agreed on the day they were
    /// written would not stay agreed.
    ///
    /// It does not fall as the crop grows or rise as it ripens: the yield is a
    /// property of the ground, so it is a promise made at sowing and kept, and
    /// a field that cannot fit it is refused rather than paid short.
    /// </summary>
    public int ProjectedYield(EntityId id) =>
        _entities.IsAlive(id) && HasCrop(_stage[id.Index]) ? YieldOf(id.Index) : 0;

    /// <summary>
    /// Rewrites the ground the field stands on. The one caller is the world,
    /// when a field's cells change under it (bulldozing shrinks one), because
    /// both numbers are only true of the cells they were taken over. One door
    /// for both, since there is exactly one moment either can change.
    /// Deliberately <b>not</b> a per-tick refresh: the ground does not move,
    /// and a value pushed on change is one the sim can hash without asking the
    /// world. False for a stale handle.
    ///
    /// The buffer is resized with the field but never emptied to fit, so a
    /// field bulldozed down to a corner can hold more than its new capacity
    /// until something collects — see <see cref="ItemBuffer.SetCapacity"/>.
    /// </summary>
    public bool SetGround(EntityId id, float fertility, int area)
    {
        if (!_entities.IsAlive(id))
        {
            return false;
        }
        int i = id.Index;
        _fertility[i] = Math.Clamp(fertility, 0f, 1f);
        _area[i] = Math.Max(0, area);
        _output[i].SetCapacity(CapacityFor(_area[i]));
        return true;
    }

    /// <summary>
    /// Grown ticks this field banks per tick right now — the product, for a
    /// caller that wants the rate rather than the result: #30's inspector shows
    /// it, #28 projects a yield off it, and a test asserts one field outgrows
    /// another with it. Zero for a dead handle, and zero for a stalled crop,
    /// which are the same answer for different reasons.
    /// </summary>
    public float GrowthPerTick(EntityId id) =>
        _entities.IsAlive(id) ? GrowthPerTick(id.Index, SeasonFactor) : 0f;

    /// <summary>The <c>Field.Id</c> the row belongs to, or 0 for a dead handle.</summary>
    public int OwnerOf(EntityId id) => _entities.IsAlive(id) ? _owner[id.Index] : 0;

    /// <summary>
    /// Whether the operation is legal from the stage — the precondition, stated
    /// once, as a pure function. M5's planner asks this before sending a
    /// machine anywhere; <see cref="Apply"/> asks it again on arrival, because
    /// the field can have moved on in between.
    /// </summary>
    public static bool Allows(CropStage stage, CropOperation operation) => operation switch
    {
        CropOperation.Plough => stage is CropStage.Fallow or CropStage.Stubble,
        CropOperation.Sow => stage == CropStage.Ploughed,
        CropOperation.Harvest => stage == CropStage.Harvestable,
        _ => false,
    };

    /// <summary>
    /// Whether the operation would be accepted on this field right now — the
    /// stage test <i>and</i> the room test, because a scheduler that only asked
    /// <see cref="Allows"/> would keep sending a machine to a field whose
    /// buffer is full.
    /// </summary>
    public bool CanApply(EntityId id, CropOperation operation)
    {
        if (!_entities.IsAlive(id) || !Allows(_stage[id.Index], operation))
        {
            return false;
        }
        int i = id.Index;
        return operation != CropOperation.Harvest || _output[i].HasRoomFor(YieldOf(i));
    }

    /// <summary>
    /// The one operation the field is waiting for, or null while all it is
    /// waiting for is time. The state machine is a line, so there is never more
    /// than one — which is what lets a dev key and (later) a standing order say
    /// "work this field" without naming an operation.
    /// </summary>
    public CropOperation? NextOperation(EntityId id)
    {
        if (!_entities.IsAlive(id))
        {
            return null;
        }
        return _stage[id.Index] switch
        {
            CropStage.Fallow or CropStage.Stubble => CropOperation.Plough,
            CropStage.Ploughed => CropOperation.Sow,
            CropStage.Harvestable => CropOperation.Harvest,
            _ => null,
        };
    }

    /// <summary>
    /// Does a piece of work to a field, or refuses. <b>The single entry point</b>
    /// for every caller — dev key today, M5's machine orders tomorrow — so a
    /// precondition cannot be skipped by picking a different door.
    ///
    /// A harvest is the one operation that produces something, and it is
    /// all-or-nothing: the whole yield goes into the field's buffer or the
    /// field is left exactly as it was, ripe and waiting.
    /// </summary>
    public CropOpResult Apply(EntityId id, CropOperation operation)
    {
        if (!_entities.IsAlive(id))
        {
            return CropOpResult.NoSuchField;
        }

        int i = id.Index;
        if (!Allows(_stage[i], operation))
        {
            return CropOpResult.WrongStage;
        }

        // Deposited before the stage moves, so a refusal cannot half-apply: the
        // crop is only cut once its yield is somewhere real.
        if (operation == CropOperation.Harvest && !_output[i].TryAdd(_harvestItem, YieldOf(i)))
        {
            return CropOpResult.OutputFull;
        }

        _stage[i] = operation switch
        {
            CropOperation.Plough => CropStage.Ploughed,
            CropOperation.Sow => CropStage.Sown,
            _ => _postHarvest,
        };
        _growth[i] = 0f;
        return CropOpResult.Ok;
    }

    public CropOpResult Plough(EntityId id) => Apply(id, CropOperation.Plough);

    public CropOpResult Sow(EntityId id) => Apply(id, CropOperation.Sow);

    public CropOpResult Harvest(EntityId id) => Apply(id, CropOperation.Harvest);

    /// <summary>
    /// One fixed tick of growth for every field that has something in the
    /// ground, walked in ascending slot order so the result never depends on
    /// the order fields were marked in. <paramref name="dt"/> is deliberately
    /// unused — see the class note on grown days.
    ///
    /// Only the two sown stages accumulate. A ripe field sits at
    /// <see cref="CropStage.Harvestable"/> until somebody takes it off, which is
    /// what makes the harvest pass a thing the player has to arrange rather than
    /// a deadline; over-ripening is M9's kind of problem, not this one's.
    /// </summary>
    public void Tick(float dt)
    {
        // Read once for the whole walk: the date cannot change mid-tick, and a
        // per-row read would invite a future factor that does.
        float season = SeasonFactor;
        for (int i = 0; i < _entities.SlotCount; i++)
        {
            if (!_entities.IsAliveSlot(i)
                || (_stage[i] != CropStage.Sown && _stage[i] != CropStage.Growing))
            {
                continue;
            }

            _growth[i] += GrowthPerTick(i, season);
            if (_stage[i] == CropStage.Sown && _growth[i] >= TicksToSprout)
            {
                _stage[i] = CropStage.Growing;
            }
            if (_stage[i] == CropStage.Growing && _growth[i] >= TicksToRipen)
            {
                _stage[i] = CropStage.Harvestable;
            }
        }
    }

    /// <summary>
    /// Grown ticks one tick adds to one field: the product of every factor.
    /// <b>Multiplied, not summed</b> — so any one of them at zero holds the
    /// crop at the stage it is in for as long as that lasts, rather than
    /// letting the other three carry it quietly to harvestable.
    ///
    /// The order of the multiplies is fixed in source and stays that way:
    /// float multiplication is not associative, and a run that reorders it is a
    /// run that hashes differently.
    /// </summary>
    private float GrowthPerTick(int slot, float seasonFactor) =>
        _baseGrowthRate * _fertility[slot] * seasonFactor * _waterGrowth;

    /// <summary>Whether there is something in the ground worth a yield.</summary>
    private static bool HasCrop(CropStage stage) =>
        stage is CropStage.Sown or CropStage.Growing or CropStage.Harvestable;

    /// <summary>
    /// Units this field gives when it is cut: its ground, floored, and never
    /// less than one — a ripe field that harvested to nothing would read as a
    /// bug rather than as poor soil, and soil poor enough to round to nothing
    /// is too poor to have ripened in the first place.
    /// </summary>
    private int YieldOf(int slot) =>
        _area[slot] <= 0 ? 0 : Math.Max(1, (int)(_yieldPerCell * _area[slot] * _fertility[slot]));

    /// <summary>
    /// The buffer size for a field of that area: whole harvests off
    /// <b>perfect</b> ground, so the first cut always fits and the ground
    /// decides how many more do.
    /// </summary>
    private int CapacityFor(int area) =>
        area <= 0 ? 0 : Math.Max(1, (int)(_yieldPerCell * area)) * _outputHarvests;

    /// <summary>The name this system's state is filed under in a state hash.</summary>
    public string StateName => StateSourceName;

    /// <summary>
    /// Liveness, every column by slot, and the schedule they are read against —
    /// the thresholds go in for the same reason the calendar's day length does:
    /// they are not state a tick moves, but they change what every future tick
    /// does, so two worlds tuned differently must not hash the same.
    /// </summary>
    public void HashState(StateHash hash)
    {
        hash.Write(TicksPerDay);
        hash.Write(DaysToSprout);
        hash.Write(DaysToRipen);
        hash.Write((int)_postHarvest);

        // The factors are configuration, not state a tick moves — but two
        // worlds tuned differently must not hash the same, and a season table
        // is walked by index because its index *is* the season. The calendar
        // itself is already in SimStateHash; only whether one is wired goes in
        // here, since that is the difference between seasons mattering and not.
        hash.Write(_baseGrowthRate);
        hash.Write(_waterGrowth);
        hash.Write(_yieldPerCell);
        hash.Write(_outputHarvests);
        hash.Write(_harvestItem.Id);
        hash.Write(_calendar != null);
        for (int i = 0; i < _seasonGrowth.Length; i++)
        {
            hash.Write(_seasonGrowth[i]);
        }

        _entities.HashState(hash);
        for (int i = 0; i < _entities.SlotCount; i++)
        {
            if (!_entities.IsAliveSlot(i))
            {
                continue;
            }

            hash.Write(i);
            hash.Write((int)_stage[i]);
            hash.Write(_growth[i]);
            hash.Write(_fertility[i]);
            hash.Write(_area[i]);
            // Contents are sim state — a save writes them and two runs that
            // harvested differently must not hash alike — and the buffer's own
            // stack order is canonical, so this walk needs no fold.
            _output[i].HashState(hash);
            hash.Write(_owner[i]);
        }
    }

    /// <summary>The stage as the UI spells it — lower case, like every other readout.</summary>
    public static string Name(CropStage stage) => stage switch
    {
        CropStage.Fallow => "fallow",
        CropStage.Ploughed => "ploughed",
        CropStage.Sown => "sown",
        CropStage.Growing => "growing",
        CropStage.Harvestable => "harvestable",
        CropStage.Stubble => "stubble",
        _ => stage.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Grows every column at once, by doubling — the same rule
    /// <c>MachineSystem</c> keeps, and for the same reason: an index is valid
    /// in all the columns or in none.
    /// </summary>
    private void EnsureCapacity(int slots)
    {
        if (slots <= _stage.Length)
        {
            return;
        }

        int capacity = Math.Max(InitialCapacity, _stage.Length);
        while (capacity < slots)
        {
            capacity *= 2;
        }

        Array.Resize(ref _stage, capacity);
        Array.Resize(ref _growth, capacity);
        Array.Resize(ref _fertility, capacity);
        Array.Resize(ref _area, capacity);
        Array.Resize(ref _output, capacity);
        Array.Resize(ref _owner, capacity);
    }
}
