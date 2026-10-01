using DriveIn.Web.Components.Pages.Theaters;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Tests.Pages;

// Buying a ticket on a showing's page, and buying a gift card.
public class PublicPurchasePageTests
{
    private static readonly CardInput Visa = new("Pat Buyer", "4242 4242 4242 4242", 12, 2030, "123");

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

    private static void EnterCard<T>(PageHost host, IRenderedComponent<T> page) where T : IComponent
    {
        page.SetField("Name on card", "Pat Buyer");
        page.SetField("Card number", "4242 4242 4242 4242");
        host.Select(page, "Month", "12");
        host.Select(page, "Year", "2030");
        page.SetField("Security code", "123");
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
        Assert.DoesNotContain("Name on card", page.Text());
        page.ClickButton("Remove");
        page.WaitForText("Name on card");
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
        Assert.DoesNotContain("Name on card", page.Text());
    }
}
