using System;
using System.Collections.Generic;
using Godot;

namespace Arable;

/// <summary>
/// The vehicle panel: select a vehicle, see what it is running and why it is
/// or is not moving, pick an action, and click its targets on the map. This
/// is #35's whole surface over #34's <see cref="MachineSystem.SetOrder"/> —
/// the order system already refuses a mismatched kind and reports a blocked
/// reason; this class only ever reads those two facts and a third, its own:
/// which target kind an action needs.
///
/// <list type="bullet">
/// <item><b>The panel never substitutes work.</b> There is no "find work" or
/// "assign automatically" button anywhere on it — #7's governing rule for the
/// whole milestone, typed as UI: every order on screen is one a player
/// clicked together, target by target.</item>
/// <item><b>Blocked reason is the headline fact, not a footnote.</b> M5's
/// tracker names the failure mode this exists to prevent — the player cannot
/// tell why an idle vehicle is idle — so the row is always present, reading
/// an em dash rather than disappearing when there is nothing to report,
/// exactly <see cref="FieldInspector"/>'s convention for "nothing to show".</item>
/// <item><b>Selection goes through <see cref="CellPicker"/></b>, the same
/// screen → cell path the field panel and the build tools use. Picking a
/// vehicle uses <see cref="MachineSystem.Occupancy"/> at that cell rather than
/// a second spatial index.</item>
/// <item><b>Legality at pick time is a type match, nothing finer.</b> A field
/// order accepts any field; a haul's source accepts any field or building,
/// its destination any building — exactly as far as <see cref="Order"/>'s own
/// doc comment takes it ("existence is checked at execution, not at
/// assignment"). Whether that field is at the right stage or that building
/// has room is <see cref="OrderBlock"/>'s job once the vehicle gets there, not
/// this panel's to guess at the click.</item>
/// </list>
///
/// It is anchored to the <b>right edge, vertically centred</b> — the mirror of
/// the field panel's left edge, and clear of both corner readouts, the
/// palette and the time controls at any window size.
/// </summary>
public partial class VehicleInspector : Control
{
    /// <summary>
    /// Joined only while a target is being picked. The field panel checks this
    /// group (not a reference to this class) before claiming a click, the same
    /// arm's-length coordination <see cref="BuildTool.ToolGroup"/> already
    /// gives the palette and the field panel.
    /// </summary>
    public const string PickingGroup = "vehicle_picking";

    private const float ScreenMargin = 18f;
    private const float PanelWidth = 236f;
    private const int TitleFontSize = 16;
    private const int RowFontSize = 13;

    // Above the tallest thing either target kind ever draws as — a harvestable
    // field (assets/dev/tile_library.tres: 1.0) or a silo (1.8) — rather than
    // BuildTool's HighlightY, which only ever ghosts a placement onto bare
    // terrain and would sit buried inside an existing field or building here.
    private const float FieldHighlightY = 1.05f;
    private const float StructureHighlightY = 1.85f;

    private static readonly Color PanelBackground = new(0.07f, 0.08f, 0.10f, 0.90f);
    private static readonly Color PanelBorder = new(1f, 1f, 1f, 0.10f);
    private static readonly Color TitleColor = new(0.96f, 0.97f, 1f);
    private static readonly Color KeyColor = new(0.62f, 0.67f, 0.75f);
    private static readonly Color ValueColor = new(0.94f, 0.95f, 0.98f);
    private static readonly Color BlockedColor = new(0.95f, 0.55f, 0.35f);

    private static readonly Color LegalHighlight = new(0.30f, 0.85f, 0.40f, 0.35f);
    private static readonly Color CursorLegalColor = new(0.30f, 0.95f, 0.45f, 0.9f);
    private static readonly Color CursorRefusedColor = new(0.95f, 0.25f, 0.2f, 0.9f);

    /// <summary>The world the panel reads and picks against. Null is legal, as elsewhere.</summary>
    [Export] public WorldGrid? World { get; set; }

    /// <summary>
    /// Resolves the driver, dismissed-worker-safe. Null falls back to the raw
    /// crew handle, the same "no such thing here" reading a scene with no
    /// <see cref="Fleet"/> gets everywhere else.
    /// </summary>
    [Export] public Fleet? Fleet { get; set; }

