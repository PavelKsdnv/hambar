using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Arable;

/// <summary>
/// Headless smoke test for build mode (M2): the <see cref="BuildTool"/> base,
/// the <see cref="PlacementRules"/> it validates against, and the ghost preview
/// that shows a refusal before the click. Instances Main.tscn and drives the
/// tools through their public, cell-driven API — activate, hover, click,
/// cancel — because headless has no cursor. Run with:
/// godot --headless --path . res://scenes/dev/BuildSmokeTest.tscn
/// Exits 0 on pass, 1 on failure.
///
/// Four tools are covered, in that order: the road tool first, then the field
/// tool — which therefore validates against a world that already holds this
/// test's roads, and is also where the "only one tool armed at a time" rule is
/// proven — then the structure tool, whose must-touch-a-road rule only means
/// anything once there is a road network to touch, and last the bulldozer,
/// which needs one of each of those on the map before it can take them off
/// again. <b>Money comes after all four</b>, because paying for a placement is
/// the one rule that needs every tool already proven: see
/// <see cref="CheckCommitChargesExactlyTheCost"/> onwards. A new tool adds its
/// assertions the same way: give it a section like
/// <see cref="CheckLegalPlacement"/> and reuse the cell-finding helpers at the
/// bottom, which look terrain up at runtime instead of hard-coding coordinates
/// that a seed change would invalidate.
///
/// <b>Tools are armed through the <see cref="BuildPalette"/></b>, which is the
/// player's only way to reach one. <see cref="BuildPalette.Select(int)"/> is
/// the same call the buttons make, so a headless run exercises the path a click
/// takes without synthesizing a click on a <see cref="Control"/>; the bar
/// itself — what it shows, what it costs, and the M8 availability seam — is
/// asserted first, from <see cref="CheckPaletteStartState"/> onwards.
///
/// <b>Prices are kept out of the sections that are not about them.</b> The
/// money section sets the balance it needs before every check it makes;
/// everything before it runs on <see cref="WorkingBalance"/>, set once the
/// starting balance has been asserted, so no assertion about legality can fail
/// for want of funds.
/// </summary>
public partial class BuildSmokeTest : Node
{
    /// <summary>
    /// Where each tool sits on the build palette, left to right. The bar's
    /// order is the order Main.tscn wires the tools in, and these are the
    /// indices <see cref="BuildPalette.Select(int)"/> takes.
    /// </summary>
    private const int RoadEntry = 0;
    private const int FieldEntry = 1;
    private const int StructureEntry = 2;
    private const int BulldozeEntry = 3;

    /// <summary>Width and height of the rectangle the field checks mark.</summary>
    private const int FieldRectWidth = 3;
    private const int FieldRectHeight = 2;

    /// <summary>
    /// The balance every section that is <i>not</i> about money runs on: far
    /// more than this test can spend, so pricing can never reach into an
    /// assertion about something else. Set once the starting balance itself has
    /// been asserted.
    /// </summary>
    private const int WorkingBalance = 1_000_000;

    private WorldGrid _world = null!;
    private RoadBuildTool _tool = null!;
    private FieldBuildTool _fieldTool = null!;
    private StructureBuildTool _structureTool = null!;
    private BulldozeTool _bulldozeTool = null!;
    private BuildPalette _palette = null!;
    private CellInspector _inspector = null!;
    private Economy _economy = null!;
    private Label _moneyReadout = null!;
    private Label _cellReadout = null!;
    private int _frame;
    private bool _failed;

    // Cells chosen from the generated terrain in _Ready, not hard-coded.
    private Vector2I _soilFrom;
    private Vector2I _soilTo;
    private Vector2I _rock;
    private Vector2I _waterAnchor;
    private Vector2I _water;
    private Vector2I _field;
    private Vector2I _fieldAnchor;
    private Vector2I _offMap;

    // The field section's cells, picked once the roads are down (see PickFieldCells).
    private Vector2I _rectFrom;
    private Vector2I _rectTo;
    private Vector2I _nextRectFrom;
    private Vector2I _nextRectTo;
    private Vector2I _belowRect;
    private Field? _markedField;

    // The structure section's cell and the buildings it places. Like the field
    // section's cells these are found (or, for the road-access checks, built)
    // at the point of use, once this test's roads and fields are on the map.
    private Vector2I _structureCell;
    private Structure? _structure;
    private Structure? _secondStructure;

    // The terrain the bulldoze section builds on, remembered cell by cell
    // *before* anything is placed on it — the headline the whole tool has to
    // survive: clearing a placement must leave the ground it stood on exactly
    // as it was found (see RememberTerrain / TerrainUnchanged).
    private readonly List<Vector2I> _rememberedCells = new();
    private readonly List<TerrainType> _rememberedTerrain = new();
    private readonly List<float> _rememberedFertility = new();

    public override void _Ready()
    {
        Node main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        AddChild(main);
        _world = main.GetNode<WorldGrid>("World");
        _tool = main.GetNode<RoadBuildTool>("RoadTool");
        _fieldTool = main.GetNode<FieldBuildTool>("FieldTool");
        _structureTool = main.GetNode<StructureBuildTool>("StructureTool");
        _bulldozeTool = main.GetNode<BulldozeTool>("BulldozeTool");
        _palette = main.GetNode<BuildPalette>("Hud/BuildPalette");
        _inspector = main.GetNode<CellInspector>("CellInspector");
        _economy = main.GetNode<Economy>("Economy");
        _moneyReadout = main.GetNode<Label>("Hud/MoneyReadout");
        _cellReadout = main.GetNode<Label>("Hud/CellReadout");
    }

