using Godot;

namespace Arable;

/// <summary>
/// Headless smoke test for M5's payroll: hiring somebody costs a fee, having
/// hired them costs a wage every in-game day, and neither of those is anything
/// the sim decided on the player's behalf. Run with:
/// godot --headless --path . res://scenes/dev/WorkerSmokeTest.tscn
/// Exits 0 on pass, 1 on failure.
///
/// <b>Stepped, not played</b>, like <see cref="SimSmokeTest"/> and
/// <see cref="CropSmokeTest"/>: the world is paused before the frame loop can
/// schedule anything and every tick here is one this test asked for, so a
/// fortnight of wages takes milliseconds and no assertion depends on how the
/// frames fell. All the waits are counted in <i>ticks</i> — a wage is charged
/// against the calendar, and a frame count says nothing about the date.
///
/// <b>The money is asserted to the coin, never as "it went down".</b> Every
/// section names the exact balance it expects, because the failure this test
/// exists to catch is a wage charged per tick, or twice a day, or once for the
/// whole staff — all of which drain the account convincingly.
///
/// <b>The teaching moment is a claim about what did <i>not</i> happen.</b>
/// "A worker with no vehicle does nothing" is asserted from two sides: hiring
/// leaves every other state source in the world byte-identical, and dismissing
/// stops the drain and changes nothing else — so the only effect a worker has
/// on this world is the money it costs. That is the M5 rule made checkable: a
/// pool that found its own work would fail the first of those.
/// </summary>
public partial class WorkerSmokeTest : Node
{
    /// <summary>
    /// Machines in the world, so "hiring changed nothing else" is a claim made
    /// about a farm that actually has vehicles in it rather than an empty map.
    /// They are never assigned to anybody — that is #33.
    /// </summary>
    private const int Machines = 2;

    /// <summary>Days the headline "fee plus N wages" assertion runs for.</summary>
    private const int WageDays = 4;

    /// <summary>Days the payroll is skipped over, to check a date jump is not a free holiday.</summary>
    private const int JumpDays = 5;

    private Node _main = null!;
    private Simulation _sim = null!;
    private Economy _economy = null!;
    private LabourPool _pool = null!;
    private EntityId _first;
    private EntityId _second;

    private bool _done;
    private bool _failed;

    public override void _Ready()
    {
        Node main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        var world = main.GetNode<WorldGrid>("World");

        // Set before the world is in the tree: _Ready is what spawns them.
        world.MachineCount = Machines;
        AddChild(main);

        _main = main;
        _sim = main.GetNode<Simulation>("Sim");
        _economy = main.GetNode<Economy>("Economy");
        _pool = main.GetNode<LabourPool>("LabourPool");

        // Nothing may tick from the wall clock: from here every tick is one
        // this test asked for.
        _sim.SetSpeed(0);
    }

    public override void _Process(double delta)
    {
        if (_done)
        {
            return;
        }
        _done = true;

        CheckAFreshFarmHasNobodyOnTheBooks();
        CheckHiringChargesTheFeeAndNothingElse();
        CheckTheWageFallsOnDayBoundaries();
        CheckEveryWorkerOnTheBooksIsPaid();
        CheckDismissalStopsTheWageAndRefundsNothing();
        CheckASkippedDayIsStillCharged();
        CheckAFeeThatCannotBePaidIsRefused();
        CheckThePoolRunsOut();
        CheckInsolvencyBanksTheDebtInsteadOfFiringAnybody();
        CheckThePayrollIsHashed();

        _main.QueueFree();
        GD.Print(_failed ? "WORKER SMOKE TEST FAILED" : "WORKER SMOKE TEST PASSED");
        GetTree().Quit(_failed ? 1 : 0);
    }

    /// <summary>Opening state: nobody hired, nothing owed, the balance untouched.</summary>
    private void CheckAFreshFarmHasNobodyOnTheBooks()
    {
        Check("a fresh farm employs nobody", _pool.Count == 0);
        Check("and the whole pool is available to hire",
            _pool.Available == _pool.PoolSize && _pool.PoolSize > 0);
        Check("an empty payroll costs nothing a day", _pool.DailyPayroll == 0L);
        Check("and owes nothing", !_pool.IsInArrears && _pool.Arrears == 0L);
        Check("the balance is the one the scene opened with",
            _economy.Balance == _economy.StartingBalance);
    }

    /// <summary>
    /// The fee leaves the balance at the moment of hiring — and nothing else in
    /// the world moves, which is the first half of "a worker does nothing".
    /// </summary>
    private void CheckHiringChargesTheFeeAndNothingElse()
    {
        int before = _economy.Balance;
        ulong world = HashEverythingButTheMoney();

        Check("hiring is accepted", _pool.TryHire(out _first) == HireResult.Ok);
        Check("and hands back a live worker", _pool.IsAlive(_first));
        Check("the fee comes straight out of the balance",
            _economy.Balance == before - _pool.HireFee);
        Check("the worker is on the books", _pool.Count == 1);
        Check("and out of the pool", _pool.Available == _pool.PoolSize - 1);
        Check("dated to the day they were taken on",
            _pool.HiredOn(_first) == _sim.Calendar.TotalDays);
        Check("a worker costs a wage a day from now on",
            _pool.DailyPayroll == _pool.DailyWage);

        // The M5 rule, stated as a hash: hiring buys a payroll line and not a
        // single change anywhere in the world. Nothing was ploughed, no machine
        // was given an order, and nobody went looking for work to do.
        Check("hiring moves nothing in the world but the payroll",
            HashEverythingButTheMoney() == world);
    }

