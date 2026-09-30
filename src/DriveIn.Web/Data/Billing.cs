using System.ComponentModel.DataAnnotations;

namespace DriveIn.Web.Data;

// A live theater's plan: what it pays per screen for each calendar month its season touches (the Terms bill whole
// months, in advance). The price is locked in when the subscription starts; an admin can change it later, with the
// notice the Terms require. Cancelling stops billing after the current month; invoices already issued stand.
public class Subscription
{
    public const string StandardPlan = "Standard";

    public int Id { get; set; }

    public int TheaterId { get; set; }
    public Theater? Theater { get; set; }

    [MaxLength(40)] public string Plan { get; set; } = StandardPlan;

    public decimal PricePerScreenPerMonth { get; set; }

    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;

    // The theater's local date billing starts: its month is the first one billed, in full.
    public DateOnly StartedOn { get; set; }

    // The first day of the last month billed, once cancelled. Null while it runs.
    public DateOnly? EndsAfterMonth { get; set; }

    // Where invoices and receipts go; the owner's email when unset.
    [MaxLength(256), EmailAddress] public string? BillingEmail { get; set; }

    public DateTimeOffset? CanceledAt { get; set; }
    public string? CanceledById { get; set; }
    public ApplicationUser? CanceledBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Invoice> Invoices { get; set; } = [];

    public bool IsActive => Status == SubscriptionStatus.Active;
}

// Stored by name, so members can be added but not renamed.
public enum SubscriptionStatus
{
    Active,
    Canceled,
}

// One month's bill for a subscription. A background job drafts it; an admin reviews it (adding credits or adjustments)
// and issues it, which gives it a number and emails it. Payments are recorded by an admin until a payment processor is
// wired up. The bill-to details are copied in when drafted and again when issued, so an invoice reads the same later
// even if the theater is renamed or deleted.
public class Invoice
{
    public int Id { get; set; }

    // Assigned when issued (so voided drafts leave no gaps in the sequence); unique.
    public int? Number { get; set; }

    // Null once the theater (and so its subscription) has been deleted; the invoice is kept as a record.
    public int? SubscriptionId { get; set; }
    public Subscription? Subscription { get; set; }
    public int? TheaterId { get; set; }
    public Theater? Theater { get; set; }

    // The first day of the month billed.
    public DateOnly PeriodMonth { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    [MaxLength(200)] public string TheaterName { get; set; } = "";
    [MaxLength(600)] public string? TheaterAddress { get; set; }
    [MaxLength(200)] public string? BillToName { get; set; }
    [MaxLength(256)] public string? BillToEmail { get; set; }

    public decimal Total { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    public DateOnly? DueOn { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    [MaxLength(500)] public string? VoidReason { get; set; }

    public List<InvoiceLine> Lines { get; set; } = [];
    public List<InvoicePayment> Payments { get; set; } = [];

    public decimal AmountPaid => Payments.Sum(p => p.Amount);
    public decimal Balance => Total - AmountPaid;

    public string DisplayNumber => FormatNumber(Number);

    // Issued and unpaid after its due date (a date in the theater's time zone, compared with today there).
    public bool IsOverdue(DateOnly today) => Status == InvoiceStatus.Issued && DueOn is DateOnly due && due < today;

    public static string FormatNumber(int? number) => number is int n ? $"INV-{n:000000}" : "Draft";
}

// Stored by name, so members can be added but not renamed.
public enum InvoiceStatus
{
    Draft,
    Issued,
    Paid,
    Void,
}

public class InvoiceLine
{
    public int Id { get; set; }

    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    public int SortOrder { get; set; }

    [MaxLength(200)] public string Description { get; set; } = "";

    public int Quantity { get; set; } = 1;

    // Negative for a credit.
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
}

// Money received against an invoice, recorded by an admin. Partial payments are allowed.
public class InvoicePayment
{
    public int Id { get; set; }

    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    public decimal Amount { get; set; }

    public PaymentMethod Method { get; set; }

    // A check number, bank transfer reference and so on.
    [MaxLength(100)] public string? Reference { get; set; }

    public DateOnly ReceivedOn { get; set; }

    public string? RecordedById { get; set; }
    public ApplicationUser? RecordedBy { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

// Stored by name, so members can be added but not renamed.
public enum PaymentMethod
{
    Check,
    BankTransfer,
    Card,
    Other,
}

public static class PaymentMethods
{
    public static string Describe(PaymentMethod method) => method switch
    {
        PaymentMethod.BankTransfer => "Bank transfer",
        PaymentMethod.Card => "Card",
        PaymentMethod.Check => "Check",
        _ => "Other",
    };
}
