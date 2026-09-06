using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Arable;

/// <summary>
/// One step on the <see cref="TimeControls"/> ladder: the multiplier it selects
/// and the button that selects it. Like <see cref="PaletteEntry"/>, it stores
/// nothing about what is <i>currently</i> selected — that is a fact about the
/// <see cref="Simulation"/> and is read off it on every refresh.
/// </summary>
public sealed class SpeedEntry
{
    internal SpeedEntry(int index, float multiplier, Button button, string label)
    {
        Index = index;
        Multiplier = multiplier;
        Button = button;
        LabelText = label;
    }

    /// <summary>Position on the ladder, which is also the sim's speed index. 0 is pause.</summary>
    public int Index { get; }

    /// <summary>How much faster than real time this step schedules ticks. 0 is stopped.</summary>
    public float Multiplier { get; }

    /// <summary>The button itself — what a click lands on.</summary>
    public Button Button { get; }

    /// <summary>What the button says: "pause", "1x", "2x".</summary>
    public string LabelText { get; }

    /// <summary>Whether this step is the one in force. Asked of the sim, never stored.</summary>
    public bool IsActive { get; internal set; }

    /// <summary>The last state the button was painted for, so colours are only redone on a change.</summary>
    internal bool? Painted { get; set; }
}

/// <summary>
/// The time controls: <b>the date, and the speed the player is watching it at</b>
/// — a readout saying which season and day it is, over one button per step of
/// <see cref="Simulation.SpeedSteps"/> (pause, 1×, 2×, 3×).
///
/// It follows <see cref="BuildPalette"/> deliberately, down to the panel style
/// and the amber "this one is armed" fill, because a second UI idiom in a
/// four-widget HUD is a cost with no payoff. The three habits worth naming,
/// since they are what make the bar honest rather than decorative:
///
/// <list type="bullet">
/// <item><b>Not a second source of truth.</b> The speed lives on the
/// <see cref="Simulation"/> (<see cref="Simulation.SpeedIndex"/>) and the date
/// on its <see cref="GameCalendar"/>; this class only reads them.
/// <see cref="Refresh"/> runs every frame, so a speed changed by any other
/// route — a test, a future pause key, a menu opening — is right on the bar the
/// same frame, with no copy here that could disagree.</item>
/// <item><b>One selection path.</b> <see cref="Select"/> is what a button press
/// calls and what a headless test calls, so a test exercises the player's path
/// without synthesizing a mouse click on a <see cref="Control"/>.</item>
/// <item><b>Built in code from the ladder.</b> The buttons come from
/// <see cref="Simulation.SpeedSteps"/> in order, so adding a 5× step for a
/// playtest is editing that export and nothing else.</item>
/// </list>
///
/// It is anchored <b>bottom-right</b>: the bottom-centre belongs to the build
/// palette and both top corners to the cell and money readouts, so this is the
/// one corner left — and it puts the date beside the controls that move it
/// rather than in a fifth unrelated place.
/// </summary>
public partial class TimeControls : Control
{
    /// <summary>Distance from the screen edges to the panel, matching the palette's.</summary>
    private const float ScreenMargin = 18f;

    private const float ButtonHeight = 30f;
    private const float ButtonWidth = 46f;
    private const int ButtonSeparation = 6;
    private const int ButtonFontSize = 13;
    private const int DateFontSize = 15;

    // Deliberately the palette's values: "armed" has to look the same on every
    // bar in the HUD, and the amber is the build cursor's own yellow.
    private static readonly Color PanelBackground = new(0.07f, 0.08f, 0.10f, 0.90f);
    private static readonly Color PanelBorder = new(1f, 1f, 1f, 0.10f);
    private static readonly Color IdleBackground = new(0.17f, 0.18f, 0.22f, 0.96f);
    private static readonly Color IdleBorder = new(1f, 1f, 1f, 0.10f);
    private static readonly Color HoverBackground = new(0.25f, 0.27f, 0.32f, 1f);
    private static readonly Color HoverBorder = new(1f, 1f, 1f, 0.24f);
    private static readonly Color ArmedBackground = new(0.96f, 0.79f, 0.24f, 1f);
    private static readonly Color ArmedHoverBackground = new(1f, 0.87f, 0.38f, 1f);
    private static readonly Color ArmedBorder = new(1f, 0.95f, 0.65f, 1f);

