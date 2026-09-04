using Godot;

namespace Arable;

/// <summary>
/// Keyboard menu: number keys 1-9 (actions menu_1..menu_9) trigger game
/// commands. Slot 1 toggles the road-building tool, slot 2 the hover readout,
/// slot 9 spawns a new machine on the road network; the other slots just log
/// that they are unassigned.
/// </summary>
public partial class MenuController : Node
{
    private const int SlotCount = 9;

    [Export] public WorldGrid? World { get; set; }
    [Export] public BuildTool? RoadTool { get; set; }
    [Export] public CellInspector? Inspector { get; set; }

    public override void _UnhandledInput(InputEvent @event)
    {
        for (int slot = 1; slot <= SlotCount; slot++)
        {
            if (@event.IsActionPressed($"menu_{slot}"))
            {
                Activate(slot);
                GetViewport().SetInputAsHandled();
                return;
            }
        }
    }

    private void Activate(int slot)
    {
        switch (slot)
        {
            case 1:
                RoadTool?.Toggle();
                break;
            case 2:
                Inspector?.Toggle();
                break;
            case 9:
                World?.SpawnMachine();
                break;
            default:
                GD.Print($"Menu slot {slot} is unassigned.");
                break;
        }
    }
}
