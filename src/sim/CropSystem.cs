using System;
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
/// which is far past the point a crop was harvested. It is a float rather than
/// an int because #27's factors are fractional multipliers on that 1.
///
/// <b>Not a Node</b> and, unlike <c>MachineSystem</c>, it holds no world
/// either: nothing here knows what a cell is. <see cref="GrowthPerTick"/> is
/// the single seam the next issue widens — #27 turns it into a product of
/// factors (fertility, season, water), and the fertility a field is worth is
/// input handed in at the row, not a lookup this class makes, so
/// <c>src/sim/</c> stays free of the world.
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

    /// <summary>Slots the arrays start at; they grow by doubling from there.</summary>
    private const int InitialCapacity = 16;

    private readonly EntityStore _entities = new();

    private readonly CropStage _postHarvest;

    // Parallel component arrays, all sized to _entities.SlotCount. Three
    // columns is the whole of a crop today; #27 adds the growth factors and
    // #28 the output buffer, each in its own column beside these.
    private CropStage[] _stage = [];
    private float[] _growth = [];
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
    public CropSystem(
        int ticksPerDay,
        int daysToSprout = DefaultDaysToSprout,
        int daysToRipen = DefaultDaysToRipen,
        bool stubbleNeedsPloughing = true)
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
    /// </summary>
    public EntityId Create(int fieldId)
    {
        EntityId id = _entities.Create();
        EnsureCapacity(_entities.SlotCount);
        int i = id.Index;

        // Every column, because a recycled slot still holds the last field's.
        _stage[i] = CropStage.Fallow;
        _growth[i] = 0f;
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

    /// <summary>Whether the operation would be accepted on this field right now.</summary>
    public bool CanApply(EntityId id, CropOperation operation) =>
        _entities.IsAlive(id) && Allows(_stage[id.Index], operation);

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
    /// Harvest currently only empties the field: the item stack it should leave
    /// behind is #28's, and hangs off this line.
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
        for (int i = 0; i < _entities.SlotCount; i++)
        {
            if (!_entities.IsAliveSlot(i)
                || (_stage[i] != CropStage.Sown && _stage[i] != CropStage.Growing))
            {
                continue;
            }

            _growth[i] += GrowthPerTick(i);
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
    /// Grown ticks one tick adds to one field: 1 at full rate. <b>The seam #27
    /// widens</b> — it becomes 1 times fertility, season and water, each factor
    /// exported, so any of them at zero stalls the crop where it stands. Today
    /// every field grows at full rate, which is why a crop nobody tends still
    /// finishes: the thing the next issue is specifically there to stop.
    /// </summary>
    private static float GrowthPerTick(int slot) => 1f;

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
        Array.Resize(ref _owner, capacity);
    }
}
