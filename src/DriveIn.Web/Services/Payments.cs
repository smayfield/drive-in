using System.Security.Cryptography;

namespace DriveIn.Web.Services;

// Card details as typed at checkout. They go straight to the processor; only the brand and last four digits are kept.
public sealed record CardInput(string NameOnCard, string Number, int ExpiryMonth, int ExpiryYear, string Cvc);

// Card is what the buyer typed online; null for a card-present charge at the gate (the card is tapped or swiped on
// the processor's terminal, so its details never reach the app).
public sealed record PaymentRequest(decimal Amount, string Description, CardInput? Card);

public sealed record PaymentResult(bool Approved, string? Reference, string? DeclineReason = null)
{
    public static PaymentResult Declined(string reason) => new(false, null, reason);
}

public interface IPaymentProcessor
{
    // False when no processor is configured: nothing can be sold online.
    bool IsAvailable { get; }

    Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct = default);
}

public sealed class PaymentOptions
{
    public const string Section = "Payments";

    // "Dummy" approves every charge without taking money (development and testing only). Anything else, including
    // unset, turns online sales off until a real processor is added.
    public string Provider { get; set; } = "";
}

// Stand-in until real card processing is added: approves everything and charges nothing.
public sealed class DummyPaymentProcessor(ILogger<DummyPaymentProcessor> logger) : IPaymentProcessor
{
    public bool IsAvailable => true;

    public Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct = default)
    {
        var reference = "TEST-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        logger.LogWarning("Dummy payment processor approved {Amount} for {Description} ({Reference}); no money was taken.",
            request.Amount, request.Description, reference);
        return Task.FromResult(new PaymentResult(true, reference));
    }
}

public sealed class UnavailablePaymentProcessor : IPaymentProcessor
{
    public bool IsAvailable => false;

    public Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct = default) =>
        Task.FromResult(PaymentResult.Declined("Online payment isn't available yet."));
}

public static class Cards
{
    public static string Digits(string? number) => new((number ?? "").Where(char.IsAsciiDigit).ToArray());

    public static string Brand(string digits) => digits switch
    {
        _ when digits.StartsWith('4') => "Visa",
        _ when digits.Length >= 2 && digits[..2] is "34" or "37" => "Amex",
        _ when digits.Length >= 2 && digits[..2] is "51" or "52" or "53" or "54" or "55" => "Mastercard",
        _ when digits.Length >= 4 && int.Parse(digits[..4]) is >= 2221 and <= 2720 => "Mastercard",
        _ when digits.StartsWith("6011") || digits.StartsWith("65") || (digits.Length >= 3 && int.Parse(digits[..3]) is >= 644 and <= 649) => "Discover",
        _ => "Card",
    };

    public static bool PassesLuhn(string digits)
    {
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1 && (d *= 2) > 9)
                d -= 9;
            sum += d;
        }
        return digits.Length > 0 && sum % 10 == 0;
    }

    // Checks what can be checked before charging. Returns the card with its number reduced to digits.
    public static CardInput Validate(CardInput? card, DateTimeOffset now)
    {
        if (card is null)
            throw new AppValidationException("Enter your card details.");
        var name = (card.NameOnCard ?? "").Trim();
        if (name.Length == 0)
            throw new AppValidationException("Enter the name on the card.");
        if (name.Length > 100)
            throw new AppValidationException("The name on the card can be at most 100 characters.");
        var digits = Digits(card.Number);
        if (digits.Length is < 13 or > 19 || !PassesLuhn(digits))
            throw new AppValidationException("That card number isn't valid.");
        if (card.ExpiryMonth is < 1 or > 12 || card.ExpiryYear is < 2000 or > 2100)
            throw new AppValidationException("Enter the card's expiry month and year.");
        // A card is good through the last day of its expiry month.
        if (new DateOnly(card.ExpiryYear, card.ExpiryMonth, 1).AddMonths(1) <= DateOnly.FromDateTime(now.UtcDateTime))
            throw new AppValidationException("That card has expired.");
        var cvc = Digits(card.Cvc);
        if (cvc.Length != (Brand(digits) == "Amex" ? 4 : 3) || cvc.Length != (card.Cvc ?? "").Trim().Length)
            throw new AppValidationException("Enter the security code from the card.");
        return card with { NameOnCard = name, Number = digits, Cvc = cvc };
    }
}
