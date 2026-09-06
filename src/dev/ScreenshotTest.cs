using Godot;

namespace Arable;

/// <summary>
/// Renders the canonical views to PNG so a human — or an agent — can look at
/// what the game actually draws. This is the visual counterpart to the headless
/// smoke tests, which assert state but never see a pixel.
///
/// Cannot run under --headless: that uses the dummy rasterizer, so there is no
/// framebuffer to read back and FramePostDraw never fires. Run windowed:
///   godot --path . res://scenes/dev/ScreenshotTest.tscn -- &lt;output-dir&gt;
/// Output dir defaults to user://screenshots. Exits 0 if every view was
/// captured, 1 otherwise.
/// </summary>
public partial class ScreenshotTest : Node
{
    /// <summary>
    /// Seconds to let the rig's exponential smoothing converge. Measured in
    /// time, not frames: the smoothing is dt-based, so a frame count would
    /// settle differently on a fast machine than a slow one.
    /// </summary>
    private const float SettleSeconds = 1.5f;

    /// <summary>20 * 1.15^10 ≈ 80, the rig's ZoomMax — i.e. as far out as the player can go.</summary>
    private const int ZoomOutSteps = 10;

    private string _outDir = "user://screenshots";
    private bool _failed;

    public override void _Ready()
    {
        string[] userArgs = OS.GetCmdlineUserArgs();
        if (userArgs.Length > 0)
        {
            _outDir = userArgs[0];
        }

        if (DisplayServer.GetName() == "headless")
        {
            GD.PrintErr("SCREENSHOT TEST FAILED: --headless cannot render; run windowed.");
            GetTree().Quit(1);
            return;
        }

        PrepareOutDir();
        GD.Print($"output dir: {ProjectSettings.GlobalizePath(_outDir)}");

        Node main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        AddChild(main);

        Run(main);
    }

    /// <summary>
    /// Creates the output dir and deletes stale PNGs, so a failed run can never
    /// leave an old screenshot behind to be mistaken for a fresh one.
    /// </summary>
    private void PrepareOutDir()
    {
        DirAccess.MakeDirRecursiveAbsolute(_outDir);
        using DirAccess dir = DirAccess.Open(_outDir);
        if (dir is null)
        {
            return;
        }

        foreach (string file in dir.GetFiles())
        {
            if (file.EndsWith(".png"))
            {
                dir.Remove(file);
            }
        }
    }

    private async void Run(Node main)
    {
        var rig = main.GetNode<CameraRig>("CameraRig");
        var camera = rig.GetNode<Camera3D>("Camera3D");

        // The hover readout is a dev instrument, not part of the canonical
        // views — and with no real cursor it would only report whatever cell
        // pixel (0, 0) happens to sit over. Switch it off so the screenshots
        // show the world and nothing else.
        main.GetNodeOrNull<CellInspector>("CellInspector")?.SetEnabled(false);

        // The view the player gets on load.
        await Settle(SettleSeconds);
        Capture("01-start", $"default pose, ortho size {camera.Size:0.0}");

        // The build ghost, which no other view can show. "The ghost shows a
        // refusal before the click" is a claim about pixels, not about state,
        // so both verdicts get a picture.
        await ShowBuildGhost(main, rig);

        // Fields, which nothing before M2 could put on the map at all: the
        // live rectangle during a drag, then the same rectangle committed.
        await ShowFieldRectangle(main, rig);

        // The must-touch-a-road rule, which is what makes the road network
        // load-bearing rather than decorative, plus the first dev-art tile
        // with real height on it.
        await ShowStructure(main, rig);

        // The bulldozer's mixed drag, the one ghost that paints per cell.
        await ShowBulldozeGhost(main, rig);

        // One 90° step: catches a rotation that skews or flips the world.
        SendAction("camera_rotate_left");
        await Settle(SettleSeconds);
        Capture("09-rotated", $"after one rotate-left, yaw {rig.RotationDegrees.Y:0.0}°");

        // As far out as the rig allows — the "does it read at farm scale" view.
        for (int i = 0; i < ZoomOutSteps; i++)
        {
            SendAction("camera_zoom_out");
        }

        await Settle(SettleSeconds);
        Capture("10-zoomed-out", $"player zoom limit, ortho size {camera.Size:0.0}");

        // Diagnostic only: a detached camera beyond the rig's ZoomMax, so the
        // whole world is in frame even when the player could never see it.
        AddOverviewCamera(main);
        await Settle(0.2f);
        Capture("11-overview", "detached diagnostic camera, whole world");

        // The hover readout, which no other view can show. Driven by an
        // explicit pixel with _Process switched off, so it names a known cell
        // instead of following a cursor this run does not have.
        camera.MakeCurrent();
        string readout = await ShowReadout(main);
        Capture("12-readout", $"hover readout at screen center — {readout.ReplaceLineEndings(" | ")}");

        // The time controls and the date they drive, which no earlier view can
        // show doing anything: the bar is on screen in all of them, but only
        // here is it standing on a date other than the first morning of play,
        // and only here does the picture say which speed is armed.
        await ShowTimeControls(main);

        // The crop stages, which no view before M4 could contain: the map had
        // nothing on it that changed by itself. Last, because it is the only
        // section that writes fields into the world *and* runs the clock, and
        // every earlier view should photograph the world it always did.
        await ShowCropStages(main, rig);

        GD.Print(_failed ? "SCREENSHOT TEST FAILED" : "SCREENSHOT TEST PASSED");
        GetTree().Quit(_failed ? 1 : 0);
    }

