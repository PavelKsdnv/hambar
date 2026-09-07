using System;
using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Headless smoke test for #34: a vehicle runs the order it was given —
/// action plus targets — until the player changes it, exposing a readable
/// step and, when stuck, a readable reason. Run with:
/// godot --headless --path . res://scenes/dev/OrderSmokeTest.tscn
/// Exits 0 on pass, 1 on failure.
///
/// <b>Stepped, not played</b>, like <see cref="WorkerSmokeTest"/>: the sim is
/// paused on arrival and every tick is one this test asked for through
/// <see cref="Simulation.Step"/>, so a truck's whole round trip takes
/// milliseconds.
///
/// <b>The governing claim is never "it moved", it is "it moved because it was
/// told to".</b> Every section names the exact order running and reads
/// <see cref="MachineSystem.StepOf"/>/<see cref="MachineSystem.BlockOf"/>
/// alongside the outcome, because a vehicle that happened to finish the right
/// job for the wrong reason is the bug M5 cares about — not "did the field get
/// ploughed" alone.
///
/// <b>The re-point test is the one that matters most</b>
/// (<see cref="CheckHaulingMovesGoodsAndFinishesBeforeRepointing"/>): a truck
/// is caught mid-haul, carrying a load, and re-pointed to haul the opposite
/// way. The assertion is not that the swap eventually happens — it is that
/// every unit already on the truck lands at the destination it picked the
/// load up for, and only once the truck is empty again does the new order
/// take over.
///
/// One road, built here rather than found on the generated map, so the fields
/// and buildings this test needs — one reachable, one deliberately isolated —
/// sit at cells this test chooses rather than cells a seed change could move.
/// <c>MarkField</c>/<c>PlaceStructure</c> are unvalidated doors like
/// <c>BuildRoadLine</c>, so nothing here needs the terrain to cooperate beyond
/// being soil, which is checked at the point of use.
/// </summary>
public partial class OrderSmokeTest : Node
{
    private Node _main = null!;
    private WorldGrid _world = null!;
    private Simulation _sim = null!;
    private LabourPool _pool = null!;
    private Fleet _fleet = null!;
    private MachineSystem _machines = null!;

    private Field _field1 = null!;
    private Field _field2 = null!;
    private Structure _structA = null!;
    private Structure _structB = null!;

    private EntityId _tractor;
    private EntityId _truck;
    private EntityId _harvester;

    private bool _done;
    private bool _failed;

    public override void _Ready()
    {
        Node main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        var world = main.GetNode<WorldGrid>("World");

        world.MachineCount = 0;
        // Flattened so a crop's growth cannot cross into winter mid-test and
        // stall the harvest section — the same fixture CropSmokeTest uses.
        world.CropSeasonGrowth = [1f, 1f, 1f, 1f];
        AddChild(main);

        _main = main;
        _world = world;
        _sim = main.GetNode<Simulation>("Sim");
        _pool = main.GetNode<LabourPool>("LabourPool");
        _fleet = main.GetNode<Fleet>("Fleet");
        _machines = world.Machines;

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

        if (!SetupWorld())
        {
            FinishAndQuit();
            return;
        }

        CheckAFreshVehicleHasNoOrder();
        CheckAnOrderDoesNothingWithoutADriver();
        CheckCrewingUnblocksItAndItPloughs();
        CheckTheOrderRepeatsAndReportsWhyItIsBlocked();
        CheckReassigningTakesOverOnceNotCommitted();
        CheckWrongVehicleKindsAreRefused();
        CheckANoSuchVehicleHandleIsRefused();
        CheckAFieldWithNoRoadAccessBlocksForever();
        CheckHaulingMovesGoodsAndFinishesBeforeRepointing();
        CheckHarvestFillsTheFieldsOwnBufferNotTheVehicles();
        CheckOrdersAreHashedSimState();

        FinishAndQuit();
    }

