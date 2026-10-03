using DriveIn.Web.Components.Pages.Theaters;
using DriveIn.Web.Components.Shared;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Tests.Pages;

// Buying a ticket on a showing's page, and buying a gift card.
public class PublicPurchasePageTests
{
    private const string Visa = "pm_test_visa_4242_0001"; // a test card token (see TestCardTokens)

    private static async Task<(TicketSalesTests.Setup S, PageHost Host)> BuyerAsync()
    {
        var s = await TicketSalesTests.SetUpAsync();
        return (s, new PageHost(s.App).SignIn(await TicketSalesTests.BuyerAsync(s.App)));
    }

    private static IRenderedComponent<Showing> OpenShowing(PageHost host, TicketSalesTests.Setup s)
    {
        var page = host.Render<Showing>(p => p.Add(x => x.Slug, "starlight").Add(x => x.ShowtimeId, s.Showing.Id));
        page.WaitForText("Choose your spot");
        return page;
    }

    // The test card form is plain inputs that only payments.js reads, so "typing a card" here is the browser handing back
    // the token payments.js would make for it.
    private static void EnterCard<T>(PageHost host, IRenderedComponent<T> page, string token = "pm_test_visa_4242_0001")
        where T : IComponent
    {
        Assert.Contains("Card number", page.Text());
        host.Context.JSInterop.SetupModule("./payments.js")
            .Setup<CardFields.CardToken>("tokenizeTest", _ => true).SetResult(new CardFields.CardToken(token, null));
    }

    [Fact]
    public async Task A_buyer_holds_a_spot_pays_and_is_sent_to_the_ticket()
    {
        var (s, host) = await BuyerAsync();
        await using var _ = host;
        await using var __ = s;
        var page = OpenShowing(host, s);
        var text = page.Text();
        Assert.Contains("Saturday, September 5 · 8:00 PM · North · ends about 10:04 PM · PG", text);
        Assert.Contains("$10.00 One occupant", text);
        Assert.Contains("7 of 7 available", text);

        page.Find("g[aria-label='Spot B2: available']").Click();
        page.WaitForText("Spot B2 is yours for 10:00");
        page.ChooseRadio("Car load");
        page.Check("Veteran");
        EnterCard(host, page);
        page.ClickButton("Pay $23.00");

        page.WaitForAssertion(() => Assert.Contains("tickets/", host.Nav.Uri));
        Assert.EndsWith("?new=sent", host.Nav.Uri);
        Assert.Equal(23m, Assert.Single(s.App.Payments.Charges).Amount);
    }

    [Fact]
    public async Task A_declined_card_keeps_the_hold_and_shows_why()
    {
        var (s, host) = await BuyerAsync();
        await using var _ = host;
        await using var __ = s;
        s.App.Payments.DeclineWith = "Insufficient funds.";
        var page = OpenShowing(host, s);
        page.Find("g[aria-label='Spot A1: available']").Click();
        page.WaitForText("Spot A1 is yours for");
        EnterCard(host, page);

        page.ClickButton("Pay $10.00");

        page.WaitForText("Insufficient funds.");
        Assert.Contains("Spot A1 is yours for", page.Text());
    }

    [Fact]
    public async Task Only_the_cards_token_reaches_the_server()
    {
        var (s, host) = await BuyerAsync();
        await using var _ = host;
        await using var __ = s;
        var page = OpenShowing(host, s);
        page.Find("g[aria-label='Spot A1: available']").Click();
        page.WaitForText("Spot A1 is yours for");
        // The card inputs aren't bound to anything: Blazor never sees what's typed into them.
        Assert.All(page.FindAll(".test-card input"), input => Assert.Null(input.GetAttribute("value")));
        EnterCard(host, page, "pm_test_visa_4242_feed");

        page.ClickButton("Pay $10.00");

        page.WaitForAssertion(() => Assert.Contains("tickets/", host.Nav.Uri));
        Assert.Equal("pm_test_visa_4242_feed", Assert.Single(s.App.Payments.Charges).PaymentMethodId);
    }

    [Fact]
    public async Task A_card_the_browser_rejects_is_never_charged()
    {
        var (s, host) = await BuyerAsync();
        await using var _ = host;
        await using var __ = s;
        var page = OpenShowing(host, s);
        page.Find("g[aria-label='Spot A1: available']").Click();
        page.WaitForText("Spot A1 is yours for");
        host.Context.JSInterop.SetupModule("./payments.js")
            .Setup<CardFields.CardToken>("tokenizeTest", _ => true).SetResult(new CardFields.CardToken(null, "That card has expired."));

        page.ClickButton("Pay $10.00");

        page.WaitForText("That card has expired.");
        Assert.Empty(s.App.Payments.Charges);
        Assert.Contains("Spot A1 is yours for", page.Text());
    }

    [Fact]
    public async Task With_Stripe_the_card_fields_are_Stripes_element()
    {
        var (s, host) = await BuyerAsync();
        await using var _ = host;
        await using var __ = s;
        s.App.Payments.Client = new PaymentClient(PaymentClientKind.Stripe, "pk_test_123");
        var stripe = host.Context.JSInterop.SetupModule("./payments.js");
        var mount = stripe.Setup<bool>("mountStripe", _ => true);
        mount.SetResult(true);
        stripe.Setup<CardFields.CardToken>("tokenizeStripe", _ => true).SetResult(new CardFields.CardToken("pm_1Qstripe", null));
        var page = OpenShowing(host, s);
        page.Find("g[aria-label='Spot A1: available']").Click();
        page.WaitForText("Spot A1 is yours for");

        Assert.Empty(page.FindAll(".test-card"));
        page.WaitForAssertion(() => Assert.Equal("pk_test_123", Assert.Single(mount.Invocations).Arguments[1]));
        Assert.Equal(1000L, mount.Invocations.Single().Arguments[2]);
        page.ClickButton("Pay $10.00");

        page.WaitForAssertion(() => Assert.Contains("tickets/", host.Nav.Uri));
        Assert.Equal("pm_1Qstripe", Assert.Single(s.App.Payments.Charges).PaymentMethodId);
    }