    /// <summary>
    /// The headline of the issue: the balance falls by the fee plus one wage
    /// per day, on the day boundary and nowhere else in between.
    /// </summary>
    private void CheckTheWageFallsOnDayBoundaries()
    {
        int start = _economy.StartingBalance;
        int day = _sim.Calendar.TicksPerDay;

        _sim.Step(day - 1);
        Check("no wage is charged part way through the first day",
            _economy.Balance == start - _pool.HireFee);
        Check("and the payroll has not run yet", _pool.LastPaidDay == 0L);

        _sim.Step(1);
        Check("the first day boundary charges exactly one wage",
            _economy.Balance == start - _pool.HireFee - _pool.DailyWage);
        Check("and the payroll records the day it ran for",
            _pool.LastPaidDay == 1L && _sim.Calendar.TotalDays == 1L);

        _sim.Step((WageDays - 1) * day);
        Check($"after {WageDays} days the balance is down the fee and {WageDays} wages",
            _economy.Balance == start - _pool.HireFee - (WageDays * _pool.DailyWage));
        Check("one charge per day, no more and no fewer",
            _pool.LastPaidDay == WageDays && _sim.Calendar.TotalDays == WageDays);
        Check("a worker nobody gave a vehicle still cost every one of them",
            _pool.Count == 1 && !_pool.IsInArrears);
    }

    /// <summary>The wage is per worker, not per payroll: two people cost twice as much.</summary>
    private void CheckEveryWorkerOnTheBooksIsPaid()
    {
        int before = _economy.Balance;
        Check("a second hire is accepted", _pool.TryHire(out _second) == HireResult.Ok);
        Check("and is charged its own fee", _economy.Balance == before - _pool.HireFee);
        Check("two workers cost two wages a day", _pool.DailyPayroll == 2 * _pool.DailyWage);

        int paid = _economy.Balance;
        _sim.Step(_sim.Calendar.TicksPerDay);
        Check("and the next boundary takes both",
            _economy.Balance == paid - (2 * _pool.DailyWage));
    }

    /// <summary>
    /// Letting somebody go stops their wage from the next boundary, buys back
    /// none of the fee, and — the second half of "a worker does nothing" —
    /// leaves the rest of the world exactly as it was.
    /// </summary>
    private void CheckDismissalStopsTheWageAndRefundsNothing()
    {
        int before = _economy.Balance;
        ulong world = HashEverythingButTheMoney();

        Check("a worker can be let go", _pool.Dismiss(_second));
        Check("the fee is not refunded", _economy.Balance == before);
        Check("they are off the books", _pool.Count == 1 && !_pool.IsAlive(_second));
        Check("and back in the pool", _pool.Available == _pool.PoolSize - 1);
        Check("letting somebody go changes nothing in the world either",
            HashEverythingButTheMoney() == world);
        Check("and a stale handle cannot fire their replacement", !_pool.Dismiss(_second));

        _sim.Step(_sim.Calendar.TicksPerDay);
        Check("the next boundary pays only the worker still employed",
            _economy.Balance == before - _pool.DailyWage);
    }

    /// <summary>
    /// A date jump — a scenario opening late, a save loaded forward — charges
    /// every day it skipped rather than quietly forgiving them. The payroll
    /// compares days instead of watching for a transition precisely so that
    /// this cannot become a free holiday.
    /// </summary>
    private void CheckASkippedDayIsStillCharged()
    {
        int before = _economy.Balance;
        long paidTo = _pool.LastPaidDay;
        int day = _sim.Calendar.TicksPerDay;

        // One tick short of the far side of the jump, so the step below lands
        // exactly on the boundary the payroll has to notice.
        _sim.Calendar.SetTicks(((paidTo + JumpDays) * day) - 1);
        _sim.Step(1);

        Check($"skipping {JumpDays} days charges all {JumpDays} at the next tick",
            _economy.Balance == before - (JumpDays * _pool.DailyWage));
        Check("and leaves the payroll level with the date",
            _pool.LastPaidDay == paidTo + JumpDays);
    }

    /// <summary>Hiring is all-or-nothing: what cannot be paid for is not hired.</summary>
    private void CheckAFeeThatCannotBePaidIsRefused()
    {
        _economy.SetBalance(_pool.HireFee - 1);
        int hired = _pool.Count;

        Check("a hire one coin short is refused",
            _pool.TryHire(out EntityId broke) == HireResult.CannotAfford);
        Check("with a dead handle, so nobody can be addressed", !_pool.IsAlive(broke));
        Check("the balance is untouched", _economy.Balance == _pool.HireFee - 1);
        Check("and nobody joined the payroll", _pool.Count == hired);
    }

