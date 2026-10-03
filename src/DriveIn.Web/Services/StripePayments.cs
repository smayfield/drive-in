using System.Globalization;
using Microsoft.Extensions.Options;
using Stripe;

namespace DriveIn.Web.Services;

// Payments:Stripe. The keys come from SSM Parameter Store in production (see README → Payments).
public sealed class StripeOptions
{
    public const string Section = "Payments:Stripe";

    // Trimmed, and never null, so a stray space or an empty config value fails Validate clearly.
    public string PublishableKey { get; set => field = value?.Trim() ?? ""; } = "";
    public string SecretKey { get; set => field = value?.Trim() ?? ""; } = "";

    // StripePaymentProcessor hasn't been run against Stripe yet (there's no Stripe account to try it with), so only
    // test-mode keys are accepted: nothing can take real money until someone has tried the whole flow in test mode and
    // removed this check.
    public static void Validate(StripeOptions options)
    {
        // The prefix alone (a placeholder) would start fine and fail at the first sale.
        static bool IsTestKey(string key, string prefix) => key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length;
        if (!IsTestKey(options.PublishableKey, "pk_test_") || !IsTestKey(options.SecretKey, "sk_test_"))
            throw new InvalidOperationException(
                $"{Section}: set PublishableKey (pk_test_...) and SecretKey (sk_test_...). Only Stripe test-mode keys are accepted " +
                "until the Stripe integration has been tried end to end (see README → Payments).");
    }
}

// Card payments through Stripe. UNTESTED against Stripe: it compiles and follows Stripe's documented API, but there's
// no Stripe account to try it with yet. See README → Payments for what to check before relying on it.
//
// Online: the buyer's browser creates a PaymentMethod with Stripe's Payment Element (payments.js), so the card never
// touches this server, and this confirms a PaymentIntent for it in one call. At the gate (card present), the charge
// goes to the theater's card reader (ICardReader).
//
// Assumptions:
// - Cards only, and no redirect-based methods (AllowRedirects = never), so a charge finishes in this one request.
// - A card that needs 3-D Secure (requires_action) is treated as declined for now; supporting it means handing the
//   intent's client secret back to the page for stripe.handleNextAction and finishing the sale afterwards.
// - The PaymentIntent's id is the payment reference stored on the ticket or gift card.
public sealed class StripePaymentProcessor(IStripeClient client, ICardReader reader, IOptions<StripeOptions> options,
    IOptions<PaymentOptions> payments, ILogger<StripePaymentProcessor> logger) : IPaymentProcessor
{
    public bool IsAvailable => true;

    // The Element is created in the same currency the server charges in (Payments:Currency), or Stripe rejects it.
    public PaymentClient Client => new(PaymentClientKind.Stripe, options.Value.PublishableKey, payments.Value.Currency);

    // Stripe won't charge less than this (50¢ in USD and most currencies it settles in).
    public const long MinimumChargeCents = 50;

    public async Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct = default)
    {
        // E.g. a few cents left after a gift card: a clean decline (the sale is undone and the gift card made whole)
        // rather than an error from Stripe.
        if (request.AmountCents < MinimumChargeCents)
            return PaymentResult.Declined(string.Create(CultureInfo.InvariantCulture,
                $"card payments must be at least {MinimumChargeCents / 100m:0.00} {request.Currency.ToUpperInvariant()}; pay the whole amount by card instead"));
        if (request.CardPresent)
            return await reader.CollectAsync(request, ct);

        var create = new PaymentIntentCreateOptions
        {
            Amount = request.AmountCents,
            Currency = request.Currency,
            Description = request.Description,
            PaymentMethod = request.PaymentMethodId,
            Confirm = true,
            AutomaticPaymentMethods = new PaymentIntentAutomaticPaymentMethodsOptions { Enabled = true, AllowRedirects = "never" },
            Metadata = request.Metadata.ToDictionary(),
            Expand = ["payment_method"],
        };
        PaymentIntent intent;
        try
        {
            intent = await new PaymentIntentService(client).CreateAsync(create,
                new RequestOptions { IdempotencyKey = request.IdempotencyKey }, ct);
        }
        catch (StripeException ex) when (ex.StripeError?.Type == "card_error")
        {
            // A decline (insufficient funds, wrong security code...): Stripe's message is meant for the buyer.
            return PaymentResult.Declined(ex.StripeError.Message ?? "declined");
        }

        var card = intent.PaymentMethod?.Card;
        var brand = card is null ? null : CardBrands.Display(card.Brand);
        switch (intent.Status)
        {
            case "succeeded":
                return new PaymentResult(true, intent.Id, CardBrand: brand, CardLast4: card?.Last4);
            case "requires_action":
                logger.LogWarning("Stripe payment {IntentId} needs customer authentication, which isn't supported yet", intent.Id);
                return PaymentResult.Declined("your bank asked for extra verification, which we can't do yet");
            default:
                logger.LogWarning("Stripe payment {IntentId} ended {Status}", intent.Id, intent.Status);
                return PaymentResult.Declined("declined");
        }
    }
}

// The gate's card reader through Stripe Terminal. NOT IMPLEMENTED: no theater has a reader yet. The intended flow
// (server-driven, so the gate page needs no reader SDK):
//   1. Each theater registers its readers to a Terminal Location of its own (a reader id per gate, stored with the theater).
//   2. Create a PaymentIntent with payment_method_types = card_present, capture_method = automatic, the same amount,
//      metadata and idempotency key as an online charge.
//   3. POST /v1/terminal/readers/{reader}/process_payment_intent; the reader prompts the guest to tap, dip or swipe.
//   4. Wait for the outcome: the terminal.reader.action_succeeded / action_failed webhook, or poll the reader.
// Until then, Stripe gate sales are declined with a clear message; demo theaters' test sales use the dummy processor.
public sealed class StripeTerminalReader : ICardReader
{
    public Task<PaymentResult> CollectAsync(PaymentRequest request, CancellationToken ct = default) =>
        Task.FromResult(PaymentResult.Declined("card readers aren't connected yet"));
}
