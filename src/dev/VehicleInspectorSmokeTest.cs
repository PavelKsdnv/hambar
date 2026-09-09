using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Headless smoke test for #35: the panel that lets a player select a
/// vehicle, read why it is or is not moving, and click an order together —
/// driven entirely through <see cref="VehicleInspector"/>'s own public
/// methods (<see cref="VehicleInspector.Select"/>,
/// <see cref="VehicleInspector.BeginPicking"/>,
/// <see cref="VehicleInspector.PickTargetAt"/>), never a synthesized mouse
/// click. That is deliberate: #35 asks for a *programmatic* door onto the
/// same target-picking mode a click drives, and this is the test that proves
/// one exists.
///
/// Run with: godot --headless --path . res://scenes/dev/VehicleInspectorSmokeTest.tscn
/// Exits 0 on pass, 1 on failure.
///
/// <b>Stepped, not played</b>, exactly like <see cref="OrderSmokeTest"/>: the
/// sim is paused on arrival, and reading the panel after a batch of
/// <see cref="Simulation.Step"/> calls is how a countdown or a blocked reason
/// is asserted without waiting on a frame clock.
///
/// <b>The claim under test is the panel's, not the order system's</b> — #34's
/// own smoke test already proves a vehicle runs the order it was given and
/// reports why it cannot; this one proves the panel reads exactly those two
/// facts, that <c>BeginPicking</c> refuses an action the vehicle's kind
/// cannot run before the map ever arms, and that <c>PickTargetAt</c> refuses
/// a target of the wrong shape (a road cell for a field order, a road cell for
/// a haul) while leaving picking mode exactly as it was.
/// </summary>
public partial class VehicleInspectorSmokeTest : Node
{
    private Node _main = null!;
    private WorldGrid _world = null!;
    private Simulation _sim = null!;
    private LabourPool _pool = null!;
    private Fleet _fleet = null!;
    private MachineSystem _machines = null!;
    private VehicleInspector _panel = null!;

    private Field _field = null!;
    private Structure _structA = null!;
    private Structure _structB = null!;
    private Vector2I _roadCell;

    private EntityId _tractor;

    private bool _done;
    private bool _failed;

    public override void _Ready()
    {
        Node main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        var world = main.GetNode<WorldGrid>("World");

        world.MachineCount = 0;
        // Flattened so a crop's growth cannot stall on a bad season mid-test —
        // the same fixture OrderSmokeTest and CropSmokeTest use.
        world.CropSeasonGrowth = [1f, 1f, 1f, 1f];
        AddChild(main);

        _main = main;
        _world = world;
        _sim = main.GetNode<Simulation>("Sim");
        _pool = main.GetNode<LabourPool>("LabourPool");
        _fleet = main.GetNode<Fleet>("Fleet");
        _machines = world.Machines;
        _panel = main.GetNode<VehicleInspector>("Hud/VehicleInspector");

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

        CheckSelectingAnIdleVehicleShowsKindAndNothingToReport();
        CheckAnActionTheVehicleCannotRunIsRefused();
        CheckPickingRefusesTheWrongTargetShapeAndAcceptsTheRightOne();
        CheckThePanelShowsNoDriverThenTheBlockedReasonOnceStageIsWrong();
        CheckAHaulNeedsTwoTargetsAndDefaultsToTheOnlyRealGood();
        CheckCancelPickingLeavesTheRunningOrderAlone();
        CheckStopOrderQueuesIdle();
        CheckVehicleAtIsTheSameLookupBothPanelsShareAtACell();

        FinishAndQuit();
    }

    /// <summary>
    /// One road, a field one step off it and two structures further along —
    /// found along the road exactly like <see cref="OrderSmokeTest"/>'s
    /// fixtures, so a seed change cannot quietly turn an assertion into a test
    /// of something else.
    /// </summary>
    private bool SetupWorld()
    {
        _world.BuildRoadLine(new Vector2I(16, 0), new Vector2I(16, 49));

        Vector2I? fieldSite = FindSiteAlongRoad(16, 5, 19);
        Vector2I? sourceSite = FindSiteAlongRoad(16, 20, 29);
        Vector2I? destSite = FindSiteAlongRoad(16, 31, 44);
        Check("a soil site is found for the field", fieldSite != null);
        Check("...for the haul source", sourceSite != null);
        Check("...for the haul destination", destSite != null);
        if (fieldSite == null || sourceSite == null || destSite == null)
        {
            return false;
        }

        _field = _world.MarkField([fieldSite.Value])!;
        _structA = _world.PlaceStructure([sourceSite.Value])!;
        _structB = _world.PlaceStructure([destSite.Value])!;
        _roadCell = new Vector2I(16, 5);
        Check("the field is marked", _field != null);
        Check("the haul source is placed", _structA != null);
        Check("the haul destination is placed", _structB != null);
        Check("the road cell used for illegal picks is actually road, not a field or a building",
            _world.IsRoad(_roadCell) && _world.GetField(_roadCell) == null
            && _world.GetStructure(_roadCell) == null);
        return _field != null && _structA != null && _structB != null;
    }

