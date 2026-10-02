using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DriveIn.Web.Services;

// How a checkout page turns a card into a payment method token for the theater's processor. Card numbers and security
// codes never reach the server, not even in test mode: the browser makes the token (payments.js) and only the token
// is sent.
//   Stripe: Stripe.js mounts Stripe's Payment Element (the card fields live in Stripe's own iframe) and creates a
//           PaymentMethod (pm_...). This keeps the site in PCI DSS's SAQ A.
//   Test:   payments.js's test card form makes a fake token (pm_test_...) that the dummy processor understands.
//   None:   nothing can be paid by card.
public enum PaymentClientKind
{
    None,
    Test,
    Stripe,
}

public sealed record PaymentClient(PaymentClientKind Kind, string? PublishableKey = null, string Currency = PaymentClient.Usd)
{
    public const string Usd = "usd";

    public static readonly PaymentClient None = new(PaymentClientKind.None);
    public static readonly PaymentClient Test = new(PaymentClientKind.Test);
}

// One charge. PaymentMethodId is the token the buyer's browser made (online); null is a card-present charge at the gate,
// where the processor's card reader takes the card. IdempotencyKey is the same for every attempt at the same charge, so
// a retry can never charge twice. Metadata (ticket id, theater id...) is stored with the charge at the processor.
public sealed record PaymentRequest(long AmountCents, string Currency, string Description, string? PaymentMethodId,
    string IdempotencyKey, IReadOnlyDictionary<string, string> Metadata)
{
    public bool CardPresent => PaymentMethodId is null;
    public decimal Amount => AmountCents / 100m;

    public static long ToCents(decimal amount) => (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);
}

// CardBrand and CardLast4 are what the processor reports about the card charged (for receipts); never the number.
public sealed record PaymentResult(bool Approved, string? Reference, string? DeclineReason = null,
    string? CardBrand = null, string? CardLast4 = null)
{
    public static PaymentResult Declined(string reason) => new(false, null, reason);
}

public interface IPaymentProcessor
{
    // False when no processor is configured: nothing can be sold online.
    bool IsAvailable { get; }

    // What checkout pages need to tokenize a card for this processor.
    PaymentClient Client { get; }

    Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct = default);
}

// A card-present charge on the processor's card reader at the gate (tap, dip or swipe), so the card's details never
// reach the app. See StripeTerminalReader for the Stripe flow this stands in for.
public interface ICardReader
{
    Task<PaymentResult> CollectAsync(PaymentRequest request, CancellationToken ct = default);
}

public sealed class PaymentOptions
{
    public const string Section = "Payments";

    // "Dummy" approves every charge without taking money (development and testing only). "Stripe" uses
    // StripePaymentProcessor (test keys only until it has been tried against Stripe; see StripeOptions). Anything else,
    // including unset, turns online sales off.
    public string Provider { get; set; } = "";

    public string Currency { get; set; } = PaymentClient.Usd;
}

// The payment method tokens the server accepts from a browser. A token is opaque: the server never sees, parses or
// validates a card number. Checking the card is the processor's job (and, in test mode, payments.js's).
public static partial class PaymentTokens
{
    public const int MaxLength = 255;

    public static string Require(string? id)
    {
        var token = id?.Trim();
        if (string.IsNullOrEmpty(token))
            throw new AppValidationException("Enter your card details.");
        if (token.Length > MaxLength || !TokenPattern().IsMatch(token))
            throw new AppValidationException("Your card details didn't come through. Enter them again.");
        return token;
    }

    // Stripe's ids are pm_ plus letters and digits; test tokens add underscores (pm_test_visa_4242_...).
    [GeneratedRegex("^pm_[A-Za-z0-9_]+$")]
    private static partial Regex TokenPattern();
}

// Card brands as shown on receipts, from a processor's brand code (Stripe's codes; the test tokens use the same ones).
public static class CardBrands
{
    public static string Display(string? brand) => brand?.ToLowerInvariant() switch
    {
        "visa" => "Visa",
        "mastercard" => "Mastercard",
        "amex" => "Amex",
        "discover" => "Discover",
        "diners" => "Diners Club",
        "jcb" => "JCB",
        "unionpay" => "UnionPay",
        _ => "Card",
    };
}

// The test tokens payments.js's test card form makes: pm_test_{brand}_{last4}_{random}, or pm_test_decline_{last4}_...
// for Stripe's "always declines" test number (4000 0000 0000 0002), so declines can be tried too.
public static partial class TestCardTokens
{
    public sealed record TestCard(string Brand, string Last4, bool Declines);

    public static TestCard? Parse(string? token)
    {
        var m = token is null ? null : Pattern().Match(token);
        if (m is not { Success: true })
            return null;
        var declines = m.Groups["brand"].Value == "decline";
        return new TestCard(declines ? "Card" : CardBrands.Display(m.Groups["brand"].Value), m.Groups["last4"].Value, declines);
    }

    [GeneratedRegex("^pm_test_(?<brand>[a-z]+)_(?<last4>[0-9]{4})_[A-Za-z0-9]+$")]
    private static partial Regex Pattern();
}

// Stand-in until real card processing is added: approves everything and charges nothing. Online it takes the test
// card form's tokens (and declines the "decline" test card); at the gate it approves the card-present charge.
public sealed class DummyPaymentProcessor(ILogger<DummyPaymentProcessor> logger) : IPaymentProcessor
{
    public bool IsAvailable => true;

    public PaymentClient Client => PaymentClient.Test;

    public Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct = default)
    {
        TestCardTokens.TestCard? card = null;
        if (!request.CardPresent)
        {
            card = TestCardTokens.Parse(request.PaymentMethodId);
            if (card is null)
                return Task.FromResult(PaymentResult.Declined("this isn't a test card"));
            if (card.Declines)
                return Task.FromResult(PaymentResult.Declined("your card was declined (test card)"));
        }
        var reference = "TEST-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        logger.LogWarning("Dummy payment processor approved {Amount} for {Description} ({Reference}); no money was taken.",
            request.Amount, request.Description, reference);
        return Task.FromResult(new PaymentResult(true, reference, CardBrand: card?.Brand, CardLast4: card?.Last4));
    }
}

public sealed class UnavailablePaymentProcessor : IPaymentProcessor
{
    public bool IsAvailable => false;

    public PaymentClient Client => PaymentClient.None;

    public Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct = default) =>
        Task.FromResult(PaymentResult.Declined("Online payment isn't available yet."));
}
