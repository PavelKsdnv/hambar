using Godot;

namespace Arable;

/// <summary>
/// Headless smoke test for #39, the issue that closes M5. It proves the
/// milestone's two headline claims in one run. Run with:
/// godot --headless --path . res://scenes/dev/AutomationLoopSmokeTest.tscn
/// Exits 0 on pass, 1 on failure.
///
/// <b>Stepped, not played</b>, like <see cref="SiloSmokeTest"/> and
/// <see cref="DepotSmokeTest"/>: every tick is one this test asked for
/// through <see cref="Simulation.Step"/>. All orders are set once, in
/// <see cref="ProgramTheCrew"/>; every check after that only steps the sim
/// and reads it back — nothing here ever calls <c>SetOrder</c>,
/// <c>TryAssign</c> or touches a buffer a second time.
///
/// <b>One chain, three standing orders, nobody touching it.</b> A harvester
/// cuts the field into the field's own buffer (`crops.md`), one truck hauls
/// that buffer into the silo, a second truck hauls the silo into the depot,
/// which sells. All three orders are given once and repeat; the field takes
/// several simulated days to ripen, so both trucks legitimately sit on
/// <see cref="OrderBlock.SourceEmpty"/> until there is something to carry —
/// waiting, which is the correct behaviour, not a stall to be fixed.
///
/// <b>The sale is read off <see cref="Economy.SalesRevenue"/>, never off the
/// balance.</b> Three hired workers draw a wage every simulated day and this
/// run spans several, so the closing balance is worth *less* than the opening
/// one plus the sale. Summing the per-tick rises instead was the first cut and
/// it is subtly wrong: a wage falling in the same tick as a delivery nets off
/// against it, and the test would then measure a sale short by a day's payroll
/// — silently, and only on the seeds where the two land together. The takings
/// counter is the sale and nothing else, so what this asserts about price
/// multiples holds whatever the payroll does.
///
/// <b>The second half is a regression guard on a design rule, not a
/// functional check</b> (<see cref="CheckAnUnprogrammedVehicleTakesNoWork"/>).
/// There is no global job pool and no machine that finds its own work: every
/// vehicle runs exactly the order a player typed into it, and a vehicle with
/// no worker and no order simply sits. Auto-assignment is exactly the kind of
/// convenience somebody adds later while trying to be helpful — this test is
/// what stops it from landing unnoticed.
/// </summary>
public partial class AutomationLoopSmokeTest : Node
{
    private const int SiloCapacity = 100;
    private const int DepotCapacity = 100;


    private Node _main = null!;
    private WorldGrid _world = null!;
    private Simulation _sim = null!;
    private LabourPool _pool = null!;
    private Fleet _fleet = null!;
    private MachineSystem _machines = null!;
    private Economy _economy = null!;
    private CropSystem _crops = null!;

    private Field _field = null!;
    private Structure _silo = null!;
    private Structure _depot = null!;

    private bool _done;
    private bool _failed;

    public override void _Ready()
    {
        Node main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        var world = main.GetNode<WorldGrid>("World");
        world.MachineCount = 0;
        AddChild(main);

        _main = main;
        _world = world;
        _sim = main.GetNode<Simulation>("Sim");
        _pool = main.GetNode<LabourPool>("LabourPool");
        _fleet = main.GetNode<Fleet>("Fleet");
        _machines = world.Machines;
        _economy = main.GetNode<Economy>("Economy");
        _crops = world.Crops;

        // Every tick from here is one this test asked for.
        _sim.SetSpeed(0);
    }

    public override void _Process(double delta)
    {
        if (_done)
        {
            return;
        }
        _done = true;

        if (!SetupWorld())
        {
            FinishAndQuit();
            return;
        }

        ProgramTheCrew(
            out EntityId harvester, out EntityId carter, out EntityId truck,
            out EntityId loner, out Vector2I lonerStart);

        CheckTheChainEarnsMoneyUnattended(harvester, carter, truck);
        CheckAnUnprogrammedVehicleTakesNoWork(loner, lonerStart);

        FinishAndQuit();
    }

