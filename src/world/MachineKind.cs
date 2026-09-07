using Godot;

namespace Arable;

/// <summary>
/// The three vehicles a farm can buy. The kind is a machine's <b>identity</b>:
/// it decides what the thing costs, how it drives, how much it can carry and —
/// from #34 onwards — which orders it will accept. Everything else about a
/// machine is position and load.
///
/// Three and no more, on purpose. The chain M5 has to be able to run end to end
/// is work a field, cut it, haul what came off it, and that is exactly one
/// vehicle each. A roster with a fourth entry before there is a fourth job to
/// do is content nothing can consume.
/// </summary>
public enum MachineKind
{
    /// <summary>Ploughs and sows. Pulls implements; carries nothing.</summary>
    Tractor,

    /// <summary>Cuts a ripe field into its own hopper. Slow, and it carries what it cut.</summary>
    Harvester,

    /// <summary>Moves goods between a field, a store and (in M7) a buyer. The hauler.</summary>
    Truck,
}

/// <summary>
/// Everything that differs between one <see cref="MachineKind"/> and another,
/// as one immutable row. Handed to <c>Machine.ApplyKind</c>, which writes it
/// <i>through</i> the scene's exports rather than past them, so spawn input
/// still comes from exactly one place.
/// </summary>
/// <param name="Name">What the vehicle is called in a log line or a HUD.</param>
/// <param name="Price">What buying one costs, through <see cref="Economy.TrySpend"/>.</param>
/// <param name="Speed">Metres a second along a route.</param>
/// <param name="TurnSpeed">How sharply it comes round onto a new heading.</param>
/// <param name="CargoCapacity">Units it can hold. Zero means it cannot haul at all.</param>
/// <param name="WorksFields">Whether field work is something it can be ordered to do.</param>
/// <param name="BodyColor">Dev art: the one thing that tells two of them apart on screen.</param>
public readonly record struct MachineSpec(
    string Name,
    int Price,
    float Speed,
    float TurnSpeed,
    int CargoCapacity,
    bool WorksFields,
    Color BodyColor);

/// <summary>
/// The vehicle roster, as a static table.
///
/// <b>Why a table and not fifteen inspector exports.</b> Every other tunable in
/// the game is an export because a playtest argues about it in isolation — one
/// starting balance, one wage, one structure capacity. Three kinds times five
/// numbers is not a knob, it is content: the numbers only mean anything
/// relative to each other, and a truck that is faster than a tractor is a fact
/// about the roster rather than about the truck. It lives in code today for the
/// same reason the crop schedule did before it moved: there is no data-file
/// pipeline yet, and inventing one for three rows would be the wrong milestone
/// to do it in. M6, which adds a roster of <i>buildings</i> with the same
/// shape, is where both should become data.
///
/// <b>What a kind does not carry.</b> No order list. The two facts #34 needs to
/// refuse an order — can this thing work a field, and can it hold anything —
/// are here as <see cref="MachineSpec.WorksFields"/> and a cargo capacity that
/// is zero for a vehicle that cannot haul. The orders themselves are #34's to
/// name, and a half-guessed enum of them here would be a taxonomy that issue
/// then has to argue with.
/// </summary>
public static class MachineKinds
{
    /// <summary>How many kinds there are — the modulus for cycling through them.</summary>
    public const int Count = 3;

    // Prices are read against a 5,000 opening balance and a 500 hire fee: one
    // vehicle plus the person to drive it is roughly a third of the farm's
    // opening money, so the second one is a decision and the third is a plan.
    // The harvester is the dearest because it is the only way to turn a ripe
    // field into goods, and the truck is the fastest because a haul is a round
    // trip and everything else waits on it.
    private static readonly MachineSpec[] Specs =
    [
        new("tractor", 1200, 5f, 8f, 0, true, new Color(0.75f, 0.22f, 0.17f)),
        new("harvester", 2000, 4f, 6f, 40, true, new Color(0.85f, 0.70f, 0.20f)),
        new("truck", 1500, 8f, 7f, 120, false, new Color(0.20f, 0.42f, 0.75f)),
    ];

    /// <summary>
    /// The row for a kind. An unknown value answers the tractor rather than
    /// throwing, the way <c>GetTerrain</c> answers <c>OutOfBounds</c>: a bad
    /// enum out of a corrupt save should leave the player with an odd vehicle,
    /// not take the load down.
    /// </summary>
    public static MachineSpec Spec(MachineKind kind) =>
        Specs[(int)kind >= 0 && (int)kind < Specs.Length ? (int)kind : 0];

    /// <summary>What buying one costs.</summary>
    public static int PriceOf(MachineKind kind) => Spec(kind).Price;

    /// <summary>What the kind is called, for a log line or a HUD.</summary>
    public static string Name(MachineKind kind) => Spec(kind).Name;

    /// <summary>The kind at an index, wrapped — how the dev key cycles the roster.</summary>
    public static MachineKind At(int index) =>
        (MachineKind)(((index % Count) + Count) % Count);
}