    private void CheckSelectingAnIdleVehicleShowsKindAndNothingToReport()
    {
        Machine? node = _world.SpawnMachine(MachineKind.Tractor);
        Check("a tractor is on the road", node != null);
        _tractor = node!.Entity;

        Check("the panel starts closed", !_panel.IsOpen);
        EntityId selected = _panel.Select(_tractor);
        Check("selecting it opens the panel on that vehicle", selected == _tractor && _panel.IsOpen);
        Check("the title names the kind", _panel.TitleText.Contains("Tractor"));
        Check("nobody is driving it yet", _panel.DriverLine == "none");
        Check("it is running no order", _panel.OrderLine == "none");
        Check("its step reads idle", _panel.StepLine == OrderText.Describe(OrderStep.Idle));
        // An em dash, not the empty string OrderText.Describe(None) actually
        // is — "nothing to report" is a word on this panel, the same
        // convention FieldInspector uses for its own empty fields.
        Check("and there is nothing blocking it to report", _panel.BlockedLine == "—");
    }

    /// <summary>
    /// #35's own requirement, ahead of any map interaction: an action the
    /// selected vehicle's kind cannot run at all — a tractor cannot haul, it
    /// pulls implements and has nowhere to put a load — is refused the moment
    /// it is chosen, and picking mode never arms for it.
    /// </summary>
    private void CheckAnActionTheVehicleCannotRunIsRefused()
    {
        Check("a tractor cannot be offered a haul action",
            !_panel.BeginPicking(OrderKind.HaulGoods));
        Check("picking mode never armed for it", _panel.PickingAction == null);
    }

    /// <summary>
    /// The map's target-picking mode: a road cell is the wrong shape for a
    /// field order and is refused, leaving the mode exactly as it was, and the
    /// field itself is accepted and turns straight into a running order.
    /// </summary>
    private void CheckPickingRefusesTheWrongTargetShapeAndAcceptsTheRightOne()
    {
        Check("ploughing is offered to a tractor", _panel.BeginPicking(OrderKind.PloughField));
        Check("picking mode is armed", _panel.PickingAction == OrderKind.PloughField);

        Check("a bare road cell is refused as a field target", !_panel.PickTargetAt(_roadCell));
        Check("refusing a target does not drop picking mode", _panel.PickingAction == OrderKind.PloughField);

        Check("the field itself is accepted",
            _panel.PickTargetAt(_field.Cells[0]));
        Check("picking mode closes once a legal target lands", _panel.PickingAction == null);
        // SetOrder queues rather than applies immediately — see Order.cs — so
        // straight after the pick this is the pending order, not yet the one
        // AdvanceOrder promotes on the next tick.
        Check("the tractor is now queued to plough",
            _machines.PendingOrderOf(_tractor)?.Kind == OrderKind.PloughField);
    }

    /// <summary>
    /// The headline claim of the whole issue: the panel's blocked line is
    /// never blank while something is actually wrong. First with no driver at
    /// all, then — once the field the order names is no longer ploughable —
    /// with the field's own stage as the reason, read straight off
    /// <see cref="MachineSystem.BlockOf"/> through <see cref="OrderText"/>.
    /// </summary>
    private void CheckThePanelShowsNoDriverThenTheBlockedReasonOnceStageIsWrong()
    {
        _sim.Step(5);
        _panel.Refresh();
        Check("an ordered but undriven tractor reports no driver on the panel",
            _panel.BlockedLine == OrderText.Describe(OrderBlock.NoDriver));
        Check("and it is not actually moving toward anything yet",
            _panel.StepLine == OrderText.Describe(OrderStep.Idle));

        Check("a worker can be hired and crewed",
            _pool.TryHire(out EntityId driver) == HireResult.Ok
            && _fleet.TryAssign(driver, _tractor) == AssignResult.Ok);

        var seen = new HashSet<(string step, string blocked)>();
        for (int t = 0; t < 400 && _world.Crops.StageOf(_field.Crop) != CropStage.Ploughed; t++)
        {
            _sim.Step();
            _panel.Refresh();
            seen.Add((_panel.StepLine, _panel.BlockedLine));
        }
        Check("the field reached ploughed", _world.Crops.StageOf(_field.Crop) == CropStage.Ploughed);
        Check("along the way the panel showed it ploughing with nothing blocking it",
            seen.Contains((OrderText.Describe(OrderStep.Ploughing), "—")));

        // The order repeats forever, so the very next attempt on an already-
        // ploughed field is the ordinary case of a blocked vehicle, not a
        // second order — see Order.cs.
        _sim.Step(10);
        _panel.Refresh();
        Check("ploughing an already-ploughed field reports the field is not ready",
            _panel.BlockedLine == OrderText.Describe(OrderBlock.WrongStage));
    }