    /// <summary>
    /// One straight road and one single-cell field, silo and depot along it —
    /// a single cell is a legal field (<see cref="Field"/>), and this test
    /// only needs the field to produce *some* grain, not a played-out one.
    /// All road is built before anything spawns, per the trap `dev-tests.md`
    /// and `build.md` both name: road laid after a machine parks changes
    /// nothing, but road laid before changes where it parks in the first
    /// place, which would silently invalidate a site found earlier.
    /// </summary>
    private bool SetupWorld()
    {
        _world.BuildRoadLine(new Vector2I(16, 0), new Vector2I(16, 40));

        Vector2I? fieldSite = FindSiteAlongRoad(16, 2, 9);
        Vector2I? siloSite = FindSiteAlongRoad(16, 11, 18);
        Vector2I? depotSite = FindSiteAlongRoad(16, 20, 27);
        Check("a soil site beside the road is found for the field", fieldSite != null);
        Check("...for the silo", siloSite != null);
        Check("...for the depot", depotSite != null);
        if (fieldSite == null || siloSite == null || depotSite == null)
        {
            return false;
        }

        _field = _world.MarkField([fieldSite.Value])!;
        _silo = _world.PlaceStructure([siloSite.Value], StructureKind.Silo, SiloCapacity)!;
        _depot = _world.PlaceStructure([depotSite.Value], StructureKind.Depot, DepotCapacity)!;
        Check("the field is marked", _field != null);
        Check("the silo is placed", _silo != null);
        Check("the depot is placed", _depot != null);
        return _field != null && _silo != null && _depot != null;
    }

    /// <summary>
    /// Everything the player would type in, all before a single
    /// <see cref="Simulation.Step"/>: plough and sow the field directly
    /// through <see cref="CropSystem"/> (setup, the same convenience
    /// <c>CropSmokeTest</c> uses to reach "sown" without a tractor —
    /// ploughing and sowing via real orders is already <c>OrderSmokeTest</c>'s
    /// job, not this one's), put four vehicles on the road, hire and crew
    /// three of them, and hand those three exactly one standing order each.
    ///
    /// <b>The silo starts empty and is never seeded.</b> <see cref="SiloSmokeTest"/>
    /// fills its sources by hand because it is testing the building; this test
    /// is testing the chain, and grain the fixture put in the silo would let
    /// the sale succeed with the harvest half of the chain broken. Every unit
    /// the depot sells here has to have been cut out of the field and driven
    /// the whole way by these two trucks.
    ///
    /// The fourth vehicle gets no worker and no order, and that absence is
    /// the entire setup for <see cref="CheckAnUnprogrammedVehicleTakesNoWork"/>.
    /// Nothing about the balance is sampled here: what the run earns is
    /// <see cref="Economy.SalesRevenue"/>, which the hire fees do not touch,
    /// so <see cref="CheckTheChainEarnsMoneyUnattended"/> needs no
    /// before-picture taken after the fees have left.
    /// </summary>
    private void ProgramTheCrew(
        out EntityId harvester, out EntityId carter, out EntityId truck, out EntityId loner,
        out Vector2I lonerStart)
    {
        Check("the field can be ploughed directly, as test setup",
            _crops.Plough(_field.Crop) == CropOpResult.Ok);
        Check("...and sown, ready to grow with no further setup",
            _crops.Sow(_field.Crop) == CropOpResult.Ok);

        Machine? harvesterNode = _world.SpawnMachine(MachineKind.Harvester);
        Machine? carterNode = _world.SpawnMachine(MachineKind.Truck);
        Machine? truckNode = _world.SpawnMachine(MachineKind.Truck);
        Machine? lonerNode = _world.SpawnMachine(MachineKind.Truck);
        Check("a harvester is on the road", harvesterNode != null);
        Check("a truck is on the road to clear the field", carterNode != null);
        Check("a truck is on the road for the sale", truckNode != null);
        Check("a fourth vehicle is on the road, and gets no worker or order", lonerNode != null);
        harvester = harvesterNode?.Entity ?? EntityId.None;
        carter = carterNode?.Entity ?? EntityId.None;
        truck = truckNode?.Entity ?? EntityId.None;
        loner = lonerNode?.Entity ?? EntityId.None;
        lonerStart = lonerNode != null ? _machines.CellOf(loner) : Vector2I.Zero;

        Check("a worker can crew the harvester",
            _pool.TryHire(out EntityId harvesterDriver) == HireResult.Ok
            && _fleet.TryAssign(harvesterDriver, harvester) == AssignResult.Ok);
        Check("a worker can crew the field truck",
            _pool.TryHire(out EntityId carterDriver) == HireResult.Ok
            && _fleet.TryAssign(carterDriver, carter) == AssignResult.Ok);
        Check("a worker can crew the sale truck",
            _pool.TryHire(out EntityId truckDriver) == HireResult.Ok
            && _fleet.TryAssign(truckDriver, truck) == AssignResult.Ok);

        // The whole chain, authored in three orders and then left alone. The
        // silo is not seeded: every unit that reaches the depot has to have
        // been cut out of the field and carried by these two trucks.
        Check("the harvester accepts a standing order to harvest the field",
            _machines.SetOrder(harvester, Order.Harvest(_field.Id)) == SetOrderResult.Ok);
        Check("a truck accepts a standing order to haul the field's own buffer into the silo",
            _machines.SetOrder(carter, Order.HaulFromField(ItemTypes.Grain, _field.Id, _silo.Id))
                == SetOrderResult.Ok);
        Check("a truck accepts a standing order to haul the silo's grain to the depot",
            _machines.SetOrder(truck, Order.Haul(ItemTypes.Grain, _silo.Id, _depot.Id))
                == SetOrderResult.Ok);

        // Nothing about the loner: no hire, no crew, no order. That absence
        // is the entire setup for the second half of this test.
    }