    public override void _Process(double delta)
    {
        _frame++;
        if (_frame == 5)
        {
            PickTestCells();
            CheckStartState();
            CheckMoneyStartState();
            CheckPaletteStartState();

            // Key 1 is the first palette entry's accelerator, and it arms the
            // road tool by calling into the palette — the same path the button
            // takes, which is what stops the bar and the world disagreeing.
            Input.ParseInputEvent(new InputEventAction { Action = "menu_1", Pressed = true });
        }
        else if (_frame == 10)
        {
            Check("key 1 activates the build tool", _tool.Active);
            Check("the accelerator went through the palette",
                _palette.ActiveIndex == RoadEntry && _palette.ActiveTool == _tool
                && OnlyButtonPressed(RoadEntry));

            CheckPaletteSelectsEveryTool();
            CheckPaletteFollowsToolsArmedElsewhere();
            CheckPaletteShowsWhatToolsCost();
            CheckPaletteAvailabilitySeam();
            Check("the palette hands the road tool back for the checks below",
                _palette.Select(RoadEntry) && OnlyToolArmed(RoadEntry));

            CheckHoverPreview();
            // Before anything is placed: the road-access rule is asserted
            // against a world whose only road is the starting one.
            CheckRoadAccessRule();
            CheckLegalPlacement();
            CheckIllegalTerrainIsRefused();
            CheckOccupiedCellIsRefused();
            CheckOffMapIsRefused();

            // Esc drops a pending anchor.
            _tool.ClickCell(_soilFrom);
            Check("a fresh anchor is pending before the cancel", _tool.Anchor == _soilFrom);
            Input.ParseInputEvent(new InputEventAction { Action = "ui_cancel", Pressed = true });
        }
        else if (_frame == 15)
        {
            Check("Esc drops the pending anchor", _tool.Anchor == null);
            Check("Esc with an anchor keeps the tool active", _tool.Active);
            Check("cancelling clears the ghost", _tool.GhostCellCount == 0);
            // Nothing told the palette about that Esc: it reads the tools every
            // frame, so the bar is already right.
            Check("the bar still shows the road tool armed after the Esc",
                _palette.ActiveIndex == RoadEntry && OnlyButtonPressed(RoadEntry));

            // Right click does the same two steps: anchor first, tool second.
            _tool.ClickCell(_soilFrom);
            Input.ParseInputEvent(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Right,
                Pressed = true,
                Position = Vector2.Zero,
            });
        }
        else if (_frame == 20)
        {
            Check("right click drops the pending anchor", _tool.Anchor == null);
            Check("right click with an anchor keeps the tool active", _tool.Active);

            Input.ParseInputEvent(new InputEventMouseButton
            {
                ButtonIndex = MouseButton.Right,
                Pressed = true,
                Position = Vector2.Zero,
            });
        }
        else if (_frame == 25)
        {
            Check("right click with no anchor leaves the tool", !_tool.Active);
            Check("leaving the tool clears the ghost", _tool.GhostCellCount == 0);
            Check("leaving the tool clears the preview", _tool.Preview == null);
            // Same again for the tool leaving by right click, and again with
            // nobody having told the bar.
            Check("the bar shows nothing armed once right click left the tool",
                _palette.ActiveIndex == -1 && OnlyButtonPressed(-1));

            // Re-arm the road tool, so the next frame can prove that arming the
            // field tool is what disarms it.
            Input.ParseInputEvent(new InputEventAction { Action = "menu_1", Pressed = true });
        }
        else if (_frame == 30)
        {
            Check("key 1 re-arms the road tool", _tool.Active);

            // The palette arms the field tool — the player's path, and the same
            // call its button makes.
            _palette.Select(FieldEntry);
        }
        else if (_frame == 35)
        {
            CheckOneToolAtATime();
            CheckRectFootprint();

            PickFieldCells();
            CheckFieldPlacement();
            CheckFieldsStayDistinct();
            CheckFieldOverlapIsRefused();
            CheckFieldOverRoadIsRefused();
            CheckFieldIntoRoughTerrainIsRefused();
            CheckClearingShrinksAndDropsAField();

            // The palette arms the structure tool — the player's path again.
            _palette.Select(StructureEntry);
        }
        else if (_frame == 40)
        {
            CheckStructureToolArmed();
            CheckStructurePlacement();
            CheckStructureNeedsRoadAccess();
            CheckStructureOnRoughGroundIsRefused();
            CheckStructureOnOccupiedGroundIsRefused();
            CheckClearingDemolishesAStructure();

            // The palette arms the bulldozer — the player's path, once more.
            _palette.Select(BulldozeEntry);
        }
        else if (_frame == 45)
        {
            CheckBulldozeToolArmed();
            CheckBulldozeClearsARoad();
            CheckBulldozeClearsAField();
            CheckBulldozeClearsAStructure();
            CheckTerrainSurvivedTheBulldozer();
            CheckBulldozeShrinksAndDropsAField();
            CheckBulldozeTakesAWholeStructure();
            CheckBulldozeSkipsEmptyGround();
            CheckNothingToClearIsRefused();
            CheckRefundSeam();
        }
        else if (_frame == 50)
        {
            // Money last: paying for a placement is the one rule that needs
            // every tool already proven, and every check below sets the balance
            // it is about.
            CheckCommitChargesExactlyTheCost();
            CheckUnaffordablePlacementIsRefused();
            CheckADragIsAffordableAsAWhole();
            CheckAnAnyCellDragPaysForWhatItActsOn();
            CheckTheAccountItself();
            CheckTheRefundSeamCreditsTheAccount();

            GD.Print(_failed ? "BUILD SMOKE TEST FAILED" : "BUILD SMOKE TEST PASSED");
            GetTree().Quit(_failed ? 1 : 0);
        }
    }

    /// <summary>The tool is inert until it is armed.</summary>
    private void CheckStartState()
    {
        Check("build tool starts inactive", !_tool.Active);
        Check("build tool starts with no anchor", _tool.Anchor == null);
        Check("build tool starts with no ghost", _tool.GhostCellCount == 0);
        Check("build tool starts with no preview", _tool.Preview == null);
        Check("build tool is wired to the world", _tool.World == _world);
        Check("the field tool starts inactive too", !_fieldTool.Active);
        Check("the field tool is wired to the world", _fieldTool.World == _world);
        Check("the structure tool starts inactive too", !_structureTool.Active);
        Check("the structure tool is wired to the world", _structureTool.World == _world);
        Check("the bulldozer starts inactive too", !_bulldozeTool.Active);
        Check("the bulldozer is wired to the world", _bulldozeTool.World == _world);
        Check("the bulldozer has removed nothing before it is used",
            _bulldozeTool.Removals.Count == 0 && _bulldozeTool.RefundTotal == 0);
        Check("no field is registered before one is marked",
            _world.Fields.Count == 0 && _world.FieldCellCount == 0);
        Check("no structure is registered before one is placed",
            _world.Structures.Count == 0 && _world.StructureCellCount == 0);
    }

    /// <summary>
    /// The account before anything has been bought: the player starts with the
    /// exported balance, the readout on screen says so, and every tool carries
    /// a price it charges against that one account.
    ///
    /// The prices are asserted as <i>relations</i> — building costs something,
    /// bulldozing does not — never as numbers. They are placeholders that live
    /// in Main.tscn precisely so they can be tuned without a rebuild, and a test
    /// spelling them out would turn every tuning pass into the code change it is
    /// supposed to avoid.
    ///
    /// It ends by putting the balance out of reach of the rest of the test: see
    /// <see cref="WorkingBalance"/>.
    /// </summary>
    private void CheckMoneyStartState()
    {
        Check("the player starts with the exported balance",
            _economy.Balance == _economy.StartingBalance);
        Check("the starting balance is a real one to spend",
            _economy.StartingBalance > 0);
        Check("the readout says what the balance is",
            _economy.Text == Economy.Describe(_economy.Balance)
            && _moneyReadout.Text == _economy.Text);
        Check("money reads as money, grouped and culture-independent",
            Economy.Describe(1234567) == "money: 1,234,567"
            && Economy.Describe(0) == "money: 0");

        Check("every build tool is wired to the one account",
            _tool.Economy == _economy && _fieldTool.Economy == _economy
            && _structureTool.Economy == _economy && _bulldozeTool.Economy == _economy);
        Check("building things costs money",
            _tool.CostPerCell > 0 && _fieldTool.CostPerCell > 0
            && _structureTool.CostPerCell > 0);
        Check("taking them off again does not", _bulldozeTool.CostPerCell == 0);

        // The readout is a visual claim, so it is checked as one: on the
        // screen, and not sitting on top of the readout already there.
        Rect2 screen = GetViewport().GetVisibleRect();
        Check("the money readout is visible", _moneyReadout.Visible);
        Check("the money readout is on screen",
            screen.Encloses(_moneyReadout.GetGlobalRect()));
        Check("the money readout does not overlap the cell readout",
            !_moneyReadout.GetGlobalRect().Intersects(_cellReadout.GetGlobalRect()));
        GD.Print($"money readout: \"{_moneyReadout.Text}\" at "
            + $"{_moneyReadout.GetGlobalRect()} on a {screen.Size} screen; "
            + $"cell readout at {_cellReadout.GetGlobalRect()}");

        _economy.SetBalance(WorkingBalance);
        Check("the balance can be set outright, which is how a test gets broke",
            _economy.Balance == WorkingBalance);
        Check("setting it takes the readout with it",
            _moneyReadout.Text == Economy.Describe(WorkingBalance));
    }

    /// <summary>
    /// The build palette before the player has touched it: one button per tool
    /// in bar order, each naming its tool and the key that arms it, nothing
    /// armed — and the bar itself where a toolbar belongs. The last part is a
    /// visual claim, so it is checked as one, the way the money readout is:
    /// on the screen, along the bottom, and clear of both corner readouts.
    /// </summary>
    private void CheckPaletteStartState()
    {
        Check("the palette holds one entry per build tool",
            _palette.Count == 4 && _palette.Entries.Count == _palette.Count);
        Check("the entries are the four tools, in bar order",
            _palette.Entry(RoadEntry)?.Tool == _tool
            && _palette.Entry(FieldEntry)?.Tool == _fieldTool
            && _palette.Entry(StructureEntry)?.Tool == _structureTool
            && _palette.Entry(BulldozeEntry)?.Tool == _bulldozeTool);
        Check("the palette finds an entry by its tool",
            _palette.IndexOf(_bulldozeTool) == BulldozeEntry
            && _palette.IndexOf(null) == -1);
        Check("there is no entry off either end of the bar",
            _palette.Entry(-1) == null && _palette.Entry(_palette.Count) == null);

        CheckPaletteDrawsToolIcons();

        // Both of the things a button says come off the tool it stands for, so
        // renaming or repricing one in the editor moves its button.
        Check("every button names its tool",
            AllEntries(entry => entry.NameText == entry.Tool.DisplayName
                && entry.NameText.Length > 0));
        Check("every button prints the key that arms it",
            _palette.Entry(RoadEntry)?.KeyText == "1"
            && _palette.Entry(FieldEntry)?.KeyText == "2"
            && _palette.Entry(StructureEntry)?.KeyText == "3"
            && _palette.Entry(BulldozeEntry)?.KeyText == "4");
        Check("each entry knows the slot it is in",
            AllEntries(entry => entry.Slot == _palette.IndexOf(entry.Tool) + 1));

        Check("every entry starts available",
            AllEntries(entry => entry.IsAvailable
                && entry.Availability == ToolAvailability.Available));
        Check("every button starts on the bar and clickable",
            AllEntries(entry => entry.Button.Visible && !entry.Button.Disabled));
        Check("no tool is armed before the player picks one",
            _palette.ActiveIndex == -1 && _palette.ActiveTool == null
            && AllEntries(entry => !entry.IsActive));
        Check("no button is drawn as the armed one", OnlyButtonPressed(-1));

        Rect2 screen = GetViewport().GetVisibleRect();
        Rect2 bar = _palette.Bar.GetGlobalRect();
        Check("the palette is visible", _palette.Visible && _palette.Bar.Visible);
        Check("the whole bar is on screen", screen.Encloses(bar));
        Check("the bar sits along the bottom of the screen",
            bar.Position.Y > screen.Size.Y * 0.6f);
        Check("the bar is centred on the screen",
            Mathf.Abs(bar.GetCenter().X - screen.GetCenter().X) < 1.5f);
        Check("the bar does not overlap the cell readout",
            !bar.Intersects(_cellReadout.GetGlobalRect()));
        Check("the bar does not overlap the money readout",
            !bar.Intersects(_moneyReadout.GetGlobalRect()));
        Check("every button is drawn inside the bar",
            AllEntries(entry => bar.Encloses(entry.Button.GetGlobalRect())));
        var buttons = new List<string>();
        foreach (PaletteEntry entry in _palette.Entries)
        {
            buttons.Add($"[{entry.KeyText} {entry.NameText} / {entry.CostText}]");
        }
        GD.Print($"build palette: {bar} on a {screen.Size} screen; buttons "
            + string.Join(", ", buttons));
    }

    /// <summary>
    /// The button shows the tool's picture, tinted for the state it is in.
    /// Icons come off the tool the way its name and price do, and this scene
    /// instantiates Main.tscn, so what it checks is the bar the player gets.
    /// Clearing one afterwards covers the other half: a tool with no picture
    /// has to stay identifiable rather than become a blank square.
    ///
    /// The tint is a <i>modulate</i>, which multiplies, so it only works on
    /// white artwork; a dark glyph would stay dark however it were tinted.
    /// Asserting the armed and idle tints sit on opposite sides of mid-grey is
    /// what would catch someone dropping a dark icon into the bar.
    /// </summary>
    private void CheckPaletteDrawsToolIcons()
    {
        PaletteEntry? road = _palette.Entry(RoadEntry);
        if (road == null)
        {
            Check("the palette has a road entry to draw", false);
            return;
        }

        // Main.tscn wires a picture to every tool, so the bar this scene builds
        // is the bar the player gets.
        Check("every tool on the bar carries an icon",
            AllEntries(entry => entry.Tool.Icon != null));
        Check("every button draws the icon its own tool carries",
            AllEntries(entry => entry.Icon.Texture == entry.Tool.Icon));
        Check("the buttons are square, now that a picture is what they show",
            AllEntries(entry => Mathf.IsEqualApprox(
                entry.Button.Size.X, entry.Button.Size.Y)));
        Check("the tool still knows the name the button no longer prints",
            road.NameText == _tool.DisplayName && road.NameText.Length > 0);
        Check("hovering says what the picture means, with its price and key",
            road.Button.TooltipText.Contains(_tool.DisplayName)
            && road.Button.TooltipText.Contains(road.CostText));

        _palette.Select(RoadEntry);
        _palette.Refresh();
        Color armed = road.Icon.SelfModulate;
        _palette.Deselect();
        _palette.Refresh();
        Color idle = road.Icon.SelfModulate;

        Check("the armed button tints its icon differently from an idle one",
            armed != idle);
        Check("the idle tint is light, to read on a dark button", idle.Luminance > 0.5f);
        Check("the armed tint is dark, to read on the amber fill", armed.Luminance < 0.5f);

        _palette.SetAvailability(RoadEntry, ToolAvailability.Locked);
        _palette.Refresh();
        Check("a locked entry dims its icon rather than hiding it",
            road.Icon.SelfModulate.A < idle.A);
        _palette.SetAvailability(RoadEntry, ToolAvailability.Available);

        // A tool with no picture wired must still be identifiable rather than a
        // blank square — the half of the palette Main.tscn does not exercise.
        Texture2D? wired = _tool.Icon;
        _tool.Icon = null;
        _palette.Refresh();
        Check("clearing a tool's icon takes it off the button too",
            road.Icon.Texture == null);
        Check("a tool with no icon still names itself somewhere",
            road.NameText == _tool.DisplayName
            && road.Button.TooltipText.Contains(_tool.DisplayName));

        // Put it back: every later section builds roads through this bar.
        _tool.Icon = wired;
        _palette.Refresh();
        Check("the icon goes back on when the tool carries one again",
            road.Icon.Texture == wired);
    }

    /// <summary>
    /// The selection path, exercised the way a button press exercises it —
    /// <see cref="BuildPalette.Select(int)"/> is the call the button makes.
    /// Picking an entry arms that tool and disarms every other one, which is
    /// the tool group's rule reached <i>through</i> the palette rather than
    /// duplicated by it.
    /// </summary>
    private void CheckPaletteSelectsEveryTool()
    {
        for (int index = 0; index < _palette.Count; index++)
        {
            PaletteEntry entry = _palette.Entry(index)!;
            string tool = entry.NameText;
            Check($"the palette selects the {tool} tool", _palette.Select(index));
            Check($"selecting {tool} arms exactly that tool", OnlyToolArmed(index));
            Check($"the bar draws {tool} as the armed entry",
                _palette.ActiveIndex == index && _palette.ActiveTool == entry.Tool
                && OnlyButtonPressed(index));
        }

        Check("selecting the armed entry again leaves it armed",
            _palette.Select(BulldozeEntry) && OnlyToolArmed(BulldozeEntry));
        Check("clicking the armed entry again puts the tool down",
            _palette.Toggle(BulldozeEntry) && !_bulldozeTool.Active
            && _palette.ActiveIndex == -1 && _palette.ActiveTool == null);
        Check("no button is drawn armed once nothing is", OnlyButtonPressed(-1));
        Check("toggling it once more picks it back up",
            _palette.Toggle(BulldozeEntry) && OnlyToolArmed(BulldozeEntry));

        Check("selecting by tool arms that tool's entry",
            _palette.Select(_fieldTool) && OnlyToolArmed(FieldEntry));
        Check("an index off the bar selects nothing",
            !_palette.Select(-1) && !_palette.Select(_palette.Count)
            && OnlyToolArmed(FieldEntry));
        Check("a tool that is not on the bar selects nothing",
            !_palette.Select((BuildTool?)null) && OnlyToolArmed(FieldEntry));

        _palette.Deselect();
        Check("leaving build mode disarms whatever was armed",
            _palette.ActiveIndex == -1 && OnlyToolArmed(-1) && OnlyButtonPressed(-1));
    }

    /// <summary>
    /// <b>The palette reflects the tools; it does not remember its own answer.</b>
    /// A tool armed or disarmed by any other route shows up on the bar, because
    /// <see cref="BuildPalette.Refresh"/> reads <see cref="BuildTool.Active"/>
    /// rather than a copy of it. The two routes the player has — Esc and right
    /// click — are input events, so they are asserted across frames in
    /// <c>_Process</c>; what is taken here is every other way a tool's state
    /// can change without the palette being told.
    /// </summary>
    private void CheckPaletteFollowsToolsArmedElsewhere()
    {
        _structureTool.SetActive(true);
        _palette.Refresh();
        Check("the bar shows a tool armed without it",
            _palette.ActiveIndex == StructureEntry && OnlyToolArmed(StructureEntry)
            && OnlyButtonPressed(StructureEntry));

        _structureTool.SetActive(false);
        _palette.Refresh();
        Check("the bar shows a tool disarmed without it",
            _palette.ActiveIndex == -1 && OnlyButtonPressed(-1));

        // Arming one tool disarms the rest through the tool group. The palette
        // has no part in that and no copy of it — it just reads the result.
        _palette.Select(RoadEntry);
        _fieldTool.SetActive(true);
        _palette.Refresh();
        Check("the bar follows the tool group when another tool takes over",
            !_tool.Active && _palette.ActiveIndex == FieldEntry
            && OnlyToolArmed(FieldEntry) && OnlyButtonPressed(FieldEntry));

        // Cancel with no anchor pending is what Esc and right click end in.
        _fieldTool.Cancel();
        _palette.Refresh();
        Check("the bar shows a tool that cancelled itself",
            _palette.ActiveIndex == -1 && OnlyToolArmed(-1) && OnlyButtonPressed(-1));
    }

    /// <summary>
    /// What a button says a placement costs is read off
    /// <see cref="BuildTool.CostPerCell"/> — the same export
    /// <see cref="PlacementRules"/> prices a plan with — so retuning a price in
    /// the editor moves the label, and the bar can never quote a number the
    /// click does not charge. The wording follows how the tool charges: per cell
    /// for a drag, flat for a single click, "free" for a tool that costs
    /// nothing.
    /// </summary>
    private void CheckPaletteShowsWhatToolsCost()
    {
        Check("every priced button carries its tool's price",
            AllEntries(entry => entry.Tool.CostPerCell == 0
                || entry.CostText.Contains(Money(entry.Tool.CostPerCell))));
        Check("a drag tool is priced per cell",
            _palette.Entry(RoadEntry)?.CostText == Money(_tool.CostPerCell) + " / cell"
            && _palette.Entry(FieldEntry)?.CostText
                == Money(_fieldTool.CostPerCell) + " / cell");
        Check("a single-click tool is priced as the thing it places",
            _palette.Entry(StructureEntry)?.CostText == Money(_structureTool.CostPerCell));
        Check("a tool that costs nothing says so rather than showing a zero",
            _bulldozeTool.CostPerCell == 0
            && _palette.Entry(BulldozeEntry)?.CostText == "free");

        int priced = _structureTool.CostPerCell;
        _structureTool.CostPerCell = 4242;
        _palette.Refresh();
        Check("retuning a tool's price moves its button label",
            _palette.Entry(StructureEntry)?.CostText == "4,242");
        _structureTool.CostPerCell = priced;
        _palette.Refresh();
        Check("putting the price back puts the label back",
            _palette.Entry(StructureEntry)?.CostText == Money(priced));
    }

    /// <summary>
    /// <b>The M8 seam.</b> An entry can be locked (still on the bar, greyed and
    /// unclickable) or hidden (off it entirely), and either way it is refused on
    /// <i>every</i> path in — the button's and the programmatic one — because a
    /// seam only one path respects is decoration. A tool that stops being
    /// available while the player is holding it is taken out of their hand. The
    /// rules that decide any of this are M8's; nothing here knows what an unlock
    /// is.
    /// </summary>
    private void CheckPaletteAvailabilitySeam()
    {
        Check("an entry can be locked",
            _palette.SetAvailability(FieldEntry, ToolAvailability.Locked)
            && _palette.GetAvailability(FieldEntry) == ToolAvailability.Locked);
        Check("a locked entry says it is not available",
            _palette.Entry(FieldEntry) is { IsAvailable: false });
        Check("a locked button stays on the bar, greyed out and unclickable",
            _palette.Entry(FieldEntry) is { Button.Visible: true, Button.Disabled: true });
        Check("a locked entry cannot be selected programmatically either",
            !_palette.Select(FieldEntry) && !_fieldTool.Active);
        Check("nor through the tool it stands for",
            !_palette.Select(_fieldTool) && !_fieldTool.Active);
        Check("nor through the path the button and its accelerator both take",
            !_palette.Toggle(FieldEntry) && !_fieldTool.Active);
        Check("locking one entry leaves the rest pickable",
            _palette.Select(RoadEntry) && OnlyToolArmed(RoadEntry));

        Check("an entry can be hidden outright",
            _palette.SetAvailability(_structureTool, ToolAvailability.Hidden)
            && _palette.GetAvailability(StructureEntry) == ToolAvailability.Hidden);
        Check("a hidden entry is off the bar altogether",
            _palette.Entry(StructureEntry) is { Button.Visible: false, Button.Disabled: true });
        Check("a hidden entry cannot be selected either",
            !_palette.Select(StructureEntry) && !_structureTool.Active);
        Check("an unavailable entry is not the armed one",
            _palette.ActiveIndex == RoadEntry);

        Check("locking the armed tool takes it out of the player's hand",
            _palette.SetAvailability(RoadEntry, ToolAvailability.Locked)
            && !_tool.Active && _palette.ActiveIndex == -1 && OnlyButtonPressed(-1));
        Check("an entry off the bar has no availability to set",
            !_palette.SetAvailability(_palette.Count, ToolAvailability.Available)
            && _palette.GetAvailability(_palette.Count) == ToolAvailability.Hidden);

        // Everything goes back: nothing in this section may leak into the
        // sections after it.
        Check("an entry can be handed back",
            _palette.SetAvailability(RoadEntry, ToolAvailability.Available)
            && _palette.SetAvailability(FieldEntry, ToolAvailability.Available)
            && _palette.SetAvailability(StructureEntry, ToolAvailability.Available));
        Check("every entry is available again",
            AllEntries(entry => entry.IsAvailable && entry.Button.Visible
                && !entry.Button.Disabled));
        Check("an entry handed back can be picked again",
            _palette.Select(FieldEntry) && OnlyToolArmed(FieldEntry));
    }

    /// <summary>
    /// Whether the entry at <paramref name="index"/> is the one armed tool —
    /// -1 meaning "no tool is armed". The palette's whole promise about arming
    /// is this, so it is asked of the tools themselves.
    /// </summary>
    private bool OnlyToolArmed(int index)
    {
        for (int i = 0; i < _palette.Count; i++)
        {
            if (_palette.Entry(i)!.Tool.Active != (i == index))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The same question asked of the <i>buttons</i>: exactly the one entry is
    /// drawn armed. Deliberately does not refresh the palette first — a caller
    /// that changed a tool without going through the bar refreshes it itself,
    /// so the checks that read this across a frame boundary are really checking
    /// that the palette keeps itself up to date.
    /// </summary>
    private bool OnlyButtonPressed(int index)
    {
        for (int i = 0; i < _palette.Count; i++)
        {
            if (_palette.Entry(i)!.Button.ButtonPressed != (i == index))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether every entry on the bar satisfies <paramref name="test"/>.</summary>
    private bool AllEntries(Func<PaletteEntry, bool> test)
    {
        foreach (PaletteEntry entry in _palette.Entries)
        {
            if (!test(entry))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// An amount as the palette prints it — grouped and
    /// <see cref="CultureInfo.InvariantCulture"/>, like the money readout, so a
    /// test can predict the label on any machine.
    /// </summary>
    private static string Money(int amount) =>
        amount.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Hovering before the anchor: no ghost line yet, but the hover square is
    /// already tinted by the verdict on the cell under it.
    /// </summary>
    private void CheckHoverPreview()
    {
        _tool.HoverAt(_soilFrom);
        Check("hovering buildable ground previews a legal placement", _tool.PreviewLegal);
        Check("no ghost line before the anchor", _tool.GhostCellCount == 0);
        Check("the hover square reads legal on buildable ground",
            _tool.CursorColor.IsEqualApprox(BuildTool.CursorLegal));

        _tool.HoverAt(_rock);
        Check("hovering rock previews a refusal", !_tool.PreviewLegal);
        Check("the refusal names the terrain",
            _tool.Preview?.Refusal == PlacementRefusal.UnbuildableTerrain);
        Check("the hover square reads refused on rock",
            _tool.CursorColor.IsEqualApprox(BuildTool.CursorRefused));

        // A click that cannot legally anchor must not anchor at all.
        Check("a click on rock is refused", !_tool.ClickCell(_rock));
        Check("a refused click sets no anchor", _tool.Anchor == null);
        Check("rock stayed unbuilt", _world.GetTile(_rock) == TileType.Empty);
    }

    /// <summary>Anchor, ghost the whole line as legal, place it.</summary>
    private void CheckLegalPlacement()
    {
        int roadsBefore = _world.RoadCellCount;
        int lineLength = WorldGrid.LineCells(_soilFrom, _soilTo).Count;

        Check("clicking buildable ground anchors", _tool.ClickCell(_soilFrom));
        Check("the anchor is the clicked cell", _tool.Anchor == _soilFrom);

        _tool.HoverAt(_soilTo);
        Check("the ghost covers the whole pending line", _tool.GhostCellCount == lineLength);
        Check("a legal drag previews as legal", _tool.PreviewLegal);
        Check("every ghost cell of a legal drag is drawn legal",
            CountGhost(_tool, BuildTool.GhostLegal) == lineLength);

        Check("the second click places the line", _tool.ClickCell(_soilTo));
        Check("placing clears the anchor", _tool.Anchor == null);
        Check("placing keeps the tool armed for the next road", _tool.Active);
        Check("every cell of the line became road", AllRoad(_soilFrom, _soilTo));
        Check("exactly the line was placed", _world.RoadCellCount == roadsBefore + lineLength);
        Check("placing clears the ghost", _tool.GhostCellCount == 0);

        // Building over the road just built is legal — that is how a branch is
        // started from the existing network — and places nothing new.
        Check("a road may be started from an existing road cell",
            _tool.PlanFor(_soilFrom).Legal);
    }

    /// <summary>
    /// A drag whose far end runs into water: refused before the click, shown as
    /// refused in the ghost, and — the partial-legality rule — the legal cells
    /// of that drag are not built either.
    /// </summary>
    private void CheckIllegalTerrainIsRefused()
    {
        int roadsBefore = _world.RoadCellCount;
        List<Vector2I> line = WorldGrid.LineCells(_waterAnchor, _water);

        Check("the drag anchors on legal ground", _tool.ClickCell(_waterAnchor));
        _tool.HoverAt(_water);
        Check("a drag into water previews as refused", !_tool.PreviewLegal);
        Check("the refusal names the terrain",
            _tool.Preview?.Refusal == PlacementRefusal.UnbuildableTerrain);
        Check("the ghost still covers the whole drag", _tool.GhostCellCount == line.Count);
        Check("the ghost marks the water cell as the offender",
            CountGhost(_tool, BuildTool.GhostIllegalCell) == 1);
        Check("no cell of a refused drag is drawn legal",
            CountGhost(_tool, BuildTool.GhostLegal) == 0);

        Check("the click on a refused drag is rejected", !_tool.ClickCell(_water));
        Check("water stayed unbuilt", _world.GetTile(_water) == TileType.Empty);
        // Partial legality is all-or-nothing: one illegal cell refuses the
        // whole line, and none of its legal cells are built either.
        Check("one illegal cell refuses the whole line",
            _world.RoadCellCount == roadsBefore);
        Check("a refused placement keeps the anchor so the player can re-aim",
            _tool.Anchor == _waterAnchor);

        _tool.Cancel();
        Check("cancel drops that anchor", _tool.Anchor == null);
    }

    /// <summary>A cell holding a different placement refuses the build.</summary>
    private void CheckOccupiedCellIsRefused()
    {
        _world.SetTile(_field, TileType.Field);
        Check("the test field was placed", _world.GetTile(_field) == TileType.Field);

        Check("the drag anchors beside the field", _tool.ClickCell(_fieldAnchor));
        _tool.HoverAt(_field);
        Check("a drag onto a built cell previews as refused", !_tool.PreviewLegal);
        Check("the refusal names the occupied cell",
            _tool.Preview?.Refusal == PlacementRefusal.Occupied);
        Check("the click onto a built cell is rejected", !_tool.ClickCell(_field));
        Check("the field survived the refused road", _world.GetTile(_field) == TileType.Field);
        Check("the anchor cell was not built either",
            _world.GetTile(_fieldAnchor) == TileType.Empty);

        _tool.Cancel();
        _world.SetTile(_field, TileType.Empty);
    }

    /// <summary>Off the map is refused, and says so distinctly.</summary>
    private void CheckOffMapIsRefused()
    {
        Check("a cell past the map edge is refused",
            _tool.PlanFor(_offMap).Refusal == PlacementRefusal.OffMap);
        Check("a click past the map edge is rejected", !_tool.ClickCell(_offMap));
        Check("nothing was built past the map edge",
            _world.GetTile(_offMap) == TileType.Empty);
    }

    /// <summary>
    /// The must-touch-a-road rule. No tool opts into it yet — the structure
    /// tool will — so it is checked straight through <see cref="PlacementRules"/>
    /// to keep it live and proven until then.
    /// </summary>
    private void CheckRoadAccessRule()
    {
        var besideRoad = new Vector2I(0, 1); // the starting road runs along z = 0
        Check("the road-access rule accepts a cell beside a road",
            PlacementRules.Check(_world, [besideRoad], PlacementRule.TouchesRoad, TileType.Field)
                .Legal);

        Vector2I? isolated = FindCell(cell =>
            !_world.IsRoad(cell) && !PlacementRules.HasRoadAccess(_world, [cell]), 6, 40);
        Check("an isolated cell exists to test the road-access rule", isolated != null);
        if (isolated is { } far)
        {
            Check("the road-access rule refuses a cell away from the network",
                PlacementRules.Check(_world, [far], PlacementRule.TouchesRoad, TileType.Field)
                    .Refusal == PlacementRefusal.NoRoadAccess);
        }

        // A placement cannot satisfy its own road requirement: a footprint made
        // only of road cells still has to border a road outside itself.
        Check("the road-access rule ignores roads inside the footprint",
            !PlacementRules.HasRoadAccess(
                _world, WorldGrid.LineCells(new Vector2I(-16, 0), new Vector2I(16, 0))));
    }

    // --- field marking -----------------------------------------------------

    /// <summary>
    /// Build mode has exactly one armed tool: arming the field tool from the
    /// palette disarms the road tool, and a disarmed tool shows nothing.
    /// Neither tool knows about the other — the <see cref="BuildTool.ToolGroup"/>
    /// scene group carries it, so a future tool gets the same for free.
    /// </summary>
    private void CheckOneToolAtATime()
    {
        Check("the palette activates the field tool", _fieldTool.Active);
        Check("arming the field tool disarms the road tool", !_tool.Active);
        Check("the disarmed tool clears its ghost", _tool.GhostCellCount == 0);
        Check("the disarmed tool clears its preview", _tool.Preview == null);
        Check("the disarmed tool drops any anchor", _tool.Anchor == null);
        Check("the structure tool is not armed either", !_structureTool.Active);
        Check("the bulldozer is not armed either", !_bulldozeTool.Active);
        Check("every tool joined the build-tool group",
            GetTree().GetNodesInGroup(BuildTool.ToolGroup).Count == 4);
    }

    /// <summary>
    /// <see cref="WorldGrid.RectCells"/> on its own: a filled rectangle
    /// inclusive of both corners, in a fixed row-major order, and the *same*
    /// list whichever of the four corners the player started the drag from.
    /// The coordinates here are deliberately literal — this is pure geometry
    /// that never touches the map, so there is no terrain to search.
    /// </summary>
    private void CheckRectFootprint()
    {
        var min = new Vector2I(2, 3);
        var max = new Vector2I(6, 5);
        var topRight = new Vector2I(max.X, min.Y);
        var bottomLeft = new Vector2I(min.X, max.Y);
        List<Vector2I> cells = WorldGrid.RectCells(min, max);

        Check("a rectangle covers width x height cells", cells.Count == 5 * 3);
        Check("the rectangle holds no cell twice",
            new HashSet<Vector2I>(cells).Count == cells.Count);
        Check("the rectangle includes all four corners",
            cells.Contains(min) && cells.Contains(max)
            && cells.Contains(topRight) && cells.Contains(bottomLeft));
        Check("the rectangle starts at the minimum corner", cells[0] == min);
        Check("the rectangle ends at the maximum corner", cells[^1] == max);
        Check("the rectangle stays inside its corners", InsideRect(cells, min, max));

        Check("dragging the opposite way covers the same cells",
            SameCells(cells, WorldGrid.RectCells(max, min)));
        Check("dragging from the top-right corner covers the same cells",
            SameCells(cells, WorldGrid.RectCells(topRight, bottomLeft)));
        Check("dragging from the bottom-left corner covers the same cells",
            SameCells(cells, WorldGrid.RectCells(bottomLeft, topRight)));

        List<Vector2I> single = WorldGrid.RectCells(min, min);
        Check("a drag that never left its cell is that one cell",
            single.Count == 1 && single[0] == min);
    }

    /// <summary>
    /// One drag, one field. The rectangle is dragged from its *far* corner, to
    /// prove the committed region does not depend on which corner started it,
    /// and the preview is asserted before the second click: the whole rectangle
    /// is ghosted, and nothing is written until the commit.
    /// </summary>
    private void CheckFieldPlacement()
    {
        List<Vector2I> rect = WorldGrid.RectCells(_rectFrom, _rectTo);
        int fieldsBefore = _world.Fields.Count;
        int cellsBefore = _world.FieldCellCount;

        Check("clicking clear soil anchors the field drag", _fieldTool.ClickCell(_rectTo));
        Check("the field anchor is the clicked corner", _fieldTool.Anchor == _rectTo);

        _fieldTool.HoverAt(_rectFrom);
        Check("a legal rectangle previews as legal", _fieldTool.PreviewLegal);
        Check("the preview covers the whole rectangle before the commit",
            _fieldTool.Preview?.Count == rect.Count);
        Check("the ghost covers the whole pending rectangle",
            _fieldTool.GhostCellCount == rect.Count);
        Check("every ghost cell of a legal rectangle is drawn legal",
            CountGhost(_fieldTool, BuildTool.GhostLegal) == rect.Count);
        Check("the preview itself writes nothing",
            _world.Fields.Count == fieldsBefore && _world.FieldCellCount == cellsBefore);

        Check("the second click marks the field", _fieldTool.ClickCell(_rectFrom));
        Check("marking clears the anchor", _fieldTool.Anchor == null);
        Check("marking keeps the tool armed for the next field", _fieldTool.Active);
        Check("marking clears the ghost", _fieldTool.GhostCellCount == 0);
        Check("every cell of the rectangle became a field tile",
            AllTile(rect, TileType.Field));
        Check("exactly the rectangle was marked",
            _world.FieldCellCount == cellsBefore + rect.Count);

        // The point of the whole feature: the rectangle is one addressable
        // entity, not a pile of field tiles.
        Check("the drag created exactly one field", _world.Fields.Count == fieldsBefore + 1);
        _markedField = _world.GetField(_rectFrom);
        Check("the marked cells belong to a field", _markedField != null);
        if (_markedField is not { } field)
        {
            return;
        }
        Check("every cell of the rectangle addresses the same field",
            AllSameField(rect, field));
        Check("the field owns exactly the rectangle's cells", field.CellCount == rect.Count);
        Check("the field knows the cells it owns", field.Contains(_rectTo));
        Check("the field bounds are the dragged rectangle",
            field.Bounds == new Rect2I(_rectFrom, _rectTo - _rectFrom + Vector2I.One));
        Check("the field is the one the world just registered", _world.Fields[^1] == field);
        Check("the field is named", !string.IsNullOrEmpty(field.Name));
    }

    /// <summary>
    /// Two rectangles that share an edge stay two fields: touching regions are
    /// deliberately never merged, because each is separately named and worked.
    /// </summary>
    private void CheckFieldsStayDistinct()
    {
        List<Vector2I> rect = WorldGrid.RectCells(_nextRectFrom, _nextRectTo);
        int fieldsBefore = _world.Fields.Count;
        int cellsBefore = _world.FieldCellCount;

        Check("the second rectangle anchors beside the first",
            _fieldTool.ClickCell(_nextRectFrom));
        _fieldTool.HoverAt(_nextRectTo);
        Check("a rectangle bordering a field previews as legal", _fieldTool.PreviewLegal);
        Check("the second click marks it", _fieldTool.ClickCell(_nextRectTo));

        Check("the second rectangle is a second field",
            _world.Fields.Count == fieldsBefore + 1);
        Check("both rectangles are owned cell for cell",
            _world.FieldCellCount == cellsBefore + rect.Count);

        Field? neighbor = _world.GetField(_nextRectFrom);
        Check("the neighbouring rectangle addresses a field", neighbor != null);
        Check("touching rectangles are not merged",
            neighbor != null && neighbor != _markedField);
        Check("the two fields really do share an edge",
            _world.GetField(_nextRectFrom + Vector2I.Left) == _markedField);
        Check("the first field did not grow",
            _markedField?.CellCount == WorldGrid.RectCells(_rectFrom, _rectTo).Count);
        Check("the neighbour owns exactly its own rectangle",
            neighbor != null && neighbor.CellCount == rect.Count
            && AllSameField(rect, neighbor));
    }

    /// <summary>
    /// <see cref="PlacementRule.VacantCell"/>, the rule fields exist for: a
    /// rectangle may not be drawn over cells a field already owns, so a cell
    /// always has exactly one owner. It is stricter than
    /// <see cref="PlacementRule.NoOverlap"/>, which — the tool placing the same
    /// tile that is already there — would have allowed it.
    /// </summary>
    private void CheckFieldOverlapIsRefused()
    {
        int fieldsBefore = _world.Fields.Count;
        int cellsBefore = _world.FieldCellCount;

        Check("NoOverlap would have let a field cover a field",
            PlacementRules.Check(_world, [_rectFrom], PlacementRule.NoOverlap, TileType.Field)
                .Legal);
        Check("VacantCell refuses that same cell",
            PlacementRules.Check(_world, [_rectFrom], PlacementRule.VacantCell, TileType.Field)
                .Refusal == PlacementRefusal.Occupied);

        Check("a field cell cannot even be anchored on", !_fieldTool.ClickCell(_rectFrom));
        Check("the refused anchor click set no anchor", _fieldTool.Anchor == null);

        Check("the drag anchors on clear soil below the field",
            _fieldTool.ClickCell(_belowRect));
        _fieldTool.HoverAt(_rectFrom);
        Check("a rectangle over an existing field previews as refused",
            !_fieldTool.PreviewLegal);
        Check("the refusal names the occupied cell",
            _fieldTool.Preview?.Refusal == PlacementRefusal.Occupied);
        Check("the ghost marks the owned cells as the offenders",
            CountGhost(_fieldTool, BuildTool.GhostIllegalCell) == FieldRectHeight);
        Check("no cell of the overlapping rectangle is drawn legal",
            CountGhost(_fieldTool, BuildTool.GhostLegal) == 0);

        Check("the click over an existing field is rejected", !_fieldTool.ClickCell(_rectFrom));
        Check("the overlapping rectangle created no field",
            _world.Fields.Count == fieldsBefore);
        Check("the overlapping rectangle marked no cell",
            _world.FieldCellCount == cellsBefore);
        Check("the existing field still owns its cells",
            _world.GetField(_rectFrom) == _markedField);
        Check("the legal part of the overlapping rectangle was not marked either",
            _world.GetTile(_belowRect) == TileType.Empty);

        _fieldTool.Cancel();
        Check("cancel drops that anchor", _fieldTool.Anchor == null);
    }

    /// <summary>Vacancy is not only about fields: a road refuses a rectangle too.</summary>
    private void CheckFieldOverRoadIsRefused()
    {
        // Searched here rather than up front: the cell beside the road has to
        // still be clear now that this test's roads and fields are on the map.
        Vector2I? found = FindCell(cell => _world.IsRoad(cell) && IsFreeSoil(cell + Vector2I.Up));
        Check("found a road cell with clear soil beside it", found != null);
        if (found is not { } road)
        {
            return;
        }

        Vector2I beside = road + Vector2I.Up;
        int fieldsBefore = _world.Fields.Count;

        Check("the drag anchors beside the road", _fieldTool.ClickCell(beside));
        _fieldTool.HoverAt(road);
        Check("a rectangle covering a road previews as refused", !_fieldTool.PreviewLegal);
        Check("the road refusal names the occupied cell",
            _fieldTool.Preview?.Refusal == PlacementRefusal.Occupied);
        Check("the click over a road is rejected", !_fieldTool.ClickCell(road));
        Check("the road survived the refused rectangle", _world.IsRoad(road));
        Check("the cell beside it was not marked either",
            _world.GetTile(beside) == TileType.Empty);
        Check("the rectangle over a road created no field",
            _world.Fields.Count == fieldsBefore);

        _fieldTool.Cancel();
    }

    /// <summary>
    /// A rectangle straddling rock or water is refused whole, exactly like the
    /// road tool's drag into water: the base tool's partial-legality rule is
    /// all-or-nothing, so none of the rectangle's legal cells are marked.
    /// </summary>
    private void CheckFieldIntoRoughTerrainIsRefused()
    {
        bool found = TryFindRectIntoRoughTerrain(out Vector2I anchor, out Vector2I rough);
        Check("found a clear soil strip running into rock or water", found);
        if (!found)
        {
            return;
        }

        List<Vector2I> rect = WorldGrid.RectCells(anchor, rough);
        int fieldsBefore = _world.Fields.Count;
        int cellsBefore = _world.FieldCellCount;

        Check("the rough-ground drag anchors on soil", _fieldTool.ClickCell(anchor));
        _fieldTool.HoverAt(rough);
        Check("a rectangle straddling rock or water previews as refused",
            !_fieldTool.PreviewLegal);
        Check("the rough-ground refusal names the terrain",
            _fieldTool.Preview?.Refusal == PlacementRefusal.UnbuildableTerrain);
        Check("the ghost still covers the whole rectangle",
            _fieldTool.GhostCellCount == rect.Count);
        Check("the ghost marks the unbuildable cell as the offender",
            CountGhost(_fieldTool, BuildTool.GhostIllegalCell) == 1);
        Check("no cell of the straddling rectangle is drawn legal",
            CountGhost(_fieldTool, BuildTool.GhostLegal) == 0);

        Check("the click on a straddling rectangle is rejected", !_fieldTool.ClickCell(rough));
        Check("the rough cell stayed unmarked", _world.GetTile(rough) == TileType.Empty);
        Check("one unbuildable cell refuses the whole rectangle",
            _world.FieldCellCount == cellsBefore && _world.Fields.Count == fieldsBefore);
        Check("not even the soil cells of that rectangle were marked",
            _world.GetTile(anchor) == TileType.Empty);
        Check("a refused rectangle keeps the anchor so the player can re-aim",
            _fieldTool.Anchor == anchor);

        _fieldTool.Cancel();
    }

    /// <summary>
    /// A field <i>is</i> its cells: clearing one shrinks the field, and
    /// clearing the last one drops the field entirely. This is the path M2's
    /// bulldoze will take, which is why cell-to-field ownership is maintained
    /// inside <see cref="WorldGrid.SetTile"/> rather than beside it.
    /// </summary>
    private void CheckClearingShrinksAndDropsAField()
    {
        Field? doomed = _world.GetField(_nextRectFrom);
        Check("the neighbouring field is still there to be cleared", doomed != null);
        if (doomed is not { } field)
        {
            return;
        }

        int fieldsBefore = _world.Fields.Count;
        int cellsBefore = _world.FieldCellCount;
        int sizeBefore = field.CellCount;

        _world.SetTile(_nextRectFrom, TileType.Empty);
        Check("clearing a field cell shrinks its field", field.CellCount == sizeBefore - 1);
        Check("the cleared cell belongs to no field", _world.GetField(_nextRectFrom) == null);
        Check("the world lost exactly that one field cell",
            _world.FieldCellCount == cellsBefore - 1);
        Check("shrinking does not drop the field",
            WorldHasField(field) && _world.Fields.Count == fieldsBefore);
        Check("the field's other cells still address it",
            _world.GetField(_nextRectTo) == field);

        foreach (Vector2I cell in new List<Vector2I>(field.Cells))
        {
            _world.SetTile(cell, TileType.Empty);
        }
        Check("a field that lost its last cell is gone", !WorldHasField(field));
        Check("dropping it leaves the other field alone",
            _world.Fields.Count == fieldsBefore - 1);
        Check("the surviving field is untouched", _world.GetField(_rectFrom) == _markedField);
        Check("only the surviving field's cells are left",
            _world.FieldCellCount == WorldGrid.RectCells(_rectFrom, _rectTo).Count);
    }

    // --- structures --------------------------------------------------------

    /// <summary>
    /// The structure tool comes last, so the road network it has to touch is
    /// already on the map. Arming it also re-proves the tool-group rule with a
    /// third member.
    /// </summary>
    private void CheckStructureToolArmed()
    {
        Check("the palette activates the structure tool", _structureTool.Active);
        Check("arming the structure tool disarms the field tool", !_fieldTool.Active);
        Check("arming the structure tool leaves the road tool disarmed", !_tool.Active);
        // No anchor to check for — a single-click tool never takes one, and it
        // ghosts the cell under the cursor from the moment it is armed.
        Check("arming the structure tool leaves no anchor pending",
            _structureTool.Anchor == null);
        Check("no structure was registered by the road or field sections",
            _world.Structures.Count == 0 && _world.StructureCellCount == 0);
    }

    /// <summary>
    /// The accepted case, and what the issue is really about: one click on free
    /// soil that shares an edge with the road network puts a building down, and
    /// what lands is a <see cref="Structure"/> with an id — not a tile the game
    /// could only ever read back as an enum value.
    /// </summary>
    private void CheckStructurePlacement()
    {
        // Searched here rather than up front: the cell beside the road has to
        // still be clear now that this test's roads and fields are on the map.
        Vector2I? found = FindCell(cell => IsFreeSoil(cell) && TouchesRoad(cell));
        Check("found clear soil beside the road network", found != null);
        if (found is not { } beside)
        {
            return;
        }

        _structureCell = beside;
        int structuresBefore = _world.Structures.Count;

        _structureTool.HoverAt(beside);
        Check("a cell beside a road previews as legal", _structureTool.PreviewLegal);
        Check("a single-click tool ghosts before any anchor",
            _structureTool.GhostCellCount == 1 && _structureTool.Anchor == null);
        Check("a structure footprint is one cell", _structureTool.Preview?.Count == 1);
        Check("the ghost cell of a legal placement is drawn legal",
            CountGhost(_structureTool, BuildTool.GhostLegal) == 1);
        Check("the hover square reads legal beside a road",
            _structureTool.CursorColor.IsEqualApprox(BuildTool.CursorLegal));
        Check("the preview itself builds nothing",
            _world.Structures.Count == structuresBefore);

        Check("one click places the structure", _structureTool.ClickCell(beside));
        Check("placing needs no second click", _structureTool.Anchor == null);
        Check("placing keeps the tool armed for the next building", _structureTool.Active);
        Check("the cell became a structure tile",
            _world.GetTile(beside) == TileType.Structure);
        Check("exactly one structure was registered",
            _world.Structures.Count == structuresBefore + 1);
        Check("the building covers exactly one cell", _world.StructureCellCount == 1);
        Check("the cell it stands on now previews as occupied",
            _structureTool.Preview?.Refusal == PlacementRefusal.Occupied);

        // The point of the whole feature: a building is an entity with an
        // identity, reachable by cell *and* by id, not a bare tile value.
        _structure = _world.GetStructure(beside);
        Check("the placed cell addresses a structure", _structure != null);
        if (_structure is not { } structure)
        {
            return;
        }
        Check("the structure has an id", structure.Id > 0);
        Check("the structure is addressable by id, not just by tile",
            _world.GetStructure(structure.Id) == structure);
        Check("an id nothing was ever handed resolves to nothing",
            _world.GetStructure(structure.Id + 1) == null);
        Check("the structure knows the cell it stands on",
            structure.CellCount == 1 && structure.Cells[0] == beside
            && structure.Contains(beside) && structure.Origin == beside);
        Check("the structure footprint is a 1x1 rectangle",
            structure.Bounds == new Rect2I(beside, Vector2I.One));
        Check("the structure is the one the world just registered",
            _world.Structures[^1] == structure);
        Check("the structure is named", !string.IsNullOrEmpty(structure.Name));
        Check("the hover readout names the building on the cell",
            _inspector.Describe(beside).Contains($"tile: structure ({structure.Name})"));
    }

    /// <summary>
    /// The rule that makes the road network load-bearing, taken through all
    /// three of its verdicts on <b>one</b> cell: refused with no road near it,
    /// still refused when the nearest road only touches its corner (access is
    /// 4-neighbour, not 8), and accepted the moment a road shares an edge with
    /// it. The roads are laid rather than searched for, so nothing but the road
    /// changes between the three answers.
    /// </summary>
    private void CheckStructureNeedsRoadAccess()
    {
        Vector2I? found = FindCell(cell =>
            IsFreeSoil(cell) && !TouchesRoad(cell)
            && IsFreeSoil(cell + Vector2I.Right) && IsFreeSoil(cell + Vector2I.One));
        Check("found clear soil away from every road", found != null);
        if (found is not { } isolated)
        {
            return;
        }

        int structuresBefore = _world.Structures.Count;
        int cellsBefore = _world.StructureCellCount;

        _structureTool.HoverAt(isolated);
        Check("a cell with no road beside it previews as refused",
            !_structureTool.PreviewLegal);
        Check("the refusal names the missing road",
            _structureTool.Preview?.Refusal == PlacementRefusal.NoRoadAccess);
        Check("the ghost still covers the cell", _structureTool.GhostCellCount == 1);
        Check("no cell of a refused placement is drawn legal",
            CountGhost(_structureTool, BuildTool.GhostLegal) == 0);
        // Road access is a footprint rule, so no single cell is the offender:
        // the ghost dims the whole placement instead of pointing at a cell.
        Check("the whole placement reads refused, with no cell blamed",
            CountGhost(_structureTool, BuildTool.GhostRefused) == 1
            && CountGhost(_structureTool, BuildTool.GhostIllegalCell) == 0);
        Check("the hover square reads refused away from the road",
            _structureTool.CursorColor.IsEqualApprox(BuildTool.CursorRefused));

        Check("the click away from the road is rejected",
            !_structureTool.ClickCell(isolated));
        Check("nothing was built away from the road",
            _world.GetTile(isolated) == TileType.Empty);
        Check("the refused click registered no structure",
            _world.Structures.Count == structuresBefore
            && _world.StructureCellCount == cellsBefore);
        Check("the refused cell addresses no structure",
            _world.GetStructure(isolated) == null);

        // A road on the diagonal touches the cell's corner, not its edge.
        _world.SetTile(isolated + Vector2I.One, TileType.Road);
        _structureTool.HoverAt(isolated);
        Check("a road touching only the corner is not road access",
            _structureTool.Preview?.Refusal == PlacementRefusal.NoRoadAccess);
        Check("the click beside a diagonal-only road is rejected",
            !_structureTool.ClickCell(isolated));
        Check("nothing was built beside the diagonal road",
            _world.GetTile(isolated) == TileType.Empty);

        // One road sharing an edge, and the very same cell becomes legal.
        _world.SetTile(isolated + Vector2I.Right, TileType.Road);
        _structureTool.HoverAt(isolated);
        Check("a road sharing an edge grants access", _structureTool.PreviewLegal);
        Check("the ghost turns legal with the road",
            CountGhost(_structureTool, BuildTool.GhostLegal) == 1);
        Check("the click is accepted once a road touches the cell",
            _structureTool.ClickCell(isolated));

        _secondStructure = _world.GetStructure(isolated);
        Check("the second building is registered",
            _secondStructure != null && _world.Structures.Count == structuresBefore + 1);
        if (_secondStructure is not { } second || _structure is not { } first)
        {
            return;
        }
        Check("ids are handed out in creation order", second.Id > first.Id);
        Check("each cell addresses its own building",
            second != first && _world.GetStructure(_structureCell) == first);
        Check("each id resolves to its own building",
            _world.GetStructure(second.Id) == second
            && _world.GetStructure(first.Id) == first);
    }

    /// <summary>
    /// Rock and water refuse a building the way they refuse a road. To prove it
    /// is the <i>terrain</i> talking and not the road rule, the check lays a
    /// road beside the rough cell first — so road access is satisfied and
    /// cannot be the reason — and takes it up again afterwards.
    /// </summary>
    private void CheckStructureOnRoughGroundIsRefused()
    {
        bool found = TryFindRoughCellWithSoilBeside(out Vector2I rough, out Vector2I beside);
        Check("found rock or water with clear soil beside it", found);
        if (!found)
        {
            return;
        }

        int structuresBefore = _world.Structures.Count;
        _world.SetTile(beside, TileType.Road);
        Check("the rough cell now has road access",
            PlacementRules.HasRoadAccess(_world, [rough]));

        _structureTool.HoverAt(rough);
        Check("rock or water is refused even with a road beside it",
            _structureTool.Preview?.Refusal == PlacementRefusal.UnbuildableTerrain);
        Check("the ghost marks the rough cell as the offender",
            CountGhost(_structureTool, BuildTool.GhostIllegalCell) == 1);
        Check("the click on rough ground is rejected", !_structureTool.ClickCell(rough));
        Check("the rough cell stayed unbuilt", _world.GetTile(rough) == TileType.Empty);
        Check("no structure was registered on rough ground",
            _world.Structures.Count == structuresBefore);

        _world.SetTile(beside, TileType.Empty);
    }

    /// <summary>
    /// <see cref="PlacementRule.VacantCell"/> again, from the building side: a
    /// structure may not be stacked on a road, a field or another building —
    /// and all three of those cells have road access, so nothing but occupancy
    /// is refusing them.
    /// </summary>
    private void CheckStructureOnOccupiedGroundIsRefused()
    {
        int structuresBefore = _world.Structures.Count;

        Check("a cell that already holds a building is refused",
            _structureTool.PlanFor(_structureCell).Refusal == PlacementRefusal.Occupied);
        Check("the click on a building is rejected",
            !_structureTool.ClickCell(_structureCell));
        Check("the building that was there survived",
            _world.GetStructure(_structureCell) == _structure);
        Check("the refused click registered no second building",
            _world.Structures.Count == structuresBefore);

        Vector2I? road = FindCell(cell =>
            _world.IsRoad(cell) && _world.IsRoad(cell + Vector2I.Right));
        Check("found a road cell with another road beside it", road != null);
        if (road is { } onRoad)
        {
            Check("a road cell is refused as occupied, not accepted for touching one",
                _structureTool.PlanFor(onRoad).Refusal == PlacementRefusal.Occupied);
            Check("the click on a road is rejected", !_structureTool.ClickCell(onRoad));
            Check("the road survived the refused building", _world.IsRoad(onRoad));
        }

        Check("a field cell is refused as occupied",
            _structureTool.PlanFor(_rectFrom).Refusal == PlacementRefusal.Occupied);
        Check("the field survived the refused building",
            _world.GetField(_rectFrom) == _markedField);
    }

    /// <summary>
    /// Clearing a structure cell takes the whole building with it — tile, cell
    /// lookup, registry entry and id. This is the path M2's bulldoze will take,
    /// and it is where a building parts company with a <see cref="Field"/>: a
    /// field shrinks cell by cell, a building is demolished whole, because half
    /// a mill is not a mill.
    /// </summary>
    private void CheckClearingDemolishesAStructure()
    {
        if (_structure is not { } structure)
        {
            return;
        }

        int structuresBefore = _world.Structures.Count;
        int cellsBefore = _world.StructureCellCount;
        int id = structure.Id;

        _world.SetTile(_structureCell, TileType.Empty);
        Check("clearing a structure cell empties the tile",
            _world.GetTile(_structureCell) == TileType.Empty);
        Check("the cleared cell addresses no building",
            _world.GetStructure(_structureCell) == null);
        Check("the building is gone from the registry",
            _world.Structures.Count == structuresBefore - 1);
        Check("its id resolves to nothing once it is demolished",
            _world.GetStructure(id) == null);
        Check("the world lost exactly that one structure cell",
            _world.StructureCellCount == cellsBefore - 1);
        Check("the terrain under a demolished building is untouched",
            _world.IsSoil(_structureCell));
        Check("the other building is untouched",
            _secondStructure != null
            && _world.GetStructure(_secondStructure.Origin) == _secondStructure);
        Check("the other building's id still resolves",
            _secondStructure != null
            && _world.GetStructure(_secondStructure.Id) == _secondStructure);
    }

    // --- bulldoze ----------------------------------------------------------

    /// <summary>
    /// The bulldozer comes last, because it needs one of everything on the map
    /// before it can take anything off again. Arming it re-proves the
    /// tool-group rule with a fourth member.
    /// </summary>
    private void CheckBulldozeToolArmed()
    {
        Check("the palette activates the bulldozer", _bulldozeTool.Active);
        Check("arming the bulldozer disarms the structure tool", !_structureTool.Active);
        Check("arming the bulldozer leaves the road and field tools disarmed",
            !_tool.Active && !_fieldTool.Active);
        Check("the bulldozer takes no anchor from being armed",
            _bulldozeTool.Anchor == null);
        Check("nothing has gone through the refund seam yet",
            _bulldozeTool.Removals.Count == 0 && _bulldozeTool.RefundTotal == 0);
    }

    /// <summary>
    /// A road, laid with the road tool and taken away with the bulldozer. The
    /// whole drag previews legal, every cell of it is drawn legal, and the
    /// second click leaves empty cells behind — one refund-seam entry per cell,
    /// because a road is nothing but its cells.
    /// </summary>
    private void CheckBulldozeClearsARoad()
    {
        Vector2I? run = FindCell(cell => IsFreeSoilLine(cell, cell + new Vector2I(2, 0)));
        Check("found clear soil to lay a road for the bulldozer", run != null);
        if (run is not { } from)
        {
            return;
        }

        Vector2I to = from + new Vector2I(2, 0);
        List<Vector2I> cells = WorldGrid.RectCells(from, to);
        RememberTerrain(cells);

        Check("the road for the bulldozer goes down", Drag(_tool, from, to));
        Check("it is road before the bulldozer touches it", AllRoad(from, to));

        _bulldozeTool.SetActive(true);
        int roadsBefore = _world.RoadCellCount;
        int removalsBefore = _bulldozeTool.Removals.Count;

        _bulldozeTool.HoverAt(from);
        Check("a cell holding a road previews as clearable", _bulldozeTool.PreviewLegal);
        Check("the hover square reads legal over something removable",
            _bulldozeTool.CursorColor.IsEqualApprox(BuildTool.CursorLegal));

        Check("clicking a built cell anchors the bulldoze drag",
            _bulldozeTool.ClickCell(from));
        _bulldozeTool.HoverAt(to);
        Check("the ghost covers the whole pending rectangle",
            _bulldozeTool.GhostCellCount == cells.Count);
        Check("every cell of an all-built drag is drawn legal",
            CountGhost(_bulldozeTool, BuildTool.GhostLegal) == cells.Count);
        Check("the preview itself removes nothing", _world.RoadCellCount == roadsBefore);

        Check("the second click clears the road", _bulldozeTool.ClickCell(to));
        Check("every cell of the road is empty again", AllTile(cells, TileType.Empty));
        Check("exactly those road cells were taken",
            _world.RoadCellCount == roadsBefore - cells.Count);
        Check("clearing keeps the bulldozer armed for the next drag", _bulldozeTool.Active);
        Check("clearing clears the ghost", _bulldozeTool.GhostCellCount == 0);
        Check("the cleared cell now previews as nothing to clear",
            _bulldozeTool.PlanFor(from).Refusal == PlacementRefusal.NothingToClear);
        Check("terrain and fertility under the bulldozed road are unchanged",
            TerrainUnchanged(cells) && FertilityUnchanged(cells));
        Check("each road cell is its own removal",
            _bulldozeTool.Removals.Count == removalsBefore + cells.Count);
        Check("the refund seam was told it was road, one cell of it",
            LastRemoval()?.Tile == TileType.Road && LastRemoval()?.CellCount == 1);
    }

    /// <summary>
    /// A field, marked with the field tool and bulldozed whole: the tiles go,
    /// and the entity is dropped with its last cell. Each cell is its own
    /// removal and names the field it was taken out of — what a per-cell refund
    /// would need to price.
    /// </summary>
    private void CheckBulldozeClearsAField()
    {
        Vector2I? block = FindCell(cell => IsFreeSoilRect(cell, cell + Vector2I.One));
        Check("found a clear soil block to mark a field for the bulldozer",
            block != null);
        if (block is not { } from)
        {
            return;
        }

        Vector2I to = from + Vector2I.One;
        List<Vector2I> cells = WorldGrid.RectCells(from, to);
        RememberTerrain(cells);

        Check("the field for the bulldozer is marked", Drag(_fieldTool, from, to));
        Field? marked = _world.GetField(from);
        Check("it is a field before the bulldozer touches it",
            marked != null && AllTile(cells, TileType.Field));
        if (marked is not { } field)
        {
            return;
        }

        _bulldozeTool.SetActive(true);
        int fieldsBefore = _world.Fields.Count;
        int fieldCellsBefore = _world.FieldCellCount;
        int removalsBefore = _bulldozeTool.Removals.Count;

        Check("the bulldoze drag anchors on a field cell", _bulldozeTool.ClickCell(from));
        _bulldozeTool.HoverAt(to);
        Check("the ghost covers the whole field rectangle",
            _bulldozeTool.GhostCellCount == cells.Count);
        Check("the second click clears the field", _bulldozeTool.ClickCell(to));

        Check("every cell of the field is empty again", AllTile(cells, TileType.Empty));
        Check("the field is gone from the registry",
            !WorldHasField(field) && _world.Fields.Count == fieldsBefore - 1);
        Check("the world lost exactly that field's cells",
            _world.FieldCellCount == fieldCellsBefore - cells.Count);
        Check("no cell of it addresses a field any more", _world.GetField(from) == null);
        Check("terrain and fertility under the bulldozed field are unchanged",
            TerrainUnchanged(cells) && FertilityUnchanged(cells));
        Check("each field cell is its own removal",
            _bulldozeTool.Removals.Count == removalsBefore + cells.Count);
        Check("the refund seam was told which field it took from",
            LastRemoval()?.Tile == TileType.Field && LastRemoval()?.Field == field);
    }

    /// <summary>
    /// A building, placed with the structure tool and bulldozed away: tile,
    /// cell lookup, registry entry and id all gone, and one removal — a
    /// building is priced as a building, not per cell.
    /// </summary>
    private void CheckBulldozeClearsAStructure()
    {
        Vector2I? found = FindCell(cell => IsFreeSoil(cell) && TouchesRoad(cell));
        Check("found clear soil beside a road for the bulldozer's building",
            found != null);
        if (found is not { } cell)
        {
            return;
        }

        RememberTerrain([cell]);
        _structureTool.SetActive(true);
        Check("the building for the bulldozer goes down", _structureTool.ClickCell(cell));
        Structure? placed = _world.GetStructure(cell);
        Check("it is a building before the bulldozer touches it", placed != null);
        if (placed is not { } structure)
        {
            return;
        }

        int id = structure.Id;
        _bulldozeTool.SetActive(true);
        int structuresBefore = _world.Structures.Count;
        int removalsBefore = _bulldozeTool.Removals.Count;

        Check("the bulldoze drag anchors on the building", _bulldozeTool.ClickCell(cell));
        Check("a drag that never left its cell clears just that cell",
            _bulldozeTool.ClickCell(cell));
        Check("the building's cell is empty again", _world.GetTile(cell) == TileType.Empty);
        Check("the cell addresses no building any more", _world.GetStructure(cell) == null);
        Check("the building is gone from the registry",
            _world.Structures.Count == structuresBefore - 1);
        Check("its id resolves to nothing once the bulldozer has been",
            _world.GetStructure(id) == null);
        Check("terrain and fertility under the bulldozed building are unchanged",
            TerrainUnchanged([cell]) && FertilityUnchanged([cell]));
        Check("a building is one removal", _bulldozeTool.Removals.Count == removalsBefore + 1);
        Check("the refund seam was told which building it took",
            LastRemoval()?.Tile == TileType.Structure && LastRemoval()?.Structure == structure);
    }

    /// <summary>
    /// The headline, over every cell the bulldozer has worked on so far: the
    /// terrain layer is read *before* anything is placed on it and compared
    /// after everything has been taken off again — type and fertility both.
    /// Clearing a placement uncovers the ground it was hiding; it never edits
    /// it.
    /// </summary>
    private void CheckTerrainSurvivedTheBulldozer()
    {
        Check("the bulldoze checks remembered ground to compare against",
            _rememberedCells.Count >= 8);
        Check("all of it was remembered as soil, so the comparison is a real one",
            RememberedCellsWithTerrain(TerrainType.Soil) == _rememberedCells.Count);
        Check("some of it carries fertility, so that comparison means something too",
            RememberedFertileCells() > 0);
        Check("terrain type under everything bulldozed so far is unchanged",
            TerrainUnchanged(_rememberedCells));
        Check("fertility under everything bulldozed so far is unchanged",
            FertilityUnchanged(_rememberedCells));
        Check("and none of what was built on it is left",
            AllTile(_rememberedCells, TileType.Empty));
    }

    /// <summary>
    /// A field shrinks under the bulldozer and only disappears with its last
    /// cell — the asymmetry with a building, from the player's side: take a
    /// bite out of a field and what is left is still that field, under the same
    /// name. The drag that finishes it off runs back across the bitten cell, so
    /// it is also a mixed drag: already-empty ground in the middle of a
    /// removal changes nothing.
    /// </summary>
    private void CheckBulldozeShrinksAndDropsAField()
    {
        Vector2I? strip = FindCell(cell => IsFreeSoilRect(cell, cell + new Vector2I(2, 0)));
        Check("found a clear soil strip to mark a field to bite into", strip != null);
        if (strip is not { } from)
        {
            return;
        }

        Vector2I to = from + new Vector2I(2, 0);
        Vector2I middle = from + Vector2I.Right;
        List<Vector2I> cells = WorldGrid.RectCells(from, to);
        RememberTerrain(cells);

        Check("the field to bite into is marked", Drag(_fieldTool, from, to));
        Field? marked = _world.GetField(from);
        Check("the strip is one field", marked != null && marked.CellCount == cells.Count);
        if (marked is not { } field)
        {
            return;
        }

        _bulldozeTool.SetActive(true);
        int fieldsBefore = _world.Fields.Count;
        int removalsBefore = _bulldozeTool.Removals.Count;

        Check("a one-cell bulldoze anchors in the middle of the field",
            _bulldozeTool.ClickCell(middle));
        Check("the second click takes just that cell", _bulldozeTool.ClickCell(middle));
        Check("the bitten cell is empty", _world.GetTile(middle) == TileType.Empty);
        Check("the field shrank instead of disappearing",
            field.CellCount == cells.Count - 1 && WorldHasField(field));
        Check("the rest of the field still addresses the same entity",
            _world.GetField(from) == field && _world.GetField(to) == field);
        Check("the bitten cell belongs to no field", _world.GetField(middle) == null);
        Check("the world still lists it", _world.Fields.Count == fieldsBefore);
        Check("one cell taken is one removal, and it names its field",
            _bulldozeTool.Removals.Count == removalsBefore + 1
            && LastRemoval()?.Field == field && LastRemoval()?.CellCount == 1);

        // The finishing drag runs the length of the strip, straight across the
        // cell that is already gone.
        Check("the finishing drag anchors on what is left", _bulldozeTool.ClickCell(from));
        _bulldozeTool.HoverAt(to);
        Check("the drag across the bitten cell is still legal", _bulldozeTool.PreviewLegal);
        Check("the ghost skips the cell that is already empty",
            CountGhost(_bulldozeTool, BuildTool.GhostLegal) == cells.Count - 1
            && CountGhost(_bulldozeTool, BuildTool.GhostRefused) == 1);
        Check("the second click clears the rest", _bulldozeTool.ClickCell(to));
        Check("a field that lost its last cell is gone",
            !WorldHasField(field) && _world.Fields.Count == fieldsBefore - 1);
        Check("the whole strip is empty", AllTile(cells, TileType.Empty));
        Check("the already-empty cell was not removed twice",
            _bulldozeTool.Removals.Count == removalsBefore + cells.Count);
        Check("terrain and fertility under the whole strip are unchanged",
            TerrainUnchanged(cells) && FertilityUnchanged(cells));
    }

    /// <summary>
    /// Where a building parts company with a field: clipping <b>one edge</b> of
    /// a 2x2 takes the whole building — tile, footprint, registry entry and id
    /// — because half a mill is not a mill. The building is placed through
    /// <see cref="WorldGrid.PlaceStructure"/>, the dev entry point (the way
    /// <see cref="WorldGrid.MarkField"/> and <see cref="WorldGrid.BuildRoadLine"/>
    /// are), because the tool still only offers 1x1 footprints and a 1x1 cannot
    /// be clipped.
    ///
    /// The drag deliberately covers two of the four cells: the second one is
    /// already gone by the time the removal reaches it, so the building goes
    /// through the refund seam exactly once.
    /// </summary>
    private void CheckBulldozeTakesAWholeStructure()
    {
        Vector2I? block = FindCell(cell => IsFreeSoilRect(cell, cell + Vector2I.One));
        Check("found a clear soil block for a 2x2 building", block != null);
        if (block is not { } origin)
        {
            return;
        }

        List<Vector2I> footprint = WorldGrid.RectCells(origin, origin + Vector2I.One);
        RememberTerrain(footprint);
        Structure? placed = _world.PlaceStructure(footprint);
        Check("the 2x2 building is registered",
            placed != null && _world.GetStructure(origin) == placed);
        if (placed is not { } building)
        {
            return;
        }

        int id = building.Id;
        Check("it covers four cells",
            building.CellCount == 4 && AllTile(footprint, TileType.Structure));

        _bulldozeTool.SetActive(true);
        int structuresBefore = _world.Structures.Count;
        int structureCellsBefore = _world.StructureCellCount;
        int removalsBefore = _bulldozeTool.Removals.Count;

        Vector2I clipTo = origin + Vector2I.Down;
        Check("the clipping drag anchors on a corner of the building",
            _bulldozeTool.ClickCell(origin));
        _bulldozeTool.HoverAt(clipTo);
        Check("the clipping drag covers two of its four cells",
            _bulldozeTool.GhostCellCount == 2);
        Check("both clipped cells are drawn legal",
            CountGhost(_bulldozeTool, BuildTool.GhostLegal) == 2);
        Check("the second click clips the building", _bulldozeTool.ClickCell(clipTo));

        Check("clipping a building takes its whole footprint",
            AllTile(footprint, TileType.Empty));
        Check("no cell of it addresses a building any more",
            _world.GetStructure(origin) == null
            && _world.GetStructure(origin + Vector2I.One) == null);
        Check("the building is gone from the registry",
            _world.Structures.Count == structuresBefore - 1);
        Check("its id resolves to nothing", _world.GetStructure(id) == null);
        Check("the world lost all four of its cells",
            _world.StructureCellCount == structureCellsBefore - footprint.Count);
        Check("a clipped building is one removal, not one per cell clipped",
            _bulldozeTool.Removals.Count == removalsBefore + 1);
        Check("the removal carries the whole footprint that went, not the drag",
            LastRemoval()?.CellCount == footprint.Count
            && LastRemoval()?.Structure == building);
        Check("terrain and fertility under the whole footprint are unchanged",
            TerrainUnchanged(footprint) && FertilityUnchanged(footprint));
    }

    /// <summary>
    /// The deliberate difference from every building tool: a bulldoze drag over
    /// a region that is only <i>partly</i> built is not refused. It clears what
    /// is there and skips what is not — dragging across a farmyard crosses
    /// empty ground as a matter of course — and the ghost says so before the
    /// click, drawing only the cells it will actually take in the legal colour.
    /// </summary>
    private void CheckBulldozeSkipsEmptyGround()
    {
        Vector2I? block = FindCell(cell => IsFreeSoilRect(cell, cell + new Vector2I(2, 1)));
        Check("found a clear soil block for the mixed drag", block != null);
        if (block is not { } origin)
        {
            return;
        }

        Vector2I roadEnd = origin + new Vector2I(2, 0);
        Vector2I far = origin + new Vector2I(2, 1);
        List<Vector2I> road = WorldGrid.RectCells(origin, roadEnd);
        List<Vector2I> bare = WorldGrid.RectCells(origin + Vector2I.Down, far);
        _world.BuildRoadLine(origin, roadEnd);
        Check("the region is half built and half bare",
            AllTile(road, TileType.Road) && AllTile(bare, TileType.Empty));

        _bulldozeTool.SetActive(true);
        int roadsBefore = _world.RoadCellCount;
        int removalsBefore = _bulldozeTool.Removals.Count;

        Check("the mixed drag anchors on the built half", _bulldozeTool.ClickCell(origin));
        _bulldozeTool.HoverAt(far);
        Check("a partly empty drag is not refused", _bulldozeTool.PreviewLegal);
        Check("the ghost covers the whole rectangle",
            _bulldozeTool.GhostCellCount == road.Count + bare.Count);
        Check("the ghost draws only the cells it will take as legal",
            CountGhost(_bulldozeTool, BuildTool.GhostLegal) == road.Count);
        Check("the ghost dims the cells it will skip",
            CountGhost(_bulldozeTool, BuildTool.GhostRefused) == bare.Count);
        Check("no cell of a removal drag is blamed as an offender",
            CountGhost(_bulldozeTool, BuildTool.GhostIllegalCell) == 0);
        Check("the skipped cells say why they are skipped",
            _bulldozeTool.Preview?.CellRefusals[road.Count] == PlacementRefusal.NothingToClear);

        Check("the second click clears the mixed drag", _bulldozeTool.ClickCell(far));
        Check("the built half is gone", AllTile(road, TileType.Empty));
        Check("exactly the built cells were taken",
            _world.RoadCellCount == roadsBefore - road.Count);
        Check("the bare half was skipped, not turned into anything",
            AllTile(bare, TileType.Empty));
        Check("only the cells that held something reached the refund seam",
            _bulldozeTool.Removals.Count == removalsBefore + road.Count);
    }

    /// <summary>
    /// What a bulldozer <i>can</i> be refused for, which is two things: a cell
    /// with nothing on it, and the map edge. Note what is not a reason — rock
    /// refuses a build, but bare rock is refused here for holding nothing, not
    /// for being rock. The building rules describe what may go down; none of
    /// them describes what may come off, which is why this tool has its own
    /// pair (<see cref="PlacementRule.InBounds"/>,
    /// <see cref="PlacementRule.OccupiedCell"/>).
    /// </summary>
    private void CheckNothingToClearIsRefused()
    {
        Vector2I? free = FindCell(IsFreeSoil);
        Check("found empty soil for the bulldozer to refuse", free != null);
        if (free is not { } empty)
        {
            return;
        }

        int removalsBefore = _bulldozeTool.Removals.Count;
        _bulldozeTool.SetActive(true);
        _bulldozeTool.HoverAt(empty);
        Check("empty ground previews as refused", !_bulldozeTool.PreviewLegal);
        Check("the refusal says there is nothing to clear",
            _bulldozeTool.Preview?.Refusal == PlacementRefusal.NothingToClear);
        Check("the wording is the bulldozer's own",
            PlacementRules.Explain(PlacementRefusal.NothingToClear) == "nothing here to clear");
        Check("the hover square reads refused over empty ground",
            _bulldozeTool.CursorColor.IsEqualApprox(BuildTool.CursorRefused));
        Check("a click on empty ground is rejected", !_bulldozeTool.ClickCell(empty));
        Check("a refused bulldoze click does not even anchor",
            _bulldozeTool.Anchor == null);
        Check("empty ground stayed empty", _world.GetTile(empty) == TileType.Empty);

        Check("bare rock is refused for holding nothing, not for being rock",
            _bulldozeTool.PlanFor(_rock).Refusal == PlacementRefusal.NothingToClear);
        Check("the click on bare rock is rejected", !_bulldozeTool.ClickCell(_rock));
        Check("the rock is still rock", _world.GetTerrain(_rock) == TerrainType.Rock);

        Check("a cell past the map edge is refused",
            _bulldozeTool.PlanFor(_offMap).Refusal == PlacementRefusal.OffMap);
        Check("the click past the map edge is rejected", !_bulldozeTool.ClickCell(_offMap));

        Check("no refused click reached the refund seam",
            _bulldozeTool.Removals.Count == removalsBefore);
    }

    /// <summary>
    /// The seam itself: every removal went through it exactly once, each one
    /// describing what came off, how much ground it held and where — the
    /// information a real refund rule needs. What it pays is still zero, and
    /// deliberately so: pricing is M7's, and <c>BulldozeTool.RefundFor</c> is the
    /// one place it changes.
    /// </summary>
    private void CheckRefundSeam()
    {
        IReadOnlyList<Removal> removals = _bulldozeTool.Removals;
        Check("the bulldozer put its removals through the seam", removals.Count > 0);

        bool described = true;
        int roads = 0, fields = 0, structures = 0;
        foreach (Removal removed in removals)
        {
            described &= removed.Tile != TileType.Empty
                && removed.CellCount > 0
                && Covers(removed.Cells, removed.Cell)
                && !string.IsNullOrEmpty(removed.Name);
            switch (removed.Tile)
            {
                case TileType.Road:
                    roads++;
                    described &= removed.Field == null && removed.Structure == null;
                    break;
                case TileType.Field:
                    fields++;
                    described &= removed.Field != null && removed.Structure == null;
                    break;
                case TileType.Structure:
                    structures++;
                    described &= removed.Structure != null && removed.Field == null;
                    break;
            }
        }

        Check("each removal names what came off, where, and how much ground", described);
        Check("the seam saw all three kinds of thing the player can build",
            roads > 0 && fields > 0 && structures > 0);
        Check("nothing is refunded yet - the seam is an M7 stub",
            _bulldozeTool.RefundTotal == 0);
    }

    // --- money -------------------------------------------------------------

    /// <summary>
    /// The plain case: a drag is priced as a total by the <i>plan</i>, the
    /// preview costs nothing however long it is held, and the commit takes
    /// exactly what the plan said — no more, and not twice.
    /// </summary>
    private void CheckCommitChargesExactlyTheCost()
    {
        Vector2I? run = FindCell(cell => IsFreeSoilLine(cell, cell + new Vector2I(3, 0)));
        Check("found clear soil to buy a road on", run != null);
        if (run is not { } from)
        {
            return;
        }

        Vector2I to = from + new Vector2I(3, 0);
        int cells = WorldGrid.LineCells(from, to).Count;
        int price = _tool.CostPerCell * cells;

        _economy.SetBalance(WorkingBalance);
        _tool.SetActive(true);
        Check("the drag anchors on ground the player can pay for", _tool.ClickCell(from));

        _tool.HoverAt(to);
        Check("the plan prices the whole drag, not one cell of it",
            cells > 1 && _tool.Preview?.Cost == price);
        Check("a drag the player can pay for previews as legal", _tool.PreviewLegal);
        Check("every cell of an affordable drag is drawn legal",
            CountGhost(_tool, BuildTool.GhostLegal) == cells);
        Check("previewing a placement charges nothing at all",
            _economy.Balance == WorkingBalance);

        Check("the second click places it", _tool.ClickCell(to));
        Check("committing deducts exactly the plan's cost",
            _economy.Balance == WorkingBalance - price);
        Check("what it paid for is on the map", AllRoad(from, to));
        Check("the readout followed the balance down",
            _moneyReadout.Text == Economy.Describe(WorkingBalance - price)
            && _economy.Text == _moneyReadout.Text);

        // A refused click is free, whatever it was refused for.
        int spent = _economy.Balance;
        Check("a click on rock is refused as it always was", !_tool.ClickCell(_rock));
        Check("and being refused costs nothing", _economy.Balance == spent);
    }

    /// <summary>
    /// The point of the whole issue: a placement the player cannot afford is
    /// refused on the same footing as an illegal one — <b>in the ghost, before
    /// the click</b>. It is checked on the structure tool because that one
    /// places on a single click, so the ghost is the only warning there is.
    ///
    /// Money is a property of the whole placement, so no cell is the offender:
    /// the ghost dims all of it and blames none of it, exactly the way
    /// <see cref="PlacementRefusal.NoRoadAccess"/> does. One coin more and the
    /// very same cell builds — nothing about the ground changed.
    /// </summary>
    private void CheckUnaffordablePlacementIsRefused()
    {
        Vector2I? found = FindCell(cell => IsFreeSoil(cell) && TouchesRoad(cell));
        Check("found clear soil beside a road to price a building on", found != null);
        if (found is not { } cell)
        {
            return;
        }

        int price = _structureTool.CostPerCell;
        int structuresBefore = _world.Structures.Count;

        _structureTool.SetActive(true);
        _economy.SetBalance(price - 1);
        _structureTool.HoverAt(cell);
        Check("a building one coin short of its price previews as refused",
            !_structureTool.PreviewLegal);
        Check("the refusal names the money",
            _structureTool.Preview?.Refusal == PlacementRefusal.CannotAfford);
        Check("the wording is the player's",
            PlacementRules.Explain(PlacementRefusal.CannotAfford) == "not enough money");
        Check("the plan still says what it would have cost",
            _structureTool.Preview?.Cost == price);
        Check("the ghost still covers the placement", _structureTool.GhostCellCount == 1);
        Check("no cell of an unaffordable placement is drawn legal",
            CountGhost(_structureTool, BuildTool.GhostLegal) == 0);
        Check("the whole placement reads refused and no cell is blamed for the money",
            CountGhost(_structureTool, BuildTool.GhostRefused) == 1
            && CountGhost(_structureTool, BuildTool.GhostIllegalCell) == 0
            && _structureTool.Preview?.CellRefusals[0] == PlacementRefusal.None);
        Check("the hover square reads refused when the money is short",
            _structureTool.CursorColor.IsEqualApprox(BuildTool.CursorRefused));

        Check("the click is rejected", !_structureTool.ClickCell(cell));
        Check("nothing was built",
            _world.GetTile(cell) == TileType.Empty
            && _world.Structures.Count == structuresBefore);
        Check("the refused click left the balance alone", _economy.Balance == price - 1);

        _economy.SetBalance(price);
        _structureTool.HoverAt(cell);
        Check("the exact price is affordable", _structureTool.PreviewLegal);
        Check("the ghost turns legal with the money, not with the ground",
            CountGhost(_structureTool, BuildTool.GhostLegal) == 1);
        Check("the click is accepted", _structureTool.ClickCell(cell));
        Check("the building went up",
            _world.GetStructure(cell) != null
            && _world.Structures.Count == structuresBefore + 1);
        Check("paying for it emptied the account", _economy.Balance == 0);
        Check("the readout says so", _moneyReadout.Text == Economy.Describe(0));
        Check("and a player with nothing can afford nothing", !_economy.CanAfford(1));
    }

    /// <summary>
    /// Affordability is a whole-placement property, like
    /// <see cref="PlacementRule.TouchesRoad"/>: a drag is bought outright or
    /// not at all. Four cells of road are affordable at exactly their total,
    /// five are not — and every one of those five is affordable on its own,
    /// which is the misreading this check exists to rule out. Pulling the drag
    /// back in to what the money covers places it.
    /// </summary>
    private void CheckADragIsAffordableAsAWhole()
    {
        Vector2I? run = FindCell(cell => IsFreeSoilLine(cell, cell + new Vector2I(4, 0)));
        Check("found a clear soil run to price a long drag on", run != null);
        if (run is not { } from)
        {
            return;
        }

        Vector2I near = from + new Vector2I(3, 0);
        Vector2I far = from + new Vector2I(4, 0);
        int nearCells = WorldGrid.LineCells(from, near).Count;
        int farCells = WorldGrid.LineCells(from, far).Count;
        int purse = _tool.CostPerCell * nearCells;

        _economy.SetBalance(purse);
        _tool.SetActive(true);
        Check("the drag anchors", _tool.ClickCell(from));

        _tool.HoverAt(near);
        Check("a drag priced at exactly the balance is legal",
            _tool.PreviewLegal && _tool.Preview?.Count == nearCells
            && _tool.Preview?.Cost == purse);

        _tool.HoverAt(far);
        Check("one cell further is refused - for the total, not for the cell",
            _tool.Preview?.Refusal == PlacementRefusal.CannotAfford
            && _tool.Preview?.Cost == _tool.CostPerCell * farCells);
        Check("though every cell of it was affordable on its own",
            farCells > nearCells && _economy.CanAfford(_tool.CostPerCell));
        Check("no cell of an unaffordable drag is drawn legal",
            CountGhost(_tool, BuildTool.GhostLegal) == 0
            && CountGhost(_tool, BuildTool.GhostRefused) == farCells);
        Check("the click on it is rejected", !_tool.ClickCell(far));
        Check("not one cell of it was built", AllTile(WorldGrid.LineCells(from, far),
            TileType.Empty));
        Check("and it cost nothing to be refused", _economy.Balance == purse);
        Check("a refused drag keeps its anchor so the player can pull it back in",
            _tool.Anchor == from);

        _tool.HoverAt(near);
        Check("the shorter drag is legal again", _tool.PreviewLegal);
        Check("and it places", _tool.ClickCell(near));
        Check("spending the balance to the last coin", _economy.Balance == 0);
        Check("exactly the cells that were paid for are road", AllRoad(from, near));
        Check("the one cell too far is not", _world.GetTile(far) == TileType.Empty);
    }

    /// <summary>
    /// How the price meets <see cref="FootprintPolicy.AnyCell"/>: <b>a drag
    /// pays for the cells it acts on</b>. A bulldoze rectangle crosses empty
    /// ground as a matter of course, and charging for cells nothing happens to
    /// would be charging for nothing — while a build, being all-or-nothing, is
    /// priced for its whole footprint. Both halves are asked straight through
    /// <see cref="PlacementRules"/> with a budget handed in, which is also the
    /// check that money reaches the evaluator as a <i>value</i>: no account, no
    /// node, nothing to reach out for.
    ///
    /// Then the same thing through the tool, by putting a price on the
    /// bulldozer at runtime — which is the export doing its job, and the one
    /// way to see an <c>AnyCell</c> footprint actually charged.
    /// </summary>
    private void CheckAnAnyCellDragPaysForWhatItActsOn()
    {
        Vector2I? block = FindCell(cell => IsFreeSoilRect(cell, cell + new Vector2I(2, 1)));
        Check("found clear soil for a half-built rectangle", block != null);
        if (block is not { } origin)
        {
            return;
        }

        Vector2I roadEnd = origin + new Vector2I(2, 0);
        Vector2I far = origin + new Vector2I(2, 1);
        List<Vector2I> rect = WorldGrid.RectCells(origin, far);
        List<Vector2I> road = WorldGrid.RectCells(origin, roadEnd);
        List<Vector2I> bare = WorldGrid.RectCells(origin + Vector2I.Down, far);
        _world.BuildRoadLine(origin, roadEnd);
        Check("the rectangle is half road and half bare ground",
            AllTile(road, TileType.Road) && AllTile(bare, TileType.Empty)
            && rect.Count == road.Count + bare.Count);

        const int perCell = 7;
        int acted = road.Count;
        PlacementRule removal = PlacementRule.InBounds | PlacementRule.OccupiedCell;

        PlacementPlan rich = RemovalPlan(rect, new PlacementBudget(perCell, WorkingBalance));
        Check("an AnyCell drag is priced for the cells it acts on",
            rich.Cost == perCell * acted);
        Check("not for the empty ground it crossed", rich.Cost < perCell * rect.Count);
        Check("and it is legal when that price is covered", rich.Legal);

        Check("a balance covering exactly the acted-on cells is enough",
            RemovalPlan(rect, new PlacementBudget(perCell, perCell * acted)).Legal);
        Check("one coin short of it is refused for the money",
            RemovalPlan(rect, new PlacementBudget(perCell, perCell * acted - 1)).Refusal
                == PlacementRefusal.CannotAfford);
        Check("a budget that could not cover the whole rectangle still buys it",
            RemovalPlan(rect, new PlacementBudget(perCell, perCell * rect.Count - 1)).Legal);

        PlacementPlan build = PlacementRules.Check(
            _world, bare, PlacementRule.BuildableTerrain | PlacementRule.VacantCell,
            TileType.Field, FootprintPolicy.EveryCell,
            new PlacementBudget(perCell, WorkingBalance));
        Check("a build is priced for its whole footprint",
            build.Legal && build.Cost == perCell * bare.Count);

        Check("a plan checked without a budget is free, and free is always affordable",
            RemovalPlan(rect, PlacementBudget.Free).Cost == 0
            && PlacementRules.Check(_world, rect, removal, TileType.Empty,
                FootprintPolicy.AnyCell).Legal
            && PlacementBudget.Free.Price(99) == 0);

        // The tool itself, priced through the export at runtime — the knob a
        // playtest would turn, and the only way to watch an AnyCell footprint
        // actually pay.
        _bulldozeTool.CostPerCell = perCell;
        _bulldozeTool.SetActive(true);
        _economy.SetBalance(perCell * acted - 1);
        Check("the priced bulldoze drag anchors on the built half",
            _bulldozeTool.ClickCell(origin));
        _bulldozeTool.HoverAt(far);
        Check("a removal it cannot pay for is refused",
            _bulldozeTool.Preview?.Refusal == PlacementRefusal.CannotAfford
            && _bulldozeTool.Preview?.Cost == perCell * acted);
        Check("and its ghost promises nothing, not even the cells that hold something",
            CountGhost(_bulldozeTool, BuildTool.GhostLegal) == 0
            && CountGhost(_bulldozeTool, BuildTool.GhostRefused) == rect.Count);
        Check("the click is rejected and the road survives",
            !_bulldozeTool.ClickCell(far) && AllTile(road, TileType.Road));

        _economy.SetBalance(perCell * acted);
        _bulldozeTool.HoverAt(far);
        Check("the price of the cells it acts on is enough to run it",
            _bulldozeTool.PreviewLegal);
        Check("and the ghost is honest again: the built cells, and only those",
            CountGhost(_bulldozeTool, BuildTool.GhostLegal) == acted
            && CountGhost(_bulldozeTool, BuildTool.GhostRefused) == bare.Count);
        Check("the second click clears it", _bulldozeTool.ClickCell(far));
        Check("the built half is gone", AllTile(rect, TileType.Empty));
        Check("and it was charged for the cells it cleared, not for the drag",
            _economy.Balance == 0);

        // Put the tool back the way the scene has it: bulldozing is free.
        _bulldozeTool.CostPerCell = 0;
        Check("the price comes back off again", _bulldozeTool.CostPerCell == 0);
    }

    /// <summary>
    /// The account on its own, and the invariant that survives however a caller
    /// is written: <see cref="Economy.TrySpend"/> refuses rather than going
    /// negative, and money only ever comes <i>in</i> through
    /// <see cref="Economy.Credit"/> — a negative spend is not a credit through
    /// the wrong door, and a credit of nothing is a genuine no-op rather than a
    /// balance write. <see cref="Economy.SetBalance"/> is the one door those two
    /// do not guard, so it clamps: a mistyped export or a corrupt save leaves
    /// the player broke, not in debt.
    /// </summary>
    private void CheckTheAccountItself()
    {
        _economy.SetBalance(100);
        Check("afford is exact at the balance",
            _economy.CanAfford(100) && !_economy.CanAfford(101));
        Check("spending more than there is is refused", !_economy.TrySpend(101));
        Check("a refused spend changes nothing", _economy.Balance == 100);
        Check("spending what is there is allowed", _economy.TrySpend(100));
        Check("which leaves exactly nothing, never less", _economy.Balance == 0);
        Check("a free placement is allowed with an empty account", _economy.TrySpend(0));
        Check("and moves nothing", _economy.Balance == 0);
        Check("a negative spend is not a credit through the wrong door",
            _economy.TrySpend(-50) && _economy.Balance == 0);

        _economy.Credit(25);
        Check("a credit puts money in", _economy.Balance == 25);
        _economy.Credit(0);
        Check("a refund of nothing is a no-op", _economy.Balance == 25);
        _economy.Credit(-10);
        Check("a negative credit is ignored, not a charge", _economy.Balance == 25);
        Check("the readout tracks every one of those",
            _moneyReadout.Text == Economy.Describe(25) && _economy.Text == _moneyReadout.Text);

        _economy.SetBalance(-500);
        Check("a negative balance set outright is clamped, not stored",
            _economy.Balance == 0);
        Check("and the readout says broke, not minus five hundred",
            _moneyReadout.Text == Economy.Describe(0) && _economy.Text == _moneyReadout.Text);
    }

    /// <summary>
    /// The bulldozer's refund seam, now wired to the account: whatever
    /// <c>RefundFor</c> returns is credited, so the balance after a bulldoze is
    /// the balance before plus <see cref="BulldozeTool.RefundTotal"/>'s change —
    /// asserted in those terms, so it stays true the day M7 makes the seam pay.
    /// Today it pays zero, which is why the balance does not move.
    /// </summary>
    private void CheckTheRefundSeamCreditsTheAccount()
    {
        Vector2I? run = FindCell(cell => IsFreeSoilLine(cell, cell + new Vector2I(2, 0)));
        Check("found clear soil for a road to buy and then take away", run != null);
        if (run is not { } from)
        {
            return;
        }

        Vector2I to = from + new Vector2I(2, 0);
        int price = _tool.CostPerCell * WorldGrid.LineCells(from, to).Count;

        _economy.SetBalance(WorkingBalance);
        Check("the road goes down", Drag(_tool, from, to));
        Check("buying it cost what the tool charges",
            _economy.Balance == WorkingBalance - price);

        int balanceBefore = _economy.Balance;
        int refundedBefore = _bulldozeTool.RefundTotal;
        int removalsBefore = _bulldozeTool.Removals.Count;
        Check("the bulldozer takes it off again", Drag(_bulldozeTool, from, to));
        int refunded = _bulldozeTool.RefundTotal - refundedBefore;

        Check("the removals went through the seam",
            _bulldozeTool.Removals.Count > removalsBefore);
        Check("the balance moved by exactly what the seam returned",
            _economy.Balance == balanceBefore + refunded);
        Check("which is nothing today - refund economics are M7's",
            refunded == 0 && _bulldozeTool.RefundTotal == 0);
        Check("so clearing leaves the balance where it was",
            _economy.Balance == balanceBefore);
        Check("and the seam credits the same account the tools charge",
            _bulldozeTool.Economy == _economy && _tool.Economy == _economy);
    }

    /// <summary>
    /// The bulldozer's own question — "what would clearing these cells cost and
    /// would it be allowed?" — asked straight through
    /// <see cref="PlacementRules"/> with a budget, no tool and no account in
    /// sight.
    /// </summary>
    private PlacementPlan RemovalPlan(IReadOnlyList<Vector2I> cells, PlacementBudget budget) =>
        PlacementRules.Check(
            _world,
            cells,
            PlacementRule.InBounds | PlacementRule.OccupiedCell,
            TileType.Empty,
            FootprintPolicy.AnyCell,
            budget);

    /// <summary>Cells the tool's ghost currently draws in that colour.</summary>
    private static int CountGhost(BuildTool tool, Color color)
    {
        int count = 0;
        for (int i = 0; i < tool.GhostCellCount; i++)
        {
            if (tool.GhostColor(i).IsEqualApprox(color))
            {
                count++;
            }
        }
        return count;
    }

    private bool AllRoad(Vector2I from, Vector2I to)
    {
        foreach (Vector2I cell in WorldGrid.LineCells(from, to))
        {
            if (!_world.IsRoad(cell))
            {
                return false;
            }
        }
        return true;
    }

    private bool AllTile(IReadOnlyList<Vector2I> cells, TileType type)
    {
        foreach (Vector2I cell in cells)
        {
            if (_world.GetTile(cell) != type)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether every cell is owned by that one field object.</summary>
    private bool AllSameField(IReadOnlyList<Vector2I> cells, Field field)
    {
        foreach (Vector2I cell in cells)
        {
            if (_world.GetField(cell) != field)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether the world still lists that field.</summary>
    private bool WorldHasField(Field field)
    {
        foreach (Field known in _world.Fields)
        {
            if (known == field)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether the list holds that cell.</summary>
    private static bool Covers(IReadOnlyList<Vector2I> cells, Vector2I cell)
    {
        foreach (Vector2I candidate in cells)
        {
            if (candidate == cell)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Arms a drag tool and runs one whole placement through it: anchor, hover
    /// the far corner, commit. Arming disarms whatever was armed before, so a
    /// bulldoze check that places something has to re-arm the bulldozer after.
    /// </summary>
    private static bool Drag(BuildTool tool, Vector2I from, Vector2I to)
    {
        tool.SetActive(true);
        if (!tool.ClickCell(from))
        {
            return false;
        }
        tool.HoverAt(to);
        return tool.ClickCell(to);
    }

    /// <summary>
    /// Records the terrain under cells *before* anything is built on them, so
    /// the bulldoze checks can compare against what the ground actually was
    /// rather than against what it reads as afterwards.
    /// </summary>
    private void RememberTerrain(IReadOnlyList<Vector2I> cells)
    {
        foreach (Vector2I cell in cells)
        {
            _rememberedCells.Add(cell);
            _rememberedTerrain.Add(_world.GetTerrain(cell));
            _rememberedFertility.Add(_world.GetFertility(cell));
        }
    }

    /// <summary>
    /// Whether every one of those cells still reads the terrain type it was
    /// remembered with. A cell that was never remembered fails, so a check
    /// cannot pass by comparing nothing.
    /// </summary>
    private bool TerrainUnchanged(IReadOnlyList<Vector2I> cells)
    {
        foreach (Vector2I cell in cells)
        {
            int i = _rememberedCells.IndexOf(cell);
            if (i < 0 || _world.GetTerrain(cell) != _rememberedTerrain[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The same for fertility, compared exactly: nothing in the placement layer
    /// is allowed to nudge it, so "close enough" would be the wrong test.
    /// </summary>
    private bool FertilityUnchanged(IReadOnlyList<Vector2I> cells)
    {
        foreach (Vector2I cell in cells)
        {
            int i = _rememberedCells.IndexOf(cell);
            if (i < 0 || _world.GetFertility(cell) != _rememberedFertility[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>How many remembered cells were that terrain when they were read.</summary>
    private int RememberedCellsWithTerrain(TerrainType terrain)
    {
        int count = 0;
        foreach (TerrainType remembered in _rememberedTerrain)
        {
            if (remembered == terrain)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>How many remembered cells had fertility on them to lose.</summary>
    private int RememberedFertileCells()
    {
        int count = 0;
        foreach (float fertility in _rememberedFertility)
        {
            if (fertility > 0f)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>The last thing the bulldozer put through the refund seam.</summary>
    private Removal? LastRemoval() =>
        _bulldozeTool.Removals.Count > 0
            ? _bulldozeTool.Removals[^1]
            : null;

    /// <summary>Same cells in the same order — footprints are order-defined.</summary>
    private static bool SameCells(IReadOnlyList<Vector2I> a, IReadOnlyList<Vector2I> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    private static bool InsideRect(IReadOnlyList<Vector2I> cells, Vector2I min, Vector2I max)
    {
        foreach (Vector2I cell in cells)
        {
            if (cell.X < min.X || cell.X > max.X || cell.Y < min.Y || cell.Y > max.Y)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Picks the cells the assertions use out of the *generated* terrain rather
    /// than hard-coding coordinates, so a changed seed or noise tuning cannot
    /// silently turn a "legal ground" cell into water.
    /// </summary>
    private void PickTestCells()
    {
        Vector2I? soil = FindCell(cell => IsFreeSoilLine(cell, cell + new Vector2I(3, 0)));
        Vector2I? rock = FindCell(cell =>
            _world.GetTerrain(cell) == TerrainType.Rock && _world.GetTile(cell) == TileType.Empty);

        // The overlap check needs a pair of cells the road line will not have
        // covered by the time it runs.
        var roadLine = new HashSet<Vector2I>(soil is { } start
            ? WorldGrid.LineCells(start, start + new Vector2I(3, 0))
            : []);
        Vector2I? field = FindCell(cell =>
            IsFreeSoil(cell) && IsFreeSoil(cell + Vector2I.Right)
            && !roadLine.Contains(cell) && !roadLine.Contains(cell + Vector2I.Right));

        Check("found a clear soil line to build on", soil != null);
        Check("found a rock cell to be refused on", rock != null);
        Check("found a clear soil pair for the overlap check", field != null);
        Check("found a soil-to-water line for the terrain check",
            TryFindLineIntoWater(out _waterAnchor, out _water));

        _soilFrom = soil ?? Vector2I.Zero;
        _soilTo = _soilFrom + new Vector2I(3, 0);
        _rock = rock ?? Vector2I.Zero;
        _field = field ?? Vector2I.Zero;
        _fieldAnchor = _field + Vector2I.Right;
        _offMap = new Vector2I(_world.MapSize / 2 + 3, 0);

        GD.Print($"build test cells: line {_soilFrom} to {_soilTo}, rock {_rock}, "
            + $"water {_water} from {_waterAnchor}, field {_field}, off-map {_offMap}");
    }

    /// <summary>
    /// Picks the field section's cells out of the world *as it is by then* —
    /// searched here rather than in <see cref="PickTestCells"/> because the
    /// road checks have laid road since, and a field rectangle has to land on
    /// ground that is still clear.
    ///
    /// One block of free soil, 2·<see cref="FieldRectWidth"/> wide and
    /// <see cref="FieldRectHeight"/> + 1 tall: its top half is the pair of
    /// touching rectangles the checks mark, and the spare row underneath is
    /// clear ground to anchor a drag that then runs onto them.
    /// </summary>
    private void PickFieldCells()
    {
        var span = new Vector2I(FieldRectWidth * 2 - 1, FieldRectHeight);
        Vector2I? block = FindCell(cell => IsFreeSoilRect(cell, cell + span));
        Check("found a clear soil block for the field rectangles", block != null);

        Vector2I origin = block ?? Vector2I.Zero;
        _rectFrom = origin;
        _rectTo = origin + new Vector2I(FieldRectWidth - 1, FieldRectHeight - 1);
        _nextRectFrom = origin + new Vector2I(FieldRectWidth, 0);
        _nextRectTo = origin + new Vector2I(FieldRectWidth * 2 - 1, FieldRectHeight - 1);
        _belowRect = origin + new Vector2I(0, FieldRectHeight);

        GD.Print($"field test cells: rectangle {_rectFrom} to {_rectTo}, neighbour "
            + $"{_nextRectFrom} to {_nextRectTo}, clear cell below {_belowRect}");
    }

    private bool IsFreeSoil(Vector2I cell) =>
        _world.IsSoil(cell) && _world.GetTile(cell) == TileType.Empty;

    private bool IsFreeSoilLine(Vector2I from, Vector2I to)
    {
        foreach (Vector2I cell in WorldGrid.LineCells(from, to))
        {
            if (!IsFreeSoil(cell))
            {
                return false;
            }
        }
        return true;
    }

    private bool IsFreeSoilRect(Vector2I from, Vector2I to)
    {
        foreach (Vector2I cell in WorldGrid.RectCells(from, to))
        {
            if (!IsFreeSoil(cell))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Whether the cell borders the road network the way
    /// <see cref="PlacementRule.TouchesRoad"/> means it — asked through the
    /// rule itself, so the test's idea of adjacency can never drift from the
    /// game's.
    /// </summary>
    private bool TouchesRoad(Vector2I cell) => PlacementRules.HasRoadAccess(_world, [cell]);

    /// <summary>
    /// A rock or water cell with clear soil beside it, so a road can be laid on
    /// that neighbour and the rough cell then judged with its road requirement
    /// already satisfied.
    /// </summary>
    private bool TryFindRoughCellWithSoilBeside(out Vector2I rough, out Vector2I beside)
    {
        Vector2I[] directions = [Vector2I.Right, Vector2I.Left, Vector2I.Up, Vector2I.Down];
        for (int radius = 1; radius <= 40; radius++)
        {
            foreach (Vector2I cell in Ring(radius))
            {
                TerrainType terrain = _world.GetTerrain(cell);
                if ((terrain != TerrainType.Rock && terrain != TerrainType.Water)
                    || _world.GetTile(cell) != TileType.Empty)
                {
                    continue;
                }
                foreach (Vector2I direction in directions)
                {
                    if (IsFreeSoil(cell + direction))
                    {
                        rough = cell;
                        beside = cell + direction;
                        return true;
                    }
                }
            }
        }
        rough = beside = Vector2I.Zero;
        return false;
    }

    /// <summary>
    /// A straight run of clear soil ending one cell short of open water, so a
    /// drag from the run's far end into the water is legal everywhere but its
    /// last cell.
    /// </summary>
    private bool TryFindLineIntoWater(out Vector2I anchor, out Vector2I water)
    {
        Vector2I[] directions = [Vector2I.Right, Vector2I.Left, Vector2I.Up, Vector2I.Down];
        for (int radius = 1; radius <= 40; radius++)
        {
            foreach (Vector2I cell in Ring(radius))
            {
                if (_world.GetTerrain(cell) != TerrainType.Water
                    || _world.GetTile(cell) != TileType.Empty)
                {
                    continue;
                }
                foreach (Vector2I direction in directions)
                {
                    Vector2I candidate = cell + direction * 3;
                    if (IsFreeSoilLine(candidate, cell + direction))
                    {
                        anchor = candidate;
                        water = cell;
                        return true;
                    }
                }
            }
        }
        anchor = water = Vector2I.Zero;
        return false;
    }

    /// <summary>
    /// A short straight strip of clear soil ending on rock or open water, so
    /// the rectangle spanned by its two ends straddles ground no field may
    /// cover — the field-tool counterpart of
    /// <see cref="TryFindLineIntoWater"/>. Searched at the point of use, so
    /// the soil cells are guaranteed still clear of this test's own fields.
    /// </summary>
    private bool TryFindRectIntoRoughTerrain(out Vector2I anchor, out Vector2I rough)
    {
        Vector2I[] directions = [Vector2I.Right, Vector2I.Left, Vector2I.Up, Vector2I.Down];
        for (int radius = 1; radius <= 40; radius++)
        {
            foreach (Vector2I cell in Ring(radius))
            {
                TerrainType terrain = _world.GetTerrain(cell);
                if ((terrain != TerrainType.Rock && terrain != TerrainType.Water)
                    || _world.GetTile(cell) != TileType.Empty)
                {
                    continue;
                }
                foreach (Vector2I direction in directions)
                {
                    if (IsFreeSoil(cell + direction) && IsFreeSoil(cell + direction * 2))
                    {
                        anchor = cell + direction * 2;
                        rough = cell;
                        return true;
                    }
                }
            }
        }
        anchor = rough = Vector2I.Zero;
        return false;
    }

    /// <summary>
    /// First cell matching the predicate, searched outward from the origin in
    /// square rings — so the cells the test uses are the ones nearest the
    /// starting area, and the search is deterministic.
    /// </summary>
    private Vector2I? FindCell(Func<Vector2I, bool> match, int minRadius = 0, int maxRadius = 40)
    {
        for (int radius = minRadius; radius <= maxRadius; radius++)
        {
            foreach (Vector2I cell in Ring(radius))
            {
                if (_world.InBounds(cell) && match(cell))
                {
                    return cell;
                }
            }
        }
        return null;
    }

    /// <summary>Cells exactly <paramref name="radius"/> steps away in Chebyshev distance.</summary>
    private static IEnumerable<Vector2I> Ring(int radius)
    {
        for (int y = -radius; y <= radius; y++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                if (Math.Max(Math.Abs(x), Math.Abs(y)) == radius)
                {
                    yield return new Vector2I(x, y);
                }
            }
        }
    }

    private void Check(string what, bool ok)
    {
        GD.Print($"{(ok ? "PASS" : "FAIL")}: {what}");
        _failed |= !ok;
    }
}