    /// <summary>
    /// A haul needs two clicks, not one — the source, then the destination —
    /// and stays armed between them. The good is never asked for: grain is the
    /// only real entry in <see cref="ItemTypes"/> today, so naming it is
    /// content nothing yet exists to pick between, the same restraint the
    /// order roster itself takes.
    /// </summary>
    private void CheckAHaulNeedsTwoTargetsAndDefaultsToTheOnlyRealGood()
    {
        Machine? node = _world.SpawnMachine(MachineKind.Truck);
        Check("a truck is on the road", node != null);
        EntityId truck = node!.Entity;
        _panel.Select(truck);

        Check("a truck cannot be offered field work", !_panel.BeginPicking(OrderKind.PloughField));
        Check("but hauling is offered", _panel.BeginPicking(OrderKind.HaulGoods));

        Check("a bare road cell is refused as a haul target", !_panel.PickTargetAt(_roadCell));
        Check("the source building is accepted but a haul needs a second target",
            _panel.PickTargetAt(_structA.Cells[0]) && _panel.PickingAction == OrderKind.HaulGoods);
        Check("the destination building completes it",
            _panel.PickTargetAt(_structB.Cells[0]) && _panel.PickingAction == null);
        Check("the truck is now queued to haul grain from the source to the destination",
            _machines.PendingOrderOf(truck) == Order.Haul(ItemTypes.Grain, _structA.Id, _structB.Id));

        // A field is a legal *source* and never a destination: grain comes out
        // of a field's own harvest buffer, and nothing sells or stores back
        // into one. The panel is the only place that rule is enforced against
        // a click, so it is asserted here rather than left to the sim.
        Check("picking can start again over the same truck", _panel.BeginPicking(OrderKind.HaulGoods));
        Check("a field is accepted as the thing to load from",
            _panel.PickTargetAt(_field.Cells[0]) && _panel.PickingAction == OrderKind.HaulGoods);
        Check("but a field is refused as the place to deliver to",
            !_panel.PickTargetAt(_field.Cells[0])
            && _panel.PickingAction == OrderKind.HaulGoods);
        Check("a building completes the field-sourced haul",
            _panel.PickTargetAt(_structB.Cells[0]) && _panel.PickingAction == null);
        Check("and the order names the field, not a building, as its source",
            _machines.PendingOrderOf(truck)
                == Order.HaulFromField(ItemTypes.Grain, _field.Id, _structB.Id));
    }

    private void CheckCancelPickingLeavesTheRunningOrderAlone()
    {
        Order? before = _machines.OrderOf(_tractor);
        _panel.Select(_tractor);
        Check("harvesting is offered", _panel.BeginPicking(OrderKind.HarvestField));

        _panel.CancelPicking();
        Check("cancelling drops picking mode", _panel.PickingAction == null);
        Check("and never touched the order actually running",
            _machines.OrderOf(_tractor) == before);
    }

    private void CheckStopOrderQueuesIdle()
    {
        _panel.Select(_tractor);
        Check("the panel's stop button reports the sim accepted it",
            _panel.StopOrder() == SetOrderResult.Ok);
        for (int t = 0; t < 20 && _machines.OrderOf(_tractor) != null; t++)
        {
            _sim.Step();
        }
        Check("stopping the order eventually leaves the vehicle idle",
            _machines.OrderOf(_tractor) == null);
    }

    /// <summary>
    /// The one lookup <see cref="FieldInspector"/> reads to yield a click that
    /// belongs to this panel instead — proven directly, since the smoke test
    /// suite never synthesizes the click itself.
    /// </summary>
    private void CheckVehicleAtIsTheSameLookupBothPanelsShareAtACell()
    {
        Vector2I cell = _machines.CellOf(_tractor);
        Check("VehicleAt resolves the tractor at its own cell",
            VehicleInspector.VehicleAt(_world, cell) == _tractor);
        Check("and answers nobody off a cell nothing could ever be parked on",
            VehicleInspector.VehicleAt(_world, new Vector2I(-999, -999)) == EntityId.None);
    }

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
        GD.Print(_failed ? "VEHICLE INSPECTOR SMOKE TEST FAILED" : "VEHICLE INSPECTOR SMOKE TEST PASSED");
        GetTree().Quit(_failed ? 1 : 0);
    }

    private void Check(string what, bool ok)
    {
        GD.Print($"{(ok ? "PASS" : "FAIL")}: {what}");
        _failed |= !ok;
    }
}