    /// <summary>
    /// M5's headline claim, whole: field to silo to depot earns money, with
    /// no input after the orders were set. One loop drives the entire chain
    /// and watches it, because the legs overlap — the field is ripening while
    /// both trucks wait on <see cref="OrderBlock.SourceEmpty"/>, and a truck
    /// may be mid-run when the next cut lands.
    ///
    /// <b>Revenue is <see cref="Economy.SalesRevenue"/>, not any reading of
    /// the balance.</b> The balance is a till two things write to: a sale
    /// credits it and the daily wage debits it, so neither the closing figure
    /// nor a sum of per-tick rises is the sale — the first needs a
    /// wage-shaped tolerance and stops measuring what it is named for, and
    /// the second quietly loses a day's payroll on any run where a pay tick
    /// and a delivery tick coincide. The takings counter has no second
    /// writer, so the price-multiple check below is exact by construction
    /// rather than by luck of the calendar.
    ///
    /// Nothing here asserts on the balance at all, and deliberately: with a
    /// payroll running there is no threshold to compare it against that is
    /// not arbitrary. That the credit lands in the account is
    /// <c>DepotSmokeTest</c>'s check, in a scene with nobody on the payroll
    /// to muddy it.
    /// </summary>
    private void CheckTheChainEarnsMoneyUnattended(
        EntityId harvester, EntityId carter, EntityId truck)
    {
        float rate = _crops.GrowthPerTick(_field.Crop);
        Check("the field has a real growth rate to ripen at", rate > 0f);
        if (rate <= 0f)
        {
            return;
        }

        // Long enough for the field to ripen, be cut, and for both trucks to
        // run their legs afterwards — the ripening dominates it.
        int budget = (int)(_crops.TicksToRipen / rate) + 1200;
        int openingTakings = _economy.SalesRevenue;
        int revenue = 0;
        bool harvesterEverCarried = false;
        bool carterEverCarried = false;
        bool truckEverCarried = false;

        // *Cargo*, not the buffers at either end. A truck parked on a source
        // it is blocked on loads the moment something appears there, inside
        // the same tick that put it there, so sampling the field's buffer or
        // the silo once a tick can read zero for a delivery that did happen.
        // A hold, by contrast, stays full for the whole drive across.
        for (int t = 0; t < budget && revenue == 0; t++)
        {
            _sim.Step();

            revenue = _economy.SalesRevenue - openingTakings;

            harvesterEverCarried |= !_machines.CargoOf(harvester)!.IsEmpty;
            carterEverCarried |= _machines.CargoOf(carter)!.CountOf(ItemTypes.Grain) > 0;
            truckEverCarried |= _machines.CargoOf(truck)!.CountOf(ItemTypes.Grain) > 0;
        }

        Check("the yield went to the field's own buffer, never the harvester's cargo",
            !harvesterEverCarried);
        Check("a truck carried grain out of the field, so the field was cut and drained",
            carterEverCarried);
        Check("a truck carried grain out of the silo, which only that first truck can fill",
            truckEverCarried);
        Check("the depot sold it and the takings rose", revenue > 0);
        Check("every unit sold was cut from the field this run, priced at PriceOf(grain)",
            revenue > 0 && revenue % _economy.PriceOf(ItemTypes.Grain) == 0);


        // Blocked is the ordinary state here, not a fault: a truck whose
        // source is empty waits for the next cut. What must never happen is
        // a vehicle that answers a block by doing something else, so the
        // orders themselves are re-read — unchanged, still theirs.
        Check("all three kept the exact orders they were given, start to finish",
            _machines.OrderOf(harvester) is { Kind: OrderKind.HarvestField }
            && _machines.OrderOf(carter) is
                { Kind: OrderKind.HaulGoods, FromKind: HaulSourceKind.Field }
            && _machines.OrderOf(truck) is
                { Kind: OrderKind.HaulGoods, FromKind: HaulSourceKind.Structure });
    }

