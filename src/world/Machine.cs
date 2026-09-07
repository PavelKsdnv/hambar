using Godot;

namespace Arable;

/// <summary>
/// The <b>drawing</b> of a vehicle (tractor, combine, truck, ...). The machine
/// itself — position, yaw, route, whether it is parked — is a row of
/// <see cref="MachineSystem"/>'s arrays, addressed by <see cref="Entity"/>;
/// this node owns nothing but the mesh, and its transform is a picture of that
/// row rather than the place the machine is. Read <see cref="SimPosition"/> for
/// where it actually is.
///
/// So it is an <see cref="ISimView"/> and nothing else. It used to be both
/// halves, which is why the interfaces were split before the arrays existed:
/// moving the state out cost this file its <c>Tick</c> and touched no other
/// part of the loop.
///
/// The exports are <b>spawn input</b>, read once by <c>WorldGrid.SpawnMachine</c>
/// and copied into the arrays. Changing <see cref="Speed"/> on a live node does
/// nothing; the sim never reads them again.
/// </summary>
public partial class Machine : Node3D, ISimView
{
    /// <summary>Top surface of road tiles — machines drive at this height.</summary>
    public const float DeckHeight = 0.1f;

    [Export] public float Speed { get; set; } = 6f;
    [Export] public float TurnSpeed { get; set; } = 8f;
    [Export] public Color BodyColor { get; set; } = new(0.75f, 0.22f, 0.17f);

    /// <summary>
    /// Units this vehicle can carry at once. <b>The knob the hauling game is
    /// played on</b>: it is deliberately smaller than a grown field's output
    /// buffer, so clearing a field is several trips and a bigger farm needs
    /// more trucks rather than the same one running faster. Per-vehicle,
    /// because the roster M5 grows into is a small truck and a big one, and
    /// that difference is this number.
    /// </summary>
    [Export] public int CargoCapacity { get; set; } = 50;

    private MachineSystem? _machines;
    private Simulation? _sim;

    /// <summary>The sim entity this node draws, or <see cref="EntityId.None"/>.</summary>
    public EntityId Entity { get; private set; } = EntityId.None;

    /// <summary>Where the machine is as of the last tick. Sim truth.</summary>
    public Vector3 SimPosition => _machines?.PositionOf(Entity) ?? Position;

    /// <summary>Where it was the tick before — the other end of the view's blend.</summary>
    public Vector3 PreviousSimPosition => _machines?.PreviousPositionOf(Entity) ?? Position;

    /// <summary>Set once it strands off-road: it stays alive but stops moving.</summary>
    public bool Parked => _machines?.IsParked(Entity) ?? false;

    /// <summary>
    /// What the machine is carrying, read off its row. A view read like the
    /// two above — the node holds no cargo of its own, and
    /// <see cref="CargoCapacity"/> is the spawn input the row was sized from,
    /// not the size it is now.
    /// </summary>
    public ItemBuffer? Cargo => _machines?.CargoOf(Entity);

    /// <summary>Binds the node to the row it draws. Called before it enters the tree.</summary>
    public void Setup(MachineSystem machines, EntityId entity, Color color)
    {
        _machines = machines;
        Entity = entity;
        BodyColor = color;
    }

    public override void _Ready()
    {
        AddToGroup("machines");
        GetNode<MeshInstance3D>("Model/Body").MaterialOverride =
            new StandardMaterial3D { AlbedoColor = BodyColor };

        _sim = Simulation.For(this);
        if (_sim == null)
        {
            GD.PushWarning($"{Name} found no Simulation node; it will not be drawn.");
            return;
        }
        _sim.RegisterView(this);
    }

    /// <summary>
    /// Freeing the node retires the entity with it. Creating and destroying an
    /// entity is lifecycle, not a state write, so it is allowed here for the
    /// same reason <c>Simulation.Register</c> is — outside a tick. Nothing else
    /// knows the node went away, and a row left behind would be an invisible
    /// machine still driving the roads.
    /// </summary>
    public override void _ExitTree()
    {
        _sim?.UnregisterView(this);
        _machines?.Despawn(Entity);
        Entity = EntityId.None;
    }

    /// <summary>
    /// Pose the node between the last two sim states. Nothing here may write
    /// sim state — this runs at frame rate, which is exactly the coupling the
    /// sim loop exists to remove.
    /// </summary>
    public void Interpolate(float alpha)
    {
        if (_machines == null || !_machines.IsAlive(Entity))
        {
            return;
        }
        Position = _machines.PreviousPositionOf(Entity).Lerp(_machines.PositionOf(Entity), alpha);
        Rotation = new Vector3(0f,
            Mathf.LerpAngle(_machines.PreviousYawOf(Entity), _machines.YawOf(Entity), alpha), 0f);
    }
}
