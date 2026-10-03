using System.ComponentModel.DataAnnotations;

namespace DriveIn.Web.Data;

// One attempt to buy a gift card, saved before the card is charged so a charge whose outcome the server didn't hear
// (a crash, a timeout) can be looked up with the processor by PaymentKey and the gift card issued or the purchase
// given up (PaymentReconcileService, or a processor webhook). On success it points at the GiftCard it issued.
public class GiftCardPurchase
{
    public int Id { get; set; }

    public int TheaterId { get; set; }
    public Theater? Theater { get; set; }

    // The buyer. Null once their account is deleted.
    public string? PurchaserId { get; set; }
    public ApplicationUser? Purchaser { get; set; }
    [MaxLength(256)] public string? PurchaserEmail { get; set; }
    [MaxLength(100)] public string? PurchaserName { get; set; }

    public decimal Amount { get; set; }

    [MaxLength(100)] public string? RecipientName { get; set; }
    [MaxLength(256)] public string? RecipientEmail { get; set; }
    [MaxLength(500)] public string? Message { get; set; }

    public GiftCardPurchaseStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }

    // A demo theater's: charged through the dummy processor, and the card it issues is a test card.
    public bool IsTest { get; set; }

    public int? GiftCardId { get; set; }
    public GiftCard? GiftCard { get; set; }

    // Changed on every update so the checkout, the reconciler and a webhook can't all finish it at once.
    public Guid Stamp { get; set; } = Guid.NewGuid();

    // The same for every attempt at this charge, so it can never be charged twice.
    public string PaymentKey => KeyFor(Id);

    public const string KeyPrefix = "giftcard-";

    public static string KeyFor(int id) => $"{KeyPrefix}{id}";
}

// Stored by name.
public enum GiftCardPurchaseStatus
{
    Paying,
    Completed,
    Failed,
}
