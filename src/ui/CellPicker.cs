using Godot;

namespace Arable;

/// <summary>
/// Screen → world-cell picking: the one implementation of "which cell is under
/// that pixel". Shared by every mouse-driven tool (road building, the hover
/// readout, and the build tools to come) so the answer can never drift between
/// callers.
///
/// The ray is cast against the ground plane at y = 0 analytically — no physics
/// bodies, no collision layers — which keeps picking exact, deterministic, and
/// independent of what happens to be drawn on the cell.
/// </summary>
public static class CellPicker
{
    private static readonly Plane Ground = new(Vector3.Up, 0f);

    /// <summary>
    /// Where the camera ray through a screen position meets the ground plane,
    /// or null when there is no camera or the ray runs parallel to the plane.
    /// </summary>
    public static Vector3? GroundPoint(Camera3D? camera, Vector2 screenPosition)
    {
        if (camera == null)
        {
            return null;
        }
        return Ground.IntersectsRay(
            camera.ProjectRayOrigin(screenPosition), camera.ProjectRayNormal(screenPosition));
    }

    /// <summary>
    /// The cell under a screen position, or null when it cannot be resolved.
    /// The cell is not clamped to the map: off-map picks come back as real
    /// coordinates, and callers ask <see cref="WorldGrid.InBounds"/> or
    /// <see cref="WorldGrid.GetTerrain"/> what that means.
    /// </summary>
    public static Vector2I? CellAt(WorldGrid? world, Camera3D? camera, Vector2 screenPosition)
    {
        if (world == null)
        {
            return null;
        }
        return GroundPoint(camera, screenPosition) is { } point ? world.WorldToCell(point) : null;
    }

    /// <summary>
    /// Same, taking the camera from the node's viewport — the form scene nodes
    /// use, since they always pick through the camera the player is looking at.
    /// </summary>
    public static Vector2I? CellAt(Node context, WorldGrid? world, Vector2 screenPosition) =>
        CellAt(world, context.GetViewport()?.GetCamera3D(), screenPosition);

    /// <summary>The cell under the mouse cursor of the node's viewport.</summary>
    public static Vector2I? CellUnderMouse(Node context, WorldGrid? world)
    {
        Viewport? viewport = context.GetViewport();
        return viewport == null
            ? null
            : CellAt(world, viewport.GetCamera3D(), viewport.GetMousePosition());
    }
}
