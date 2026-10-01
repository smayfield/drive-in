using DriveIn.Web.Components.Pages.Manage;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests.Pages;

public class ManageGiftCardsPageTests
{
    private static readonly CardInput Visa = new("Pat Buyer", "4242 4242 4242 4242", 12, 2030, "123");

    [Fact]
    public async Task The_owner_turns_gift_card_sales_on_and_off()
    {
        var s = await TicketSalesTests.SetUpAsync();
        await using var host = new PageHost(s.App).SignIn(s.Owner);
        var page = host.Render<ManageGiftCards>(p => p.Add(x => x.Id, s.Theater.Id));
        page.WaitForText("None sold yet.");

        page.Check("Sell gift cards");
        page.ClickButton("Save");
        page.WaitForText("Gift card sales are on.");
        await using (var db = s.App.Db())
            Assert.True((await db.Theaters.SingleAsync()).GiftCardsEnabled);

        page.Check("Sell gift cards", false);
        page.ClickButton("Save");
        page.WaitForText("Gift card sales are off. Cards already sold still work.");
    }

    [Fact]
    public async Task Sold_cards_are_listed_without_their_codes()
    {
        var s = await TicketSalesTests.SetUpAsync();
        await s.App.Get<TheaterService>().UpdateGiftCardSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true);
        var card = (await s.Sales.PurchaseGiftCardAsync(await TicketSalesTests.BuyerAsync(s.App), s.Theater.Id,
            new GiftCardPurchaseInput(50m, "Sam", null, null, Visa), TestApp.BaseUri)).Card;
        var viewer = await s.App.CreateUserAsync("books@example.com", s.Theater.Id);
        await s.App.GrantAsync(viewer, ViewGiftCards);
        await using var host = new PageHost(s.App).SignIn(viewer);

        var page = host.Render<ManageGiftCards>(p => p.Add(x => x.Id, s.Theater.Id));

        page.WaitForText("1 sold for $50.00 · $50.00 not yet spent.");
        var text = page.Text();
        Assert.Contains($"…{card.Last4}", text);
        Assert.Contains("buyer@example.com", text);
        Assert.DoesNotContain(card.Code, page.Markup);
        Assert.DoesNotContain("Sell gift cards", text); // viewing isn't managing
    }

    [Fact]
    public async Task Staff_without_gift_card_rights_are_turned_away()
    {
        var s = await TicketSalesTests.SetUpAsync();
        var staff = await s.App.CreateUserAsync("staff@example.com", s.Theater.Id);
        await s.App.GrantAsync(staff, SellAtGate);
        await using var host = new PageHost(s.App).SignIn(staff);

        host.Render<ManageGiftCards>(p => p.Add(x => x.Id, s.Theater.Id));

        Assert.EndsWith("Account/AccessDenied", host.Nav.Uri);
    }
}
