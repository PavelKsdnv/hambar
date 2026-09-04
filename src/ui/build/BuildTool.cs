using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// Shared base for every mouse-driven placement tool: hover highlight, ghost
/// preview of what would be placed, validation that refuses an illegal
/// placement <i>before</i> the click, and the anchor to preview to place to
/// cancel interaction. Subclasses supply only three things — which tile they
/// write (<see cref="PlacedTile"/>), which rules they opt into
/// (<see cref="Rules"/>), and which cells a drag covers
/// (<see cref="Footprint"/>) — so a new tool is a dozen lines, not a copy of
/// this file.
///
/// Interaction: the first left click anchors, a second left click places, and
/// right click / Esc drops the anchor first and leaves the tool second. A tool
/// that places on a single click sets <see cref="NeedsAnchor"/> to false and
/// skips the anchor step. A refused click changes nothing at all and keeps the
/// anchor, so the player can simply re-aim.
///
/// Every state-changing entry point is public and cell-driven
/// (<see cref="SetActive"/>, <see cref="HoverAt"/>, <see cref="ClickCell"/>,
/// <see cref="Cancel"/>): that is how the headless smoke test drives tools
/// without a cursor, and reading <see cref="Preview"/> or
/// <see cref="GhostColor"/> back is how it checks what the player would see.
/// </summary>
public abstract partial class BuildTool : Node3D
{
    /// <summary>
    /// Scene group every build tool joins on entering the tree. Arming one tool
    /// disarms the rest through it, so the player can never have two tools
    /// listening to the same click — and a new tool gets that for free, without
    /// the menu (or the M2 palette) having to know the full list.
    /// </summary>
    public const string ToolGroup = "build_tools";

    /// <summary>Highlights sit just above the road deck so they never z-fight.</summary>
    protected const float HighlightY = Machine.DeckHeight + 0.05f;

    private const float BlinkPeriod = 0.5f;
    private const float BlinkDutyCycle = 0.6f;

    /// <summary>Hover square over ground the tool would accept.</summary>
    public static readonly Color CursorLegal = new(1f, 0.95f, 0.3f, 0.8f);

    /// <summary>Hover square while the placement under the cursor is refused.</summary>
    public static readonly Color CursorRefused = new(0.95f, 0.25f, 0.2f, 0.85f);

    /// <summary>Ghost cell of a placement that would go through.</summary>
    public static readonly Color GhostLegal = new(1f, 0.85f, 0.25f, 0.45f);

    /// <summary>Ghost cell that is itself the reason the placement is refused.</summary>
    public static readonly Color GhostIllegalCell = new(0.95f, 0.15f, 0.12f, 0.6f);

    /// <summary>
    /// Ghost cell that is fine on its own but belongs to a refused placement —
    /// dimmed, and never the legal colour, because nothing here will be built.
    /// </summary>
    public static readonly Color GhostRefused = new(0.8f, 0.2f, 0.16f, 0.45f);

    [Export] public WorldGrid? World { get; set; }

    /// <summary>Whether the tool is armed and reacting to the mouse.</summary>
    public bool Active { get; private set; }

    /// <summary>First clicked cell of a two-click placement, if one is pending.</summary>
    public Vector2I? Anchor { get; private set; }

    /// <summary>Cell the tool is currently pointed at, or null when there is none.</summary>
    public Vector2I? HoverCell { get; private set; }

    /// <summary>
    /// Verdict on the placement a click would make right now — what the ghost
    /// is showing. Null when the tool is inactive or pointing nowhere.
    /// </summary>
    public PlacementPlan? Preview { get; private set; }

    /// <summary>Whether a click right now would be accepted.</summary>
    public bool PreviewLegal => Preview is { Legal: true };

    /// <summary>Cells the ghost currently draws.</summary>
    public int GhostCellCount => _previewMesh.InstanceCount;

    /// <summary>
    /// Colour the ghost draws a cell in — legal, refused, or illegal. Read from
    /// the tints handed to the ghost mesh rather than from the mesh itself:
    /// under <c>--headless</c> the dummy renderer keeps no per-instance colours,
    /// so <c>MultiMesh.GetInstanceColor</c> would answer black in the very test
    /// that needs to see them.
    /// </summary>
    public Color GhostColor(int index) => _ghostColors[index];

    /// <summary>Colour of the blinking hover square.</summary>
    public Color CursorColor => _cursorMaterial.AlbedoColor;

    // --- subclass contract -------------------------------------------------

