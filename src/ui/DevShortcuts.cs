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
/// node keeps exactly those commands:
///
/// | Key | Action | Why it is here |
/// |---|---|---|
/// | <b>7</b> | work the field under the cursor | drives M4's crop operations until M5 programs machines to |
/// | <b>8</b> | toggle the hover readout (<see cref="CellInspector"/>) | a dev instrument, not a build tool |
/// | <b>9</b> | spawn a machine on the road network | dev/scenario command, until M5 gives machines jobs |
///
/// <b>The dev keys sit at the top of the number row on purpose.</b> The palette
/// claims <c>menu_1</c> upwards, one key per entry in bar order, so it grows
/// from the bottom of the row; taking the top keys keeps the two ranges from meeting
/// while the roster is nowhere near that long. The readout moved off key 2 for
/// exactly that reason — key 2 is the second tool on the bar now.
///
/// Nothing here reaches a build tool. That is the point: after this issue there
/// is one way to choose a tool, and it is the palette.
/// </summary>
public partial class DevShortcuts : Node
{
    /// <summary>
    /// Key 7: applies the one operation the field under the cursor is waiting
    /// for — plough, sow or harvest, whichever its stage allows.
    /// </summary>
    private const int WorkFieldSlot = 7;

    /// <summary>Key 8: switches the hover readout off and on.</summary>
    private const int ReadoutSlot = 8;

    /// <summary>Key 9: puts another machine on the road network.</summary>
    private const int SpawnMachineSlot = 9;

    [Export] public WorldGrid? World { get; set; }
    [Export] public CellInspector? Inspector { get; set; }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event.IsActionPressed($"menu_{WorkFieldSlot}"))
        {
            WorkFieldUnderCursor();
            GetViewport().SetInputAsHandled();
        }
        else if (@event.IsActionPressed($"menu_{ReadoutSlot}"))
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

    /// <summary>
    /// Runs M4's crop state machine by hand: whatever the field under the
    /// cursor is waiting for, do that. It goes through
    /// <see cref="CropSystem.Apply"/> like every other caller, so this key can
    /// never put a field into a state real work could not have produced — a
    /// refusal is printed, not worked around.
    ///
    /// Printing rather than a HUD line is deliberate: the field panel is a
    /// separate issue, and a dev key that quietly did nothing when the crop was
    /// still growing would read as a broken key.
    /// </summary>
    private void WorkFieldUnderCursor()
    {
        if (World == null)
        {
            return;
        }

        Vector2I? cell = CellPicker.CellUnderMouse(this, World);
        Field? field = cell is { } c ? World.GetField(c) : null;
        if (field == null)
        {
            GD.Print($"dev: no field at {(cell is { } at ? $"{at.X}, {at.Y}" : "the cursor")}");
            return;
        }

        CropSystem crops = World.Crops;
        string stage = CropSystem.Name(crops.StageOf(field.Crop));
        if (crops.NextOperation(field.Crop) is not { } operation)
        {
            GD.Print($"dev: {field.Name} is {stage} "
                + $"({crops.GrowthOf(field.Crop) / crops.TicksPerDay:F2}"
                + $"/{crops.DaysToRipen} days) — nothing to do yet");
            return;
        }

        CropOpResult result = crops.Apply(field.Crop, operation);
        // The buffer goes on the line because a harvest that deposited nothing
        // and one that filled the field's store look identical without it, and
        // OutputFull is a refusal the player is meant to be able to explain.
        ItemBuffer? output = crops.OutputOf(field.Crop);
        GD.Print($"dev: {operation.ToString().ToLowerInvariant()} {field.Name} "
            + $"({stage}) → {result}: now {CropSystem.Name(crops.StageOf(field.Crop))}, "
            + $"holding {output} {crops.HarvestItem.Name}");
    }
}
