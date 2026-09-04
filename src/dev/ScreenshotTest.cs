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

        // One 90° step: catches a rotation that skews or flips the world.
        SendAction("camera_rotate_left");
        await Settle(SettleSeconds);
        Capture("04-rotated", $"after one rotate-left, yaw {rig.RotationDegrees.Y:0.0}°");

        // As far out as the rig allows — the "does it read at farm scale" view.
        for (int i = 0; i < ZoomOutSteps; i++)
        {
            SendAction("camera_zoom_out");
        }

        await Settle(SettleSeconds);
        Capture("05-zoomed-out", $"player zoom limit, ortho size {camera.Size:0.0}");

        // Diagnostic only: a detached camera beyond the rig's ZoomMax, so the
        // whole world is in frame even when the player could never see it.
        AddOverviewCamera(main);
        await Settle(0.2f);
        Capture("06-overview", "detached diagnostic camera, whole world");

        // The hover readout, which no other view can show. Driven by an
        // explicit pixel with _Process switched off, so it names a known cell
        // instead of following a cursor this run does not have.
        camera.MakeCurrent();
        string readout = await ShowReadout(main);
        Capture("07-readout", $"hover readout at screen center — {readout.ReplaceLineEndings(" | ")}");

        GD.Print(_failed ? "SCREENSHOT TEST FAILED" : "SCREENSHOT TEST PASSED");
        GetTree().Quit(_failed ? 1 : 0);
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
