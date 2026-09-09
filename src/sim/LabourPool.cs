using System;
using Godot;

namespace Arable;

/// <summary>
/// What came of asking to hire somebody. Anything but <see cref="Ok"/> means
/// <b>nobody was hired and nothing was charged</b>.
///
/// A refusal is a return value and not a warning, for the same reason
/// <see cref="CropOpResult"/> is one: the HUD that will grey out a hire button
/// asks far more often than it hires, and a log line per refusal is noise the
/// smoke tests would then have to be exempted from.
/// </summary>
public enum HireResult
{
    /// <summary>Hired. The fee has left the balance, and the wage starts at the next day boundary.</summary>
    Ok,

    /// <summary>Everybody in the pool is already on the books. Money would not have helped.</summary>
    NobodyAvailable,

    /// <summary>The fee is more than the balance. Hiring is all-or-nothing — see <see cref="LabourPool"/>.</summary>
    CannotAfford,
}

/// <summary>
/// The <b>labour pool</b>: a small, fixed set of people who can be hired for an
/// up-front fee, and the payroll that charges a daily wage for the ones who
/// were.
///
/// <b>A worker is pure cost until the player gives it something to drive.</b>
/// That is the point, not an oversight. M5's governing rule is that automation
/// is <i>authored</i>, never automatic: there is no job pool here, nothing that
/// looks for idle hands, and nothing that pairs a worker with a vehicle.
/// Assignment is #33, and it is something the player types. A pool that quietly
/// put its people to work would teach the opposite lesson on the first day of
/// the milestone, so the absence of that convenience is load-bearing.
///
/// <b>Workers are interchangeable.</b> No names, no skills, no experience: one
/// wage for everybody, and a row's only distinguishing state is the day it was
/// hired. A skill number is content nothing can consume until there is work to
/// be good at — M6 is what gives the work any variety — and inventing one now
/// would be a column the save format carries for two milestones with no reader.
///
/// <b>Why a <see cref="Node"/>, when <c>CropSystem</c> and <c>MachineSystem</c>
/// are plain classes.</b> Those are owned by <c>WorldGrid</c>, because a crop
/// row and a machine are things on the grid and it is what creates them. A
/// worker is on nothing: no existing node's job is already to own the pool, and
/// an empty owner invented to preserve the symmetry would be a file that does
/// nothing. Being a node buys the two things this actually needs — the fee and
/// the wage as inspector numbers a playtest can argue about, and the
/// <see cref="Economy"/> as scene wiring, exactly like a <c>BuildTool</c>'s.
///
/// <b>Money, and the two different readings of "there is no money".</b> The fee
/// goes through <c>Economy.TrySpend</c>, which is all-or-nothing, so a hire the
/// player cannot afford is <i>refused</i> and changes nothing. The wage is not
/// refusable in the same way — the worker is already hired, and a day passing
/// is not a request. A pool with <b>no</b> <see cref="Economy"/> wired hires
/// and employs for free: that is "this scene has no account", the same reading
/// under which a tool with no economy builds for free, and it is what keeps a
/// dev scene working.
///
/// <b>Insolvency: the balance floors at zero and the shortfall becomes
/// <see cref="Arrears"/>.</b> When a boundary's payroll is more than the
/// balance, what there is is paid and the rest is owed; the debt is added to
/// the next boundary's payroll, so it settles itself the moment income exists
/// without anybody having to write a second rule for when. Nobody is fired and
/// nobody stops working. Three alternatives were rejected:
/// <list type="bullet">
/// <item><i>Letting the balance go negative.</i> M2 made "never negative" an
/// invariant of <c>Economy</c>, and every placement check reads that number
/// through <c>CanAfford</c>; a negative balance would silently change what
/// affordability <i>means</i> for the whole build palette, to buy one integer
/// of expressiveness that a separate debt column gives anyway.</item>
/// <item><i>Firing the unpaid automatically.</i> That is the sim quietly
/// repairing the player's payroll — the exact class of convenience M5 forbids.
/// Going broke has to be something the player did and can see.</item>
/// <item><i>Forgiving the unpaid day.</i> Then insolvency is free, and the wage
/// stops being a pressure at precisely the moment it starts to matter.</item>
/// </list>
/// What arrears <i>cost</i> — interest, people quitting, a loss condition — is
/// deferred to M7/M8: a consequence needs a market to be a consequence in.
/// Today the debt is a number that goes up, is never forgiven, and is paid off
/// first when money arrives.
/// </summary>
public partial class LabourPool : Node, ISimSystem, IHashableState
{
    /// <summary>The name the payroll is filed under in a state hash.</summary>
    public const string StateSourceName = "labour";