    /// <summary>
    /// The second headline claim, and a <b>regression guard on a design
    /// rule, not a functional check</b>: automation here is authored, not
    /// automatic. There is no global job pool and no machine that goes
    /// looking for work, so a vehicle nobody hired a driver for and nobody
    /// gave an order to must come out of this run exactly as it went in —
    /// same cell, same empty order, same empty hold. Auto-assignment is the
    /// kind of convenience somebody adds later while trying to be helpful;
    /// this test exists to fail loudly the day that happens.
    /// </summary>
    private void CheckAnUnprogrammedVehicleTakesNoWork(EntityId loner, Vector2I lonerStart)
    {
        Check("it was never given a driver", _fleet.DriverOf(loner) == EntityId.None);
        Check("it never acquired an order, standing or queued",
            _machines.OrderOf(loner) == null && _machines.PendingOrderOf(loner) == null);
        Check("it reports idle, with no reason to wait since it was never told to do anything",
            _machines.StepOf(loner) == OrderStep.Idle && _machines.BlockOf(loner) == OrderBlock.None);
        Check("it did not move a single cell across the entire run",
            _machines.CellOf(loner) == lonerStart && _machines.RouteLengthOf(loner) == 0);
        Check("it holds nothing", _machines.CargoOf(loner)!.IsEmpty);
    }

    /// <summary>Steps one tick at a time until <paramref name="condition"/> holds.</summary>
    private bool StepUntil(System.Func<bool> condition, int maxTicks)
    {
        if (condition())
        {
            return true;
        }
        for (int t = 0; t < maxTicks; t++)
        {
            _sim.Step();
            if (condition())
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A road-adjacent, empty, soil cell one step east of the road at
    /// <paramref name="roadX"/>, searched over <paramref name="zFrom"/>..
    /// <paramref name="zTo"/> — wherever the generated ground actually has soil
    /// along the road this test built, not a literal cell a seed change could
    /// turn to rock.
    /// </summary>
    private Vector2I? FindSiteAlongRoad(int roadX, int zFrom, int zTo)
    {
        for (int z = zFrom; z <= zTo; z++)
        {
            var road = new Vector2I(roadX, z);
            var site = new Vector2I(roadX + 1, z);
            if (_world.IsRoad(road) && _world.IsSoil(site) && _world.GetTile(site) == TileType.Empty)
            {
                return site;
            }
        }
        return null;
    }

    private void FinishAndQuit()
    {
        _main.QueueFree();
        GD.Print(_failed ? "AUTOMATION LOOP SMOKE TEST FAILED" : "AUTOMATION LOOP SMOKE TEST PASSED");
        GetTree().Quit(_failed ? 1 : 0);
    }

    private void Check(string what, bool ok)
    {
        GD.Print($"{(ok ? "PASS" : "FAIL")}: {what}");
        _failed |= !ok;
    }
}
