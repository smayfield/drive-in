using System.ComponentModel.DataAnnotations;

namespace DriveIn.Web.Data;

// One spot at one showing, bought online. A ticket starts as a short hold while the buyer pays; at most one ticket
// per spot per showing exists at a time (a unique index), so the first to hold a spot gets it. Expired holds are
// deleted (TicketSalesService.ReleaseExpiredHoldsAsync). A sold ticket is only good for its showing, and sales are
// final: there's no refund or cancel.
public class Ticket
{
    public const int HoldMinutes = 10;

    public int Id { get; set; }

    public int ShowtimeId { get; set; }
    public Showtime? Showtime { get; set; }

    // Row (from the screen) and spot (left to right facing the screen), both from 1, as in Screen.RowSpots.
    public int Row { get; set; }
    public int Spot { get; set; }

    // The label when it was held, e.g. "B7"; screens can't relabel spots that have upcoming tickets.
    [MaxLength(10)]
    public string SpotLabel { get; set; } = "";

    // What the guest is driving; a Large vehicle may only have a spot marked for large vehicles (Screen.LargeSpots).
    public VehicleSize VehicleSize { get; set; }

    // The buyer. Null once their account is deleted; the sale is kept.
    public string? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public TicketStatus Status { get; set; }

    // While Held: when the hold lapses and the spot goes back on sale.
    public DateTimeOffset? HeldUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Changed on every update so concurrent changes (paying vs. the hold expiring) can't both win.
    public Guid Stamp { get; set; } = Guid.NewGuid();

    // Sold by a theater in demo mode: a trial run through the dummy payment processor, not a real sale. Test
    // tickets are deleted when the theater goes live.
    public bool IsTest { get; set; }

    // --- Set when payment starts (Paying), kept when sold ---

    // The processor's idempotency key for this charge (TicketSalesService.TicketCharge), and when it started. A charge
    // whose outcome the server didn't hear (a crash, a timeout) is looked up with the processor by this key and the
    // sale finished or undone (PaymentReconcileService, or a processor webhook).
    [MaxLength(100)] public string? PaymentKey { get; set; }
    public DateTimeOffset? PaymentStartedAt { get; set; }

    // --- Set when payment starts and finished when sold (undone if it falls through) ---

    public DateTimeOffset? SoldAt { get; set; }

    // Where the receipt was sent.
    [MaxLength(256)]
    public string? Email { get; set; }

    // The price option bought, as it was at the time; later price changes don't touch sold tickets.
    [MaxLength(60)]
    public string? OptionName { get; set; }
    public decimal OptionPrice { get; set; }
    public List<TicketAddOn> AddOns { get; set; } = [];
    public decimal Total { get; set; }

    // From the payment processor. Card numbers are never stored.
    [MaxLength(20)] public string? CardBrand { get; set; }
    [MaxLength(4)] public string? CardLast4 { get; set; }
    [MaxLength(100)] public string? PaymentReference { get; set; }

    // Part of Total paid from a gift card (see GiftCard); the rest, CardAmount, went on the card. Set (with the gift
    // card's balance reduced) when payment starts and undone if the card is declined.
    public int? GiftCardId { get; set; }
    public GiftCard? GiftCard { get; set; }
    [MaxLength(4)] public string? GiftCardLast4 { get; set; }
    public decimal GiftCardAmount { get; set; }

    public decimal CardAmount => Total - GiftCardAmount;

    // Random and unguessable: the QR code on the receipt links to tickets/{Code}.
    [MaxLength(32)]
    public string? Code { get; set; }

    // Short enough to read out at the gate, e.g. "K7QM". Unique among the theater's tickets for showings that haven't
    // ended when it's issued (checked, not enforced by the DB), so gate lookups also match on the theater and date.
    [MaxLength(ShortCodes.Length)]
    public string? ShortCode { get; set; }

    // Sold at the gate rather than online: no buyer account (UserId is null), no receipt, paid on the terminal.
    // Stored rather than derived from SoldById, which is cleared if the employee's account is deleted.
    public bool SoldAtGate { get; set; }

    // Gate sales: the employee who sold it, while their account exists.
    public string? SoldById { get; set; }
    public ApplicationUser? SoldBy { get; set; }

    // Free admission (a "comp") given by an employee to a guest: a sold ticket at no charge, with no buyer account.
    // SoldById is the employee who gave it. See TicketSalesService.Comps.
    public bool IsComp { get; set; }
    [MaxLength(100)] public string? GuestName { get; set; }
    [MaxLength(500)] public string? CompReason { get; set; }

    // Set when gate staff let the car in; a ticket admits once.
    public DateTimeOffset? AdmittedAt { get; set; }
}

// Stored by name, so members can be added but not renamed.
public enum TicketStatus
{
    Held,
    // Being charged. Never swept as an expired hold: if the server dies mid-charge we can't tell whether the card was
    // charged, so the spot stays off sale until PaymentReconcileService (or a processor webhook) asks the processor
    // by PaymentKey, then finishes the sale or puts the spot back, rather than risk selling it twice.
    Paying,
    // A free-admission request waiting for approval. Holds the spot with no expiry (never swept) until it is
    // approved (becomes Sold) or denied (deleted).
    Pending,
    Sold,
}

// An add-on as it applied to a sold ticket.
public class TicketAddOn
{
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }

    public int Position { get; set; }

    [Required, MaxLength(60)]
    public string Name { get; set; } = "";

    public AddOnKind Kind { get; set; }

    // As configured: dollars, or percent for PercentDiscount.
    public decimal Amount { get; set; }

    // What it did to the total: positive for fees, negative for discounts.
    public decimal Effect { get; set; }
}

public static class ShortCodes
{
    public const int Length = 4;

    // No 0/O, 1/I/L or 5/S, which are easy to mix up when read aloud or off a phone.
    public const string Alphabet = "ABCDEFGHJKMNPQRTUVWXYZ2346789";

    public static string New() =>
        new(System.Security.Cryptography.RandomNumberGenerator.GetItems<char>(Alphabet, Length));

    // What a guest reads out: case and spaces don't matter.
    public static string? Normalize(string? input)
    {
        var s = new string((input ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
        return s.Length == Length && s.All(Alphabet.Contains) ? s : null;
    }
}