    /// <summary>
    /// Tile the tool writes. Also what <see cref="PlacementRule.NoOverlap"/>
    /// treats as a harmless no-op when a cell already holds it.
    /// </summary>
    protected abstract TileType PlacedTile { get; }

    /// <summary>Legality rules this tool opts into.</summary>
    protected abstract PlacementRule Rules { get; }

    /// <summary>
    /// Cells a placement from <paramref name="anchor"/> to
    /// <paramref name="cell"/> would cover. Called with anchor == cell for the
    /// anchoring click and for single-click tools.
    /// </summary>
    protected abstract IReadOnlyList<Vector2I> Footprint(Vector2I anchor, Vector2I cell);

    /// <summary>
    /// Writes the placement. Only ever called with a legal plan. The default
    /// stamps <see cref="PlacedTile"/> over every cell of the footprint;
    /// override for tools that place something other than plain tiles.
    /// </summary>
    protected virtual void Apply(PlacementPlan plan)
    {
        foreach (Vector2I cell in plan.Cells)
        {
            World!.SetTile(cell, PlacedTile);
        }
    }

    /// <summary>
    /// False for tools that place on a single click (no drag), which then show
    /// their ghost as soon as the cursor moves.
    /// </summary>
    protected virtual bool NeedsAnchor => true;

    // -----------------------------------------------------------------------

    private MeshInstance3D _cursor = null!;
    private StandardMaterial3D _cursorMaterial = null!;
    private MultiMesh _previewMesh = null!;
    private readonly List<Color> _ghostColors = [];
    private float _time;