    /// <summary>
    /// People available to hire: four. Small on purpose — a cap is what makes
    /// hiring a decision rather than a slider, and "the region has four people
    /// looking for work" is a sentence a player can hold. A placeholder like
    /// every other number here; where labour comes from is M7's problem.
    /// </summary>
    public const int DefaultPoolSize = 4;

    /// <summary>
    /// The up-front fee: 500, which is two structures out of a 5,000 opening
    /// balance. It has to hurt enough that hiring a fourth worker "just in
    /// case" is not free.
    /// </summary>
    public const int DefaultHireFee = 500;

    /// <summary>
    /// The daily wage: 50, deliberately a tenth of the fee. A day is 30 s at
    /// 1x, so a worker costs their own fee again in wages inside a five-minute
    /// playtest — which is the span over which the drain has to become visible
    /// if it is going to teach anything.
    /// </summary>
    public const int DefaultDailyWage = 50;

    /// <summary>Slots to allocate on the first hire; it grows by doubling after that.</summary>
    private const int InitialCapacity = 8;

    /// <summary>
    /// The account the fee and the wages come out of. Null is legal and means
    /// this scene has no money, not that the player is broke.
    /// </summary>
    [Export] public Economy? Economy { get; set; }

    /// <summary>How many people can be on the books at once. See <see cref="DefaultPoolSize"/>.</summary>
    [Export] public int PoolSize { get; set; } = DefaultPoolSize;

    /// <summary>What hiring one costs, once. See <see cref="DefaultHireFee"/>.</summary>
    [Export] public int HireFee { get; set; } = DefaultHireFee;

    /// <summary>What each one costs per in-game day. See <see cref="DefaultDailyWage"/>.</summary>
    [Export] public int DailyWage { get; set; } = DefaultDailyWage;

    private readonly EntityStore _entities = new();

    /// <summary>
    /// The day each worker was taken on, by slot. The only column a worker row
    /// has while workers are interchangeable, and it is real state rather than
    /// decoration: it is what a payroll line would show, and what any later
    /// proration rule would have to be computed from.
    /// </summary>
    private long[] _hiredOn = [];

    /// <summary>
    /// The last day the payroll was run for. <b>This is how the day boundary is
    /// detected</b> — <c>GameCalendar</c> deliberately fires no rollover
    /// signal, precisely so that a system keeps the day it last acted on in its
    /// own state, where a save writes it and a replay reproduces it, instead of
    /// depending on an event a load would have to re-fire.
    ///
    /// Comparing days rather than watching for a transition is also what makes
    /// a date jump behave: a scenario opening on day 90, or a save loaded
    /// forward, charges every day it skipped exactly once instead of quietly
    /// forgiving all of them.
    /// </summary>
    private long _lastPaidDay;

    /// <summary>
    /// Wages owed and not paid. A <c>long</c> because, unlike a balance, this
    /// is an accumulator nothing bounds: <c>TrySpend</c> keeps the account from
    /// ever going below zero, but nothing stops a bankrupt farm running for a
    /// simulated century.
    /// </summary>
    private long _arrears;

    private GameCalendar? _calendar;
    private Simulation? _sim;

    /// <summary>Workers currently on the books, and being paid for.</summary>
    public int Count => _entities.Count;

