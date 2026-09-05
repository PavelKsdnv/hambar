using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Arable;

/// <summary>
/// Whether a palette entry can be picked, and how the palette draws it when it
/// cannot. <b>This is the M8 seam</b>: unlock tiers decide the value, the
/// palette only renders it, and neither the button nor
/// <see cref="BuildPalette.Select(int)"/> will arm a tool that is not
/// <see cref="Available"/> — a locked entry is locked on every path into the
/// palette, not just the one with a mouse on it.
/// </summary>
public enum ToolAvailability
{
    /// <summary>Pickable: the button is lit and the tool can be armed.</summary>
    Available,

    /// <summary>
    /// On the bar but not pickable — greyed out and unclickable. The default
    /// answer for a tool the player has not unlocked, because a tool they can
    /// see they do not have is a tool they can work towards.
    /// </summary>
    Locked,

    /// <summary>
    /// Not on the bar at all. For entries that should not even be advertised —
    /// a spoiler, or a tool a scenario removes.
    /// </summary>
    Hidden,
}

/// <summary>
/// One button on the <see cref="BuildPalette"/>: the tool it arms, the widgets
/// that draw it, and its <see cref="Availability"/>. Everything else it shows —
/// whether it is armed, what it costs — is read off the <see cref="Tool"/> on
/// every <see cref="BuildPalette.Refresh"/>, never stored here, so the entry
/// cannot drift from the tool it stands for.
/// </summary>
public sealed class PaletteEntry
{
    internal PaletteEntry(
        BuildTool tool, int slot, Button button, TextureRect icon, Label cost, Label key)
    {
        Tool = tool;
        Slot = slot;
        Button = button;
        Icon = icon;
        CostLabel = cost;
        KeyLabel = key;
    }

    /// <summary>The tool this entry arms.</summary>
    public BuildTool Tool { get; }

    /// <summary>
    /// 1-based position on the bar, which is also its accelerator key
    /// (entry 1 is key 1). Zero when the entry sits past the number row and has
    /// no key of its own.
    /// </summary>
    public int Slot { get; }

    /// <summary>The button itself — what a click lands on, and what M8 greys out.</summary>
    public Button Button { get; }

    /// <summary>
    /// The tool's picture, and the whole of what the button shows for
    /// <i>which</i> tool it is — the name is on the tooltip instead.
    /// </summary>
    public TextureRect Icon { get; }

    internal Label CostLabel { get; }

    internal Label KeyLabel { get; }

    /// <summary>
    /// What the button calls the tool. It is no longer printed on the button —
    /// the icon is what the player reads at a glance — but an icon bar still
    /// has to be able to say what a picture means, so the name reaches the
    /// player through <see cref="Button"/>'s tooltip and reaches code through
    /// here. Refreshed off the tool, like everything else the entry shows.
    /// </summary>
    public string NameText { get; internal set; } = string.Empty;

    /// <summary>What the button says the tool costs — read from the tool, never hard-coded.</summary>
    public string CostText => CostLabel.Text;

    /// <summary>The accelerator key printed in the button's corner.</summary>
    public string KeyText => KeyLabel.Text;

    /// <summary>Whether the player may pick this entry. See <see cref="ToolAvailability"/>.</summary>
    public ToolAvailability Availability { get; internal set; } = ToolAvailability.Available;

    /// <summary>Shorthand for "pickable right now".</summary>
    public bool IsAvailable => Availability == ToolAvailability.Available;

    /// <summary>
    /// Whether this entry's tool is the armed one. Asked of the tool, so it is
    /// true however the tool was armed — the palette, a key, or code.
    /// </summary>
    public bool IsActive => Tool.Active;

    /// <summary>The last state the button was painted for, so colours are only redone on a change.</summary>
    internal (bool Active, ToolAvailability Availability)? Painted { get; set; }
}

