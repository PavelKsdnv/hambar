using Godot;

namespace Arable;

/// <summary>
/// What is left of the number-key menu now that the <see cref="BuildPalette"/>
/// exists: <b>two dev shortcuts, and nothing a player needs.</b>
///
/// The old <c>MenuController</c> was a stand-in for a build UI — keys 1, 3, 4
/// and 5 armed the four build tools and the rest logged "unassigned" — and the
/// palette has taken that job over entirely, buttons and accelerators alike.
/// What did not belong to it in the first place still needs a home, so this
/// node keeps exactly those two commands:
///
/// | Key | Action | Why it is here |
/// |---|---|---|
/// | <b>8</b> | toggle the hover readout (<see cref="CellInspector"/>) | a dev instrument, not a build tool |
/// | <b>9</b> | spawn a machine on the road network | dev/scenario command, until M4 gives machines jobs |
///
/// <b>The dev keys sit at the top of the number row on purpose.</b> The palette
/// claims <c>menu_1</c> upwards, one key per entry in bar order, so it grows
/// from the bottom of the row; taking 8 and 9 keeps the two ranges from meeting
/// while the roster is nowhere near that long. The readout moved off key 2 for
/// exactly that reason — key 2 is the second tool on the bar now.
///
/// Nothing here reaches a build tool. That is the point: after this issue there
/// is one way to choose a tool, and it is the palette.
/// </summary>
public partial class DevShortcuts : Node
{
    /// <summary>Key 8: switches the hover readout off and on.</summary>
    private const int ReadoutSlot = 8;

    /// <summary>Key 9: puts another machine on the road network.</summary>
    private const int SpawnMachineSlot = 9;

    [Export] public WorldGrid? World { get; set; }
    [Export] public CellInspector? Inspector { get; set; }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed($"menu_{ReadoutSlot}"))
        {
            Inspector?.Toggle();
            GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed($"menu_{SpawnMachineSlot}"))
        {
            World?.SpawnMachine();
            GetViewport().SetInputAsHandled();
        }
    }
}