    /// <summary>Slots ever handed out — the exclusive upper bound for a walk.</summary>
    public int SlotCount => _entities.SlotCount;

    /// <summary>
    /// People left in the pool. Derived, so it is neither hashed nor saved: the
    /// pool size is configuration and the head count is state, and this is the
    /// subtraction between them.
    /// </summary>
    public int Available => Math.Max(0, PoolSize) - _entities.Count;

    /// <summary>What the current staff costs per day, before any arrears.</summary>
    public long DailyPayroll => (long)_entities.Count * Math.Max(0, DailyWage);

    /// <summary>Wages owed and never paid. Zero on a solvent farm — see <see cref="LabourPool"/>.</summary>
    public long Arrears => _arrears;

    /// <summary>Whether the last boundary went unpaid, in whole or in part.</summary>
    public bool IsInArrears => _arrears > 0L;

    /// <summary>The last day the payroll ran for. Sim state, not a diagnostic.</summary>
    public long LastPaidDay => _lastPaidDay;

    /// <summary>Whether the handle still addresses the worker it was made for.</summary>
    public bool IsAlive(EntityId worker) => _entities.IsAlive(worker);

    /// <summary>Whether the slot holds a worker — the walk's predicate.</summary>
    public bool IsAliveSlot(int slot) => _entities.IsAliveSlot(slot);

    /// <summary>The handle for a slot, for turning a walk back into an id.</summary>
    public EntityId IdAt(int slot) => _entities.IdAt(slot);

    /// <summary>The day this worker was taken on; -1 for a stale handle.</summary>
    public long HiredOn(EntityId worker) =>
        _entities.IsAlive(worker) ? _hiredOn[worker.Index] : -1L;

    /// <summary>
    /// Wires the pool to the sim and opens the payroll on today's date, so the
    /// first charge falls at the next boundary rather than immediately.
    ///
    /// The exports are vetted the way <c>Economy</c> vets its starting balance
    /// and <c>Simulation</c> its speed ladder: repair and complain, never take
    /// the scene down over a number typed in the inspector. A negative wage in
    /// particular has to be clamped rather than paid, since it would otherwise
    /// be income through a door that is not <c>Economy.Credit</c>.
    /// </summary>
    public override void _Ready()
    {
        if (PoolSize < 0 || HireFee < 0 || DailyWage < 0)
        {
            GD.PushWarning(
                "LabourPool: negative PoolSize/HireFee/DailyWage "
                + $"({PoolSize}/{HireFee}/{DailyWage}); clamped to 0.");
            PoolSize = Math.Max(0, PoolSize);
            HireFee = Math.Max(0, HireFee);
            DailyWage = Math.Max(0, DailyWage);
        }

        _sim = Simulation.For(this);
        _calendar = _sim?.Calendar;
        _lastPaidDay = _calendar?.TotalDays ?? 0L;
        _sim?.Register(this);
    }

    public override void _ExitTree() => _sim?.Unregister(this);

    /// <summary>
    /// Takes somebody on. The fee is charged here and now, all of it or none of
    /// it, so a hire can never put the player in debt — and that asymmetry with
    /// the wage is the whole shape of the insolvency rule: you may not hire what
    /// you cannot pay for, but a day passing is not something you can decline.
    ///
    /// <paramref name="worker"/> is <c>default</c> — a dead handle — on every
    /// refusal, so a caller that ignores the result still cannot address
    /// somebody who was never hired.
    /// </summary>
    public HireResult TryHire(out EntityId worker)
    {
        worker = default;
        if (Available <= 0)
        {
            return HireResult.NobodyAvailable;
        }

        // TrySpend is all-or-nothing, so this one call is both the
        // affordability test and the charge; a CanAfford-then-spend pair would
        // be two places for the answer to change between.
        if (Economy != null && !Economy.TrySpend(Math.Max(0, HireFee)))
        {
            return HireResult.CannotAfford;
        }

        worker = _entities.Create();
        EnsureCapacity(_entities.SlotCount);

        // Every column, always: a recycled slot still holds the last worker's
        // values, and a hire date inherited from somebody who left would date
        // this one to before the farm existed.
        _hiredOn[worker.Index] = _calendar?.TotalDays ?? 0L;
        return HireResult.Ok;
    }

