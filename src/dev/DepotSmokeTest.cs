using Godot;

namespace Arable;

/// <summary>
/// Headless smoke test for #38: a depot is a named <see cref="StructureKind"/>
/// that turns a delivery into money rather than storing it. Run with:
/// godot --headless --path . res://scenes/dev/DepotSmokeTest.tscn
/// Exits 0 on pass, 1 on failure.
///
/// <b>Stepped, not played</b>, like <see cref="SiloSmokeTest"/>: every tick is
/// one this test asked for through <see cref="Simulation.Step"/>.
///
/// <b>The governing claim is the second delivery</b>
/// (<see cref="CheckASecondDeliverySellsTooInsteadOfFillingUp"/>): the depot's
/// <see cref="Structure.Storage"/> is a mouth, not a store, so nothing about
/// selling a load leaves it any less able to take the next one. Everything
/// before it (build, first sale) is the ordinary path that section's repeat
/// delivery is the exception-that-should-not-be to.
/// </summary>
public partial class DepotSmokeTest : Node
{
    /// <summary>
    /// Clears one truckload (see <see cref="MachineKinds"/>) with room to
    /// spare, matching what <see cref="StructureKinds"/> falls back to.
    /// </summary>
    private const int DepotCapacity = 200;

    private Node _main = null!;
    private WorldGrid _world = null!;
    private Simulation _sim = null!;
    private LabourPool _pool = null!;
    private Fleet _fleet = null!;
    private MachineSystem _machines = null!;
    private Economy _economy = null!;

    private GridMap _gridMap = null!;

    private Structure _depot = null!;
    private Structure _firstSource = null!;
    private Structure _secondSource = null!;

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
        _gridMap = world.GetNode<GridMap>("GridMap");

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

        CheckADepotIsBuiltAndDrawsItsOwnMesh();
        CheckHaulingSellsIntoTheDepotAndCreditsTheBalance();
        CheckASecondDeliverySellsTooInsteadOfFillingUp();