    [Fact]
    public async Task If_Stripe_cant_load_the_buyer_is_told_instead_of_the_page_breaking()
    {
        var (s, host) = await BuyerAsync();
        await using var _ = host;
        await using var __ = s;
        s.App.Payments.Client = new PaymentClient(PaymentClientKind.Stripe, "pk_test_123");
        var stripe = host.Context.JSInterop.SetupModule("./payments.js");
        stripe.Setup<bool>("mountStripe", _ => true).SetException(new Microsoft.JSInterop.JSException("Couldn't load Stripe."));
        stripe.Setup<CardFields.CardToken>("tokenizeStripe", _ => true).SetException(new Microsoft.JSInterop.JSException("not mounted"));
        var page = OpenShowing(host, s);
        page.Find("g[aria-label='Spot A1: available']").Click();

        page.WaitForText("The card form couldn't load.");
        page.ClickButton("Pay $10.00");

        page.WaitForText("The card form isn't working.");
        Assert.Empty(s.App.Payments.Charges);
        Assert.Contains("Spot A1 is yours for", page.Text());
    }

    [Fact]
    public async Task Choosing_a_different_spot_releases_the_hold()
    {
        var (s, host) = await BuyerAsync();
        await using var _ = host;
        await using var __ = s;
        var page = OpenShowing(host, s);
        page.Find("g[aria-label='Spot A1: available']").Click();
        page.WaitForText("Spot A1 is yours for");

        page.ClickButton("Choose a different spot");

        page.WaitForText("Choose your spot");
        await using var db = s.App.Db();
        Assert.Empty(await db.Tickets.ToListAsync());
    }

    [Fact]
    public async Task A_gift_card_can_pay_for_the_whole_ticket()
    {
        var (s, host) = await BuyerAsync();
        await using var _ = host;
        await using var __ = s;
        await s.App.Get<TheaterService>().UpdateGiftCardSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true);
        var card = (await s.Sales.PurchaseGiftCardAsync(await TicketSalesTests.BuyerAsync(s.App, "giver@example.com"), s.Theater.Id,
            new GiftCardPurchaseInput(50m, "Sam", null, null, Visa), TestApp.BaseUri)).Card;
        s.App.Payments.Charges.Clear();
        var page = OpenShowing(host, s);
        page.Find("g[aria-label='Spot A1: available']").Click();
        page.WaitForText("Spot A1 is yours for");

        page.SetField("Gift card code (optional)", card.Code);
        page.ClickButton("Apply");
        page.WaitForText("Covers the whole $10.00. $40.00 will be left on the card.");
        Assert.DoesNotContain("Card number", page.Text());
        page.ClickButton("Remove");
        page.WaitForText("Card number");
        page.SetField("Gift card code (optional)", card.Code);
        page.ClickButton("Apply");
        page.WaitForText("Covers the whole");
        page.ClickButton("Pay with gift card");

        page.WaitForAssertion(() => Assert.Contains("tickets/", host.Nav.Uri));
        Assert.Empty(s.App.Payments.Charges);
    }

    [Fact]
    public async Task A_showing_under_another_theaters_address_is_not_found()
    {
        var (s, host) = await BuyerAsync();
        await using var _ = host;
        await using var __ = s;

        var page = host.Render<Showing>(p => p.Add(x => x.Slug, "elsewhere").Add(x => x.ShowtimeId, s.Showing.Id));

        page.WaitForText("Showing not found.");
    }

    // --- Gift cards ---

    [Fact]
    public async Task A_gift_card_is_bought_for_someone_else()
    {
        var (s, host) = await BuyerAsync();
        await using var _ = host;
        await using var __ = s;
        await s.App.Get<TheaterService>().UpdateGiftCardSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true);
        var page = host.Render<GiftCards>(p => p.Add(x => x.Slug, "starlight"));
        page.WaitForText("Pay $50.00");

        page.ClickButton("$25.00");
        page.SetField("Recipient's name", "Sam");
        page.SetField("Recipient's email (we'll send them the card)", "sam@example.com");
        page.SetField("Message", "Enjoy!");
        EnterCard(host, page);
        page.ClickButton("Pay $25.00");

        page.WaitForText("Your gift card");
        Assert.Contains("We emailed it to you. We also emailed it to the recipient.", page.Text());
        await using var db = s.App.Db();
        var bought = await db.GiftCards.SingleAsync();
        Assert.Equal(25m, bought.InitialAmount);
        Assert.Contains(GiftCardCodes.Format(bought.Code), page.Text());

        page.ClickButton("Buy another");
        page.WaitForText("For someone else? (optional)");
    }

    [Fact]
    public async Task Gift_cards_arent_sold_when_the_theater_has_them_off()
    {
        var (s, host) = await BuyerAsync();
        await using var _ = host;
        await using var __ = s;

        var page = host.Render<GiftCards>(p => p.Add(x => x.Slug, "starlight"));

        page.WaitForText("Gift cards");
        page.WaitForAssertion(() => Assert.Contains("mud-alert-text-info", page.Markup));
        Assert.DoesNotContain("Card number", page.Text());
    }
}
