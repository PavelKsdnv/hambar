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
/// <b>Finding, not a workaround.</b> <see cref="OrderKind.HaulGoods"/> can only
/// name a <see cref="Structure"/> at each end — <see cref="Order.Haul"/> takes
/// two structure ids, and <see cref="MachineSystem"/>'s haul execution only
/// ever calls <see cref="WorldGrid.GetStructure(int)"/> on them. A field's own
/// harvest buffer (<see cref="CropSystem.OutputOf"/>) is not a
/// <see cref="Structure"/> and has no id in that registry, so no haul order
/// can name it — "field to silo" is not, today, one order a player can give.
/// See `## Orders` in `orders.md`: it already flagged this as unbuilt and
/// named this issue as the one that might first need it. This test does not
/// invent a fifth order kind to bridge that gap; it proves the two halves
/// that genuinely exist — a harvester filling the field's own buffer
/// unattended, and a truck's standing haul order unattended clearing a silo
/// into a depot for money — separately, with the silo seeded at setup exactly
/// the way <see cref="SiloSmokeTest"/>'s sources are. Closing the actual gap
/// is left to whoever picks it up next.
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

    /// <summary>Small on purpose — matches the scale of the silo/depot tests, not a played economy.</summary>
    private const int SiloStock = 30;

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
            out EntityId harvester, out EntityId truck, out EntityId loner,
            out Vector2I lonerStart, out int preSaleBalance);

        // The sale finishes in well under a day (see the wage-slack note in
        // the check itself), so it is checked first; the field takes several
        // simulated days to ripen and is checked after, in the same run.
        CheckTheCrewEarnsMoneyUnattended(truck, preSaleBalance);
        CheckTheFieldIsHarvestedIntoItsOwnBufferUnattended(harvester);
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
    /// job, not this one's), hire and crew a harvester and a truck, seed the
    /// silo the way <see cref="SiloSmokeTest"/> seeds its sources (see the
    /// finding in this class's own doc comment for why that is the source
    /// rather than the field), and hand each vehicle exactly one standing
    /// order. <paramref name="preSaleBalance"/> is captured last, after the
    /// hire fees have already left the balance — comparing against a
    /// pre-hire number would never see it rise (`orders.md`/`labour.md`'s
    /// own trap).
    /// </summary>
    private void ProgramTheCrew(
        out EntityId harvester, out EntityId truck, out EntityId loner,
        out Vector2I lonerStart, out int preSaleBalance)
    {
        Check("the field can be ploughed directly, as test setup",
            _crops.Plough(_field.Crop) == CropOpResult.Ok);
        Check("...and sown, ready to grow with no further setup",
            _crops.Sow(_field.Crop) == CropOpResult.Ok);

        Machine? harvesterNode = _world.SpawnMachine(MachineKind.Harvester);
        Machine? truckNode = _world.SpawnMachine(MachineKind.Truck);
        Machine? lonerNode = _world.SpawnMachine(MachineKind.Truck);
        Check("a harvester is on the road", harvesterNode != null);
        Check("a truck is on the road for the sale", truckNode != null);
        Check("a third vehicle is on the road, and gets no worker or order", lonerNode != null);
        harvester = harvesterNode?.Entity ?? EntityId.None;
        truck = truckNode?.Entity ?? EntityId.None;
        loner = lonerNode?.Entity ?? EntityId.None;
        lonerStart = lonerNode != null ? _machines.CellOf(loner) : Vector2I.Zero;

        Check("a worker can crew the harvester",
            _pool.TryHire(out EntityId harvesterDriver) == HireResult.Ok
            && _fleet.TryAssign(harvesterDriver, harvester) == AssignResult.Ok);
        Check("a worker can crew the truck",
            _pool.TryHire(out EntityId truckDriver) == HireResult.Ok
            && _fleet.TryAssign(truckDriver, truck) == AssignResult.Ok);

        _silo.Storage.Add(ItemTypes.Grain, SiloStock);

        Check("the harvester accepts a standing order to harvest the field",
            _machines.SetOrder(harvester, Order.Harvest(_field.Id)) == SetOrderResult.Ok);
        Check("the truck accepts a standing order to haul the silo's grain to the depot",
            _machines.SetOrder(truck, Order.Haul(ItemTypes.Grain, _silo.Id, _depot.Id))
                == SetOrderResult.Ok);

        // Nothing about the loner: no hire, no crew, no order. That absence
        // is the entire setup for the second half of this test.

        preSaleBalance = _economy.Balance;
    }

    /// <summary>
    /// The first headline claim, the half of it a haul order can actually
    /// run today: a truck given one standing order drives to the silo, loads,
    /// delivers to the depot and sells — and the balance rises — with no
    /// further input. The exact sale amount is not asserted against an exact
    /// balance: a run this long can cross a day boundary and pay a wage in
    /// the same window (`orders.md`'s trap), so the rise is bounded instead —
    /// at most the full sale (nothing else in this run adds money), at least
    /// the sale minus one day's wage for the two hired workers (the only way
    /// a wage tick could land inside this short a window).
    /// </summary>
    private void CheckTheCrewEarnsMoneyUnattended(EntityId truck, int preSaleBalance)
    {
        bool sold = StepUntil(() => _economy.Balance > preSaleBalance, 400);
        Check("a truck hauls the silo's grain to the depot and sells it, with no further input",
            sold && _machines.CargoOf(truck)!.IsEmpty && _silo.Storage.IsEmpty);
        Check("nothing blocked the delivery", _machines.BlockOf(truck) == OrderBlock.None);

        int rise = _economy.Balance - preSaleBalance;
        int saleValue = _economy.PriceOf(ItemTypes.Grain) * SiloStock;
        int wageSlack = _pool.DailyWage * 2;
        Check("the rise is exactly the grain sale, give or take one day's wage for the two workers",
            rise <= saleValue && rise >= saleValue - wageSlack);
    }

    /// <summary>
    /// The other half of the first claim: the field, worked by its own
    /// standing harvest order, ripens and is cut with no further input, and
    /// the yield lands in the field's own buffer — never the harvester's
    /// cargo (`crops.md`). This is the leg a haul order cannot continue from;
    /// see the finding in this class's own doc comment.
    /// </summary>
    private void CheckTheFieldIsHarvestedIntoItsOwnBufferUnattended(EntityId harvester)
    {
        float rate = _crops.GrowthPerTick(_field.Crop);
        Check("the field has a real growth rate to ripen at", rate > 0f);
        if (rate <= 0f)
        {
            return;
        }

        int growTicks = (int)(_crops.TicksToRipen / rate) + 20;
        bool ripe = StepUntil(() => _crops.StageOf(_field.Crop) == CropStage.Harvestable, growTicks);
        Check("the field ripens on its own over the simulated days", ripe);

        bool cut = StepUntil(() => _crops.StageOf(_field.Crop) != CropStage.Harvestable, 400);
        Check("the harvester cuts it the moment it is ready, with no further input", cut);
        Check("the yield landed in the field's own buffer, not the harvester's",
            _crops.OutputOf(_field.Crop)!.CountOf(ItemTypes.Grain) > 0
            && _machines.CargoOf(harvester)!.IsEmpty);
        Check("nothing blocked the harvester", _machines.BlockOf(harvester) == OrderBlock.None);
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
