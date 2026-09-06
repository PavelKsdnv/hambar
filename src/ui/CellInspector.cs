using Godot;

namespace Arable;

/// <summary>
/// Hover readout: a screen-corner label naming the cell under the cursor — its
/// coordinates, the terrain and fertility underneath, and whatever the player
/// placed on top. This is the dev instrument that lets a human confirm the
/// generated world is what the generator thinks it is; the player-facing
/// inspector panels (fields, buildings) are separate, later work.
///
/// Toggled with dev key 8 (see <see cref="DevShortcuts"/>), so it can be
/// switched off for screenshots. Picking
/// goes through <see cref="CellPicker"/>, the same code path the build tools
/// use, so the readout can never disagree with what a click would hit.
/// </summary>
public partial class CellInspector : Node
{
    [Export] public WorldGrid? World { get; set; }

    /// <summary>Label the readout is written to; the inspector owns its visibility.</summary>
    [Export] public Label? Readout { get; set; }

    /// <summary>Whether the readout starts switched on (off = a clean screen).</summary>
    [Export] public bool EnabledOnStart { get; set; } = true;

    public bool Enabled { get; private set; }

    /// <summary>Cell resolved by the last <see cref="Inspect"/>; null when none was.</summary>
    public Vector2I? HoverCell { get; private set; }

    /// <summary>Text of the last readout — what the label shows.</summary>
    public string Text { get; private set; } = string.Empty;

    public override void _Ready() => SetEnabled(EnabledOnStart);

    public void Toggle() => SetEnabled(!Enabled);

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        if (Readout != null)
        {
            Readout.Visible = enabled;
        }
        if (!enabled)
        {
            HoverCell = null;
            Text = string.Empty;
        }
    }

    public override void _Process(double delta)
    {
        if (!Enabled)
        {
            return;
        }
        Viewport? viewport = GetViewport();
        if (viewport != null)
        {
            Inspect(viewport.GetMousePosition());
        }
    }

    /// <summary>
    /// Reads the cell at a screen position and refreshes the readout, returning
    /// the cell. Public and position-driven so the headless smoke test can drive
    /// a known pixel without a real cursor.
    /// </summary>
    public Vector2I? Inspect(Vector2 screenPosition)
    {
        HoverCell = CellPicker.CellAt(this, World, screenPosition);
        Text = Describe(HoverCell);
        if (Readout != null)
        {
            Readout.Text = Text;
        }
        return HoverCell;
    }

    /// <summary>
    /// The readout text for a cell: coordinates, terrain (+ fertility, which
    /// only soil has), and the placed tile — plus the name of the field or
    /// building that owns the cell, when one does. Off the map the terrain layer
    /// already answers <see cref="TerrainType.OutOfBounds"/>, so that case needs
    /// no extra bounds check — it just reads differently.
    /// </summary>
    public string Describe(Vector2I? cell)
    {
        if (World == null || cell is not { } c)
        {
            return "cell: none";
        }

        TerrainType terrain = World.GetTerrain(c);
        string fertility = terrain == TerrainType.Soil
            ? World.GetFertility(c).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
            : "-";
        // A placed cell names the entity that owns it, because the entity —
        // not the cell — is what the game addresses farmland (see Field) and
        // buildings (see Structure) by.
        string tile = TileName(World.GetTile(c));
        if (World.GetField(c) is { } field)
        {
            tile += $" ({field.Name})";
        }
        else if (World.GetStructure(c) is { } structure)
        {
            tile += $" ({structure.Name})";
        }

        return $"cell: {c.X}, {c.Y}\n"
            + $"terrain: {TerrainName(terrain)}   fertility: {fertility}\n"
            + $"tile: {tile}";
    }

    private static string TerrainName(TerrainType terrain) => terrain switch
    {
        TerrainType.Soil => "soil",
        TerrainType.Rock => "rock",
        TerrainType.Water => "water",
        _ => "off the map",
    };

    private static string TileName(TileType tile) => tile switch
    {
        TileType.Road => "road",
        TileType.Field => "field",
        TileType.Structure => "structure",
        _ => "empty",
    };
}
