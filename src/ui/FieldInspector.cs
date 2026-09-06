using System.Globalization;
using Godot;

namespace Arable;

/// <summary>
/// Everything the field panel says about one field at one instant, computed in
/// one place so the panel and a test read the same numbers.
///
/// <b>Every member is a fact about the field itself.</b> Nothing here names
/// what else in the world might be responsible for a number being what it is —
/// no season, no missing machine, no full store elsewhere. That restraint is
/// the point rather than an omission: M6's building panels report under the
/// same rule and under a much sharper version of it, and a habit only holds if
/// the easiest panel in the game keeps it too.
/// </summary>
public readonly struct FieldReport
{
    internal FieldReport(
        CropStage stage, CropStage? nextStage, bool stalled, float daysRemaining,
        int yield, ItemType crop, int stored, int capacity)
    {
        Stage = stage;
        NextStage = nextStage;
        Stalled = stalled;
        DaysRemaining = daysRemaining;
        Yield = yield;
        Crop = crop;
        Stored = stored;
        Capacity = capacity;
    }

    /// <summary>What is standing on the field right now.</summary>
    public CropStage Stage { get; }

    /// <summary>
    /// The stage <i>time alone</i> moves the field to, or null when nothing but
    /// work will — a fallow field never ploughs itself, and a ripe one waits
    /// indefinitely. A countdown against a stage only work can reach would be a
    /// lie with a decimal point on it.
    /// </summary>
    public CropStage? NextStage { get; }

    /// <summary>
    /// The crop is in the ground and banking nothing: one of the growth factors
    /// is at zero, so it stays where it is for as long as that lasts.
    /// <see cref="DaysRemaining"/> is meaningless here and is deliberately not
    /// a large number — dividing by a zero rate is how a panel comes to promise
    /// a harvest in eight thousand days instead of admitting it is not moving.
    /// </summary>
    public bool Stalled { get; }

    /// <summary>
    /// Game days to <see cref="NextStage"/> <b>at the rate the field is growing
    /// at right now</b> — the growth model's own product, not a nominal
    /// schedule, so poor ground and a slow season are already in it. It is a
    /// projection of the present and not a forecast: it does not know the
    /// season turns in two days, and it moves when the conditions do. Zero when
    /// there is nothing to count down to.
    /// </summary>
    public float DaysRemaining { get; }

    /// <summary>
    /// What cutting this field would yield — <see cref="CropSystem.ProjectedYield"/>
    /// itself, never a second formula over the same inputs, so the number shown
    /// and the number delivered cannot drift apart. Zero when the ground is empty.
    /// </summary>
    public int Yield { get; }

    /// <summary>The good this field's harvest produces.</summary>
    public ItemType Crop { get; }

    /// <summary>Units sitting in the field's output buffer.</summary>
    public int Stored { get; }

    /// <summary>How many it can hold before a harvest is refused whole.</summary>
    public int Capacity { get; }

    /// <summary>Whether a countdown is running: time moves this field, and it is moving.</summary>
    public bool CountingDown => NextStage != null && !Stalled;

    /// <summary>
    /// Reads a crop row. A dead handle reads as an untouched field rather than
    /// throwing, which is the answer <see cref="CropSystem"/> gives its other
    /// callers; the panel drops the selection before it can ask.
    /// </summary>
    public static FieldReport For(CropSystem crops, EntityId row)
    {
        CropStage stage = crops.StageOf(row);
        CropStage? next = stage switch
        {
            CropStage.Sown => CropStage.Growing,
            CropStage.Growing => CropStage.Harvestable,
            _ => null,
        };

        // The live product of every growth factor, which is what makes the
        // countdown honest on poor ground and in a bad season.
        float rate = crops.GrowthPerTick(row);
        bool stalled = next != null && rate <= 0f;
        float days = 0f;
        if (next != null && !stalled)
        {
            float threshold = stage == CropStage.Sown ? crops.TicksToSprout : crops.TicksToRipen;
            float remaining = Mathf.Max(0f, threshold - crops.GrowthOf(row));
            days = remaining / rate / crops.TicksPerDay;
        }

        ItemBuffer? output = crops.OutputOf(row);
        return new FieldReport(
            stage, next, stalled, days, crops.ProjectedYield(row), crops.HarvestItem,
            output?.Total ?? 0, output?.Capacity ?? 0);
    }
}