    /// <summary>
    /// One straight road out of the starting strip, and the fixtures this
    /// test hangs off it: a reachable field, an isolated one with no road
    /// frontage at all, and two structures for the haul. False (and every
    /// section below skipped) only if the generated ground has no soil at
    /// all along a 45-cell run, which nothing in this world's noise settings
    /// should ever produce.
    /// </summary>
    private bool SetupWorld()
    {
        _world.BuildRoadLine(new Vector2I(16, 0), new Vector2I(16, 45));

        Vector2I? fieldSite = FindSiteAlongRoad(16, 5, 19);
        Vector2I? sourceSite = FindSiteAlongRoad(16, 20, 29);
        Vector2I? destSite = FindSiteAlongRoad(16, 31, 44);
        Check("a soil site beside the new road is found for the field", fieldSite != null);
        Check("...for the haul source", sourceSite != null);
        Check("...for the haul destination", destSite != null);
        if (fieldSite == null || sourceSite == null || destSite == null)
        {
            return false;
        }

        _field1 = _world.MarkField([fieldSite.Value])!;
        // Far from every road this test built or the map started with —
        // never touched by anything below, only ever the target that proves
        // a blocked vehicle waits rather than substituting other work.
        _field2 = _world.MarkField([new Vector2I(-40, -40)])!;
        _structA = _world.PlaceStructure([sourceSite.Value])!;
        _structB = _world.PlaceStructure([destSite.Value])!;
        Check("field 1 is marked", _field1 != null);
        Check("the isolated field is marked", _field2 != null);
        Check("the haul source is placed", _structA != null);
        Check("the haul destination is placed", _structB != null);
        return _field1 != null && _field2 != null && _structA != null && _structB != null;
    }

    private void CheckAFreshVehicleHasNoOrder()
    {
        Machine? node = _world.SpawnMachine(MachineKind.Tractor);
        Check("a tractor is on the road", node != null);
        _tractor = node!.Entity;

        Check("a fresh vehicle runs no order", _machines.OrderOf(_tractor) == null);
        Check("and reports idle, with nothing blocking it",
            _machines.StepOf(_tractor) == OrderStep.Idle
            && _machines.BlockOf(_tractor) == OrderBlock.None
            && !_machines.IsBlocked(_tractor));
    }

    /// <summary>
    /// The other half of #33's rule, restated for orders: a vehicle with
    /// nobody driving it does not run its order, the same way it does not
    /// drive on its own.
    /// </summary>
    private void CheckAnOrderDoesNothingWithoutADriver()
    {
        Check("a plough order is accepted for a tractor",
            _machines.SetOrder(_tractor, Order.Plough(_field1.Id)) == SetOrderResult.Ok);

        _sim.Step(5);
        Check("an ordered but undriven tractor never gets a route",
            _machines.RouteLengthOf(_tractor) == 0);
        Check("and reports exactly why: no driver",
            _machines.StepOf(_tractor) == OrderStep.Idle
            && _machines.BlockOf(_tractor) == OrderBlock.NoDriver);
        Check("the field itself has not moved",
            _world.Crops.StageOf(_field1.Crop) == CropStage.Fallow);
    }

    private void CheckCrewingUnblocksItAndItPloughs()
    {
        Check("a worker can be hired", _pool.TryHire(out EntityId driver) == HireResult.Ok);
        Check("and put in the tractor's cab", _fleet.TryAssign(driver, _tractor) == AssignResult.Ok);

        var seen = new HashSet<(OrderStep, OrderBlock)>();
        bool ploughed = StepUntil(
            () => _world.Crops.StageOf(_field1.Crop) == CropStage.Ploughed, 400, _tractor, seen);

        Check("a crewed tractor drives to the field and ploughs it", ploughed);
        Check("and the tick it ploughed reported ploughing, not blocked",
            seen.Contains((OrderStep.Ploughing, OrderBlock.None)));
    }

