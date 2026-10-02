using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;
using static DriveIn.Web.Tests.TicketSalesTests;

namespace DriveIn.Web.Tests;

// Theaters' payout accounts (Stripe Connect, faked here), and that a processor which pays theaters directly won't sell for
// a live theater without one.
public class PayoutTests
{
    private const string Visa = "pm_test_visa_4242_0001";

    private static async Task SetPayoutAsync(Setup s, PayoutStatus status, string? account = "acct_ready")
    {
        await using var db = s.App.Db();
        var theater = await db.Theaters.SingleAsync(t => t.Id == s.Theater.Id);
        (theater.PayoutAccountId, theater.PayoutStatus) = (account, status);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_owner_sets_up_payouts_through_the_processors_onboarding()
    {
        await using var s = await SetUpAsync();
        var payouts = s.App.Get<PayoutService>();

        var link = await payouts.StartOnboardingAsync(s.OwnerPrincipal, s.Theater.Id, TestApp.BaseUri);

        Assert.Equal("https://connect.example.test/setup/acct_fake1", link);
        var (account, returnUrl, refreshUrl) = Assert.Single(s.App.Payouts.Links);
        Assert.Equal("acct_fake1", account);
        Assert.Equal($"{TestApp.BaseUri.TrimEnd('/')}/manage/{s.Theater.Id}/payouts?done=1", returnUrl);
        Assert.EndsWith("payouts?expired=1", refreshUrl);
        var theater = (await payouts.GetAsync(s.OwnerPrincipal, s.Theater.Id)).Theater;
        Assert.Equal(("acct_fake1", PayoutStatus.Pending), (theater.PayoutAccountId, theater.PayoutStatus));

        // Starting again continues the same account rather than making another.
        await payouts.StartOnboardingAsync(s.OwnerPrincipal, s.Theater.Id, TestApp.BaseUri);
        Assert.Single(s.App.Payouts.Accounts);

        s.App.Payouts.Accounts["acct_fake1"] = PayoutStatus.Enabled;
        var refreshed = await payouts.RefreshAsync(s.OwnerPrincipal, s.Theater.Id);
        Assert.Equal(PayoutStatus.Enabled, refreshed.PayoutStatus);
        Assert.Equal(s.App.Time.GetUtcNow(), refreshed.PayoutStatusCheckedAt);
    }

    [Fact]
    public async Task Payouts_need_ManagePayouts_which_no_default_role_has()
    {
        await using var s = await SetUpAsync();
        var payouts = s.App.Get<PayoutService>();
        var manager = await s.App.CreateUserAsync("manager@example.com", employeeTheaterId: s.Theater.Id);
        await s.App.GrantAsync(manager, DefaultTheaterRoles.All.Single(r => r.Name == "Manager").Permissions);
        var granted = await s.App.CreateUserAsync("books@example.com", employeeTheaterId: s.Theater.Id);
        await s.App.GrantAsync(granted, [ManagePayouts]);

        Assert.DoesNotContain(DefaultTheaterRoles.All, r => r.Permissions.Contains(ManagePayouts));
        await Assert.ThrowsAsync<AccessDeniedException>(() => payouts.GetAsync(Principals.For(manager), s.Theater.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => payouts.StartOnboardingAsync(Principals.For(manager), s.Theater.Id, TestApp.BaseUri));
        await Assert.ThrowsAsync<AccessDeniedException>(() => payouts.RefreshAsync(Principals.For(manager), s.Theater.Id));
        Assert.Empty(s.App.Payouts.Accounts);
        // Granted explicitly, it works.
        await payouts.StartOnboardingAsync(Principals.For(granted), s.Theater.Id, TestApp.BaseUri);
        Assert.Single(s.App.Payouts.Accounts);
    }

    [Fact]
    public async Task Without_a_processor_that_pays_theaters_there_is_nothing_to_set_up()
    {
        await using var s = await SetUpAsync();
        s.App.Payouts.IsAvailable = false;

        var view = await s.App.Get<PayoutService>().GetAsync(s.OwnerPrincipal, s.Theater.Id);

        Assert.False(view.Available);
        var ex = await Assert.ThrowsAsync<AppValidationException>(() =>
            s.App.Get<PayoutService>().StartOnboardingAsync(s.OwnerPrincipal, s.Theater.Id, TestApp.BaseUri));
        Assert.Contains("aren't set up", ex.Message);
    }

    [Fact]
    public async Task A_live_theater_cant_sell_by_card_until_its_payout_account_is_enabled()
    {
        await using var s = await SetUpAsync();
        s.App.Payments.RequiresPayoutAccount = true;
        await s.App.Get<TheaterService>().UpdateGiftCardSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true);
        var buyer = await BuyerAsync(s.App);

        var showing = await s.Sales.GetShowingAsync(buyer, s.Showing.Id);
        Assert.Equal("This theater isn't selling tickets online yet.", showing.NotOnSaleReason);
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1));
        Assert.Equal("This theater isn't selling gift cards online yet.", (await s.Sales.GetGiftCardOfferAsync(buyer, "starlight")).NotAvailableReason);

