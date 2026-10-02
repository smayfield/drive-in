using Microsoft.Extensions.Options;
using Stripe;

namespace DriveIn.Web.Services;

// Payments:Stripe. The keys come from SSM Parameter Store in production (see README → Payments).
public sealed class StripeOptions
{
    public const string Section = "Payments:Stripe";

    public string PublishableKey { get; set; } = "";
    public string SecretKey { get; set; } = "";

    // The signing secret (whsec_...) of the webhook endpoint at /payments/stripe/webhook. Optional: without it the
    // endpoint answers 404 and PaymentReconcileService alone finishes charges whose outcome wasn't heard.
    public string WebhookSecret { get; set; } = "";

    // StripePaymentProcessor hasn't been run against Stripe yet (there's no Stripe account to try it with), so only
    // test-mode keys are accepted: nothing can take real money until someone has tried the whole flow in test mode and
    // removed this check.
    public static void Validate(StripeOptions options)
    {
        if (!options.PublishableKey.StartsWith("pk_test_", StringComparison.Ordinal)
            || !options.SecretKey.StartsWith("sk_test_", StringComparison.Ordinal))
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
// - Its metadata carries the idempotency key as payment_key, so a charge whose outcome the server didn't hear can be
//   found again with the Search API (which can lag by about a minute; PaymentReconcileService waits longer than that).
public sealed class StripePaymentProcessor(IStripeClient client, ICardReader reader, IOptions<StripeOptions> options,
    IOptions<PaymentOptions> payments, ILogger<StripePaymentProcessor> logger) : IPaymentProcessor
{
    public bool IsAvailable => true;

    // The Element is created in the same currency the server charges in (Payments:Currency), or Stripe rejects it.
    public PaymentClient Client => new(PaymentClientKind.Stripe, options.Value.PublishableKey, payments.Value.Currency);

    public async Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct = default)
    {
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
            Metadata = new Dictionary<string, string>(request.Metadata) { [PaymentKeyMetadata] = request.IdempotencyKey },
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

        switch (intent.Status)
        {
            case "succeeded":
                return Succeeded(intent);
            case "requires_action":
                logger.LogWarning("Stripe payment {IntentId} needs customer authentication, which isn't supported yet", intent.Id);
                return PaymentResult.Declined("your bank asked for extra verification, which we can't do yet");
            default:
                logger.LogWarning("Stripe payment {IntentId} ended {Status}", intent.Id, intent.Status);
                return PaymentResult.Declined("declined");
        }
    }

    // By the intent's id when known (a webhook gives it), else by searching for the key in its metadata.
    public async Task<PaymentStatus> GetStatusAsync(PaymentLookup lookup, CancellationToken ct = default)
    {
        var service = new PaymentIntentService(client);
        PaymentIntent? intent;
        if (lookup.Reference is { } id && id.StartsWith("pi_", StringComparison.Ordinal))
            intent = await service.GetAsync(id, new PaymentIntentGetOptions { Expand = ["payment_method"] }, cancellationToken: ct);
        else
        {
            var found = await service.SearchAsync(new PaymentIntentSearchOptions
            {
                Query = $"metadata['{PaymentKeyMetadata}']:'{lookup.IdempotencyKey.Replace("'", "")}'",
                Expand = ["data.payment_method"],
            }, cancellationToken: ct);
            // One key is one charge (Stripe returns the same intent for a repeated key), so there's at most one.
            intent = found.Data.OrderByDescending(i => i.Created).FirstOrDefault();
        }
        if (intent is null)
            return PaymentStatus.NotFound;
        return intent.Status switch
        {
            "succeeded" => new PaymentStatus(PaymentState.Succeeded, Succeeded(intent)),
            "processing" or "requires_capture" or "requires_confirmation" => PaymentStatus.Pending,
            // requires_payment_method (declined), canceled, and requires_action (3-D Secure, which checkout can't finish).
            _ => new PaymentStatus(PaymentState.Failed, PaymentResult.Declined(intent.LastPaymentError?.Message ?? intent.Status)),
        };
    }

    public const string PaymentKeyMetadata = "payment_key";

    private static PaymentResult Succeeded(PaymentIntent intent)
    {
        var card = intent.PaymentMethod?.Card;
        return new PaymentResult(true, intent.Id, CardBrand: card is null ? null : CardBrands.Display(card.Brand), CardLast4: card?.Last4);
    }
}

// POST /payments/stripe/webhook: Stripe tells us a PaymentIntent succeeded or failed. Only signed events are accepted
// (Stripe-Signature, checked with Payments:Stripe:WebhookSecret); without a secret the endpoint is off (404). The event
// is only a nudge: TicketSalesService.SettlePaymentAsync asks Stripe for the intent itself and finishes or undoes the
// sale idempotently, so a repeated, late or out-of-order event is harmless. UNTESTED against Stripe; in the Dashboard,
// point a webhook at https://drive-in.online/payments/stripe/webhook for payment_intent.succeeded and
// payment_intent.payment_failed.
public static class StripeWebhook
{
    public const string Path = "/payments/stripe/webhook";

    // Stripe's events are a few kilobytes.
    private const int MaxBodyBytes = 256 * 1024;

    public static async Task<IResult> HandleAsync(HttpRequest request, string? secret, TicketSalesService sales, string baseUri,
        ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(secret))
            return Results.NotFound();
        if (request.ContentLength > MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        string signature = request.Headers["Stripe-Signature"].ToString();
        if (signature.Length == 0)
            return Results.BadRequest(); // Stripe.net throws a NullReferenceException on a missing header
        using var reader = new StreamReader(request.Body);
        var json = await reader.ReadToEndAsync(ct);
        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(json, signature, secret, throwOnApiVersionMismatch: false);
        }
        catch (StripeException ex)
        {
            logger.LogWarning("Refused a Stripe webhook: {Reason}", ex.Message);
            return Results.BadRequest();
        }
        if (stripeEvent.Type is EventTypes.PaymentIntentSucceeded or EventTypes.PaymentIntentPaymentFailed
            && stripeEvent.Data.Object is PaymentIntent intent
            && intent.Metadata is not null
            && intent.Metadata.TryGetValue(StripePaymentProcessor.PaymentKeyMetadata, out var key))
            await sales.SettlePaymentAsync(key, intent.Id, baseUri, ct);
        return Results.Ok();
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