    /// <summary>
    /// The order is still <see cref="OrderKind.PloughField"/> and the field is
    /// now ploughed, so the one thing it is legal from — <c>Fallow</c> or
    /// <c>Stubble</c> — no longer holds. The order does not go looking for
    /// something else to plough or drive off: it stays exactly where it is,
    /// reporting exactly why, for as long as that remains true.
    /// </summary>
    private void CheckTheOrderRepeatsAndReportsWhyItIsBlocked()
    {
        _sim.Step(10);
        Check("a plough order left on already-ploughed ground neither re-ploughs nor moves on",
            _world.Crops.StageOf(_field1.Crop) == CropStage.Ploughed);
        Check("and reports exactly why: the field is not at a stage it can act on",
            _machines.StepOf(_tractor) == OrderStep.Ploughing
            && _machines.BlockOf(_tractor) == OrderBlock.WrongStage);
        Check("the order itself is unchanged — it keeps trying, never substituting other work",
            _machines.OrderOf(_tractor)?.Kind == OrderKind.PloughField);
    }

    /// <summary>
    /// The tractor is stationary and empty-handed — not mid-commitment — so a
    /// re-point here is the "no cycle to protect" case: it takes over on the
    /// very next tick rather than waiting for the field to ripen for ploughing
    /// again.
    /// </summary>
    private void CheckReassigningTakesOverOnceNotCommitted()
    {
        Check("re-pointing an uncommitted tractor is accepted",
            _machines.SetOrder(_tractor, Order.Sow(_field1.Id)) == SetOrderResult.Ok);
        Check("it is queued, not yet running",
            _machines.OrderOf(_tractor)?.Kind == OrderKind.PloughField
            && _machines.PendingOrderOf(_tractor)?.Kind == OrderKind.SowField);

        _sim.Step(1);
        Check("a vehicle with nothing in progress takes the new order on the very next tick",
            _machines.OrderOf(_tractor)?.Kind == OrderKind.SowField);

        var seen = new HashSet<(OrderStep, OrderBlock)>();
        bool sown = StepUntil(() => _world.Crops.StageOf(_field1.Crop) == CropStage.Sown, 200, _tractor, seen);
        Check("and the field is sown", sown);
    }

    private void CheckWrongVehicleKindsAreRefused()
    {
        Machine? truckNode = _world.SpawnMachine(MachineKind.Truck);
        Machine? harvesterNode = _world.SpawnMachine(MachineKind.Harvester);
        Check("a truck and a harvester are on the road", truckNode != null && harvesterNode != null);
        _truck = truckNode!.Entity;
        _harvester = harvesterNode!.Entity;

        Check("a truck cannot be ordered to plough — it does not work fields",
            _machines.SetOrder(_truck, Order.Plough(_field1.Id)) == SetOrderResult.WrongVehicleKind);
        Check("a tractor cannot be ordered to haul — it has nowhere to put a load",
            _machines.SetOrder(_tractor, Order.Haul(ItemTypes.Grain, _structA.Id, _structB.Id))
                == SetOrderResult.WrongVehicleKind);
        Check("neither refusal changed what either vehicle is actually running",
            _machines.OrderOf(_truck) == null && _machines.OrderOf(_tractor)?.Kind == OrderKind.SowField);
    }

    private void CheckANoSuchVehicleHandleIsRefused()
    {
        Check("a dead vehicle handle is refused rather than silently accepted",
            _machines.SetOrder(EntityId.None, Order.Plough(_field1.Id)) == SetOrderResult.NoSuchVehicle);
    }