        await SetPayoutAsync(s, PayoutStatus.Pending);
        Assert.False((await s.Sales.GetShowingAsync(buyer, s.Showing.Id)).OnSale);

        await SetPayoutAsync(s, PayoutStatus.Enabled);
        Assert.True((await s.Sales.GetShowingAsync(buyer, s.Showing.Id)).OnSale);
    }

    [Fact]
    public async Task Charges_go_to_the_theaters_payout_account_with_the_platform_fee()
    {
        await using var s = await SetUpAsync();
        s.App.Payments.RequiresPayoutAccount = true;
        await SetPayoutAsync(s, PayoutStatus.Enabled, "acct_starlight");
        s.App.Get<Microsoft.Extensions.Options.IOptions<PaymentOptions>>().Value.ApplicationFeePercent = 2.5m;
        await s.App.Get<TheaterService>().UpdateGiftCardSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true);
        var buyer = await BuyerAsync(s.App);

        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        await s.Sales.PurchaseAsync(buyer, hold.TicketId, Buy(s.CarLoad), TestApp.BaseUri);
        await s.Sales.PurchaseGiftCardAsync(buyer, s.Theater.Id, new GiftCardPurchaseInput(50m, null, null, null, Visa), TestApp.BaseUri);

        Assert.All(s.App.Payments.Charges, c => Assert.Equal("acct_starlight", c.PayoutAccountId));
        Assert.Equal([63L, 125L], s.App.Payments.Charges.Select(c => c.ApplicationFeeCents)); // 2.5% of $25 and $50
    }

    [Fact]
    public async Task Without_payout_accounts_charges_carry_none_and_demo_theaters_need_none()
    {
        await using var s = await SetUpAsync();
        var buyer = await BuyerAsync(s.App);

        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        await s.Sales.PurchaseAsync(buyer, hold.TicketId, Buy(s.Single), TestApp.BaseUri);
        var charge = Assert.Single(s.App.Payments.Charges);
        Assert.Equal((null, 0L), (charge.PayoutAccountId, charge.ApplicationFeeCents));

        // A demo theater sells test tickets through the dummy processor, which needs no payout account.
        s.App.Payments.RequiresPayoutAccount = true;
        await using (var db = s.App.Db())
        {
            (await db.Theaters.SingleAsync(t => t.Id == s.Theater.Id)).Mode = TheaterMode.Demo;
            await db.SaveChangesAsync();
        }
        Assert.True((await s.Sales.GetShowingAsync(s.OwnerPrincipal, s.Showing.Id)).OnSale);
    }

    [Fact]
    public void Stripe_charges_are_destination_charges_on_the_theaters_behalf()
    {
        var request = new PaymentRequest(2500, "usd", "Starlight: Jaws, spot A1", "pm_1Qabc", "ticket-1-x",
            new Dictionary<string, string> { ["ticket_id"] = "1" }, "acct_starlight", 63);

        var options = StripePaymentProcessor.CreateOptions(request);

        Assert.Equal((2500L, "usd", "pm_1Qabc", true), (options.Amount, options.Currency, options.PaymentMethod, options.Confirm));
        Assert.Equal(("acct_starlight", "acct_starlight", 63L), (options.OnBehalfOf, options.TransferData.Destination, options.ApplicationFeeAmount));
        Assert.Equal("ticket-1-x", options.Metadata[StripePaymentProcessor.PaymentKeyMetadata]);
        Assert.Equal("never", options.AutomaticPaymentMethods.AllowRedirects);

        var noFee = StripePaymentProcessor.CreateOptions(request with { ApplicationFeeCents = 0 });
        Assert.Null(noFee.ApplicationFeeAmount);
    }

    [Fact]
    public async Task Stripe_refuses_an_online_charge_without_a_payout_account()
    {
        var stripe = new StripePaymentProcessor(new Stripe.StripeClient("sk_test_x"), new StripeTerminalReader(),
            Microsoft.Extensions.Options.Options.Create(new StripeOptions()), Microsoft.Extensions.Options.Options.Create(new PaymentOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StripePaymentProcessor>.Instance);

        Assert.True(stripe.RequiresPayoutAccount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stripe.ChargeAsync(
            new PaymentRequest(1000, "usd", "x", "pm_1Qabc", "k", new Dictionary<string, string>())));
    }
}