    private PanelContainer _panel = null!;
    private Label _title = null!;
    private Label _driverValue = null!;
    private Label _orderValue = null!;
    private Label _stepValue = null!;
    private Label _blockedValue = null!;
    private Label _hint = null!;
    private Button _ploughBtn = null!;
    private Button _sowBtn = null!;
    private Button _harvestBtn = null!;
    private Button _haulBtn = null!;
    private Button _stopBtn = null!;
    private GridContainer _actionsRow = null!;
    private Control _pickingRow = null!;

    private MeshInstance3D _cursor = null!;
    private StandardMaterial3D _cursorMaterial = null!;
    private MultiMesh _highlightMesh = null!;

    /// <summary>The vehicle the panel is following, or <see cref="EntityId.None"/> when closed.</summary>
    public EntityId Selected { get; private set; } = EntityId.None;

    /// <summary>The action awaiting a map click, or null when the panel is not picking.</summary>
    public OrderKind? PickingAction { get; private set; }

    /// <summary>The haul's source, once named — null until the first of the two picks lands.</summary>
    private int? _haulFromId;

    /// <summary>Which registry <see cref="_haulFromId"/> names. Meaningless while it is null.</summary>
    private HaulSourceKind _haulFromKind;

    public bool IsOpen => Selected != EntityId.None;

    /// <summary>The box itself — where it sits is a claim a smoke test checks like any other.</summary>
    public Control Panel => _panel;

    public string TitleText => _title.Text;
    public string DriverLine => _driverValue.Text;
    public string OrderLine => _orderValue.Text;
    public string StepLine => _stepValue.Text;
    public string BlockedLine => _blockedValue.Text;
    public string HintText => _hint.Text;
    public bool IsPloughEnabled => !_ploughBtn.Disabled;
    public bool IsHaulEnabled => !_haulBtn.Disabled;

    public override void _Ready()
    {
        // A full-screen Control would swallow every click meant for the world.
        MouseFilter = MouseFilterEnum.Ignore;
        Build();
        BuildHighlight();
        Refresh();
    }

