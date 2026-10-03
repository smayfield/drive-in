using DriveIn.Web.Components.Pages.Manage;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests.Pages;

public class ManageGatePageTests
{
    // 5 PM in Chicago on the day of the 8 PM showing (see TicketSalesTests.SetUpAsync).
    private static readonly DateTimeOffset ShowDayAfternoon = new(2026, 9, 5, 22, 0, 0, TimeSpan.Zero);
    private static readonly CardInput Visa = new("Pat Buyer", "4242 4242 4242 4242", 12, 2030, "123");

    private static async Task<(PageHost Host, TicketSalesTests.Setup S)> OwnerAsync()
    {
        var s = await TicketSalesTests.SetUpAsync();
        s.App.Time.SetUtcNow(ShowDayAfternoon);
        return (new PageHost(s.App).SignIn(s.Owner), s);
    }

    private static IRenderedComponent<ManageGate> Open(PageHost host, TicketSalesTests.Setup s)
    {
        var page = host.Render<ManageGate>(p => p.Add(x => x.Id, s.Theater.Id));
        page.WaitForText("Sell a ticket");
        return page;
    }

    private static void Find(IRenderedComponent<ManageGate> page, string code)
    {
        page.Find("input.gate-input").Change(code);
        page.Find("form").Submit();
    }

    private static async Task<Ticket> TicketAsync(TicketSalesTests.Setup s, string code)
    {
        await using var db = s.App.Db();
        return await db.Tickets.SingleAsync(t => t.Code == code);
    }

    [Fact]
    public async Task A_ticket_is_found_by_its_gate_code_and_checked_in()
    {
        var (host, s) = await OwnerAsync();
        await using var _ = host;
        var code = await TicketSalesTests.SellAsync(s, await TicketSalesTests.BuyerAsync(s.App));
        var page = Open(host, s);

        Find(page, (await TicketAsync(s, code)).ShortCode!);
        page.WaitForText("Valid");
        Assert.Contains("Spot A1 · North", page.Text());
        page.ClickButton("Check in");

        page.WaitForText("Checked in: spot A1 on North.");
        Assert.NotNull((await TicketAsync(s, code)).AdmittedAt);

        Find(page, code);
        page.WaitForText("Not valid");
    }

    [Fact]
    public async Task An_unknown_code_finds_nothing()
    {
        var (host, s) = await OwnerAsync();
        await using var _ = host;
        var page = Open(host, s);

        Find(page, "ZZZZ");

        page.WaitForAssertion(() => Assert.Contains("mud-alert", page.Markup));
        Assert.DoesNotContain("Valid", page.Text());
    }

    [Fact]
    public async Task A_ticket_can_be_moved_to_another_spot()
    {
        var (host, s) = await OwnerAsync();
        await using var _ = host;
        var code = await TicketSalesTests.SellAsync(s, await TicketSalesTests.BuyerAsync(s.App));
        var page = Open(host, s);
        Find(page, code);
        page.WaitForText("Move to another spot");

        page.ClickButton("Move to another spot");
        page.WaitForText("Pick an available spot");
        page.ClickButton("Cancel");
        page.ClickButton("Move to another spot");
        page.WaitForText("Pick an available spot");
        page.Find("g[aria-label='Spot B4: available']").Click();
        page.ClickButton("Move A1 → B4");

        page.WaitForText("Moved to spot B4 on North. Send the car there.");
        Assert.Equal("B4", (await TicketAsync(s, code)).SpotLabel);
    }

    [Fact]
    public async Task A_spot_is_held_and_sold_at_the_gate()
    {
        var (host, s) = await OwnerAsync();
        await using var _ = host;
        var page = Open(host, s);

        page.Find("button.gate-showing").Click();
        page.WaitForText("Pick an available spot for the car. 7 of 7 are open.");
        page.Find("g[aria-label='Spot A2: available']").Click();
        page.WaitForText("Spot A2");
        Assert.Contains("held 10:00", page.Text());
        page.ChooseRadio("Car load");
        page.Check("Outside food");
        page.WaitForText("Charge card $30.00");
        page.ClickButton("Charge card $30.00");

        page.WaitForText("Sold and checked in");
        Assert.Contains("Send the car to spot A2 on North.", page.Text());
        Assert.Equal(30m, Assert.Single(s.App.Payments.Charges).Amount);
    }

    [Fact]
    public async Task Cancelling_a_gate_hold_frees_the_spot()
    {
        var (host, s) = await OwnerAsync();
        await using var _ = host;
        var page = Open(host, s);
        page.Find("button.gate-showing").Click();
        page.WaitForText("are open");
        page.Find("g[aria-label='Spot A2: available']").Click();
        page.WaitForText("Spot A2");

        page.ClickButton("Cancel");

        page.WaitForText("7 of 7 are open");
        await using var db = s.App.Db();
        Assert.Empty(await db.Tickets.ToListAsync());
    }

    [Fact]
    public async Task A_gift_card_can_pay_at_the_gate()
    {
        var (host, s) = await OwnerAsync();
        await using var _ = host;
        await s.App.Get<TheaterService>().UpdateGiftCardSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true);
        var card = (await s.Sales.PurchaseGiftCardAsync(await TicketSalesTests.BuyerAsync(s.App), s.Theater.Id,
            new GiftCardPurchaseInput(50m, "Sam", null, null, Visa), TestApp.BaseUri)).Card;
        s.App.Payments.Charges.Clear();
        var page = Open(host, s);
        page.Find("button.gate-showing").Click();
        page.WaitForText("are open");
        page.Find("g[aria-label='Spot A2: available']").Click();
        page.WaitForText("Spot A2");

        page.SetField("Gift card code (optional)", card.Code);
        page.ClickButton("Apply");
        page.WaitForText($"Gift card ending {card.Last4} has $50.00.");
        page.ClickButton("Sell (gift card pays)");

        page.WaitForText("Sold and checked in");
        Assert.Empty(s.App.Payments.Charges);
    }

    [Fact]
    public async Task A_seller_without_check_in_rights_only_sees_the_sale_panel()
    {
        var s = await TicketSalesTests.SetUpAsync();
        var staff = await s.App.CreateUserAsync("gate@example.com", s.Theater.Id);
        await s.App.GrantAsync(staff, SellAtGate);
        await using var host = new PageHost(s.App).SignIn(staff);

        var page = host.Render<ManageGate>(p => p.Add(x => x.Id, s.Theater.Id));

        page.WaitForText("No showings are left today."); // the clock is days before the showing
        Assert.Empty(page.FindAll("input.gate-input"));
    }

    [Fact]
    public async Task Staff_with_no_gate_rights_are_turned_away()
    {
        var s = await TicketSalesTests.SetUpAsync();
        var staff = await s.App.CreateUserAsync("usher@example.com", s.Theater.Id);
        await s.App.GrantAsync(staff, ViewReports);
        await using var host = new PageHost(s.App).SignIn(staff);

        host.Render<ManageGate>(p => p.Add(x => x.Id, s.Theater.Id));

        Assert.EndsWith("Account/AccessDenied", host.Nav.Uri);
    }
}