    /// <summary>
    /// Captures the time controls in the two states a still picture can tell
    /// apart: running at the fastest step, and paused. Which button is filled
    /// amber is a claim about pixels rather than about state, and so is "the
    /// season and the day are legible on screen" — the whole point of the
    /// readout.
    ///
    /// The date is <b>set</b> rather than waited for. Reaching midsummer
    /// honestly would take twenty minutes of real time even at 3x, and the
    /// picture is about legibility, not about arithmetic the smoke test already
    /// pins. Setting it also keeps the view deterministic: a date the run
    /// drifted into would depend on how long the searches above happened to
    /// take.
    /// </summary>
    private async System.Threading.Tasks.Task ShowTimeControls(Node main)
    {
        const int MidSeasonDay = 7;

        var sim = main.GetNodeOrNull<Simulation>("Sim");
        var controls = main.GetNodeOrNull<TimeControls>("Hud/TimeControls");
        if (sim == null || controls == null)
        {
            GD.Print("FAIL: time controls — no Sim or Hud/TimeControls in Main.tscn");
            _failed = true;
            return;
        }

        sim.Calendar.SetDate(1, Season.Summer, MidSeasonDay);

        int fastest = sim.Speeds.Count - 1;
        controls.Select(fastest);
        await Settle(0.3f);
        Capture("13-time-running",
            $"time controls running at {controls.Entry(fastest)?.LabelText} "
            + $"— readout says \"{controls.DateText}\"");

        controls.Select(0);
        await Settle(0.3f);
        Capture("14-time-paused",
            $"time controls paused, the world still drawn — readout says "
            + $"\"{controls.DateText}\", clock at tick {sim.TickCount}");

        if (!sim.IsPaused || controls.ActiveIndex != 0)
        {
            GD.Print("FAIL: 14-time-paused — the pause button did not stop the clock");
            _failed = true;
        }
    }