    private static readonly Color LabelColor = new(0.94f, 0.95f, 0.98f);
    private static readonly Color ArmedLabelColor = new(0.13f, 0.10f, 0.02f);
    private static readonly Color DateColor = new(0.86f, 0.90f, 0.96f);

    /// <summary>
    /// The simulation whose clock these buttons drive and whose calendar the
    /// readout shows. Wired in Main.tscn; null is legal, so a scene with a HUD
    /// and no sim draws an empty corner instead of crashing on load.
    /// </summary>
    [Export] public Simulation? Sim { get; set; }

    private readonly List<SpeedEntry> _entries = new();

    private PanelContainer _panel = null!;
    private Label _dateLabel = null!;
    private StyleBoxFlat _idleStyle = null!;
    private StyleBoxFlat _hoverStyle = null!;
    private StyleBoxFlat _armedStyle = null!;
    private StyleBoxFlat _armedHoverStyle = null!;

    /// <summary>
    /// The panel the readout and buttons sit on. Public because where it is on
    /// screen — and that it is clear of the palette and the money readout — is
    /// a claim the smoke test checks like any other.
    /// </summary>
    public Control Panel => _panel;

    /// <summary>The speed buttons, in ladder order.</summary>
    public IReadOnlyList<SpeedEntry> Entries => _entries;

    /// <summary>How many steps are on the bar.</summary>
    public int Count => _entries.Count;

    /// <summary>What the date readout says right now.</summary>
    public string DateText => _dateLabel.Text;

    /// <summary>The step drawn as selected, or -1 when there is no sim to ask.</summary>
    public int ActiveIndex => Sim?.SpeedIndex ?? -1;

    public override void _Ready()
    {
        // A full-screen Control would swallow every click meant for the world;
        // only the panel itself takes the mouse.
        MouseFilter = MouseFilterEnum.Ignore;
        Build();
        Refresh();
    }

    /// <summary>
    /// Repaints from the sim every frame, which is how this observes rather
    /// than mirrors: nothing has to tell it that the day rolled over or that
    /// something else changed the speed.
    /// </summary>
    public override void _Process(double delta) => Refresh();

    /// <summary>
    /// <b>The selection path.</b> Puts the sim on that step of its ladder and
    /// repaints. Returns false, changing nothing, for an index the sim does not
    /// have — or when there is no sim wired at all.
    /// </summary>
    public bool Select(int index)
    {
        if (Sim == null || !Sim.SetSpeed(index))
        {
            return false;
        }

        Refresh();
        return true;
    }

    /// <summary>The entry at <paramref name="index"/>, or null when there is none.</summary>
    public SpeedEntry? Entry(int index) =>
        index >= 0 && index < _entries.Count ? _entries[index] : null;

    /// <summary>
    /// Redraws the date and the armed button from the sim. Public because a
    /// test — or a script that just changed the speed — wants the bar up to
    /// date within the frame rather than after the next one.
    /// </summary>
    public void Refresh()
    {
        _dateLabel.Text = Sim?.Calendar.Describe() ?? "—";

        int active = ActiveIndex;
        foreach (SpeedEntry entry in _entries)
        {
            bool armed = entry.Index == active;
            entry.IsActive = armed;
            entry.Button.SetPressedNoSignal(armed);
            if (entry.Painted == armed)
            {
                continue;
            }

            entry.Painted = armed;
            entry.Button.AddThemeColorOverride("font_color",
                armed ? ArmedLabelColor : LabelColor);
            entry.Button.AddThemeColorOverride("font_pressed_color", ArmedLabelColor);
            entry.Button.AddThemeColorOverride("font_hover_color",
                armed ? ArmedLabelColor : LabelColor);
            entry.Button.AddThemeColorOverride("font_hover_pressed_color", ArmedLabelColor);
        }
    }

    /// <summary>
    /// What a step's button says. The stop is named rather than numbered —
    /// "0x" is a puzzle, "pause" is not — and every running step is written as
    /// a multiplier, with <see cref="CultureInfo.InvariantCulture"/> so a
    /// half-step reads the same on every machine.
    /// </summary>
    public static string SpeedLabel(float multiplier) => multiplier <= 0f
        ? "pause"
        : multiplier.ToString("0.##", CultureInfo.InvariantCulture) + "x";