        FinishAndQuit();
    }

    /// <summary>
    /// One straight road and three structures along it: the depot under test,
    /// and two separate sources — one per delivery — so a fill level left over
    /// from the first sale can never be mistaken for the second's.
    /// </summary>
    private bool SetupWorld()
    {
        _world.BuildRoadLine(new Vector2I(16, 0), new Vector2I(16, 40));

        Vector2I? depotSite = FindSiteAlongRoad(16, 2, 9);
        Vector2I? firstSourceSite = FindSiteAlongRoad(16, 11, 18);
        Vector2I? secondSourceSite = FindSiteAlongRoad(16, 20, 27);
        Check("a soil site beside the road is found for the depot", depotSite != null);
        Check("...for the first delivery's source", firstSourceSite != null);
        Check("...for the second delivery's source", secondSourceSite != null);
        if (depotSite == null || firstSourceSite == null || secondSourceSite == null)
        {
            return false;
        }

        _depot = _world.PlaceStructure([depotSite.Value], StructureKind.Depot, DepotCapacity)!;
        _firstSource = _world.PlaceStructure([firstSourceSite.Value])!;
        _secondSource = _world.PlaceStructure([secondSourceSite.Value])!;
        Check("the depot is placed", _depot != null);
        Check("the first source is placed", _firstSource != null);
        Check("the second source is placed", _secondSource != null);
        return _depot != null && _firstSource != null && _secondSource != null;
    }

    /// <summary>
    /// The first Done-when box: a depot is a real, named kind, not the generic
    /// placeholder and not a silo wearing a different label.
    /// </summary>
    private void CheckADepotIsBuiltAndDrawsItsOwnMesh()
    {
        Check("the placed building is a depot, not an unnamed placeholder",
            _depot.Kind == StructureKind.Depot && _depot.Name.StartsWith("Depot"));
        Check("it holds to the capacity it was placed with",
            _depot.Storage.Capacity == DepotCapacity);
        // The entity being a depot and the world *drawing* a depot are two
        // different claims, and only this one fails quietly: every assertion
        // above passes while the cell still shows the silo's mesh or the
        // fallback box, because nothing but a rendered frame ever looks at the
        // mesh. Caught exactly that way once for the silo — see `## Structures`
        // in build.md — so the depot gets the same assertion.
        int depotItem = _gridMap.MeshLibrary!.FindItemByName("Depot");
        int siloItem = _gridMap.MeshLibrary.FindItemByName("Silo");
        int fallbackItem = _gridMap.MeshLibrary.FindItemByName("Structure");
        Check("the mesh library has a depot item distinct from the silo and the fallback",
            depotItem >= 0 && siloItem >= 0 && fallbackItem >= 0
            && depotItem != siloItem && depotItem != fallbackItem);
        Check("the cell draws the depot's own mesh, not the silo's or the fallback box",
            _gridMap.GetCellItem(new Vector3I(_depot.Origin.X, 0, _depot.Origin.Y)) == depotItem);
        Check("it starts holding nothing",
            _depot.Storage.IsEmpty && _depot.Storage.CountOf(ItemTypes.Grain) == 0);
    }

    /// <summary>
    /// The remaining Done-when boxes: a truck can deliver to the depot through
    /// an ordinary haul order, the load is removed from the world rather than
    /// piling up in <see cref="Structure.Storage"/>, and the balance rises by
    /// exactly <see cref="Economy.PriceOf"/> times the amount delivered — the
    /// one function #38 puts the price behind.
    /// </summary>
    private void CheckHaulingSellsIntoTheDepotAndCreditsTheBalance()
    {
        const int amount = 20;
        _firstSource.Storage.Add(ItemTypes.Grain, amount);

        Machine? node = _world.SpawnMachine(MachineKind.Truck);
        Check("a truck is on the road for the delivery", node != null);
        EntityId truck = node!.Entity;
        Check("a worker can crew it",
            _pool.TryHire(out EntityId driver) == HireResult.Ok
            && _fleet.TryAssign(driver, truck) == AssignResult.Ok);
        // Captured after hiring, not before: the hire fee dwarfs a 20-unit
        // sale, and comparing against a pre-hire balance would never see it rise.
        int startingBalance = _economy.Balance;
        Check("a haul order into the depot is accepted",
            _machines.SetOrder(truck, Order.Haul(ItemTypes.Grain, _firstSource.Id, _depot.Id))
                == SetOrderResult.Ok);

        bool sold = StepUntil(() => _economy.Balance > startingBalance, 400);
        Check("the truck drives to the source, loads, delivers to the depot",
            sold && _machines.CargoOf(truck)!.IsEmpty && _firstSource.Storage.IsEmpty);
        Check("the delivered grain was removed from the world, not stored",
            _depot.Storage.IsEmpty && _depot.Storage.CountOf(ItemTypes.Grain) == 0);
        Check("the balance rose by exactly PriceOf(grain) times the amount delivered",
            _economy.Balance == startingBalance + _economy.PriceOf(ItemTypes.Grain) * amount);
        Check("nothing blocked a delivery that fit",
            _machines.BlockOf(truck) == OrderBlock.None);

        // Stood down rather than left repeating: an empty source means this
        // order would only ever report SourceEmpty from here.
        _machines.SetOrder(truck, null);
    }

    /// <summary>
    /// The claim a depot exists to prove over a silo: selling a load never
    /// leaves the depot any less able to take the next one, because nothing
    /// is left behind to fill it up. A second, independent delivery sells for
    /// the same price per unit and never reports <see cref="OrderBlock.DestinationFull"/>.
    /// </summary>
    private void CheckASecondDeliverySellsTooInsteadOfFillingUp()
    {
        Check("the depot is still empty after the first sale",
            _depot.Storage.IsEmpty);

        const int amount = 35;
        _secondSource.Storage.Add(ItemTypes.Grain, amount);

        Machine? node = _world.SpawnMachine(MachineKind.Truck);
        Check("a second truck is on the road for the delivery", node != null);
        EntityId truck = node!.Entity;
        Check("a worker can crew it",
            _pool.TryHire(out EntityId driver) == HireResult.Ok
            && _fleet.TryAssign(driver, truck) == AssignResult.Ok);
        int startingBalance = _economy.Balance;
        Check("a second haul into the same depot is accepted",
            _machines.SetOrder(truck, Order.Haul(ItemTypes.Grain, _secondSource.Id, _depot.Id))
                == SetOrderResult.Ok);

        bool sold = StepUntil(() => _economy.Balance > startingBalance, 400);
        Check("the second truck also delivers and sells",
            sold && _machines.CargoOf(truck)!.IsEmpty && _secondSource.Storage.IsEmpty);
        Check("the balance rose by exactly PriceOf(grain) times this delivery's amount",
            _economy.Balance == startingBalance + _economy.PriceOf(ItemTypes.Grain) * amount);
        Check("the depot never reported itself full — a mouth, not a store",
            _machines.BlockOf(truck) == OrderBlock.None && _depot.Storage.IsEmpty);
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
        GD.Print(_failed ? "DEPOT SMOKE TEST FAILED" : "DEPOT SMOKE TEST PASSED");
        GetTree().Quit(_failed ? 1 : 0);
    }

    private void Check(string what, bool ok)
    {
        GD.Print($"{(ok ? "PASS" : "FAIL")}: {what}");
        _failed |= !ok;
    }
}