    /// <summary>Labour is finite: money cannot conjure a fifth person out of a pool of four.</summary>
    private void CheckThePoolRunsOut()
    {
        _economy.SetBalance(_pool.HireFee * (_pool.PoolSize + 2));
        while (_pool.Available > 0)
        {
            if (_pool.TryHire(out EntityId _) != HireResult.Ok)
            {
                break;
            }
        }

        Check("the pool can be hired out entirely",
            _pool.Count == _pool.PoolSize && _pool.Available == 0);

        int rich = _economy.Balance;
        Check("and an empty pool refuses the next hire however rich the farm is",
            _pool.TryHire(out EntityId nobody) == HireResult.NobodyAvailable);
        Check("charging nothing for the refusal", _economy.Balance == rich);
        Check("and handing back nobody", !_pool.IsAlive(nobody));
    }

    /// <summary>
    /// The insolvency rule: the balance floors at zero, the shortfall is banked
    /// as arrears, nobody is fired, and the debt is paid off first once money
    /// arrives. Documented in <see cref="LabourPool"/> — this is the half that
    /// has to keep being true.
    /// </summary>
    private void CheckInsolvencyBanksTheDebtInsteadOfFiringAnybody()
    {
        int day = _sim.Calendar.TicksPerDay;
        long payroll = _pool.DailyPayroll;
        int staff = _pool.Count;
        var covered = (int)(payroll - 20L);

        _economy.SetBalance(covered);
        _sim.Step(day);
        Check("a payroll the balance cannot cover empties the account",
            _economy.Balance == 0);
        Check("and banks the shortfall as arrears",
            _pool.Arrears == payroll - covered && _pool.IsInArrears);
        Check("nobody is fired for going unpaid", _pool.Count == staff);

        _sim.Step(day);
        Check("a second unpaid day adds the whole payroll to the debt",
            _pool.Arrears == (payroll - covered) + payroll && _economy.Balance == 0);

        // Part payment: everything there is goes to the debt, and what is left
        // of it stays owed.
        long owed = _pool.Arrears;
        _economy.Credit(100);
        _sim.Step(day);
        Check("money that arrives is taken by the payroll before anything else",
            _economy.Balance == 0 && _pool.Arrears == owed + payroll - 100);

        owed = _pool.Arrears;
        _economy.Credit((int)(owed + payroll + 500L));
        _sim.Step(day);
        Check("and a farm that can pay clears the debt and the day together",
            _pool.Arrears == 0L && !_pool.IsInArrears && _economy.Balance == 500);
    }

    /// <summary>
    /// Worker state is sim state: it is walked by the determinism harness, or
    /// two runs that hired differently would be called identical.
    /// </summary>
    private void CheckThePayrollIsHashed()
    {
        Check("the payroll is a hashed state source",
            HasState(_sim, LabourPool.StateSourceName));

        ulong before = HashOf(_pool);
        _sim.Step(_sim.Calendar.TicksPerDay);
        Check("running a day moves the payroll's own hash",
            HashOf(_pool) != before);

        // Two bare pools, off the tree: no calendar and no account, so nothing
        // ticks and the only difference between them is who was hired.
        var left = new LabourPool();
        var right = new LabourPool();
        Check("two farms that hired nobody hash alike", HashOf(left) == HashOf(right));

        left.TryHire(out EntityId _);
        Check("hiring one changes the hash", HashOf(left) != HashOf(right));

        right.TryHire(out EntityId _);
        Check("and workers are interchangeable: the same hire hashes the same",
            HashOf(left) == HashOf(right));

        right.DailyWage = left.DailyWage + 1;
        Check("two farms paying different wages do not hash alike",
            HashOf(left) != HashOf(right));

        left.Free();
        right.Free();
    }

    /// <summary>
    /// Everything the sim hashes except the payroll and the balance, folded the
    /// way <see cref="SimStateHash"/> folds its sources. What is left is "the
    /// world": the grid, the crops and the machines. Hiring must not move it.
    /// </summary>
    private ulong HashEverythingButTheMoney()
    {
        var member = new StateHash();
        ulong fold = 0UL;
        int count = 0;
        foreach (IHashableState source in _sim.States)
        {
            if (source.StateName == LabourPool.StateSourceName
                || source.StateName == "economy")
            {
                continue;
            }

            member.Reset();
            member.Write(source.StateName);
            source.HashState(member);
            fold = StateHash.Fold(fold, member.Value);
            count++;
        }

        var hash = new StateHash();
        hash.WriteUnordered(fold, count);
        return hash.Value;
    }

    private static ulong HashOf(LabourPool pool)
    {
        var hash = new StateHash();
        pool.HashState(hash);
        return hash.Value;
    }

    private static bool HasState(Simulation sim, string name)
    {
        foreach (IHashableState state in sim.States)
        {
            if (state.StateName == name)
            {
                return true;
            }
        }
        return false;
    }

    private void Check(string what, bool ok)
    {
        GD.Print($"{(ok ? "PASS" : "FAIL")}: {what}");
        _failed |= !ok;
    }
}
