using System;
using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Headless smoke test for #37: the silo is a named <see cref="StructureKind"/>
/// with its own tunable capacity, and the in/out interface a haul order binds
/// to (<see cref="Structure.Id"/> plus its single <see cref="Structure.Storage"/>)
/// already does deposit, withdraw and backpressure through the machinery #34
/// built for any two structures. Run with:
/// godot --headless --path . res://scenes/dev/SiloSmokeTest.tscn
/// Exits 0 on pass, 1 on failure.
///
/// <b>Stepped, not played</b>, like <see cref="OrderSmokeTest"/>: every tick is
/// one this test asked for through <see cref="Simulation.Step"/>.
///
/// <b>The governing claim is the overflow section</b>
/// (<see cref="CheckHaulingIntoAFullSiloBlocksAndKeepsTheLoad"/>): a truck
/// carrying more than a silo has room for tops it up to exactly its capacity
/// and then blocks, reporting why, holding the remainder — it never dumps the
/// rest elsewhere, deletes it, or goes looking for other work. Everything
/// before it (build, deposit, withdraw) is the ordinary path that section's
/// full silo is the exception to.
///
/// One road and four structures, all placed here rather than found on the
/// generated map, so the sizes and stock levels this test depends on are
/// exact. A separate source and truck per section keeps a fill level from one
/// check from leaking into the next; only the silo under test
/// (<see cref="_silo"/>) is shared, and its capacity is small on purpose so
/// filling it is a couple of truckloads, not a played-out economy.
/// </summary>
public partial class SiloSmokeTest : Node
{
    /// <summary>Small on purpose: a couple of truckloads fills this, not a played economy.</summary>
    private const int SiloCapacity = 50;

    private Node _main = null!;
    private WorldGrid _world = null!;
    private Simulation _sim = null!;
    private LabourPool _pool = null!;
    private Fleet _fleet = null!;
    private MachineSystem _machines = null!;

    private GridMap _gridMap = null!;

    private Structure _silo = null!;
    private Structure _depositSource = null!;
    private Structure _withdrawDest = null!;
    private Structure _overflowSource = null!;

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

        CheckASiloIsBuiltHoldingToCapacityAndReportingContents();
        CheckHaulingDepositsIntoTheSilo();
        CheckHaulingWithdrawsFromTheSilo();
        CheckHaulingIntoAFullSiloBlocksAndKeepsTheLoad();