/// <summary>
/// The field panel: click a field, and it says what is standing there, when
/// time will move it on, what cutting it would give, and what is already in its
/// buffer. It closes when the selection goes.
///
/// <list type="bullet">
/// <item><b>A view, and only a view.</b> It reads the sim and never writes it —
/// there is no button here that ploughs anything. Like
/// <see cref="TimeControls"/> it repaints every frame from the state rather
/// than caching a copy, so the countdown runs down and a stage the sim moved is
/// right on the panel the same frame.</item>
/// <item><b>Facts about the field, never a pointer at the culprit.</b> A
/// stalled crop is reported stalled; the panel does not say which factor is
/// zero, name a season, or advise anything. See <see cref="FieldReport"/>.</item>
/// <item><b>Selection goes through <see cref="CellPicker"/></b>, the same
/// screen → cell path the build tools and the hover readout use, so what a
/// click selects is what the cursor was over.</item>
/// <item><b>An armed build tool owns the click.</b> Clicking with the field
/// tool up marks farmland; it does not also open a panel. Selecting is what the
/// mouse does when no tool has claimed it.</item>
/// </list>
///
/// It is anchored to the <b>left edge, vertically centred</b>: the corners are
/// spoken for (readout, money, palette, time controls), and a panel that
/// appeared where the world was clicked would cover the thing being inspected.
/// The full HUD pass is M10's; this is one panel, in the idiom the other bars
/// already set.
/// </summary>
public partial class FieldInspector : Control
{
    /// <summary>Distance from the screen edge, matching the other HUD panels.</summary>
    private const float ScreenMargin = 18f;

    /// <summary>
    /// Held, so the panel does not resize as the digits of the countdown
    /// change — a box that breathes every frame reads as a bug.
    /// </summary>
    private const float PanelWidth = 236f;

    private const int TitleFontSize = 16;
    private const int RowFontSize = 13;

    // The palette's and the time controls' values: one panel idiom in the HUD.
    private static readonly Color PanelBackground = new(0.07f, 0.08f, 0.10f, 0.90f);
    private static readonly Color PanelBorder = new(1f, 1f, 1f, 0.10f);
    private static readonly Color TitleColor = new(0.96f, 0.97f, 1f);
    private static readonly Color KeyColor = new(0.62f, 0.67f, 0.75f);
    private static readonly Color ValueColor = new(0.94f, 0.95f, 0.98f);

    /// <summary>
    /// The world the panel reads. Wired in Main.tscn; null is legal, so a scene
    /// with a HUD and no world draws nothing instead of crashing on load.
    /// </summary>
    [Export] public WorldGrid? World { get; set; }

    private PanelContainer _panel = null!;
    private Label _title = null!;
    private Label _stage = null!;
    private Label _timing = null!;
    private Label _yield = null!;
    private Label _stored = null!;

    /// <summary>The field the panel is following, or null when it is closed.</summary>
    public Field? Selected { get; private set; }

    /// <summary>What the panel is saying right now, or null when it is closed.</summary>
    public FieldReport? Current { get; private set; }

    /// <summary>Whether the panel is up.</summary>
    public bool IsOpen => Selected != null;

    /// <summary>
    /// The box itself. Public because where it sits — and that it is clear of
    /// the other HUD panels — is a claim a smoke test checks like any other.
    /// </summary>
    public Control Panel => _panel;

    /// <summary>The field's name, as the panel titles it.</summary>
    public string TitleText => _title.Text;

    /// <summary>The stage line, as shown.</summary>
    public string StageText => _stage.Text;

    /// <summary>The countdown line, as shown.</summary>
    public string TimingText => _timing.Text;

    /// <summary>The yield line, as shown.</summary>
    public string YieldText => _yield.Text;

    /// <summary>The buffer line, as shown.</summary>
    public string StoredText => _stored.Text;

    public override void _Ready()
    {
        // A full-screen Control would swallow every click meant for the world;
        // only the panel itself takes the mouse.
        MouseFilter = MouseFilterEnum.Ignore;
        Build();
        Refresh();
    }

    /// <summary>
    /// Repaints from the sim every frame — which is how the countdown runs down
    /// and how a field bulldozed out from under the panel closes it, with
    /// nothing having to tell this class that either happened.
    /// </summary>
    public override void _Process(double delta) => Refresh();