    /// <summary>
    /// The six crop stages standing next to each other, at three zooms. This is
    /// the view that answers M1's question with content that actually changes:
    /// the whole claim of #29 is that a field's stage is readable off the map
    /// without opening a panel, and only a picture can say whether it is.
    ///
    /// <b>Abutting, not spaced.</b> Six fields in a block with no gap is the
    /// hard case — a stage only reads if it reads against the stage next to it,
    /// not against bare ground — and it is what a real farm looks like.
    ///
    /// <b>Grown, not set.</b> The stages are reached by ploughing, sowing and
    /// stepping the clock, so the picture is of the state machine's own output;
    /// there is no door that writes a stage directly, and inventing one for a
    /// screenshot is how a view starts lying. The date is put at the start of
    /// spring first, because the run arrives here at whatever date the earlier
    /// views left, and a crop sown in the shipped winter never ripens at all.
    ///
    /// <b>Nothing here refreshes the view.</b> The tiles change because
    /// <c>WorldGrid</c> sweeps its fields every frame and <see cref="Settle"/>
    /// waits real ones — which is the "without a manual refresh" claim,
    /// photographed rather than asserted.
    /// </summary>
    private async System.Threading.Tasks.Task ShowCropStages(Node main, CameraRig rig)
    {
        // Six 2x2 fields laid out 3 across and 2 down, in stage order.
        const int Span = 2;
        const int Columns = 3;
        const int Rows = 2;
        const int SearchRadius = 18;

        // Growth is banked per tick against a rate the ground sets, so the wait
        // is a budget rather than a schedule: generous enough for poor soil,
        // finite so a stalled crop ends the run instead of hanging it.
        const int StepChunk = 100;
        const int MaxTicks = 60_000;

        CropStage[] wanted =
        [
            CropStage.Fallow, CropStage.Ploughed, CropStage.Sown,
            CropStage.Growing, CropStage.Harvestable, CropStage.Stubble,
        ];

        var world = main.GetNodeOrNull<WorldGrid>("World");
        var sim = main.GetNodeOrNull<Simulation>("Sim");
        if (world == null || sim == null)
        {
            GD.Print("FAIL: crop stages — no World or Sim in Main.tscn");
            _failed = true;
            return;
        }

        // The readout was pinned to the screen centre by the view above; it is a
        // dev instrument, and these frames are about the tiles.
        main.GetNodeOrNull<CellInspector>("CellInspector")?.SetEnabled(false);
        sim.Calendar.SetDate(1, Season.Spring, 1);

        Vector2I? corner = FindCropBlock(world, Columns * Span, Rows * Span, SearchRadius);
        if (corner is not { } origin)
        {
            GD.Print($"FAIL: crop stages — no clear {Columns * Span}x{Rows * Span} "
                + $"soil block within {SearchRadius} cells of the origin");
            _failed = true;
            return;
        }

        var fields = new Field[wanted.Length];
        for (int i = 0; i < wanted.Length; i++)
        {
            var from = new Vector2I(
                origin.X + (i % Columns) * Span, origin.Y + (i / Columns) * Span);
            Vector2I to = from + new Vector2I(Span - 1, Span - 1);
            Field? field = world.MarkField(WorldGrid.RectCells(from, to));
            if (field == null)
            {
                GD.Print($"FAIL: crop stages — marking the {wanted[i]} field failed");
                _failed = true;
                return;
            }
            fields[i] = field;
        }

        CropSystem crops = world.Crops;

        // Everything but the fallow one gets ploughed. The two that have to be
        // standing ripe when the shutter falls are sown first and grown out
        // together; one of them is then cut, which is what leaves stubble.
        for (int i = 1; i < fields.Length; i++)
        {
            crops.Plough(fields[i].Crop);
        }
        crops.Sow(fields[4].Crop);
        crops.Sow(fields[5].Crop);
        StepUntil(sim, crops, fields[4].Crop, CropStage.Harvestable, StepChunk, MaxTicks);
        StepUntil(sim, crops, fields[5].Crop, CropStage.Harvestable, StepChunk, MaxTicks);
        crops.Harvest(fields[5].Crop);

        // Only now the growing one, so it is still half-grown while the ripe one
        // waits — a ripe field never moves on by itself, which is what lets one
        // frame hold two stages that are days apart.
        crops.Sow(fields[3].Crop);
        StepUntil(sim, crops, fields[3].Crop, CropStage.Growing, StepChunk, MaxTicks);

        // And the sown one last of all: a single tick of growth would sprout it.
        crops.Sow(fields[2].Crop);

        var reached = new System.Text.StringBuilder();
        bool asWanted = true;
        for (int i = 0; i < fields.Length; i++)
        {
            CropStage stage = crops.StageOf(fields[i].Crop);
            asWanted &= stage == wanted[i];
            reached.Append(reached.Length > 0 ? ", " : string.Empty)
                .Append(CropSystem.Name(stage));
        }

        if (!asWanted)
        {
            GD.Print($"FAIL: crop stages — the fields ended on {reached}, "
                + "not the six stages asked for");
            _failed = true;
        }

        rig.Position = world.CellToWorld(
            origin + new Vector2I(Columns * Span / 2, Rows * Span / 2));
        string what = $"six 2x2 fields abutting, left to right then down: {reached}";

        // Back in from the zoom-out view above, then out again a step at a time,
        // because the readability claim is about the range the rig allows and
        // not about one framing.
        await ZoomTo(rig, ZoomOutSteps, zoomIn: true);
        Capture("15-crop-stages-near", $"{what} — ortho size {Zoom(rig):0.0}");

        await ZoomTo(rig, ZoomOutSteps / 2, zoomIn: false);
        Capture("16-crop-stages-mid", $"the same six at mid zoom — ortho size {Zoom(rig):0.0}");

        await ZoomTo(rig, ZoomOutSteps - ZoomOutSteps / 2, zoomIn: false);
        Capture("17-crop-stages-far",
            $"the same six at the rig's far limit — ortho size {Zoom(rig):0.0}");
    }

