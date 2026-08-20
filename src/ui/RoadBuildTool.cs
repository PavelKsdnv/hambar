using Godot;

namespace Arable;

/// <summary>
/// Road-building tool, toggled by menu key 1. While active, a blinking square
/// highlights the cell under the cursor. The first left click anchors the road
/// start and a preview line follows the cursor; a second left click places the
/// road — a straight Bresenham line between the two cells, stair-stepping
/// through diagonals so the road stays 4-connected for machines.
/// Right click (or Esc) cancels the pending anchor first, then
/// deactivates the tool. The tool stays active after placing, ready for the
/// next road.
/// </summary>
public partial class RoadBuildTool : Node3D
{
    /// <summary>Highlights hover just above the road deck so they never z-fight.</summary>
    private const float HighlightY = Machine.DeckHeight + 0.05f;
    private const float BlinkPeriod = 0.5f;
    private const float BlinkDutyCycle = 0.6f;

    private static readonly Color CursorColor = new(1f, 0.95f, 0.3f, 0.8f);
    private static readonly Color PreviewColor = new(1f, 0.85f, 0.25f, 0.45f);

    [Export] public WorldGrid? World { get; set; }

    public bool Active { get; private set; }
    public Vector2I? Anchor { get; private set; }

    private MeshInstance3D _cursor = null!;
    private MultiMesh _previewMesh = null!;
    private Vector2I? _hoverCell;
    private float _time;

    public override void _Ready()
    {
        _cursor = new MeshInstance3D { Mesh = MakeSquare(CursorColor), Visible = false };
        AddChild(_cursor);

        _previewMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = MakeSquare(PreviewColor),
        };
        AddChild(new MultiMeshInstance3D { Multimesh = _previewMesh });
    }

    public void Toggle()
    {
        Active = !Active;
        Anchor = null;
        if (!Active)
        {
            _cursor.Visible = false;
            _previewMesh.InstanceCount = 0;
        }
    }

    /// <summary>
    /// One build click on a cell: the first anchors the road start, the second
    /// places the road line and re-arms for the next one. Called from mouse
    /// input with the picked cell; public so tests can drive the tool directly.
    /// </summary>
    public void ClickCell(Vector2I cell)
    {
        if (Anchor is { } anchor)
        {
            World?.BuildRoadLine(anchor, cell);
            Anchor = null;
        }
        else
        {
            Anchor = cell;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Active)
        {
            return;
        }

        if (@event is InputEventMouseButton { Pressed: true } click)
        {
            if (click.ButtonIndex == MouseButton.Left && _hoverCell is { } cell)
            {
                ClickCell(cell);
                GetViewport().SetInputAsHandled();
            }
            else if (click.ButtonIndex == MouseButton.Right)
            {
                CancelStep();
                GetViewport().SetInputAsHandled();
            }
        }
        else if (@event.IsActionPressed("ui_cancel"))
        {
            CancelStep();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (!Active || World == null)
        {
            return;
        }
        _time += (float)delta;
        _hoverCell = PickCell();

        if (_hoverCell is { } hover)
        {
            _cursor.Position = World.CellToWorld(hover) + Vector3.Up * HighlightY;
            _cursor.Visible = Mathf.PosMod(_time, BlinkPeriod) < BlinkPeriod * BlinkDutyCycle;
        }
        else
        {
            _cursor.Visible = false;
        }

        UpdatePreview();
    }

    /// <summary>Right click / Esc: drop the anchor if set, otherwise exit the tool.</summary>
    private void CancelStep()
    {
        if (Anchor != null)
        {
            Anchor = null;
        }
        else
        {
            Toggle();
        }
    }

    /// <summary>The world cell under the mouse cursor (ray vs. ground plane).</summary>
    private Vector2I? PickCell()
    {
        Camera3D? camera = GetViewport().GetCamera3D();
        if (camera == null)
        {
            return null;
        }
        Vector2 mouse = GetViewport().GetMousePosition();
        Vector3? hit = new Plane(Vector3.Up, 0f)
            .IntersectsRay(camera.ProjectRayOrigin(mouse), camera.ProjectRayNormal(mouse));
        return hit is { } point ? World!.WorldToCell(point) : null;
    }

    private void UpdatePreview()
    {
        if (Anchor is not { } anchor || _hoverCell is not { } hover)
        {
            _previewMesh.InstanceCount = 0;
            return;
        }

        var cells = WorldGrid.LineCells(anchor, hover);
        _previewMesh.InstanceCount = cells.Count;
        for (int i = 0; i < cells.Count; i++)
        {
            Vector3 position = World!.CellToWorld(cells[i]) + Vector3.Up * HighlightY;
            _previewMesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, position));
        }
    }

    /// <summary>Flat unshaded translucent square, slightly smaller than a cell.</summary>
    private static PlaneMesh MakeSquare(Color color) => new()
    {
        Size = new Vector2(1.8f, 1.8f),
        Material = new StandardMaterial3D
        {
            AlbedoColor = color,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        },
    };
}
