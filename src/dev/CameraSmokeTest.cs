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
            Check("zoom shrinks ortho size", _camera.Size < _startZoom - 0.5f);
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