        FinishAndQuit();
    }

    /// <summary>
    /// One straight road, and four structures along it: the silo under test at
    /// a fixed small capacity, a source with just enough grain for a clean
    /// under-capacity deposit, a destination for the withdraw section, and a
    /// second, well-stocked source kept for the overflow section alone so its
    /// truckload cannot be mistaken for the deposit section's.
    /// </summary>
    private bool SetupWorld()
    {
        _world.BuildRoadLine(new Vector2I(16, 0), new Vector2I(16, 40));

        Vector2I? siloSite = FindSiteAlongRoad(16, 2, 9);
        Vector2I? depositSourceSite = FindSiteAlongRoad(16, 11, 18);
        Vector2I? withdrawDestSite = FindSiteAlongRoad(16, 20, 27);
        Vector2I? overflowSourceSite = FindSiteAlongRoad(16, 29, 36);
        Check("a soil site beside the road is found for the silo", siloSite != null);
        Check("...for the deposit source", depositSourceSite != null);
        Check("...for the withdraw destination", withdrawDestSite != null);
        Check("...for the overflow source", overflowSourceSite != null);
        if (siloSite == null || depositSourceSite == null || withdrawDestSite == null
            || overflowSourceSite == null)
        {
            return false;
        }

        _silo = _world.PlaceStructure([siloSite.Value], StructureKind.Silo, SiloCapacity)!;
        _depositSource = _world.PlaceStructure([depositSourceSite.Value])!;
        _withdrawDest = _world.PlaceStructure([withdrawDestSite.Value])!;
        _overflowSource = _world.PlaceStructure([overflowSourceSite.Value])!;
        Check("the silo is placed", _silo != null);
        Check("the deposit source is placed", _depositSource != null);
        Check("the withdraw destination is placed", _withdrawDest != null);
        Check("the overflow source is placed", _overflowSource != null);
        return _silo != null && _depositSource != null && _withdrawDest != null
            && _overflowSource != null;
    }

    /// <summary>
    /// The first two Done-when boxes: a silo is a real, named kind — not the
    /// generic placeholder — sized by whatever placed it rather than a
    /// world-wide number, and its contents are exactly what
    /// <see cref="ItemBuffer"/> already reports for any carrier.
    /// </summary>
    private void CheckASiloIsBuiltHoldingToCapacityAndReportingContents()
    {
        Check("the placed building is a silo, not an unnamed placeholder",
            _silo.Kind == StructureKind.Silo && _silo.Name.StartsWith("Silo"));
        Check("it holds to the capacity it was placed with, not a world-wide number",
            _silo.Storage.Capacity == SiloCapacity);
        // The entity being a silo and the world *drawing* a silo are two
        // different claims, and only this one fails quietly: every assertion
        // above passes while the cell still shows the fallback box, because
        // nothing but a rendered frame ever looks at the mesh. Caught exactly
        // that way once — see `## Structures` in build.md.
        int siloItem = _gridMap.MeshLibrary!.FindItemByName("Silo");
        int fallbackItem = _gridMap.MeshLibrary.FindItemByName("Structure");
        Check("the mesh library still has both a silo and a fallback item",
            siloItem >= 0 && fallbackItem >= 0 && siloItem != fallbackItem);
        Check("the cell draws the silo's own mesh, not the fallback box",
            _gridMap.GetCellItem(new Vector3I(_silo.Origin.X, 0, _silo.Origin.Y)) == siloItem);
        Check("it starts empty and reports so",
            _silo.Storage.IsEmpty && _silo.Storage.CountOf(ItemTypes.Grain) == 0);
    }

    /// <summary>
    /// A truck can deposit into the silo through an ordinary haul order — the
    /// load fits with room to spare, so nothing here should block.
    /// </summary>
    private void CheckHaulingDepositsIntoTheSilo()
    {
        const int amount = 20;
        _depositSource.Storage.Add(ItemTypes.Grain, amount);

        Machine? node = _world.SpawnMachine(MachineKind.Truck);
        Check("a truck is on the road for the deposit", node != null);
        EntityId truck = node!.Entity;
        Check("a worker can crew it",
            _pool.TryHire(out EntityId driver) == HireResult.Ok
            && _fleet.TryAssign(driver, truck) == AssignResult.Ok);
        Check("a haul order into the silo is accepted",
            _machines.SetOrder(truck, Order.Haul(ItemTypes.Grain, _depositSource.Id, _silo.Id))
                == SetOrderResult.Ok);

        bool delivered = StepUntil(() => _silo.Storage.CountOf(ItemTypes.Grain) >= amount, 400);
        Check("the truck drives to the source, loads, delivers to the silo",
            delivered && _machines.CargoOf(truck)!.IsEmpty);
        Check("the deposit landed exactly, and reports in the silo's contents",
            _silo.Storage.CountOf(ItemTypes.Grain) == amount
            && _depositSource.Storage.IsEmpty);
        Check("nothing blocked a delivery that fit",
            _machines.BlockOf(truck) == OrderBlock.None);

        // Stood down rather than left repeating: an empty source means this
        // order would only ever report SourceEmpty from here, but the next
        // section's silo is shared, and an idle vehicle cannot be mistaken
        // for one still working it.
        _machines.SetOrder(truck, null);
    }

    /// <summary>
    /// A second truck can withdraw the grain the deposit section just put in,
    /// through the same order shape with the silo as the source this time —
    /// the "out" half of the in/out interface.
    /// </summary>
    private void CheckHaulingWithdrawsFromTheSilo()
    {
        int stock = _silo.Storage.CountOf(ItemTypes.Grain);
        Check("the silo is still holding what the deposit section left it",
            stock > 0);

        Machine? node = _world.SpawnMachine(MachineKind.Truck);
        Check("a second truck is on the road for the withdrawal", node != null);
        EntityId truck = node!.Entity;
        Check("a worker can crew it",
            _pool.TryHire(out EntityId driver) == HireResult.Ok
            && _fleet.TryAssign(driver, truck) == AssignResult.Ok);
        Check("a haul order out of the silo is accepted",
            _machines.SetOrder(truck, Order.Haul(ItemTypes.Grain, _silo.Id, _withdrawDest.Id))
                == SetOrderResult.Ok);

        bool delivered = StepUntil(() => _withdrawDest.Storage.CountOf(ItemTypes.Grain) >= stock, 400);
        Check("the truck loads from the silo and delivers to the destination", delivered);
        Check("the silo gave up exactly what it held, and reports it",
            _silo.Storage.IsEmpty && _withdrawDest.Storage.CountOf(ItemTypes.Grain) == stock);
        Check("nothing blocked a withdrawal the silo could fully supply",
            _machines.BlockOf(truck) == OrderBlock.None);

        // Stood down for the same reason the deposit truck was: left running,
        // this order would drive straight back to the now-empty silo and load
        // out from under the overflow section the instant that truck tops it
        // up, which would make the overflow section's numbers move for a
        // reason that has nothing to do with what it is testing.
        _machines.SetOrder(truck, null);
    }

    /// <summary>
    /// The Done-when box this milestone is really about: a truck carrying more
    /// than the empty silo has room for tops it up to <i>exactly</i> its
    /// capacity, then blocks reporting <see cref="OrderBlock.DestinationFull"/>
    /// — never past capacity, never losing the remainder, never wandering off
    /// to find something else to do with it. The block is checked to persist
    /// over further ticks, not just on the tick it first appears.
    /// </summary>
    private void CheckHaulingIntoAFullSiloBlocksAndKeepsTheLoad()
    {
        Check("the silo is empty again before the overflow load arrives",
            _silo.Storage.IsEmpty);

        const int overflowStock = 300;
        _overflowSource.Storage.Add(ItemTypes.Grain, overflowStock);

        Machine? node = _world.SpawnMachine(MachineKind.Truck);
        Check("a third truck is on the road for the overflow", node != null);
        EntityId truck = node!.Entity;
        int cargoCapacity = _machines.CargoOf(truck)!.Capacity;
        Check("the truck's own hold is bigger than the silo — the point of this section",
            cargoCapacity > SiloCapacity);
        Check("a worker can crew it",
            _pool.TryHire(out EntityId driver) == HireResult.Ok
            && _fleet.TryAssign(driver, truck) == AssignResult.Ok);
        Check("a haul from the well-stocked source into the silo is accepted",
            _machines.SetOrder(truck, Order.Haul(ItemTypes.Grain, _overflowSource.Id, _silo.Id))
                == SetOrderResult.Ok);

        var seen = new HashSet<(OrderStep, OrderBlock)>();
        bool loaded = StepUntil(() => !_machines.CargoOf(truck)!.IsEmpty, 400, truck, seen);
        Check("the truck loads a full hold — more than the silo can take", loaded);
        Check("it picked up a full load", _machines.CargoOf(truck)!.CountOf(ItemTypes.Grain) == cargoCapacity);

        bool toppedUp = StepUntil(() => _silo.Storage.IsFull, 400, truck, seen);
        Check("the silo fills to exactly its capacity, not one unit over",
            toppedUp && _silo.Storage.CountOf(ItemTypes.Grain) == SiloCapacity);

        int expectedRemainder = cargoCapacity - SiloCapacity;
        Check("and reports exactly why it stopped there: the destination is full",
            _machines.StepOf(truck) == OrderStep.Unloading
            && _machines.BlockOf(truck) == OrderBlock.DestinationFull);
        Check("the reason is the readable one a player sees, not a raw enum",
            OrderText.Describe(OrderBlock.DestinationFull) == "waiting: destination full");
        Check("the rest of the load stayed on the truck instead of being lost",
            _machines.CargoOf(truck)!.CountOf(ItemTypes.Grain) == expectedRemainder);
        Check("it was seen actually driving and unloading, not skipping straight to the answer",
            seen.Contains((OrderStep.DrivingToDestination, OrderBlock.None))
            || seen.Contains((OrderStep.Unloading, OrderBlock.None)));

        Vector2I stuckAt = _machines.CellOf(truck);
        _sim.Step(30);
        Check("the block holds rather than clearing itself a few ticks later",
            _machines.BlockOf(truck) == OrderBlock.DestinationFull
            && _machines.CellOf(truck) == stuckAt);
        Check("the silo never grew past capacity while the truck kept trying",
            _silo.Storage.CountOf(ItemTypes.Grain) == SiloCapacity);
        Check("the truck still has exactly the remainder — nothing vanished, nothing dumped elsewhere",
            _machines.CargoOf(truck)!.CountOf(ItemTypes.Grain) == expectedRemainder);
        Check("the order itself is unchanged — waiting, not substituting other work",
            _machines.OrderOf(truck)?.ToStructureId == _silo.Id);
    }

    /// <summary>Steps until <paramref name="condition"/> holds, recording nothing along the way.</summary>
    private bool StepUntil(Func<bool> condition, int maxTicks) =>
        StepUntil(condition, maxTicks, EntityId.None, null);

    /// <summary>
    /// Steps one tick at a time until <paramref name="condition"/> holds,
    /// recording every (step, block) pair <paramref name="watch"/> was seen in
    /// along the way — see <see cref="OrderSmokeTest"/>'s helper of the same
    /// shape for why: it lets a check assert a phase was actually visited
    /// without pinning it to a specific tick.
    /// </summary>
    private bool StepUntil(
        Func<bool> condition, int maxTicks, EntityId watch, HashSet<(OrderStep, OrderBlock)>? seen)
    {
        if (condition())
        {
            return true;
        }
        for (int t = 0; t < maxTicks; t++)
        {
            _sim.Step();
            seen?.Add((_machines.StepOf(watch), _machines.BlockOf(watch)));
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
        GD.Print(_failed ? "SILO SMOKE TEST FAILED" : "SILO SMOKE TEST PASSED");
        GetTree().Quit(_failed ? 1 : 0);
    }

    private void Check(string what, bool ok)
    {
        GD.Print($"{(ok ? "PASS" : "FAIL")}: {what}");
        _failed |= !ok;
    }
}