    /// <summary>
    /// <see cref="Field"/>s are legal with no road frontage at all — nothing
    /// requires one to mark a field. An order naming one is not a validation
    /// error, it is the ordinary shape of a blocked vehicle: it never gets a
    /// route, never moves, and never goes looking for a field it *can* reach.
    /// </summary>
    private void CheckAFieldWithNoRoadAccessBlocksForever()
    {
        Check("re-pointing the tractor at an isolated field is accepted — reachability is not "
            + "checked at assignment time", _machines.SetOrder(_tractor, Order.Plough(_field2.Id))
                == SetOrderResult.Ok);

        _sim.Step(20);
        Vector2I stuckAt = _machines.CellOf(_tractor);
        Check("a field with no road frontage never gets the tractor a route",
            _machines.RouteLengthOf(_tractor) == 0);
        Check("and it reports exactly why it is idle: no road access",
            _machines.StepOf(_tractor) == OrderStep.DrivingToField
            && _machines.BlockOf(_tractor) == OrderBlock.NoRoadAccess);

        _sim.Step(20);
        Check("it waits rather than finding other work",
            _machines.CellOf(_tractor) == stuckAt
            && _machines.BlockOf(_tractor) == OrderBlock.NoRoadAccess
            && _world.Crops.StageOf(_field1.Crop) == CropStage.Sown);
    }

    /// <summary>
    /// The brief's own example, made an assertion: a truck caught carrying a
    /// load and re-pointed to haul the opposite way finishes delivering to
    /// the destination it already picked the load up for, and only once it is
    /// empty again does the new order take over.
    /// </summary>
    private void CheckHaulingMovesGoodsAndFinishesBeforeRepointing()
    {
        _structA.Storage.Add(ItemTypes.Grain, 80);
        Check("the source starts holding the grain the truck will pick up",
            _structA.Storage.CountOf(ItemTypes.Grain) == 80);

        Check("a worker can crew the truck",
            _pool.TryHire(out EntityId driver) == HireResult.Ok
            && _fleet.TryAssign(driver, _truck) == AssignResult.Ok);
        Check("a haul order from A to B is accepted",
            _machines.SetOrder(_truck, Order.Haul(ItemTypes.Grain, _structA.Id, _structB.Id))
                == SetOrderResult.Ok);

        var seen = new HashSet<(OrderStep, OrderBlock)>();
        bool loaded = StepUntil(() => !_machines.CargoOf(_truck)!.IsEmpty, 400, _truck, seen);
        Check("the truck drives to the source and loads", loaded);
        Check("it picked up exactly what the source had",
            _structA.Storage.CountOf(ItemTypes.Grain) == 0
            && _machines.CargoOf(_truck)!.CountOf(ItemTypes.Grain) == 80);

        // Mid-delivery: re-point it to haul the opposite way. It is carrying
        // a load, so this must wait.
        Check("re-pointing a loaded truck is accepted, but queued",
            _machines.SetOrder(_truck, Order.Haul(ItemTypes.Grain, _structB.Id, _structA.Id))
                == SetOrderResult.Ok);
        Check("the loaded truck is still running the order it picked the load up under",
            _machines.OrderOf(_truck)?.FromStructureId == _structA.Id);

        bool delivered = StepUntil(() => _machines.CargoOf(_truck)!.IsEmpty, 400, _truck, seen);
        Check("the truck finishes delivering the load it already picked up", delivered);
        Check("every unit it carried landed at the destination it picked the load up for",
            _structB.Storage.CountOf(ItemTypes.Grain) == 80);
        Check("none of it was diverted back to the source mid-trip",
            _structA.Storage.CountOf(ItemTypes.Grain) == 0);
        Check("it was seen actually driving and unloading, not skipping straight to the answer",
            seen.Contains((OrderStep.DrivingToDestination, OrderBlock.None))
            && seen.Contains((OrderStep.Unloading, OrderBlock.None)));

        _sim.Step(1);
        Check("only now — its cycle finished — does the re-point take over",
            _machines.OrderOf(_truck)?.FromStructureId == _structB.Id);

        bool reloaded = StepUntil(() => !_machines.CargoOf(_truck)!.IsEmpty, 400, _truck, seen);
        Check("and the new order runs for real, picking up from B",
            reloaded && _machines.CargoOf(_truck)!.CountOf(ItemTypes.Grain) == 80);
    }

