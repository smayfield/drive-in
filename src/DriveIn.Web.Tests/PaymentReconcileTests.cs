using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static DriveIn.Web.Authorization.TheaterPermissions;
using static DriveIn.Web.Tests.TicketSalesTests;

namespace DriveIn.Web.Tests;

// Charges whose outcome checkout never heard (the processor timed out after charging, or the server died between the
// charge and saving the sale) are settled with the processor: by PaymentReconcileService's pass, or a Stripe webhook.
public class PaymentReconcileTests
{
    private const string WebhookSecret = "whsec_test_secret";

    private static async Task<Ticket> TicketAsync(Setup s, int id)
    {
        await using var db = s.App.Db();
        return await db.Tickets.Include(t => t.AddOns).SingleAsync(t => t.Id == id);
    }

    // A purchase whose charge went through at the processor but whose answer the server never got.
    private static async Task<(ClaimsPrincipal Buyer, HoldView Hold)> UnheardChargeAsync(Setup s, Exception lost, PurchaseInput? input = null)
    {
        var buyer = await BuyerAsync(s.App);
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 2, 1);
        s.App.Payments.LoseAnswerWith = lost;
        var ex = await Assert.ThrowsAsync<AppValidationException>(() =>
            s.Sales.PurchaseAsync(buyer, hold.TicketId, input ?? Buy(s.CarLoad, s.OutsideFood), TestApp.BaseUri));
        Assert.Contains("You won't be charged twice", ex.Message);
        s.App.Payments.LoseAnswerWith = null;
        return (buyer, hold);
    }

    [Fact]
    public async Task A_charge_that_went_through_unheard_is_finished_by_reconciliation()
    {
        await using var s = await SetUpAsync();
        var (buyer, hold) = await UnheardChargeAsync(s, new TimeoutException("processor timed out"));

        // Paying, with everything the sale needs already saved, and the spot off sale (even after the hold runs out).
        var paying = await TicketAsync(s, hold.TicketId);
        Assert.Equal((TicketStatus.Paying, "Car load", 30m), (paying.Status, paying.OptionName, paying.Total));
        Assert.Equal("Outside food", Assert.Single(paying.AddOns).Name);
        Assert.StartsWith($"ticket-{hold.TicketId}-", paying.PaymentKey);
        s.App.Time.Advance(TimeSpan.FromMinutes(Ticket.HoldMinutes + 1));
        await s.Sales.ReleaseExpiredHoldsAsync();
        Assert.Equal(SpotState.Held, (await s.Sales.GetAvailabilityAsync(await BuyerAsync(s.App, "other@example.com"), s.Showing.Id))[2, 1]);

        // Too soon (just under ReconcileAfter since payment started) is left alone; then it's asked about and sold.
        var summary = await s.Sales.ReconcilePaymentsAsync(TestApp.BaseUri);
        Assert.Equal(new ReconcileSummary(1, 0, 0), summary);
        var sold = await TicketAsync(s, hold.TicketId);
        Assert.Equal((TicketStatus.Sold, "Visa", "4242", "FAKE-1"), (sold.Status, sold.CardBrand, sold.CardLast4, sold.PaymentReference));
        Assert.NotNull(sold.Code);
        Assert.Single(s.App.Payments.Charges); // asked about, never charged again
        Assert.Contains(s.App.Email.Sent, m => m.To == "buyer@example.com" && m.Subject.Contains("ticket", StringComparison.OrdinalIgnoreCase));
        Assert.Single(await s.Sales.ListMyTicketsAsync(buyer));

        // Settled once: another pass does nothing.
        Assert.Equal(new ReconcileSummary(0, 0, 0), await s.Sales.ReconcilePaymentsAsync(TestApp.BaseUri));
    }

    [Fact]
    public async Task Reconciliation_waits_until_a_charge_has_had_time_to_finish()
    {
        await using var s = await SetUpAsync();
        var (_, hold) = await UnheardChargeAsync(s, new TimeoutException());

        s.App.Time.Advance(TicketSalesService.ReconcileAfter - TimeSpan.FromSeconds(1));
        Assert.Equal(new ReconcileSummary(0, 0, 0), await s.Sales.ReconcilePaymentsAsync(TestApp.BaseUri));
        Assert.Empty(s.App.Payments.Lookups);
        Assert.Equal(TicketStatus.Paying, (await TicketAsync(s, hold.TicketId)).Status);
    }

    [Fact]
    public async Task A_charge_the_processor_never_got_is_given_up_after_a_while_and_the_gift_card_made_whole()
    {
        await using var s = await SetUpAsync();
        var buyer = await BuyerAsync(s.App);
        await s.App.Get<TheaterService>().UpdateGiftCardSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true);
        var card = (await s.Sales.PurchaseGiftCardAsync(buyer, s.Theater.Id, new GiftCardPurchaseInput(20m, null, null, null, "pm_test_visa_4242_0002"),
            TestApp.BaseUri)).Card;
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        s.App.Payments.FailWith = new HttpRequestException("unreachable"); // never reached the processor
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.PurchaseAsync(buyer, hold.TicketId,
            new PurchaseInput(s.CarLoad.Id, [], "pm_test_visa_4242_0001", card.Code), TestApp.BaseUri));
        s.App.Payments.FailWith = null;
        await using (var db = s.App.Db())
            Assert.Equal(0m, (await db.GiftCards.SingleAsync(g => g.Id == card.Id)).Balance); // $20 of the $25 is on hold

        // Unknown to the processor: it may just not be searchable yet, so keep waiting...
        s.App.Time.Advance(TicketSalesService.ReconcileAfter);
        Assert.Equal(new ReconcileSummary(0, 0, 1), await s.Sales.ReconcilePaymentsAsync(TestApp.BaseUri));
        // ...until GiveUpAfter: then it's undone.
        s.App.Time.Advance(TicketSalesService.GiveUpAfter);
        Assert.Equal(new ReconcileSummary(0, 1, 0), await s.Sales.ReconcilePaymentsAsync(TestApp.BaseUri));

        var undone = await TicketAsync(s, hold.TicketId);
        Assert.Equal((TicketStatus.Held, null, null, 0m), (undone.Status, undone.PaymentKey, undone.OptionName, undone.GiftCardAmount));
        Assert.Empty(undone.AddOns);
        await using (var db = s.App.Db())
        {
            var gift = await db.GiftCards.Include(g => g.Transactions).SingleAsync(g => g.Id == card.Id);
            Assert.Equal(20m, gift.Balance);
            Assert.Equal(GiftCardTransactionKind.Restore, gift.Transactions.OrderBy(t => t.Id).Last().Kind);
        }
        // The hold ran out long ago, so the sweeper puts the spot back on sale.
        await s.Sales.ReleaseExpiredHoldsAsync();
        Assert.Equal(SpotState.Available, (await s.Sales.GetAvailabilityAsync(await BuyerAsync(s.App, "next@example.com"), s.Showing.Id))[1, 1]);
    }

    [Fact]
    public async Task A_charge_the_processor_declined_is_undone_and_one_still_going_waits()
    {
        await using var s = await SetUpAsync();
        var (_, declined) = await UnheardChargeAsync(s, new TimeoutException());
        var key = (await TicketAsync(s, declined.TicketId)).PaymentKey!;
        s.App.Payments.Outcomes[key] = new PaymentStatus(PaymentState.Failed, PaymentResult.Declined("insufficient funds"));
        var other = await BuyerAsync(s.App, "other@example.com");
        var pendingHold = await s.Sales.HoldAsync(other, s.Showing.Id, 1, 2);
        s.App.Payments.FailWith = new TimeoutException();
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.PurchaseAsync(other, pendingHold.TicketId, Buy(s.Single), TestApp.BaseUri));
        s.App.Payments.FailWith = null;
        s.App.Payments.Outcomes[(await TicketAsync(s, pendingHold.TicketId)).PaymentKey!] = PaymentStatus.Pending;

        s.App.Time.Advance(TicketSalesService.GiveUpAfter);
        Assert.Equal(new ReconcileSummary(0, 1, 1), await s.Sales.ReconcilePaymentsAsync(TestApp.BaseUri));

        Assert.Equal(TicketStatus.Held, (await TicketAsync(s, declined.TicketId)).Status);
        Assert.Equal(TicketStatus.Paying, (await TicketAsync(s, pendingHold.TicketId)).Status); // never given up while pending
    }

    [Fact]
    public async Task A_gate_sale_finished_by_reconciliation_is_checked_in()
    {
        await using var s = await SetUpAsync();
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 9, 5, 22, 0, 0, TimeSpan.Zero));
        var employee = await s.App.CreateUserAsync("gate@example.com", employeeTheaterId: s.Theater.Id);
        await s.App.GrantAsync(employee, [SellAtGate]);
        var attendant = Principals.For(employee);
        var hold = await s.Sales.HoldAtGateAsync(attendant, s.Showing.Id, 1, 3);
        s.App.Payments.LoseAnswerWith = new TimeoutException();
        var ex = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.SellAtGateAsync(attendant, hold.TicketId, s.Single.Id, []));
        Assert.Contains("Don't take the card again", ex.Message);

        s.App.Time.Advance(TicketSalesService.ReconcileAfter);
        await s.Sales.ReconcilePaymentsAsync(TestApp.BaseUri);

        var sold = await TicketAsync(s, hold.TicketId);
        Assert.Equal((TicketStatus.Sold, true, (string?)null, employee.Id), (sold.Status, sold.SoldAtGate, sold.UserId, sold.SoldById));
        Assert.NotNull(sold.AdmittedAt);
        Assert.True(Assert.Single(s.App.Payments.Charges).CardPresent);
    }

    [Fact]
    public async Task Finishing_a_sale_twice_sells_it_once()
    {
        await using var s = await SetUpAsync();
        var (_, hold) = await UnheardChargeAsync(s, new TimeoutException());
        var key = (await TicketAsync(s, hold.TicketId)).PaymentKey;
        var paid = new PaymentResult(true, "FAKE-1", CardBrand: "Visa", CardLast4: "4242");

        var first = await s.Sales.CompletePaidTicketAsync(hold.TicketId, key, paid);
        var second = await s.Sales.CompletePaidTicketAsync(hold.TicketId, key, paid);

        Assert.True(first.CompletedHere);
        Assert.False(second.CompletedHere);
        Assert.Equal(first.Ticket!.Code, second.Ticket!.Code);
        // And an undo that arrives late doesn't touch a sold ticket.
        Assert.False(await s.Sales.AbortTicketPaymentAsync(hold.TicketId, key));
        Assert.Equal(TicketStatus.Sold, (await TicketAsync(s, hold.TicketId)).Status);
    }

    [Fact]
    public async Task A_gift_card_whose_charge_went_through_unheard_is_issued_and_emailed_by_reconciliation()
    {
        await using var s = await SetUpAsync();
        await s.App.Get<TheaterService>().UpdateGiftCardSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true);
        var buyer = await BuyerAsync(s.App);
        s.App.Payments.LoseAnswerWith = new TimeoutException();
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.PurchaseGiftCardAsync(buyer, s.Theater.Id,
            new GiftCardPurchaseInput(40m, "Sam", "sam@example.com", "Enjoy!", "pm_test_visa_4242_0001"), TestApp.BaseUri));
        s.App.Payments.LoseAnswerWith = null;
        await using (var db = s.App.Db())
        {
            Assert.Empty(await db.GiftCards.ToListAsync());
            Assert.Equal(GiftCardPurchaseStatus.Paying, (await db.GiftCardPurchases.SingleAsync()).Status);
        }

        s.App.Time.Advance(TicketSalesService.ReconcileAfter);
        Assert.Equal(new ReconcileSummary(1, 0, 0), await s.Sales.ReconcilePaymentsAsync(TestApp.BaseUri));

        await using (var db = s.App.Db())
        {
            var purchase = await db.GiftCardPurchases.Include(p => p.GiftCard).SingleAsync();
            Assert.Equal(GiftCardPurchaseStatus.Completed, purchase.Status);
            Assert.Equal((40m, 40m, "Sam", "FAKE-1"), (purchase.GiftCard!.InitialAmount, purchase.GiftCard.Balance, purchase.GiftCard.RecipientName,
                purchase.GiftCard.PaymentReference));
        }
        Assert.Contains(s.App.Email.Sent, m => m.To == "buyer@example.com");
        Assert.Contains(s.App.Email.Sent, m => m.To == "sam@example.com");
        Assert.Single(s.App.Payments.Charges);
    }

    [Fact]
    public async Task A_declined_gift_card_purchase_is_failed_and_issues_nothing()
    {
        await using var s = await SetUpAsync();
        await s.App.Get<TheaterService>().UpdateGiftCardSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true);
        s.App.Payments.DeclineWith = "Do not honor.";
        var buyer = await BuyerAsync(s.App);

        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.PurchaseGiftCardAsync(buyer, s.Theater.Id,
            new GiftCardPurchaseInput(40m, null, null, null, "pm_test_visa_4242_0001"), TestApp.BaseUri));

        await using var db = s.App.Db();
        var purchase = await db.GiftCardPurchases.SingleAsync();
        Assert.Equal(GiftCardPurchaseStatus.Failed, purchase.Status);
        Assert.Empty(await db.GiftCards.ToListAsync());
        Assert.Equal($"giftcard-{purchase.Id}", Assert.Single(s.App.Payments.Charges).IdempotencyKey);
    }

    // --- Webhook ---

    private static string Signed(string json, string secret, DateTimeOffset at)
    {
        var timestamp = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{json}"));
        return $"t={timestamp},v1={Convert.ToHexStringLower(mac)}";
    }

    private static string IntentEvent(string type, string intentId, string paymentKey) => $$"""
        {
          "id": "evt_test_1", "object": "event", "api_version": "2025-01-01", "created": 1790000000, "livemode": false,
          "type": "{{type}}",
          "data": { "object": { "id": "{{intentId}}", "object": "payment_intent", "amount": 3000, "currency": "usd",
            "status": "succeeded", "metadata": { "payment_key": "{{paymentKey}}" } } }
        }
        """;

    private static HttpRequest Request(string json, string? signature)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        if (signature is not null)
            http.Request.Headers["Stripe-Signature"] = signature;
        return http.Request;
    }

    private static Task<IResult> HandleAsync(Setup s, string json, string? signature, string? secret = WebhookSecret) =>
        StripeWebhook.HandleAsync(Request(json, signature), secret, s.Sales, TestApp.BaseUri, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task A_signed_payment_succeeded_webhook_finishes_the_sale_right_away()
    {
        await using var s = await SetUpAsync();
        var (_, hold) = await UnheardChargeAsync(s, new TimeoutException());
        var key = (await TicketAsync(s, hold.TicketId)).PaymentKey!;
        var json = IntentEvent("payment_intent.succeeded", "pi_test_123", key);

        var result = await HandleAsync(s, json, Signed(json, WebhookSecret, DateTimeOffset.UtcNow));

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(TicketStatus.Sold, (await TicketAsync(s, hold.TicketId)).Status);
        // The event is only a nudge: the processor itself was asked, by the intent's id.
        Assert.Equal(new PaymentLookup(key, "pi_test_123"), s.App.Payments.Lookups.Last());
        // A repeat (Stripe retries) is harmless.
        await HandleAsync(s, json, Signed(json, WebhookSecret, DateTimeOffset.UtcNow));
        Assert.Single(s.App.Email.Sent, m => m.To == "buyer@example.com");
    }

    [Fact]
    public async Task Unsigned_or_wrongly_signed_webhooks_are_refused_and_change_nothing()
    {
        await using var s = await SetUpAsync();
        var (_, hold) = await UnheardChargeAsync(s, new TimeoutException());
        var json = IntentEvent("payment_intent.succeeded", "pi_test_123", (await TicketAsync(s, hold.TicketId)).PaymentKey!);

        var unsigned = await HandleAsync(s, json, signature: null);
        var forged = await HandleAsync(s, json, Signed(json, "whsec_someone_else", DateTimeOffset.UtcNow));
        var stale = await HandleAsync(s, json, Signed(json, WebhookSecret, DateTimeOffset.UtcNow.AddHours(-1)));

        Assert.All([unsigned, forged, stale], r => Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(r).StatusCode));
        Assert.Equal(TicketStatus.Paying, (await TicketAsync(s, hold.TicketId)).Status);
    }

    [Fact]
    public async Task Without_a_webhook_secret_the_endpoint_is_off()
    {
        await using var s = await SetUpAsync();
        var json = IntentEvent("payment_intent.succeeded", "pi_test_123", "ticket-1-x");

        var result = await HandleAsync(s, json, Signed(json, WebhookSecret, DateTimeOffset.UtcNow), secret: "");

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }
}
