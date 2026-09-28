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

    // The buyer. Null once their account is deleted; the sale is kept.
    public string? UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public TicketStatus Status { get; set; }

    // While Held: when the hold lapses and the spot goes back on sale.
    public DateTimeOffset? HeldUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Changed on every update so concurrent changes (paying vs. the hold expiring) can't both win.
    public Guid Stamp { get; set; } = Guid.NewGuid();

    // --- Set when sold ---

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

    // Random and unguessable: the QR code on the receipt links to tickets/{Code}.
    [MaxLength(32)]
    public string? Code { get; set; }

    // Set when gate staff let the car in; a ticket admits once.
    public DateTimeOffset? AdmittedAt { get; set; }
}

// Stored by name, so members can be added but not renamed.
public enum TicketStatus
{
    Held,
    Paying,
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