    private void Build()
    {
        _idleStyle = ButtonStyle(IdleBackground, IdleBorder, 1);
        _hoverStyle = ButtonStyle(HoverBackground, HoverBorder, 1);
        _armedStyle = ButtonStyle(ArmedBackground, ArmedBorder, 2);
        _armedHoverStyle = ButtonStyle(ArmedHoverBackground, ArmedBorder, 2);

        var background = new StyleBoxFlat
        {
            BgColor = PanelBackground,
            BorderColor = PanelBorder,
            ShadowColor = new Color(0f, 0f, 0f, 0.45f),
            ShadowSize = 10,
        };
        background.SetBorderWidthAll(1);
        background.SetCornerRadiusAll(12);
        background.SetContentMarginAll(10);

        _panel = new PanelContainer { Name = "TimePanel" };
        _panel.AddThemeStyleboxOverride("panel", background);

        // Bottom-right and grown inward, so it stays out of the palette's way
        // at any window size.
        _panel.AnchorLeft = 1f;
        _panel.AnchorRight = 1f;
        _panel.AnchorTop = 1f;
        _panel.AnchorBottom = 1f;
        _panel.GrowHorizontal = GrowDirection.Begin;
        _panel.GrowVertical = GrowDirection.Begin;
        _panel.OffsetLeft = -ScreenMargin;
        _panel.OffsetRight = -ScreenMargin;
        _panel.OffsetTop = -ScreenMargin;
        _panel.OffsetBottom = -ScreenMargin;
        AddChild(_panel);

        var column = new VBoxContainer
        {
            Name = "Column",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        column.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(column);

        _dateLabel = new Label
        {
            Name = "DateReadout",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _dateLabel.AddThemeFontSizeOverride("font_size", DateFontSize);
        _dateLabel.AddThemeColorOverride("font_color", DateColor);
        column.AddChild(_dateLabel);

        var row = new HBoxContainer
        {
            Name = "Speeds",
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        row.AddThemeConstantOverride("separation", ButtonSeparation);
        column.AddChild(row);

        IReadOnlyList<float> steps = Sim?.Speeds ?? (IReadOnlyList<float>)System.Array.Empty<float>();
        for (int i = 0; i < steps.Count; i++)
        {
            SpeedEntry entry = MakeEntry(i, steps[i]);
            _entries.Add(entry);
            row.AddChild(entry.Button);
        }

        // A scene with no sim wired gets no panel rather than an empty box
        // floating over the world — the same rule the palette holds.
        _panel.Visible = _entries.Count > 0;
    }

    private SpeedEntry MakeEntry(int index, float multiplier)
    {
        string label = SpeedLabel(multiplier);
        var button = new Button
        {
            Name = $"Speed{index}Button",
            Text = label,
            ToggleMode = true,
            // Nothing on the HUD keeps keyboard focus: a focused button would
            // draw a focus ring in every screenshot after the first click.
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(ButtonWidth, ButtonHeight),
            TooltipText = multiplier <= 0f
                ? "stop sim time (the camera keeps working)"
                : $"run sim time at {label}",
        };
        button.AddThemeFontSizeOverride("font_size", ButtonFontSize);
        button.AddThemeStyleboxOverride("normal", _idleStyle);
        button.AddThemeStyleboxOverride("hover", _hoverStyle);
        button.AddThemeStyleboxOverride("pressed", _armedStyle);
        button.AddThemeStyleboxOverride("hover_pressed", _armedHoverStyle);
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());

        // The button takes the same path a test takes, which is what stops the
        // bar and the clock disagreeing about the speed.
        button.Pressed += () => Select(index);

        return new SpeedEntry(index, multiplier, button, label);
    }

    private static StyleBoxFlat ButtonStyle(Color background, Color border, int borderWidth)
    {
        var style = new StyleBoxFlat { BgColor = background, BorderColor = border };
        style.SetBorderWidthAll(borderWidth);
        style.SetCornerRadiusAll(8);
        return style;
    }
}
