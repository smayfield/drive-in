using System.ComponentModel.DataAnnotations;

namespace DriveIn.Web.Data;

// A gift card sold by a theater (when Theater.GiftCardsEnabled) and spent toward tickets at that theater, online or at
// the gate. It's a bearer instrument: whoever presents Code can spend it, so the code is random and unguessable and is
// only ever shown to the buyer (and emailed to them and the recipient); staff see its last four characters. It has no
// expiry, and its remaining Balance carries over from one ticket to the next. Sales are final: no cash-out or refund.
public class GiftCard
{
    public const decimal MinAmount = 5m;
    public const decimal MaxAmount = 500m;

    public int Id { get; set; }

    public int TheaterId { get; set; }
    public Theater? Theater { get; set; }

    // GiftCardCodes.Length characters from ShortCodes.Alphabet, no dashes. Unique across all theaters.
    [MaxLength(GiftCardCodes.Length)]
    public string Code { get; set; } = "";

    public decimal InitialAmount { get; set; }
    public decimal Balance { get; set; }

    public DateTimeOffset PurchasedAt { get; set; }

    // The buyer. Null once their account is deleted; the card and its balance stay valid.
    public string? PurchaserId { get; set; }
    public ApplicationUser? Purchaser { get; set; }
    [MaxLength(256)] public string? PurchaserEmail { get; set; }

    // Optional: who it's for, and a note included in their email.
    [MaxLength(100)] public string? RecipientName { get; set; }
    [MaxLength(256)] public string? RecipientEmail { get; set; }
    [MaxLength(500)] public string? Message { get; set; }

    // From the payment processor. Card numbers are never stored.
    [MaxLength(20)] public string? CardBrand { get; set; }
    [MaxLength(4)] public string? CardLast4 { get; set; }
    [MaxLength(100)] public string? PaymentReference { get; set; }

    // Sold by a theater in demo mode: only spendable at that theater, and deleted when it goes live (with test tickets).
    public bool IsTest { get; set; }

    // Changed on every update so two tickets can't spend the same balance at once.
    public Guid Stamp { get; set; } = Guid.NewGuid();

    public List<GiftCardTransaction> Transactions { get; set; } = [];

    // What staff see instead of the code.
    public string Last4 => Code.Length >= 4 ? Code[^4..] : Code;
}

// One change to a gift card's balance. Amount is signed: positive for the purchase and for money put back when a
// payment fell through, negative for a redemption.
public class GiftCardTransaction
{
    public long Id { get; set; }

    public int GiftCardId { get; set; }
    public GiftCard? GiftCard { get; set; }

    public GiftCardTransactionKind Kind { get; set; }
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }

    // The ticket it paid for (or was put back from). Not a foreign key: tickets can be deleted with their test data.
    public int? TicketId { get; set; }

    public DateTimeOffset At { get; set; }
}

// Stored by name, so members can be added but not renamed.
public enum GiftCardTransactionKind
{
    Purchase,
    Redeem,
    // A redemption undone because the rest of the payment didn't go through.
    Restore,
}

public static class GiftCardCodes
{
    // 16 characters from a 29-character alphabet is about 78 bits: not guessable.
    public const int Length = 16;

    public static string New() =>
        new(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(ShortCodes.Alphabet, Length));

    // What someone types or pastes: case, spaces and dashes don't matter. Null when it can't be a gift card code.
    public static string? Normalize(string? input)
    {
        var s = new string((input ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
        return s.Length == Length && s.All(ShortCodes.Alphabet.Contains) ? s : null;
    }

    // "K7QM-2X9T-WD4R-N3PA"
    public static string Format(string code) =>
        string.Join('-', Enumerable.Range(0, (code.Length + 3) / 4).Select(i => code.Substring(i * 4, Math.Min(4, code.Length - i * 4))));
}
