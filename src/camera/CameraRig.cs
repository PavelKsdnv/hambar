using Godot;

namespace Arable;

/// <summary>
/// RTS-style isometric camera rig. The rig node itself sits on the ground plane
/// and only yaws; the child Camera3D holds the fixed iso pitch and orthographic
/// projection. Pan with WASD/arrows or middle-mouse drag, zoom with the wheel,
/// rotate in 90° steps with Q/E.
/// </summary>
public partial class CameraRig : Node3D
{
    [Export] public float PanSpeed { get; set; } = 25f;
    [Export] public float ZoomMin { get; set; } = 6f;
    [Export] public float ZoomMax { get; set; } = 80f;
    [Export] public float ZoomStepFactor { get; set; } = 1.15f;
    [Export] public float ZoomSmoothing { get; set; } = 12f;
    [Export] public float RotateSmoothing { get; set; } = 10f;

    private Camera3D _camera = null!;
    private float _yaw;
    private float _targetYaw;

    /// <summary>
    /// Yaw the rig is easing toward, in radians. Exposed read-only so tests can
    /// assert the exact rotation step without waiting out the smoothing.
    /// </summary>
    public float TargetYaw => _targetYaw;
    private float _targetZoom;
    private bool _dragging;

    public override void _Ready()
    {
        _camera = GetNode<Camera3D>("Camera3D");
        _yaw = Rotation.Y;
        _targetYaw = _yaw;
        _targetZoom = _camera.Size;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed("camera_zoom_in"))
        {
            _targetZoom = Mathf.Clamp(_targetZoom / ZoomStepFactor, ZoomMin, ZoomMax);
        }
        else if (@event.IsActionPressed("camera_zoom_out"))
        {
            _targetZoom = Mathf.Clamp(_targetZoom * ZoomStepFactor, ZoomMin, ZoomMax);
        }
        else if (@event.IsActionPressed("camera_rotate_left"))
        {
            _targetYaw += Mathf.Pi / 2f;
        }
        else if (@event.IsActionPressed("camera_rotate_right"))
        {
            _targetYaw -= Mathf.Pi / 2f;
        }
        else if (@event.IsAction("camera_drag"))
        {
            _dragging = @event.IsPressed();
        }
        else if (_dragging && @event is InputEventMouseMotion motion)
        {
            DragPan(motion.Relative);
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        Vector2 input = Input.GetVector(
            "camera_left", "camera_right", "camera_forward", "camera_back");
        if (input != Vector2.Zero)
        {
            // Scale pan speed with zoom so screen-space speed feels constant.
            float speed = PanSpeed * (_camera.Size / 20f);
            Vector3 dir = new(input.X, 0f, input.Y);
            Position += new Basis(Vector3.Up, _yaw) * dir * (speed * dt);
        }

        // Exponential decay smoothing, frame-rate independent.
        float yawWeight = 1f - Mathf.Exp(-RotateSmoothing * dt);
        _yaw = Mathf.Lerp(_yaw, _targetYaw, yawWeight);
        Rotation = new Vector3(0f, _yaw, 0f);

        float zoomWeight = 1f - Mathf.Exp(-ZoomSmoothing * dt);
        _camera.Size = Mathf.Lerp(_camera.Size, _targetZoom, zoomWeight);
    }

    /// <summary>Moves the rig so the ground point under the cursor follows the drag.</summary>
    private void DragPan(Vector2 relative)
    {
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        // Orthographic size is the world-space height of the view.
        float unitsPerPixel = _camera.Size / viewport.Y;
        // Vertical screen motion is foreshortened by the camera pitch when
        // projected onto the ground plane.
        float pitch = -_camera.Rotation.X;
        Vector3 move = new(
            -relative.X * unitsPerPixel,
            0f,
            -relative.Y * unitsPerPixel / Mathf.Sin(pitch));
        Position += new Basis(Vector3.Up, _yaw) * move;
    }
}
