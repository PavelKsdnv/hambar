using Godot;

namespace Arable;

/// <summary>
/// Headless smoke test for <see cref="CameraRig"/>: instances Main.tscn, feeds
/// synthetic input, and asserts the rig pans, rotates, and zooms. Run with:
/// godot --headless res://scenes/dev/CameraSmokeTest.tscn
/// Exits 0 on pass, 1 on failure.
/// </summary>
public partial class CameraSmokeTest : Node
{
    private CameraRig _rig = null!;
    private Camera3D _camera = null!;
    private Vector3 _startPosition;
    private Vector3 _dragStart;
    private float _startYaw;
    private float _startZoom;
    private int _frame;
    private bool _failed;

    public override void _Ready()
    {
        Node main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        AddChild(main);
        _rig = main.GetNode<CameraRig>("CameraRig");
        _camera = _rig.GetNode<Camera3D>("Camera3D");
        _startPosition = _rig.Position;
        _startYaw = _rig.Rotation.Y;
        _startZoom = _camera.Size;

        Input.ActionPress("camera_forward");
    }

    public override void _Process(double delta)
    {
        _frame++;
        if (_frame == 30)
        {
            Check("pan moves the rig", _rig.Position.DistanceTo(_startPosition) > 0.1f);
            Input.ActionRelease("camera_forward");
            SendAction("camera_rotate_left");
            SendAction("camera_zoom_in");
        }
        else if (_frame == 90)
        {
            Check("rotate changes yaw", Mathf.Abs(_rig.Rotation.Y - _startYaw) > 0.5f);
            // A rotate step is exactly a quarter turn: the rig eases into it, so
            // compare the settled target rather than the smoothed angle.
            Check("rotate steps by 90 degrees",
                Mathf.Abs(Mathf.Abs(_rig.TargetYaw - _startYaw) - Mathf.Pi / 2f) < 0.001f);
            Check("zoom shrinks ortho size", _camera.Size < _startZoom - 0.5f);

            // Middle-mouse drag pan: press the drag button, then move the mouse.
            _dragStart = _rig.Position;
            Input.ParseInputEvent(
                new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = true });
            Input.ParseInputEvent(new InputEventMouseMotion { Relative = new Vector2(120f, 80f) });
        }
        else if (_frame == 92)
        {
            Check("middle-drag pans the rig", _rig.Position.DistanceTo(_dragStart) > 0.1f);

            // Releasing the button must stop the drag: further motion is ignored.
            Input.ParseInputEvent(
                new InputEventMouseButton { ButtonIndex = MouseButton.Middle, Pressed = false });
            _dragStart = _rig.Position;
            Input.ParseInputEvent(new InputEventMouseMotion { Relative = new Vector2(120f, 80f) });
        }
        else if (_frame == 94)
        {
            Check("releasing the drag button stops panning",
                _rig.Position.DistanceTo(_dragStart) < 0.001f);
            GD.Print(_failed ? "SMOKE TEST FAILED" : "SMOKE TEST PASSED");
            GetTree().Quit(_failed ? 1 : 0);
        }
    }

    private static void SendAction(string action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
    }

    private void Check(string what, bool ok)
    {
        GD.Print($"{(ok ? "PASS" : "FAIL")}: {what}");
        _failed |= !ok;
    }
}
