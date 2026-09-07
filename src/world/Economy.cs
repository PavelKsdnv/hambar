using System.Globalization;
using Godot;

namespace Arable;

/// <summary>
/// The player's money, and <b>the one thing that owns it</b>. Every amount the
/// game moves — build costs today, wages in M5, sales in M7 — goes through this
/// node, so there is exactly one balance, one place it changes, and one place
/// to serialize when saves arrive.
///
/// It is deliberately a <i>stub economy</i>: a number with a starting value and
/// two ways to move it. Nothing here decides what anything is worth. Prices
/// live on the tools that charge them (<see cref="BuildTool.CostPerCell"/>),
/// refunds on the seam that pays them (<c>BulldozeTool.RefundFor</c>), and the
/// market that will make either of them mean something is M7's. What this issue
/// is about is the plumbing: money that can be spent, refused, credited and
/// read.
///
/// <b>Spending is asked for, never assumed.</b> <see cref="TrySpend"/> is the
/// only way out of the account and it can say no, which is what keeps the
/// balance from ever going negative however a caller is written. A placement is
/// refused *before* that, in <see cref="PlacementRules"/> — see
/// <see cref="PlacementBudget"/> for why affordability is a validation input
/// and not a check bolted on after the plan — so in practice
/// <see cref="TrySpend"/> only ever refuses a caller that skipped the plan.
///
/// The exported <see cref="StartingBalance"/> and <see cref="Readout"/> label
/// are wired in Main.tscn: playtests will want to move the starting number
/// without a rebuild, and the readout is the whole of the money UI for now.
/// </summary>
public partial class Economy : Node, IHashableState
{
    /// <summary>
    /// What the player starts with. A placeholder — the number a playtest will
    /// argue about first, which is exactly why it is exported rather than a
    /// constant.
    /// </summary>
    [Export] public int StartingBalance { get; set; } = 5000;

    /// <summary>
    /// Label the balance is written to; null is legal (a scene with no HUD,
    /// which is how the account keeps working in tests and dev scenes).
    /// </summary>
    [Export] public Label? Readout { get; set; }

    /// <summary>The money the player has. Never negative.</summary>
    public int Balance { get; private set; }

    /// <summary>What the readout says — the label's text, kept for the tests.</summary>
    public string Text { get; private set; } = string.Empty;

    private Simulation? _sim;

    /// <summary>
    /// Opens the account. <see cref="StartingBalance"/> is inspector input, so
    /// this is the boundary a bad number actually arrives at — the one place in
    /// the game that can hand <see cref="SetBalance"/> a negative, since
    /// <see cref="TrySpend"/> and <see cref="Credit"/> both vet their own. A bad
    /// export should leave the player broke and the log loud, not take the scene
    /// down on load.
    /// </summary>
    public override void _Ready()
    {
        if (StartingBalance < 0)
        {
            GD.PushWarning(
                $"Economy: StartingBalance is negative ({StartingBalance}); clamped to 0.");
        }
        SetBalance(StartingBalance);

        // The balance is world state — M10 saves it, and a determinism run that
        // did not cover it would call two worlds identical while the player was
        // broke in one of them.
        _sim = Simulation.For(this);
        _sim?.RegisterState(this);
    }

    public override void _ExitTree() => _sim?.UnregisterState(this);

    /// <summary>
    /// What one unit of grain sells for at the depot (#38). Exported, like
    /// <see cref="StartingBalance"/>, so a playtest can retune it without a
    /// rebuild — there is no market yet to derive it from.
    /// </summary>
    [Export] public int GrainPrice { get; set; } = 8;

    /// <summary>The name the balance is filed under in a state hash.</summary>
    public string StateName => "economy";

    /// <summary>
    /// One integer. The readout text is a view of it and is deliberately not
    /// hashed — a formatted string is the last place a divergence should be
    /// discovered.
    /// </summary>
    public void HashState(StateHash hash) => hash.Write(Balance);

    /// <summary>Whether that much could be spent right now.</summary>
    public bool CanAfford(int amount) => amount <= Balance;

    /// <summary>
    /// Takes <paramref name="amount"/> out of the account, or refuses and
    /// changes nothing. A free placement (<paramref name="amount"/> zero) is
    /// always allowed and moves nothing; a negative amount is *not* a credit
    /// through the wrong door — money only ever comes in through
    /// <see cref="Credit"/>.
    /// </summary>
    public bool TrySpend(int amount)
    {
        if (amount <= 0)
        {
            return true;
        }
        if (amount > Balance)
        {
            return false;
        }
        SetBalance(Balance - amount);
        return true;
    }

    /// <summary>
    /// Puts money in: the bulldozer's refunds today, M5's deliveries and M7's
    /// sales later. Non-positive amounts are ignored, so a "refund" of zero —
    /// which is what the M7 stub pays — is a genuine no-op rather than a
    /// balance write.
    /// </summary>
    public void Credit(int amount)
    {
        if (amount <= 0)
        {
            return;
        }
        SetBalance(Balance + amount);
    }

    /// <summary>
    /// <b>M7 SWAP POINT.</b> What a unit of <paramref name="type"/> is worth
    /// right now — a flat constant per type today, the live price series
    /// (demand shocks, margins) M7 replaces this body with. Every sale in the
    /// game is required to go through this one function, named here so the
    /// swap has exactly one place to happen: nothing outside <see cref="Sell"/>
    /// may read <see cref="GrainPrice"/> directly.
    /// </summary>
    public int PriceOf(ItemType type) => type == ItemTypes.Grain ? GrainPrice : 0;

    /// <summary>
    /// The depot's whole economy: credits <see cref="PriceOf"/> times
    /// <paramref name="quantity"/>. The caller (<c>MachineSystem.RunHaulOrder</c>)
    /// has already removed the units from the depot's <see cref="Structure.Storage"/>
    /// — this only ever moves money, the same split <see cref="Credit"/> keeps
    /// from a build refund.
    /// </summary>
    public void Sell(ItemType type, int quantity)
    {
        if (quantity <= 0)
        {
            return;
        }
        Credit(PriceOf(type) * quantity);
    }

    /// <summary>
    /// Sets the balance outright and refreshes the readout. The one write the
    /// other two share, and the entry point a save-load (or a test that wants
    /// the player broke) needs — not a way for game code to award itself money,
    /// which is <see cref="Credit"/>.
    ///
    /// This is also the only door a number can come through without
    /// <see cref="TrySpend"/> having vetted it — a mistyped
    /// <see cref="StartingBalance"/> in the inspector, a corrupt save — so it is
    /// where "never negative" is actually enforced, by clamping rather than
    /// throwing. It clamps <i>quietly</i>: the invariant has to hold however
    /// this is called, but complaining about a bad number is the job of whoever
    /// accepted it from outside (see <see cref="_Ready"/> for the exported one).
    /// Enforcement everywhere, noise only at the boundary — which is what lets a
    /// caller deliberately test the clamp without the log crying wolf.
    /// </summary>
    public void SetBalance(int balance)
    {
        if (balance < 0)
        {
            balance = 0;
        }
        Balance = balance;
        Text = Describe(balance);
        if (Readout != null)
        {
            Readout.Text = Text;
        }
    }

    /// <summary>
    /// The balance as the HUD shows it. Formatted with
    /// <see cref="CultureInfo.InvariantCulture"/>, like the fertility readout,
    /// so the text is the same on every machine and a test can predict it.
    /// </summary>
    public static string Describe(int balance) =>
        "money: " + balance.ToString("N0", CultureInfo.InvariantCulture);
}