/// <summary>
/// The build palette: the on-screen bar the player picks build tools from, and
/// the replacement for the number-key menu that stood in for it
/// (<see cref="DevShortcuts"/> is what is left of that menu). One button per
/// tool, each showing the tool's name, what it costs and the key that arms it,
/// with the armed one filled in the same amber the build cursor uses so it
/// reads at a glance.
///
/// <b>It is not a second source of truth.</b> Which tool is armed is a fact
/// about the tools — <see cref="BuildTool.Active"/>, kept unique by the
/// <see cref="BuildTool.ToolGroup"/> — and this class only ever reads it.
/// <see cref="Refresh"/> repaints every button from the tools' own state and
/// runs every frame, so a tool disarmed by any other route (Esc, right click,
/// another tool arming, a dev script calling <c>SetActive</c>) shows up on the
/// bar immediately. The palette stores exactly one thing the tools do not
/// know: each entry's <see cref="ToolAvailability"/>, which is M8's seam.
///
/// <b>Selecting is one path, and the buttons take it.</b>
/// <see cref="Select(int)"/> / <see cref="Toggle(int)"/> are what a button
/// press calls, what the number-key accelerator calls, and what a headless test
/// calls — so a test exercises the player's path without synthesizing a mouse
/// click on a <see cref="Control"/>, and a locked entry is refused on all three.
///
/// The bar is built in code from the wired <see cref="Tools"/> list — order on
/// the bar is the order of that list, which is a design decision rather than an
/// accident of scene-tree order — and styled with
/// <see cref="StyleBoxFlat"/>es, because the default button look is not a
/// toolbar.
/// </summary>
public partial class BuildPalette : Control
{
    /// <summary>Distance from the bottom edge of the screen to the bar.</summary>
    private const float BottomMargin = 18f;

    /// <summary>
    /// Buttons are square: the tool is read as a picture now, and a picture has
    /// no reason to be wider than it is tall.
    /// </summary>
    private const float ButtonSide = 72f;

    private const int ButtonSeparation = 8;

    /// <summary>How much of the button the icon itself takes.</summary>
    private const float IconSide = 34f;

    private const int CostFontSize = 12;
    private const int KeyFontSize = 11;

    /// <summary>Highest entry that can still have a number key of its own.</summary>
    private const int LastKeySlot = 9;

    // Idle, hovered, armed and locked, in one place so the bar can be retuned
    // without hunting through the builder.
    private static readonly Color BarBackground = new(0.07f, 0.08f, 0.10f, 0.90f);
    private static readonly Color BarBorder = new(1f, 1f, 1f, 0.10f);
    private static readonly Color IdleBackground = new(0.17f, 0.18f, 0.22f, 0.96f);
    private static readonly Color IdleBorder = new(1f, 1f, 1f, 0.10f);
    private static readonly Color HoverBackground = new(0.25f, 0.27f, 0.32f, 1f);
    private static readonly Color HoverBorder = new(1f, 1f, 1f, 0.24f);
    private static readonly Color LockedBackground = new(0.12f, 0.13f, 0.15f, 0.85f);
    private static readonly Color LockedBorder = new(1f, 1f, 1f, 0.05f);

    /// <summary>
    /// The armed button's fill — the build cursor's yellow
    /// (<see cref="BuildTool.CursorLegal"/>), so "this is the tool that is
    /// listening to your clicks" is the same colour on the bar as it is on the
    /// ground.
    /// </summary>
    private static readonly Color ArmedBackground = new(0.96f, 0.79f, 0.24f, 1f);
    private static readonly Color ArmedHoverBackground = new(1f, 0.87f, 0.38f, 1f);
    private static readonly Color ArmedBorder = new(1f, 0.95f, 0.65f, 1f);

    // The icon tints are a modulate, which multiplies — which is why the source
    // SVGs are white artwork. A dark glyph could not be lit up again.
    private static readonly Color IconColor = new(0.94f, 0.95f, 0.98f);
    private static readonly Color CostColor = new(0.72f, 0.76f, 0.83f);
    private static readonly Color KeyColor = new(1f, 1f, 1f, 0.38f);
    private static readonly Color ArmedIconColor = new(0.13f, 0.10f, 0.02f);
    private static readonly Color ArmedCostColor = new(0.32f, 0.24f, 0.04f);
    private static readonly Color ArmedKeyColor = new(0.32f, 0.24f, 0.04f);
    private static readonly Color LockedIconColor = new(1f, 1f, 1f, 0.32f);
    private static readonly Color LockedCostColor = new(1f, 1f, 1f, 0.22f);