    /// <summary>
    /// A harvest calls <see cref="CropSystem.Apply"/> exactly as the plough
    /// and sow orders do, and that entry point deposits into the field's own
    /// buffer — never the vehicle's. The harvester's <see cref="MachineSpec.CargoCapacity"/>
    /// exists for a later milestone; this order does not use it.
    /// </summary>
    private void CheckHarvestFillsTheFieldsOwnBufferNotTheVehicles()
    {
        CropSystem crops = _world.Crops;
        float rate = crops.GrowthPerTick(_field1.Crop);
        Check("field 1 has a real growth rate to ripen at", rate > 0f);
        if (rate <= 0f)
        {
            return;
        }

        _sim.Step((int)(crops.TicksToRipen / rate) + 20);
        Check("field 1 reaches harvestable", crops.StageOf(_field1.Crop) == CropStage.Harvestable);

        Check("a worker can crew the harvester",
            _pool.TryHire(out EntityId driver) == HireResult.Ok
            && _fleet.TryAssign(driver, _harvester) == AssignResult.Ok);
        Check("a harvest order is accepted for the harvester",
            _machines.SetOrder(_harvester, Order.Harvest(_field1.Id)) == SetOrderResult.Ok);

        var seen = new HashSet<(OrderStep, OrderBlock)>();
        bool cut = StepUntil(
            () => crops.StageOf(_field1.Crop) != CropStage.Harvestable, 400, _harvester, seen);
        Check("the harvester drives there and cuts it", cut);
        Check("the yield landed in the field's own buffer",
            crops.OutputOf(_field1.Crop)!.CountOf(ItemTypes.Grain) > 0);
        Check("and the harvester's own cargo is untouched — the field's buffer holds the cut",
            _machines.CargoOf(_harvester)!.IsEmpty);
    }

    /// <summary>
    /// An order is sim state a save has to carry, so setting or clearing one
    /// has to move the hash <see cref="MachineSystem"/> already contributes.
    /// </summary>
    private void CheckOrdersAreHashedSimState()
    {
        ulong before = SimStateHash.Of(_sim);
        Check("giving a vehicle a fresh order moves the state hash",
            _machines.SetOrder(_harvester, Order.Plough(_field1.Id)) == SetOrderResult.Ok
            && SimStateHash.Of(_sim) != before);

        ulong withOrder = SimStateHash.Of(_sim);
        Check("and queuing it back to idle moves the hash again",
            _machines.SetOrder(_harvester, null) == SetOrderResult.Ok
            && SimStateHash.Of(_sim) != withOrder);
    }

    /// <summary>
    /// Steps one tick at a time until <paramref name="condition"/> holds,
    /// recording every (step, block) pair <paramref name="watch"/> was seen
    /// in along the way — the record is what lets a check assert a phase was
    /// actually visited without pinning it to a specific tick, which the
    /// travel distance to a fixture this test built cannot promise.
    /// </summary>
    private bool StepUntil(
        Func<bool> condition, int maxTicks, EntityId watch, HashSet<(OrderStep, OrderBlock)> seen)
    {
        if (condition())
        {
            return true;
        }
        for (int t = 0; t < maxTicks; t++)
        {
            _sim.Step();
            seen.Add((_machines.StepOf(watch), _machines.BlockOf(watch)));
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
    /// <paramref name="zTo"/> — so the test's fixtures sit wherever the
    /// generated ground actually has soil along the road it built, not at a
    /// literal cell a seed change could turn to rock.
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
        GD.Print(_failed ? "ORDER SMOKE TEST FAILED" : "ORDER SMOKE TEST PASSED");
        GetTree().Quit(_failed ? 1 : 0);
    }

    private void Check(string what, bool ok)
    {
        GD.Print($"{(ok ? "PASS" : "FAIL")}: {what}");
        _failed |= !ok;
    }
}