    /// <summary>
    /// Lets somebody go, effective immediately. False for a stale handle, so
    /// dismissing twice cannot fire whoever moved into the slot in between.
    ///
    /// <b>No refund, and no proration either way.</b> A boundary charges
    /// whoever is on the books when it falls: hiring at dusk costs a full day
    /// and leaving at dawn costs nothing. Charging fractions of a day would
    /// need a per-worker accrual, a rule for what the fee buys, and a notion of
    /// "day" finer than the one the clock shows the player — three decisions
    /// bought in place of one nobody can misread.
    ///
    /// Arrears are <i>not</i> cleared by dismissal: wages already owed are owed
    /// whether or not the person is still there.
    /// </summary>
    public bool Dismiss(EntityId worker) => _entities.Destroy(worker);

    /// <summary>
    /// Runs the payroll when the date has moved on, and does nothing at all on
    /// the other 599 ticks of the day.
    ///
    /// The calendar advances <i>before</i> the systems tick, so the charge
    /// falls on the first tick of the new day — the day the player is looking
    /// at when the money moves.
    /// </summary>
    public void Tick(float dt)
    {
        if (_calendar == null)
        {
            return;
        }

        long today = _calendar.TotalDays;
        if (today <= _lastPaidDay)
        {
            // Not a new day — or the date was set backwards, which is a
            // scenario or a load, and not a reason to hand wages back.
            return;
        }

        long days = today - _lastPaidDay;
        _lastPaidDay = today;
        Charge(days);
    }

    /// <summary>
    /// Pays what the balance covers of <paramref name="days"/> days of wages
    /// plus everything still owed, and keeps the rest as <see cref="Arrears"/>.
    /// The one place money leaves the account for labour.
    /// </summary>
    private void Charge(long days)
    {
        long due = DailyPayroll * days + _arrears;
        if (due <= 0L)
        {
            return;
        }

        if (Economy == null)
        {
            // No account in this scene: labour is free here, and a debt that
            // could never be paid would be a number with nothing behind it.
            return;
        }

        // Old debt comes off first by construction — it is already inside
        // `due`, and whatever cannot be paid goes straight back into the same
        // number, so there is one rule instead of a settlement order.
        int paid = (int)Math.Min(due, Economy.Balance);
        Economy.TrySpend(paid);
        _arrears = due - paid;
    }

    /// <summary>The name the payroll is filed under in a state hash.</summary>
    public string StateName => StateSourceName;

    /// <summary>
    /// The head count, each worker's hire date, and the payroll's own two
    /// numbers. The fee, wage and pool size are configuration rather than state
    /// a tick moves, but they go in for the reason <c>CropSystem</c>'s tunables
    /// do: two worlds tuned differently must not hash alike.
    ///
    /// <see cref="Available"/> is derived and stays out, as does the
    /// <c>Economy</c>'s balance — that is hashed by the account that owns it,
    /// and hashing it here as well would report one divergence as two.
    /// </summary>
    public void HashState(StateHash hash)
    {
        hash.Write(PoolSize);
        hash.Write(HireFee);
        hash.Write(DailyWage);
        hash.Write(_lastPaidDay);
        hash.Write(_arrears);

        _entities.HashState(hash);
        for (int i = 0; i < _entities.SlotCount; i++)
        {
            if (!_entities.IsAliveSlot(i))
            {
                continue;
            }

            hash.Write(i);
            hash.Write(_hiredOn[i]);
        }
    }

    private void EnsureCapacity(int slots)
    {
        if (slots <= _hiredOn.Length)
        {
            return;
        }

        int capacity = Math.Max(InitialCapacity, _hiredOn.Length);
        while (capacity < slots)
        {
            capacity *= 2;
        }

        Array.Resize(ref _hiredOn, capacity);
    }
}