    /// <summary>
    /// Runs the clock until the field reaches the stage, in chunks, giving up
    /// after <paramref name="maxTicks"/>. Stepped rather than played: the growth
    /// is days of game time and the shutter should not wait for them.
    /// </summary>
    private static void StepUntil(
        Simulation sim, CropSystem crops, EntityId row, CropStage want,
        int chunk, int maxTicks)
    {
        for (int t = 0; t < maxTicks && crops.StageOf(row) != want; t += chunk)
        {
            sim.Step(chunk);
        }
    }

    /// <summary>
    /// The min corner of a clear, workable block of soil big enough for the
    /// field layout — searched outward from the origin, never assumed, so a seed
    /// change moves the picture rather than quietly photographing a refusal.
    /// Poor ground is skipped as well as unusable ground: a crop on soil that
    /// barely grows would spend the whole tick budget getting ripe.
    /// </summary>
    private static Vector2I? FindCropBlock(WorldGrid world, int width, int height, int radius)
    {
        const float WantFertility = 0.35f;

        // Two passes, because the fertility floor is a preference and the clear
        // ground is a requirement: a stony seed still gets a picture.
        for (int pass = 0; pass < 2; pass++)
        {
            float floor = pass == 0 ? WantFertility : 0f;
            for (int r = 0; r <= radius; r++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != r)
                        {
                            continue;
                        }

                        var corner = new Vector2I(dx, dz);
                        if (IsClearBlock(world, corner, width, height, floor))
                        {
                            return corner;
                        }
                    }
                }
            }
        }
        return null;
    }

    private static bool IsClearBlock(
        WorldGrid world, Vector2I corner, int width, int height, float minFertility)
    {
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                var cell = new Vector2I(corner.X + x, corner.Y + z);
                if (!world.IsSoil(cell)
                    || world.GetTile(cell) != TileType.Empty
                    || world.GetFertility(cell) < minFertility)
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>The rig camera's current orthographic size — how far out the view is.</summary>
    private static float Zoom(CameraRig rig) => rig.GetNode<Camera3D>("Camera3D").Size;

    /// <summary>
    /// Steps the zoom and lets the rig's smoothing land on it. Through the input
    /// action rather than the camera, so the framing is one the player could
    /// actually reach — and settled by time, because the smoothing is dt-based.
    /// </summary>
    private async System.Threading.Tasks.Task ZoomTo(CameraRig rig, int steps, bool zoomIn)
    {
        for (int i = 0; i < steps; i++)
        {
            SendAction(zoomIn ? "camera_zoom_in" : "camera_zoom_out");
        }
        await Settle(SettleSeconds);
    }

    /// <summary>
    /// Switches the hover readout on and pins it to the middle of the screen.
    /// The inspector normally follows the mouse in <c>_Process</c>; there is no
    /// cursor in a screenshot run, so processing is stopped and the position is
    /// supplied directly, which makes the captured readout deterministic.
    /// Returns the text it ended up showing.
    /// </summary>
    private async System.Threading.Tasks.Task<string> ShowReadout(Node main)
    {
        var inspector = main.GetNodeOrNull<CellInspector>("CellInspector");
        if (inspector == null)
        {
            GD.Print("FAIL: 05-readout — no CellInspector in Main.tscn");
            _failed = true;
            return string.Empty;
        }

        inspector.SetEnabled(true);
        inspector.SetProcess(false);
        inspector.Inspect(GetViewport().GetVisibleRect().Size / 2f);
        await Settle(0.2f);
        return inspector.Text;
    }

    /// <summary>
    /// Captures the build tool's ghost preview in both verdicts: a drag the
    /// rules accept and one crossing ground they refuse. Which cells those are
    /// is found by asking the tool itself over a window around the origin,
    /// never assumed — a seed change must not quietly turn these into two
    /// pictures of the same verdict.
    ///
    /// The tool normally takes its hovered cell from the cursor in
    /// <c>_Process</c>; a screenshot run has no cursor, so processing is
    /// stopped and the hover is supplied directly, which is also what makes the
    /// captured ghost deterministic. The rig is parked over the drag so the
    /// ghost is framed wherever the seed put the unbuildable ground.
    /// </summary>
    private async System.Threading.Tasks.Task ShowBuildGhost(Node main, CameraRig rig)
    {
        var world = main.GetNodeOrNull<WorldGrid>("World");
        var tool = main.GetNodeOrNull<BuildTool>("RoadTool");
        if (world == null || tool == null)
        {
            GD.Print("FAIL: build ghost — no World or RoadTool in Main.tscn");
            _failed = true;
            return;
        }

        Vector3 home = rig.Position;
        tool.SetProcess(false);

        // The origin is on the carved start road, so anchoring there is always
        // legal and both drags share one anchor.
        var anchor = new Vector2I(0, 0);
        await CaptureGhost(world, tool, rig, anchor, wantLegal: true,
            name: "02-ghost-legal", what: "road drag the rules accept");
        await CaptureGhost(world, tool, rig, anchor, wantLegal: false,
            name: "03-ghost-refused", what: "road drag refused before the click");

        tool.SetActive(false);
        tool.SetProcess(true);
        rig.Position = home;
    }

    /// <summary>
    /// Captures a field being marked, in the two states a still picture can
    /// tell apart: the filled rectangle ghosted mid-drag, and the field left
    /// on the map after the commit. Neither existed before M2 — terrain
    /// generation places only road, so no earlier view has ever contained a
    /// field tile, and "the rectangle previews live during the drag" is a
    /// claim about pixels rather than about state.
    ///
    /// Where the rectangle goes is found by asking the tool, never assumed: a
    /// seed change moves the clear soil, and a hard-coded rectangle would
    /// quietly start photographing a refusal. The capture writes a real field
    /// into the world and deliberately leaves it there, so the later rotated,
    /// zoomed and overview shots all carry it too.
    /// </summary>
    private async System.Threading.Tasks.Task ShowFieldRectangle(Node main, CameraRig rig)
    {
        const int Span = 3;
        const int SearchRadius = 14;

        var world = main.GetNodeOrNull<WorldGrid>("World");
        var tool = main.GetNodeOrNull<BuildTool>("FieldTool");
        if (world == null || tool == null)
        {
            GD.Print("FAIL: field rectangle — no World or FieldTool in Main.tscn");
            _failed = true;
            return;
        }

        Vector3 home = rig.Position;
        tool.SetProcess(false);
        tool.SetActive(true);

        for (int radius = 2; radius <= SearchRadius; radius++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != radius)
                    {
                        continue;
                    }

                    var from = new Vector2I(dx, dz);
                    var to = new Vector2I(dx + Span - 1, dz + Span - 1);
                    tool.SetActive(true);   // re-arm: also drops the old anchor
                    if (!tool.ClickCell(from))
                    {
                        continue;
                    }

                    tool.HoverAt(to);
                    if (tool.Preview is not { Legal: true } plan)
                    {
                        continue;
                    }

                    rig.Position = world.CellToWorld((from + to) / 2);
                    await Settle(0.3f);
                    Capture("04-field-ghost",
                        $"field rectangle previewed mid-drag — {from} to {to}, {Describe(plan)}");

                    tool.ClickCell(to);
                    tool.SetActive(false);
                    await Settle(0.3f);
                    Field? field = world.GetField(from);
                    Capture("05-field-marked",
                        $"the same rectangle committed — {field?.ToString() ?? "no field!"}, "
                        + $"bounds {field?.Bounds.ToString() ?? "-"}");

                    if (field == null)
                    {
                        GD.Print("FAIL: 05-field-marked — the commit registered no field");
                        _failed = true;
                    }

                    tool.SetProcess(true);
                    rig.Position = home;
                    return;
                }
            }
        }

        GD.Print($"FAIL: field rectangle — no legal {Span}x{Span} rectangle "
            + $"within {SearchRadius} cells of the origin");
        _failed = true;
        tool.SetActive(false);
        tool.SetProcess(true);
        rig.Position = home;
    }

    /// <summary>
    /// Captures the structure tool in both verdicts: a cell the road rule
    /// refuses, and a cell beside the road where the building actually lands.
    /// Two separate claims about pixels live here — that the ghost shows the
    /// refusal <i>before</i> the click, and that a placed structure reads as a
    /// building rather than as another flat tile, which is the first dev-art
    /// mesh with real height and so has never appeared in any view.
    ///
    /// Both cells are found by asking the tool, never assumed. The structure
    /// tool places on a single click, so the refused cell is only ever
    /// hovered; the legal one is clicked and the building deliberately left
    /// standing for the later views.
    /// </summary>
    private async System.Threading.Tasks.Task ShowStructure(Node main, CameraRig rig)
    {
        var world = main.GetNodeOrNull<WorldGrid>("World");
        var tool = main.GetNodeOrNull<BuildTool>("StructureTool");
        if (world == null || tool == null)
        {
            GD.Print("FAIL: structure — no World or StructureTool in Main.tscn");
            _failed = true;
            return;
        }

        Vector3 home = rig.Position;
        tool.SetProcess(false);
        tool.SetActive(true);

        await CaptureStructure(world, tool, rig, PlacementRefusal.NoRoadAccess,
            place: false, name: "06-structure-refused",
            what: "structure refused before the click, no road beside it");
        await CaptureStructure(world, tool, rig, PlacementRefusal.None,
            place: true, name: "07-structure-placed",
            what: "structure placed on a cell touching the road");

        tool.SetActive(false);
        tool.SetProcess(true);
        rig.Position = home;
    }

    /// <summary>
    /// Hovers outward from the origin until the tool returns
    /// <paramref name="want"/>, frames that cell and captures it — committing
    /// the placement first when <paramref name="place"/> is set, so the shot
    /// shows the building itself rather than its ghost.
    /// </summary>
    private async System.Threading.Tasks.Task CaptureStructure(
        WorldGrid world, BuildTool tool, CameraRig rig,
        PlacementRefusal want, bool place, string name, string what)
    {
        const int SearchRadius = 14;

        for (int radius = 1; radius <= SearchRadius; radius++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != radius)
                    {
                        continue;
                    }

                    var cell = new Vector2I(dx, dz);
                    tool.HoverAt(cell);
                    if (tool.Preview is not { } plan || plan.Refusal != want)
                    {
                        continue;
                    }

                    string caption = $"{what} — {cell}, {Describe(plan)}";
                    if (place)
                    {
                        tool.ClickCell(cell);
                        tool.HoverAt(null);
                        caption += $", {world.GetStructure(cell)?.ToString() ?? "no structure!"}";
                    }

                    rig.Position = world.CellToWorld(cell);
                    await Settle(0.3f);
                    Capture(name, caption);

                    if (place && world.GetStructure(cell) == null)
                    {
                        GD.Print($"FAIL: {name} — the click registered no structure");
                        _failed = true;
                    }
                    return;
                }
            }
        }

        GD.Print($"FAIL: {name} — no cell within {SearchRadius} of the origin "
            + $"gave verdict {want}");
        _failed = true;
    }

    /// <summary>
    /// Captures the bulldozer hovering a <i>mixed</i> drag — one covering both
    /// cells that hold something and cells that do not. That is the only ghost
    /// in the game painted per cell rather than per verdict: under
    /// <see cref="FootprintPolicy.AnyCell"/> the drag as a whole is legal while
    /// some of its cells will be skipped, and the picture is the claim that the
    /// ghost says so instead of promising to clear all of it.
    ///
    /// The drag is only ever hovered, never clicked: committing it would take
    /// the start road out of every view that follows.
    /// </summary>
    private async System.Threading.Tasks.Task ShowBulldozeGhost(Node main, CameraRig rig)
    {
        const int SearchRadius = 10;

        var world = main.GetNodeOrNull<WorldGrid>("World");
        var tool = main.GetNodeOrNull<BuildTool>("BulldozeTool");
        if (world == null || tool == null)
        {
            GD.Print("FAIL: bulldoze ghost — no World or BulldozeTool in Main.tscn");
            _failed = true;
            return;
        }

        Vector3 home = rig.Position;
        tool.SetProcess(false);

        for (int radius = 1; radius <= SearchRadius; radius++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != radius)
                    {
                        continue;
                    }

                    var from = new Vector2I(dx, dz);
                    var to = new Vector2I(dx + 2, dz + 2);
                    tool.SetActive(true);   // re-arm: also drops the old anchor
                    if (!tool.ClickCell(from))
                    {
                        continue;
                    }

                    tool.HoverAt(to);
                    // Mixed means both kinds present: legal overall, yet with
                    // cells the click will pass over.
                    if (tool.Preview is not { Legal: true } plan
                        || CountLegalCells(plan) is var kept
                        && (kept == 0 || kept == plan.Count))
                    {
                        continue;
                    }

                    rig.Position = world.CellToWorld((from + to) / 2);
                    await Settle(0.3f);
                    Capture("08-bulldoze-mixed",
                        $"bulldoze drag, {kept} of {plan.Count} cells hold something "
                        + $"— {from} to {to}, {Describe(plan)}");

                    tool.SetActive(false);
                    tool.SetProcess(true);
                    rig.Position = home;
                    return;
                }
            }
        }

        GD.Print($"FAIL: 08-bulldoze-mixed — no mixed drag within {SearchRadius} of the origin");
        _failed = true;
        tool.SetActive(false);
        tool.SetProcess(true);
        rig.Position = home;
    }

    /// <summary>How many cells of the plan the tool would actually act on.</summary>
    private static int CountLegalCells(PlacementPlan plan)
    {
        int legal = 0;
        for (int i = 0; i < plan.Count; i++)
        {
            if (plan.CellLegal(i))
            {
                legal++;
            }
        }
        return legal;
    }

    /// <summary>
    /// Anchors the tool at <paramref name="anchor"/> and hovers outward until
    /// the verdict is the wanted one, then frames and captures that ghost.
    /// </summary>
    private async System.Threading.Tasks.Task CaptureGhost(
        WorldGrid world, BuildTool tool, CameraRig rig,
        Vector2I anchor, bool wantLegal, string name, string what)
    {
        const int SearchRadius = 12;

        for (int radius = 2; radius <= SearchRadius; radius++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    // Ring only: inner cells were covered by a smaller radius.
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != radius)
                    {
                        continue;
                    }

                    var target = new Vector2I(anchor.X + dx, anchor.Y + dz);
                    tool.SetActive(true);      // re-arm: also drops the old anchor
                    tool.ClickCell(anchor);
                    tool.HoverAt(target);
                    if (tool.Preview is not { } plan || plan.Legal != wantLegal)
                    {
                        continue;
                    }

                    rig.Position = world.CellToWorld((anchor + target) / 2);
                    await Settle(0.3f);
                    Capture(name, $"{what} — {anchor} to {target}, {Describe(plan)}");
                    return;
                }
            }
        }

        GD.Print($"FAIL: {name} — no {(wantLegal ? "legal" : "refused")} drag "
            + $"within {SearchRadius} cells of {anchor}");
        _failed = true;
    }

    /// <summary>
    /// The plan as one line, cell by cell — so the screenshot's caption says
    /// which cells the ghost is tinting and why, instead of leaving a reader
    /// to infer a verdict from pixel colour.
    /// </summary>
    private static string Describe(PlacementPlan plan)
    {
        var cells = new System.Text.StringBuilder();
        for (int i = 0; i < plan.Count; i++)
        {
            cells.Append(i > 0 ? ", " : string.Empty)
                .Append(plan.Cells[i])
                .Append(plan.CellLegal(i) ? string.Empty : $" [{plan.CellRefusals[i]}]");
        }
        string verdict = plan.Legal ? "legal" : PlacementRules.Explain(plan.Refusal);
        return $"{plan.Count} cells, {verdict}: {cells}";
    }

    private static void AddOverviewCamera(Node main)
    {
        var overview = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = 260f,
            Far = 2000f,
            Position = new Vector3(300f, 300f, 300f),
        };
        main.AddChild(overview);
        overview.LookAt(Vector3.Zero, Vector3.Up);
        overview.MakeCurrent();
    }

    private static void SendAction(string action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
    }

    private async System.Threading.Tasks.Task Settle(float seconds)
    {
        await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        await ToSignal(RenderingServer.Singleton, RenderingServerInstance.SignalName.FramePostDraw);
    }

    private void Capture(string name, string what)
    {
        Image image = GetViewport().GetTexture().GetImage();
        if (image is null || image.IsEmpty())
        {
            GD.Print($"FAIL: {name} — viewport produced no image");
            _failed = true;
            return;
        }

        string path = $"{_outDir}/{name}.png";
        Error err = image.SavePng(path);
        if (err != Error.Ok)
        {
            GD.Print($"FAIL: {name} — SavePng returned {err}");
            _failed = true;
            return;
        }

        GD.Print($"PASS: {name} — {what} — {ProjectSettings.GlobalizePath(path)}");
    }
}
