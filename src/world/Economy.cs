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
public partial class Economy : Node
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
    }

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