    /// <summary>Repaints every frame, like the field panel, so a stale panel never outlives its vehicle.</summary>
    public override void _Process(double delta)
    {
        Refresh();
        if (PickingAction == null)
        {
            return;
        }
        // A build tool armed mid-pick (a hotkey, not a click this panel would
        // have seen) wins outright rather than fighting this mode for the
        // mouse: the picking simply stands down.
        if (AnyToolArmed())
        {
            CancelPicking();
        }
        else
        {
            RefreshHighlight();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (World == null)
        {
            return;
        }

        if (PickingAction != null)
        {
            HandlePickingInput(@event);
            return;
        }

        if (AnyToolArmed())
        {
            return;
        }

        if (@event is InputEventMouseButton { Pressed: true } click && click.ButtonIndex == MouseButton.Left)
        {
            Vector2I? cell = CellPicker.CellAt(this, World, click.Position);
            EntityId hit = cell is { } c ? VehicleAt(World, c) : EntityId.None;
            if (hit != EntityId.None)
            {
                Select(hit);
                GetViewport().SetInputAsHandled();
            }
            else if (Selected != EntityId.None)
            {
                // Not marked handled: an empty click, or one on a field, is
                // still the field panel's to answer.
                Deselect();
            }
        }
        else if (@event.IsActionPressed("ui_cancel") && Selected != EntityId.None)
        {
            Deselect();
            GetViewport().SetInputAsHandled();
        }
    }

    // ------------------------------------------------------------------
    // Selection
    // ------------------------------------------------------------------

    /// <summary>
    /// The vehicle standing on that cell, or <see cref="EntityId.None"/> — the
    /// one lookup both this panel's own click handling and
    /// <see cref="FieldInspector"/>'s yield check share, so neither can
    /// disagree with the other about what a cell holds.
    /// </summary>
    public static EntityId VehicleAt(WorldGrid world, Vector2I cell)
    {
        IReadOnlyList<EntityId> here = world.Machines.Occupancy.At(cell);
        return here.Count > 0 ? here[0] : EntityId.None;
    }

    /// <summary>Selects a vehicle by handle — the door a headless test drives without a cursor.</summary>
    public EntityId Select(EntityId vehicle)
    {
        CancelPicking();
        Selected = World != null && World.Machines.IsAlive(vehicle) ? vehicle : EntityId.None;
        Refresh();
        return Selected;
    }

    /// <summary>The same, from a screen position, through the shared <see cref="CellPicker"/>.</summary>
    public EntityId SelectAt(Vector2 screenPosition)
    {
        Vector2I? cell = CellPicker.CellAt(this, World, screenPosition);
        EntityId hit = cell is { } c && World != null ? VehicleAt(World, c) : EntityId.None;
        return Select(hit);
    }

    public EntityId Deselect() => Select(EntityId.None);

    // ------------------------------------------------------------------
    // Panel
    // ------------------------------------------------------------------

    /// <summary>Rereads the selected vehicle and redraws — public for the same reason FieldInspector's is.</summary>
    public void Refresh()
    {
        if (Selected != EntityId.None && (World == null || !World.Machines.IsAlive(Selected)))
        {
            Selected = EntityId.None;
            CancelPicking();
        }

        if (Selected == EntityId.None || World == null)
        {
            _panel.Visible = false;
            return;
        }

        MachineSystem machines = World.Machines;
        MachineKind kind = machines.KindOf(Selected);
        string kindName = MachineKinds.Name(kind);
        _title.Text = $"{char.ToUpperInvariant(kindName[0])}{kindName[1..]} #{Selected.Index}";

        EntityId driver = Fleet != null ? Fleet.DriverOf(Selected) : machines.CrewOf(Selected);
        _driverValue.Text = driver == EntityId.None ? "none" : $"worker #{driver.Index}";

        _orderValue.Text = machines.OrderOf(Selected)?.ToString() ?? "none";
        _stepValue.Text = OrderText.Describe(machines.StepOf(Selected));

        OrderBlock block = machines.BlockOf(Selected);
        _blockedValue.Text = block == OrderBlock.None ? "—" : OrderText.Describe(block);
        _blockedValue.AddThemeColorOverride("font_color", block == OrderBlock.None ? ValueColor : BlockedColor);

        _ploughBtn.Disabled = !MachineSystem.Accepts(kind, OrderKind.PloughField);
        _sowBtn.Disabled = !MachineSystem.Accepts(kind, OrderKind.SowField);
        _harvestBtn.Disabled = !MachineSystem.Accepts(kind, OrderKind.HarvestField);
        _haulBtn.Disabled = !MachineSystem.Accepts(kind, OrderKind.HaulGoods);
        _stopBtn.Disabled = machines.OrderOf(Selected) == null && machines.PendingOrderOf(Selected) == null;

        _actionsRow.Visible = PickingAction == null;
        _pickingRow.Visible = PickingAction != null;
        if (PickingAction is { } picking)
        {
            _hint.Text = HintFor(picking);
        }

        _panel.Visible = true;
    }

    private string HintFor(OrderKind kind) => kind switch
    {
        OrderKind.PloughField => "pick a field to plough",
        OrderKind.SowField => "pick a field to sow",
        OrderKind.HarvestField => "pick a field to harvest",
        OrderKind.HaulGoods when _haulFromId == null => "pick a field or building to load from",
        OrderKind.HaulGoods => "pick a building to deliver to",
        _ => "",
    };

    // ------------------------------------------------------------------
    // Actions and target picking
    // ------------------------------------------------------------------

    /// <summary>
    /// Arms picking for an action, refusing one the selected vehicle's kind
    /// cannot run at all (<see cref="MachineSystem.Accepts"/>) before the map
    /// ever enters picking mode. Public: the button calls it, and so does a
    /// headless test — the same door.
    /// </summary>
    public bool BeginPicking(OrderKind kind)
    {
        if (Selected == EntityId.None || World == null
            || !MachineSystem.Accepts(World.Machines.KindOf(Selected), kind))
        {
            return false;
        }

        CancelPicking();
        DisarmBuildTools();
        PickingAction = kind;
        _haulFromId = null;
        _haulFromKind = HaulSourceKind.Structure;
        AddToGroup(PickingGroup);
        RefreshHighlight();
        Refresh();
        return true;
    }

    /// <summary>Leaves picking mode without issuing anything. Idempotent.</summary>
    public void CancelPicking()
    {
        if (PickingAction == null)
        {
            return;
        }
        PickingAction = null;
        _haulFromId = null;
        _haulFromKind = HaulSourceKind.Structure;
        RemoveFromGroup(PickingGroup);
        ClearHighlight();
        Refresh();
    }

    /// <summary>Sends the vehicle back to idle once its current cycle allows it.</summary>
    public void StopOrder()
    {
        if (Selected != EntityId.None)
        {
            World?.Machines.SetOrder(Selected, null);
        }
    }

    /// <summary>
    /// The programmatic equivalent of a picking click: resolves the target at
    /// <paramref name="cell"/>, refuses one of the wrong kind, and — for a
    /// haul — takes two calls, the first naming the source (a field or a
    /// building) and the second the destination (a building only). This is
    /// the one door both a mouse click and a headless test use to name a
    /// target; neither ever calls <see cref="MachineSystem.SetOrder"/>
    /// directly. Returns whether the pick was accepted; a refusal leaves
    /// picking mode exactly as it was so the player can re-aim.
    /// </summary>
    public bool PickTargetAt(Vector2I cell)
    {
        if (PickingAction is not { } kind || World == null || Selected == EntityId.None)
        {
            return false;
        }

        if (kind == OrderKind.HaulGoods)
        {
            if (_haulFromId == null)
            {
                if (World.GetField(cell) is { } sourceField)
                {
                    _haulFromId = sourceField.Id;
                    _haulFromKind = HaulSourceKind.Field;
                }
                else if (World.GetStructure(cell) is { } sourceStructure)
                {
                    _haulFromId = sourceStructure.Id;
                    _haulFromKind = HaulSourceKind.Structure;
                }
                else
                {
                    GD.Print("VehicleInspector: refused - no field or building there");
                    return false;
                }
                RefreshHighlight();
                Refresh();
                return true;
            }

            Structure? destination = World.GetStructure(cell);
            if (destination == null)
            {
                GD.Print("VehicleInspector: refused - no building there");
                return false;
            }
            Order haul = _haulFromKind == HaulSourceKind.Field
                ? Order.HaulFromField(ItemTypes.Grain, _haulFromId.Value, destination.Id)
                : Order.Haul(ItemTypes.Grain, _haulFromId.Value, destination.Id);
            World.Machines.SetOrder(Selected, haul);
            CancelPicking();
            return true;
        }

        Field? field = World.GetField(cell);
        if (field == null)
        {
            GD.Print("VehicleInspector: refused - no field there");
            return false;
        }

        Order order = kind switch
        {
            OrderKind.PloughField => Order.Plough(field.Id),
            OrderKind.SowField => Order.Sow(field.Id),
            _ => Order.Harvest(field.Id),
        };
        World.Machines.SetOrder(Selected, order);
        CancelPicking();
        return true;
    }

    private void HandlePickingInput(InputEvent @event)
    {
        if (AnyToolArmed())
        {
            CancelPicking();
            return;
        }

        if (@event is InputEventMouseButton { Pressed: true } click)
        {
            if (click.ButtonIndex == MouseButton.Left)
            {
                if (CellPicker.CellAt(this, World, click.Position) is { } cell)
                {
                    PickTargetAt(cell);
                }
                GetViewport().SetInputAsHandled();
            }
            else if (click.ButtonIndex == MouseButton.Right)
            {
                CancelPicking();
                GetViewport().SetInputAsHandled();
            }
        }
        else if (@event.IsActionPressed("ui_cancel"))
        {
            CancelPicking();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>
    /// Whether any build tool is armed, asked of the tools themselves exactly
    /// as <see cref="FieldInspector"/> asks — the one flag
    /// <see cref="BuildTool.SetActive"/> writes, read the same way everywhere.
    /// </summary>
    private bool AnyToolArmed()
    {
        if (!IsInsideTree())
        {
            return false;
        }
        foreach (Node node in GetTree().GetNodesInGroup(BuildTool.ToolGroup))
        {
            if (node is BuildTool { Active: true })
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Clears the mouse for picking mode before it starts, rather than fighting a tool for it.</summary>
    private void DisarmBuildTools()
    {
        if (!IsInsideTree())
        {
            return;
        }
        foreach (Node node in GetTree().GetNodesInGroup(BuildTool.ToolGroup))
        {
            if (node is BuildTool { Active: true } tool)
            {
                tool.SetActive(false);
            }
        }
    }

    // ------------------------------------------------------------------
    // Highlight (3D) — same idiom as BuildTool's ghost, independent of it:
    // a placement ghost prices a footprint, this only ever marks "yes"/"no".
    // ------------------------------------------------------------------

    /// <summary>
    /// Parented to <see cref="World"/>, never to this <see cref="Control"/>:
    /// this panel lives under the <c>Hud</c> <see cref="CanvasLayer"/>, and a
    /// <see cref="MeshInstance3D"/> there never reaches the 3D scene the build
    /// tools' own ghosts render into. A null world leaves picking with nothing
    /// to draw, which is already true of everything else it needs a world for.
    /// </summary>
    private void BuildHighlight()
    {
        if (World == null)
        {
            return;
        }

        _cursorMaterial = MakeMaterial(CursorLegalColor);
        _cursor = new MeshInstance3D { Mesh = MakeSquare(_cursorMaterial), Visible = false };
        World.AddChild(_cursor);

        _highlightMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = MakeSquare(MakeMaterial(Colors.White, vertexColorAsAlbedo: true)),
        };
        World.AddChild(new MultiMeshInstance3D { Multimesh = _highlightMesh });
    }

    /// <summary>
    /// Lights every cell of every legal target and tints the hovered cell by
    /// whether it is one of them — the whole of "highlights legal targets and
    /// refuses illegal ones at pick time". Recomputed every frame while
    /// picking, like the field panel's own repaint: cheap, since a farm has a
    /// handful of fields and buildings, not hundreds.
    /// </summary>
    private void RefreshHighlight()
    {
        if (PickingAction is not { } kind || World == null)
        {
            ClearHighlight();
            return;
        }

        // What is legal depends on the action *and*, for a haul, which half of
        // it is still unnamed: a load may come off a field's own harvest buffer
        // or out of a building, while a delivery only ever goes into a
        // building. Lighting the union while the source is open is the whole
        // visible difference the field-as-source seam makes to the player.
        bool pickingHaulSource = kind == OrderKind.HaulGoods && _haulFromId == null;
        bool allowFields = kind != OrderKind.HaulGoods || pickingHaulSource;
        bool allowStructures = kind == OrderKind.HaulGoods;

        // Height is per cell, not per pick: a field's highlight has only a crop
        // to clear and a building's has the whole silo, so a union of both
        // drawn at one Y would sink into one or float absurdly over the other.
        var cells = new List<(Vector2I Cell, float Y)>();
        if (allowFields)
        {
            foreach (Field f in World.Fields)
            {
                foreach (Vector2I c in f.Cells)
                {
                    cells.Add((c, FieldHighlightY));
                }
            }
        }
        if (allowStructures)
        {
            foreach (Structure s in World.Structures)
            {
                foreach (Vector2I c in s.Cells)
                {
                    cells.Add((c, StructureHighlightY));
                }
            }
        }

        _highlightMesh.InstanceCount = cells.Count;
        for (int i = 0; i < cells.Count; i++)
        {
            _highlightMesh.SetInstanceTransform(
                i,
                new Transform3D(
                    Basis.Identity,
                    World.CellToWorld(cells[i].Cell) + Vector3.Up * cells[i].Y));
            _highlightMesh.SetInstanceColor(i, LegalHighlight);
        }

        Vector2I? hover = CellPicker.CellUnderMouse(this, World);
        if (hover is { } cell)
        {
            bool overField = allowFields && World.GetField(cell) != null;
            bool overStructure = allowStructures && World.GetStructure(cell) != null;
            float cursorY = overField ? FieldHighlightY : StructureHighlightY;
            _cursor.Position = World.CellToWorld(cell) + Vector3.Up * (cursorY + 0.02f);
            _cursor.Visible = true;
            _cursorMaterial.AlbedoColor =
                overField || overStructure ? CursorLegalColor : CursorRefusedColor;
        }
        else
        {
            _cursor.Visible = false;
        }
    }

    private void ClearHighlight()
    {
        _highlightMesh.InstanceCount = 0;
        _cursor.Visible = false;
    }

    private static PlaneMesh MakeSquare(Material material) => new()
    {
        Size = new Vector2(1.8f, 1.8f),
        Material = material,
    };

    private static StandardMaterial3D MakeMaterial(Color color, bool vertexColorAsAlbedo = false) => new()
    {
        AlbedoColor = color,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        VertexColorUseAsAlbedo = vertexColorAsAlbedo,
    };

    // ------------------------------------------------------------------
    // Build (UI)
    // ------------------------------------------------------------------

    private void Build()
    {
        var background = new StyleBoxFlat
        {
            BgColor = PanelBackground,
            BorderColor = PanelBorder,
            ShadowColor = new Color(0f, 0f, 0f, 0.45f),
            ShadowSize = 10,
        };
        background.SetBorderWidthAll(1);
        background.SetCornerRadiusAll(12);
        background.SetContentMarginAll(12);

        _panel = new PanelContainer
        {
            Name = "VehiclePanel",
            MouseFilter = MouseFilterEnum.Stop,
            CustomMinimumSize = new Vector2(PanelWidth, 0f),
            Visible = false,
        };
        _panel.AddThemeStyleboxOverride("panel", background);

        // Right edge, vertically centred, grown inward — the field panel's
        // mirror image, clear of it and of every other HUD element.
        _panel.AnchorLeft = 1f;
        _panel.AnchorRight = 1f;
        _panel.AnchorTop = 0.5f;
        _panel.AnchorBottom = 0.5f;
        _panel.GrowHorizontal = GrowDirection.Begin;
        _panel.GrowVertical = GrowDirection.Both;
        _panel.OffsetLeft = -ScreenMargin;
        _panel.OffsetRight = -ScreenMargin;
        _panel.OffsetTop = 0f;
        _panel.OffsetBottom = 0f;
        AddChild(_panel);

        var column = new VBoxContainer { Name = "Column", MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(column);

        _title = MakeLabel("Name", TitleColor, TitleFontSize);
        column.AddChild(_title);

        var rows = new GridContainer { Name = "Rows", Columns = 2, MouseFilter = MouseFilterEnum.Ignore };
        rows.AddThemeConstantOverride("h_separation", 12);
        rows.AddThemeConstantOverride("v_separation", 4);
        column.AddChild(rows);

        _driverValue = AddRow(rows, "driver");
        _orderValue = AddRow(rows, "order");
        _stepValue = AddRow(rows, "step");
        _blockedValue = AddRow(rows, "blocked");

        _actionsRow = new GridContainer { Name = "Actions", Columns = 2, MouseFilter = MouseFilterEnum.Ignore };
        column.AddChild(_actionsRow);
        _ploughBtn = MakeButton(_actionsRow, "plough", () => BeginPicking(OrderKind.PloughField));
        _sowBtn = MakeButton(_actionsRow, "sow", () => BeginPicking(OrderKind.SowField));
        _harvestBtn = MakeButton(_actionsRow, "harvest", () => BeginPicking(OrderKind.HarvestField));
        _haulBtn = MakeButton(_actionsRow, "haul", () => BeginPicking(OrderKind.HaulGoods));

        _stopBtn = new Button { Text = "stop order" };
        _stopBtn.Pressed += StopOrder;
        column.AddChild(_stopBtn);

        _pickingRow = new VBoxContainer { Name = "Picking", MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        column.AddChild(_pickingRow);
        _hint = MakeLabel("Hint", ValueColor, RowFontSize);
        _pickingRow.AddChild(_hint);
        var cancelBtn = new Button { Text = "cancel (esc)" };
        cancelBtn.Pressed += CancelPicking;
        _pickingRow.AddChild(cancelBtn);
    }

    private static Button MakeButton(GridContainer row, string text, Action onPressed)
    {
        var button = new Button { Text = text };
        button.Pressed += onPressed;
        row.AddChild(button);
        return button;
    }

    private static Label AddRow(GridContainer rows, string key)
    {
        rows.AddChild(MakeLabel($"{key}Key", KeyColor, RowFontSize, key));
        Label value = MakeLabel($"{key}Value", ValueColor, RowFontSize);
        value.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rows.AddChild(value);
        return value;
    }

    private static Label MakeLabel(string name, Color color, int fontSize, string text = "")
    {
        var label = new Label
        {
            Name = name,
            Text = text,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }
}