    public override void _Ready()
    {
        AddToGroup(ToolGroup);
        _cursorMaterial = MakeMaterial(CursorLegal);
        _cursor = new MeshInstance3D { Mesh = MakeSquare(_cursorMaterial), Visible = false };
        AddChild(_cursor);

        // Per-instance colours (a white albedo modulated by them) are what lets
        // one ghost mesh show legal and illegal cells at the same time.
        _previewMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = MakeSquare(MakeMaterial(Colors.White, vertexColorAsAlbedo: true)),
        };
        AddChild(new MultiMeshInstance3D { Multimesh = _previewMesh });
    }

    public void Toggle() => SetActive(!Active);

    /// <summary>
    /// Arms or disarms the tool, always dropping any pending anchor. Arming
    /// disarms every other build tool: build mode has one active tool.
    /// </summary>
    public void SetActive(bool active)
    {
        Active = active;
        Anchor = null;
        if (active)
        {
            DisarmOtherTools();
        }
        else
        {
            HoverCell = null;
            _cursor.Visible = false;
        }
        RefreshPreview();
    }

    /// <summary>
    /// Disarms every other tool in <see cref="ToolGroup"/>. Only ever called
    /// when arming, so it cannot recurse.
    /// </summary>
    private void DisarmOtherTools()
    {
        if (!IsInsideTree())
        {
            return;
        }
        foreach (Node node in GetTree().GetNodesInGroup(ToolGroup))
        {
            if (node is BuildTool other && other != this && other.Active)
            {
                other.SetActive(false);
            }
        }
    }

    /// <summary>
    /// Points the tool at a cell (null when the cursor resolves to none) and
    /// refreshes the ghost. <c>_Process</c> calls this with the picked cell;
    /// the headless test calls it directly, which is how the ghost can be
    /// asserted without a cursor.
    /// </summary>
    public void HoverAt(Vector2I? cell)
    {
        HoverCell = cell;
        RefreshPreview();
    }

    /// <summary>
    /// The verdict on the placement a click at <paramref name="cell"/> would
    /// make right now: the pending drag when anchored, otherwise that single
    /// cell — which is also what the anchoring click is validated against, so
    /// anchoring on illegal ground is refused straight away.
    /// </summary>
    public PlacementPlan PlanFor(Vector2I cell)
    {
        if (World == null)
        {
            return PlacementPlan.Nothing;
        }
        Vector2I anchor = Anchor ?? cell;
        return PlacementRules.Check(World, Footprint(anchor, cell), Rules, PlacedTile);
    }

    /// <summary>
    /// One build click on a cell: anchors first, places second (or places
    /// immediately when <see cref="NeedsAnchor"/> is false). A refused
    /// placement writes nothing and keeps the anchor, so the player can re-aim.
    /// Returns whether the click was accepted. Public so tests — and later the
    /// build palette — can drive the tool without a cursor.
    /// </summary>
    public bool ClickCell(Vector2I cell)
    {
        if (World == null)
        {
            return false;
        }

        PlacementPlan plan = PlanFor(cell);
        if (!plan.Legal)
        {
            GD.Print($"{Name}: refused - {PlacementRules.Explain(plan.Refusal)}");
            HoverAt(cell);
            return false;
        }

        if (NeedsAnchor && Anchor == null)
        {
            Anchor = cell;
        }
        else
        {
            Apply(plan);
            Anchor = null;
        }

        HoverAt(cell);
        return true;
    }

    /// <summary>Right click / Esc: drop the pending anchor, else leave the tool.</summary>
    public void Cancel()
    {
        if (Anchor != null)
        {
            Anchor = null;
            RefreshPreview();
        }
        else
        {
            SetActive(false);
        }
    }

    /// <summary>
    /// The world cell at a screen position, through the shared
    /// <see cref="CellPicker"/> — the same code path the hover readout uses, so
    /// what the readout names is always what a click would build on. Public and
    /// position-driven so the headless smoke test can drive a known pixel.
    /// </summary>
    public Vector2I? PickCell(Vector2 screenPosition) =>
        CellPicker.CellAt(this, World, screenPosition);

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Active)
        {
            return;
        }

        if (@event is InputEventMouseButton { Pressed: true } click)
        {
            if (click.ButtonIndex == MouseButton.Left && HoverCell is { } cell)
            {
                ClickCell(cell);
                GetViewport().SetInputAsHandled();
            }
            else if (click.ButtonIndex == MouseButton.Right)
            {
                Cancel();
                GetViewport().SetInputAsHandled();
            }
        }
        else if (@event.IsActionPressed("ui_cancel"))
        {
            Cancel();
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
        HoverAt(CellPicker.CellUnderMouse(this, World));

        if (HoverCell is { } hover)
        {
            _cursor.Position = World.CellToWorld(hover) + Vector3.Up * HighlightY;
            _cursor.Visible = Mathf.PosMod(_time, BlinkPeriod) < BlinkPeriod * BlinkDutyCycle;
        }
        else
        {
            _cursor.Visible = false;
        }
    }

    /// <summary>
    /// Recomputes the verdict for the hovered cell and repaints both the hover
    /// square and the ghost from it — which is what puts the refusal on screen
    /// before the click instead of after it.
    /// </summary>
    private void RefreshPreview()
    {
        if (!Active || World == null || HoverCell is not { } hover)
        {
            Preview = null;
            ClearGhost();
            _cursorMaterial.AlbedoColor = CursorLegal;
            return;
        }

        PlacementPlan plan = PlanFor(hover);
        Preview = plan;
        _cursorMaterial.AlbedoColor = plan.Legal ? CursorLegal : CursorRefused;

        // Before the anchor there is no footprint to outline yet: the blinking
        // hover square, already tinted by the verdict, is the whole preview.
        if (NeedsAnchor && Anchor == null)
        {
            ClearGhost();
            return;
        }

        _ghostColors.Clear();
        _previewMesh.InstanceCount = plan.Count;
        for (int i = 0; i < plan.Count; i++)
        {
            Vector3 position = World.CellToWorld(plan.Cells[i]) + Vector3.Up * HighlightY;
            Color color = GhostColorFor(plan, i);
            _ghostColors.Add(color);
            _previewMesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, position));
            _previewMesh.SetInstanceColor(i, color);
        }
    }

    private void ClearGhost()
    {
        _ghostColors.Clear();
        _previewMesh.InstanceCount = 0;
    }

    /// <summary>
    /// A legal placement draws every cell legal; a refused one draws none of
    /// them legal (nothing there is going to be built) and marks the offending
    /// cells strongest, so the player can see which cell killed the drag.
    /// </summary>
    private static Color GhostColorFor(PlacementPlan plan, int index) =>
        plan.Legal ? GhostLegal
        : plan.CellLegal(index) ? GhostRefused
        : GhostIllegalCell;

    /// <summary>Flat unshaded translucent square, slightly smaller than a cell.</summary>
    private static PlaneMesh MakeSquare(Material material) => new()
    {
        Size = new Vector2(1.8f, 1.8f),
        Material = material,
    };

    private static StandardMaterial3D MakeMaterial(
        Color color, bool vertexColorAsAlbedo = false) => new()
    {
        AlbedoColor = color,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        VertexColorUseAsAlbedo = vertexColorAsAlbedo,
    };
}