    public override void _UnhandledInput(InputEvent @event)
    {
        // An armed tool owns the mouse and the escape key both: clicking with
        // the bulldozer up must not also open a panel on what it just removed.
        if (World == null || AnyToolArmed())
        {
            return;
        }

        if (@event is InputEventMouseButton { Pressed: true } click
            && click.ButtonIndex == MouseButton.Left)
        {
            SelectAt(click.Position);
            GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed("ui_cancel") && IsOpen)
        {
            Deselect();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>
    /// <b>The selection path.</b> Selects whatever field owns the cell, and
    /// deselects when none does — clicking bare ground closes the panel, which
    /// is the whole of "closes on deselect". Returns the field now selected.
    /// </summary>
    public Field? Select(Vector2I cell)
    {
        Selected = World?.GetField(cell);
        Refresh();
        return Selected;
    }

    /// <summary>
    /// The same, from a screen position, through the shared
    /// <see cref="CellPicker"/>. Public and position-driven so a headless test
    /// or a screenshot run can select a known cell without a cursor.
    /// </summary>
    public Field? SelectAt(Vector2 screenPosition)
    {
        Vector2I? cell = CellPicker.CellAt(this, World, screenPosition);
        return cell is { } picked ? Select(picked) : Deselect();
    }

    /// <summary>Closes the panel. Always returns null, so it can tail a selection.</summary>
    public Field? Deselect()
    {
        Selected = null;
        Refresh();
        return null;
    }

    /// <summary>
    /// Rereads the selected field and redraws. Public because a test — or
    /// anything that has just moved the sim — wants the panel current within
    /// the frame rather than after the next one.
    /// </summary>
    public void Refresh()
    {
        Field? field = Selected;
        // A field bulldozed away takes its crop row with it, so the liveness of
        // the row is the liveness of the field — and a stale handle is exactly
        // what the panel must not read.
        if (field != null && (World == null || !World.Crops.IsAlive(field.Crop)))
        {
            field = null;
            Selected = null;
        }

        if (field == null || World == null)
        {
            Current = null;
            _panel.Visible = false;
            return;
        }

        FieldReport report = FieldReport.For(World.Crops, field.Crop);
        Current = report;
        _title.Text = field.Name;
        _stage.Text = CropSystem.Name(report.Stage);
        _timing.Text = Timing(report);
        _yield.Text = Yield(report);
        _stored.Text = Stored(report);
        _panel.Visible = true;
    }

    /// <summary>
    /// The countdown line. Three cases, and the middle one is the whole reason
    /// this is a method: a crop banking nothing is <b>stalled</b>, said in a
    /// word, rather than a growing crop with an enormous number beside it. An
    /// em dash is for the stages time does not move at all.
    /// </summary>
    public static string Timing(FieldReport report)
    {
        if (report.NextStage is not { } next)
        {
            return "—";
        }
        return report.Stalled
            ? "stalled"
            : $"{CropSystem.Name(next)} in {Days(report.DaysRemaining)}";
    }

    /// <summary>
    /// The yield line: what the cut would give, or an em dash when there is
    /// nothing in the ground. A ploughed field printing "0 grain" would read as
    /// a bad harvest rather than as no crop.
    /// </summary>
    public static string Yield(FieldReport report) =>
        report.Yield <= 0 ? "—" : $"{report.Yield} {report.Crop.Name}";

    /// <summary>
    /// What the field is holding, against what it can hold. The capacity is
    /// half the line on purpose: a full buffer refuses the next harvest whole,
    /// and this is where a player can see that coming — stated as the field's
    /// own number, with no suggestion about what to do about it.
    /// </summary>
    public static string Stored(FieldReport report) =>
        $"{report.Stored} / {report.Capacity}";

    /// <summary>
    /// Days, to one decimal, with <see cref="CultureInfo.InvariantCulture"/> so
    /// the panel reads the same everywhere. Anything under a rounded tenth is
    /// written as under a tenth rather than as "0.0 days", which reads as a
    /// stage that is never coming.
    /// </summary>
    public static string Days(float days) => days < 0.05f
        ? "<0.1 days"
        : days.ToString("0.0", CultureInfo.InvariantCulture) + " days";

    /// <summary>
    /// Whether any build tool is armed, asked of the tools themselves
    /// (<see cref="BuildTool.ToolGroup"/>) rather than of the palette — the
    /// same single source of truth the palette reads, so a tool armed by a key,
    /// by a button or by a test all read alike here.
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
            Name = "FieldPanel",
            // The panel takes the mouse, so clicking the panel is not also a
            // click on the world behind it — which would close it.
            MouseFilter = MouseFilterEnum.Stop,
            CustomMinimumSize = new Vector2(PanelWidth, 0f),
            Visible = false,
        };
        _panel.AddThemeStyleboxOverride("panel", background);

        // Left edge, vertically centred, grown inward — clear of both corner
        // readouts, the palette and the time controls at any window size.
        _panel.AnchorLeft = 0f;
        _panel.AnchorRight = 0f;
        _panel.AnchorTop = 0.5f;
        _panel.AnchorBottom = 0.5f;
        _panel.GrowHorizontal = GrowDirection.End;
        _panel.GrowVertical = GrowDirection.Both;
        _panel.OffsetLeft = ScreenMargin;
        _panel.OffsetRight = ScreenMargin;
        _panel.OffsetTop = 0f;
        _panel.OffsetBottom = 0f;
        AddChild(_panel);

        var column = new VBoxContainer
        {
            Name = "Column",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        column.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(column);

        _title = MakeLabel("Name", TitleColor, TitleFontSize);
        column.AddChild(_title);

        var rows = new GridContainer
        {
            Name = "Rows",
            Columns = 2,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        rows.AddThemeConstantOverride("h_separation", 12);
        rows.AddThemeConstantOverride("v_separation", 4);
        column.AddChild(rows);

        _stage = AddRow(rows, "stage");
        _timing = AddRow(rows, "next");
        _yield = AddRow(rows, "yield");
        _stored = AddRow(rows, "stored");
    }

    /// <summary>One "key   value" line. The key is fixed; the value is repainted.</summary>
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
            // Nothing inside the panel eats a click meant for the panel itself.
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }
}