    /// <summary>
    /// The tools on the bar, left to right. Wired in Main.tscn; the palette
    /// reads each tool's name and price off the tool itself, so adding the M5
    /// silo or the M6 mill to this list is the whole of putting it on the bar.
    /// </summary>
    [Export] public Godot.Collections.Array<BuildTool> Tools { get; set; } = new();

    private readonly List<PaletteEntry> _entries = new();

    private PanelContainer _bar = null!;
    private StyleBoxFlat _idleStyle = null!;
    private StyleBoxFlat _hoverStyle = null!;
    private StyleBoxFlat _armedStyle = null!;
    private StyleBoxFlat _armedHoverStyle = null!;
    private StyleBoxFlat _lockedStyle = null!;

    /// <summary>The buttons on the bar, in the order they are drawn.</summary>
    public IReadOnlyList<PaletteEntry> Entries => _entries;

    /// <summary>
    /// The panel the buttons sit on — the toolbar as the player sees it. Public
    /// because where the bar is on screen, and that it is clear of the corner
    /// readouts, is a claim <see cref="BuildSmokeTest"/> checks like any other.
    /// </summary>
    public Control Bar => _bar;

    /// <summary>How many entries the bar holds.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Index of the armed entry, or -1 when no tool is armed. Derived from the
    /// tools every time it is asked, never cached — that is the whole reason
    /// the bar cannot disagree with the world.
    /// </summary>
    public int ActiveIndex
    {
        get
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Tool.Active)
                {
                    return i;
                }
            }
            return -1;
        }
    }

    /// <summary>The armed tool, or null when there is none.</summary>
    public BuildTool? ActiveTool
    {
        get
        {
            int index = ActiveIndex;
            return index < 0 ? null : _entries[index].Tool;
        }
    }

    public override void _Ready()
    {
        // A full-screen Control would otherwise swallow every click meant for
        // the world; only the bar itself takes the mouse.
        MouseFilter = MouseFilterEnum.Ignore;
        BuildBar();
        Refresh();
    }

    /// <summary>
    /// Repaints the bar from the tools. Called every frame, which is how the
    /// palette observes rather than mirrors: nothing has to tell it that a tool
    /// was disarmed by Esc, by a right click, or by another tool arming.
    /// </summary>
    public override void _Process(double delta) => Refresh();

    /// <summary>
    /// Number keys as accelerators: key <c>n</c> does exactly what clicking the
    /// n-th button does, because it calls the same <see cref="Toggle(int)"/>.
    /// The keys are an accelerator for the palette, not a way around it — which
    /// is what stops a button and the world disagreeing about what is armed.
    /// The dev shortcuts keep the top of the number row (see
    /// <see cref="DevShortcuts"/>).
    /// </summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        for (int index = 0; index < _entries.Count && index < LastKeySlot; index++)
        {
            if (@event.IsActionPressed($"menu_{index + 1}"))
            {
                Toggle(index);
                GetViewport().SetInputAsHandled();
                return;
            }
        }
    }

    /// <summary>Where <paramref name="tool"/> sits on the bar, or -1 if it is not on it.</summary>
    public int IndexOf(BuildTool? tool)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Tool == tool)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>The entry at <paramref name="index"/>, or null when there is none.</summary>
    public PaletteEntry? Entry(int index) =>
        index >= 0 && index < _entries.Count ? _entries[index] : null;

    /// <summary>
    /// <b>The selection path</b>: arms the entry's tool (and, through the tool
    /// group, disarms every other one). Returns false — changing nothing — for
    /// an index that is not on the bar or an entry that is not
    /// <see cref="ToolAvailability.Available"/>, so the M8 seam holds on the
    /// programmatic path as firmly as it does on the button.
    ///
    /// This is what a button press, a number key and a headless test all call.
    /// </summary>
    public bool Select(int index)
    {
        if (Entry(index) is not { IsAvailable: true } entry)
        {
            return false;
        }

        entry.Tool.SetActive(true);
        Refresh();
        return true;
    }

    /// <summary>Selects the entry that holds <paramref name="tool"/>.</summary>
    public bool Select(BuildTool? tool) => Select(IndexOf(tool));

    /// <summary>
    /// What a click on the button does: arms the entry, or disarms it when it
    /// is already the armed one. Same availability rules as
    /// <see cref="Select(int)"/>, and returns whether the press was accepted —
    /// not whether the tool ended up armed.
    /// </summary>
    public bool Toggle(int index)
    {
        if (Entry(index) is not { IsAvailable: true } entry)
        {
            return false;
        }

        entry.Tool.Toggle();
        Refresh();
        return true;
    }

    /// <summary>Leaves build mode: disarms whichever tool is armed, if any.</summary>
    public void Deselect()
    {
        ActiveTool?.SetActive(false);
        Refresh();
    }

    /// <summary>Whether the entry can be picked right now.</summary>
    public ToolAvailability GetAvailability(int index) =>
        Entry(index)?.Availability ?? ToolAvailability.Hidden;

    /// <summary>
    /// <b>The M8 seam.</b> Sets whether an entry can be picked, and how it is
    /// drawn when it cannot — greyed out (<see cref="ToolAvailability.Locked"/>)
    /// or off the bar entirely (<see cref="ToolAvailability.Hidden"/>). An
    /// entry that stops being available is disarmed on the spot, so the player
    /// is never left holding a tool the palette says they do not have.
    ///
    /// M8 owns the <i>rules</i> — which tier unlocks what — and calls this;
    /// nothing here knows what an unlock is.
    /// </summary>
    public bool SetAvailability(int index, ToolAvailability availability)
    {
        if (Entry(index) is not { } entry)
        {
            return false;
        }

        entry.Availability = availability;
        if (availability != ToolAvailability.Available && entry.Tool.Active)
        {
            entry.Tool.SetActive(false);
        }
        Refresh();
        return true;
    }

    /// <summary>Sets the availability of the entry holding <paramref name="tool"/>.</summary>
    public bool SetAvailability(BuildTool? tool, ToolAvailability availability) =>
        SetAvailability(IndexOf(tool), availability);

    /// <summary>
    /// Redraws every button from its tool: the name and the price come off the
    /// tool (so retuning <see cref="BuildTool.CostPerCell"/> in the editor moves
    /// the label), armed-ness comes off <see cref="BuildTool.Active"/>, and only
    /// the availability comes from the palette. Public because a test — or M8,
    /// after a tier change — wants the bar up to date within the frame instead
    /// of after the next one.
    /// </summary>
    public void Refresh()
    {
        foreach (PaletteEntry entry in _entries)
        {
            entry.NameText = DisplayNameOf(entry.Tool);
            entry.Button.TooltipText = TooltipFor(entry.Tool, entry.Slot);
            entry.Icon.Texture = entry.Tool.Icon;
            entry.CostLabel.Text = CostTextFor(entry.Tool);

            bool active = entry.Tool.Active;
            entry.Button.Visible = entry.Availability != ToolAvailability.Hidden;
            entry.Button.Disabled = entry.Availability != ToolAvailability.Available;
            entry.Button.SetPressedNoSignal(active && entry.IsAvailable);

            if (entry.Painted == (active, entry.Availability))
            {
                continue;
            }

            entry.Painted = (active, entry.Availability);
            bool locked = entry.Availability != ToolAvailability.Available;
            entry.Icon.SelfModulate =
                locked ? LockedIconColor : active ? ArmedIconColor : IconColor;
            entry.CostLabel.AddThemeColorOverride("font_color",
                locked ? LockedCostColor : active ? ArmedCostColor : CostColor);
            entry.KeyLabel.AddThemeColorOverride("font_color",
                locked ? LockedCostColor : active ? ArmedKeyColor : KeyColor);
        }
    }

    /// <summary>
    /// What the button calls the tool: the tool's own
    /// <see cref="BuildTool.DisplayName"/>, falling back to its node name so a
    /// tool wired in without one still gets a legible button.
    /// </summary>
    private static string DisplayNameOf(BuildTool tool) =>
        string.IsNullOrWhiteSpace(tool.DisplayName) ? tool.Name.ToString() : tool.DisplayName;

    /// <summary>
    /// What the button says a placement costs, worded by how the tool charges:
    /// a drag tool is priced per cell, a single-click tool is simply priced,
    /// and a tool that costs nothing says so rather than showing a zero. The
    /// number always comes from <see cref="BuildTool.CostPerCell"/> — the same
    /// export <see cref="PlacementRules"/> prices a plan with — and is grouped
    /// with <see cref="CultureInfo.InvariantCulture"/> like the money readout,
    /// so the bar and the balance agree on every machine.
    /// </summary>
    /// <summary>
    /// What hovering the button says: the name the icon stands for, its price,
    /// and the key that arms it. This is where the tool's name went when the
    /// button became a picture, so it is built from the tool on every
    /// <see cref="Refresh"/> rather than frozen at build time.
    /// </summary>
    private static string TooltipFor(BuildTool tool, int slot) => slot > 0
        ? $"{DisplayNameOf(tool)} ({CostTextFor(tool)}) — key {slot}"
        : $"{DisplayNameOf(tool)} ({CostTextFor(tool)})";

    private static string CostTextFor(BuildTool tool)
    {
        if (tool.CostPerCell == 0)
        {
            return "free";
        }

        string amount = tool.CostPerCell.ToString("N0", CultureInfo.InvariantCulture);
        return tool.NeedsAnchor ? amount + " / cell" : amount;
    }

    /// <summary>
    /// Builds the bar: a rounded panel at the bottom centre of the screen,
    /// holding one button per wired tool. Done in code rather than in the scene
    /// because the roster is the <see cref="Tools"/> list — M5 and M6 add to it,
    /// and neither should have to draw a button by hand.
    /// </summary>
    private void BuildBar()
    {
        _idleStyle = ButtonStyle(IdleBackground, IdleBorder, 1);
        _hoverStyle = ButtonStyle(HoverBackground, HoverBorder, 1);
        _armedStyle = ButtonStyle(ArmedBackground, ArmedBorder, 2);
        _armedHoverStyle = ButtonStyle(ArmedHoverBackground, ArmedBorder, 2);
        _lockedStyle = ButtonStyle(LockedBackground, LockedBorder, 1);

        var background = new StyleBoxFlat
        {
            BgColor = BarBackground,
            BorderColor = BarBorder,
            ShadowColor = new Color(0f, 0f, 0f, 0.45f),
            ShadowSize = 10,
        };
        background.SetBorderWidthAll(1);
        background.SetCornerRadiusAll(12);
        background.SetContentMarginAll(10);

        _bar = new PanelContainer { Name = "Bar" };
        _bar.AddThemeStyleboxOverride("panel", background);

        // Anchored to the bottom centre and grown from there, so the bar stays
        // centred and clear of the readouts in both top corners whatever the
        // window size and however many tools end up on it.
        _bar.AnchorLeft = 0.5f;
        _bar.AnchorRight = 0.5f;
        _bar.AnchorTop = 1f;
        _bar.AnchorBottom = 1f;
        _bar.GrowHorizontal = GrowDirection.Both;
        _bar.GrowVertical = GrowDirection.Begin;
        _bar.OffsetTop = -BottomMargin;
        _bar.OffsetBottom = -BottomMargin;
        AddChild(_bar);

        var row = new HBoxContainer { Name = "Tools" };
        row.AddThemeConstantOverride("separation", ButtonSeparation);
        _bar.AddChild(row);

        foreach (BuildTool? tool in Tools)
        {
            if (tool == null)
            {
                continue;
            }

            PaletteEntry entry = MakeEntry(tool, _entries.Count);
            _entries.Add(entry);
            row.AddChild(entry.Button);
        }

        // An empty bar is not a bar: a scene that wires no tools gets no panel
        // rather than an empty box floating over the world.
        _bar.Visible = _entries.Count > 0;
    }

    /// <summary>
    /// One square button: the tool's icon over its price, with the accelerator
    /// key small in the top-right corner and the tool's name on the tooltip.
    /// The inner widgets ignore the mouse so every pixel of the button is the
    /// button.
    /// </summary>
    private PaletteEntry MakeEntry(BuildTool tool, int index)
    {
        int slot = index + 1;
        var button = new Button
        {
            Name = $"{tool.Name}Button",
            ToggleMode = true,
            // Nothing on this bar should keep keyboard focus: a focused button
            // would draw a focus ring in every screenshot after the first click.
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(ButtonSide, ButtonSide),
        };
        button.AddThemeStyleboxOverride("normal", _idleStyle);
        button.AddThemeStyleboxOverride("hover", _hoverStyle);
        button.AddThemeStyleboxOverride("pressed", _armedStyle);
        button.AddThemeStyleboxOverride("hover_pressed", _armedHoverStyle);
        button.AddThemeStyleboxOverride("disabled", _lockedStyle);
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());

        var padding = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        padding.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        padding.AddThemeConstantOverride("margin_left", 6);
        padding.AddThemeConstantOverride("margin_right", 6);
        padding.AddThemeConstantOverride("margin_top", 7);
        padding.AddThemeConstantOverride("margin_bottom", 7);
        button.AddChild(padding);

        var column = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        column.AddThemeConstantOverride("separation", 3);
        padding.AddChild(column);

        // KeepAspectCentered, so an icon whose source is not square is letter-
        // boxed rather than stretched into a different shape than it was drawn.
        var icon = new TextureRect
        {
            Name = "ToolIcon",
            Texture = tool.Icon,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(IconSide, IconSide),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        column.AddChild(icon);

        // A tool with no icon wired still has to be identifiable, so the name
        // takes the icon's place rather than leaving a blank square.
        if (tool.Icon == null)
        {
            var fallback = new Label
            {
                Name = "ToolNameFallback",
                Text = DisplayNameOf(tool),
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            fallback.AddThemeFontSizeOverride("font_size", CostFontSize);
            fallback.AddThemeColorOverride("font_color", IconColor);
            icon.AddChild(fallback);
            fallback.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        }

        var costLabel = new Label
        {
            Name = "ToolCost",
            Text = CostTextFor(tool),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        costLabel.AddThemeFontSizeOverride("font_size", CostFontSize);
        column.AddChild(costLabel);

        var keyLabel = new Label
        {
            Name = "ToolKey",
            Text = slot <= LastKeySlot ? slot.ToString(CultureInfo.InvariantCulture) : string.Empty,
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorLeft = 1f,
            AnchorRight = 1f,
            GrowHorizontal = GrowDirection.Begin,
            GrowVertical = GrowDirection.End,
            OffsetLeft = -10f,
            OffsetRight = -10f,
            OffsetTop = 5f,
            OffsetBottom = 5f,
        };
        keyLabel.AddThemeFontSizeOverride("font_size", KeyFontSize);
        button.AddChild(keyLabel);

        button.TooltipText = TooltipFor(tool, slot <= LastKeySlot ? slot : 0);

        // The button takes the same path a test or a key takes. It is the only
        // thing the click knows how to do, which is why the bar cannot end up
        // arming something the rest of the game disagrees about.
        button.Pressed += () => Toggle(index);

        return new PaletteEntry(tool, slot <= LastKeySlot ? slot : 0,
            button, icon, costLabel, keyLabel)
        {
            NameText = DisplayNameOf(tool),
        };
    }

    private static StyleBoxFlat ButtonStyle(Color background, Color border, int borderWidth)
    {
        var style = new StyleBoxFlat { BgColor = background, BorderColor = border };
        style.SetBorderWidthAll(borderWidth);
        style.SetCornerRadiusAll(8);
        return style;
    }
}
