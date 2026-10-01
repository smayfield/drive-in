using DriveIn.Web.Components.Pages.Manage;
using DriveIn.Web.Services;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests.Pages;

public class ManageReportsPageTests
{
    private static readonly CardInput Visa = new("Pat Buyer", "4242 4242 4242 4242", 12, 2030, "123");

    // A $10 ticket for the Sep 5 showing and a $50 gift card, then the clock moves to Sep 10.
    private static async Task<TicketSalesTests.Setup> WithSalesAsync()
    {
        var s = await TicketSalesTests.SetUpAsync();
        var buyer = await TicketSalesTests.BuyerAsync(s.App);
        await TicketSalesTests.SellAsync(s, buyer);
        await s.App.Get<TheaterService>().UpdateGiftCardSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true);
        await s.Sales.PurchaseGiftCardAsync(buyer, s.Theater.Id, new GiftCardPurchaseInput(50m, "Sam", null, null, Visa), TestApp.BaseUri);
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 9, 10, 17, 0, 0, TimeSpan.Zero));
        return s;
    }

    [Fact]
    public async Task This_months_sales_and_gift_cards_are_shown_with_csv_links()
    {
        var s = await WithSalesAsync();
        await using var host = new PageHost(s.App).SignIn(s.Owner);

        var page = host.Render<ManageReports>(p => p.Add(x => x.Id, s.Theater.Id));

        page.WaitForText("Sep 1, 2026 to Sep 10, 2026.");
        var text = page.Text();
        Assert.Contains("Cars 1 1 showing", text);
        Assert.Contains("Ticket sales $10.00", text);
        Assert.Contains("Gift cards sold $50.00 1 card", text);
        Assert.Contains("Sat Sep 5, 2026", text);
        Assert.Contains("One occupant 1 $10.00", text);
        Assert.Contains("1 card · $50.00 not yet spent.", text);
        Assert.Contains($"manage/{s.Theater.Id}/reports/days.csv?from=2026-09-01&amp;to=2026-09-10", page.Markup);
    }

    [Fact]
    public async Task Presets_and_chosen_dates_change_the_range()
    {
        var s = await WithSalesAsync();
        await using var host = new PageHost(s.App).SignIn(s.Owner);
        var page = host.Render<ManageReports>(p => p.Add(x => x.Id, s.Theater.Id));
        page.WaitForText("Sep 1, 2026 to Sep 10, 2026.");

        page.ClickButton("Today");
        page.WaitForText("Thursday, Sep 10, 2026.");
        Assert.Contains("No showings in these dates.", page.Text());

        page.ClickButton("Last month");
        page.WaitForText("Aug 1, 2026 to Aug 31, 2026.");

        page.ClickButton("Show"); // the pickers hold the range on screen
        page.WaitForText("Aug 1, 2026 to Aug 31, 2026.");
        Assert.DoesNotContain("mud-alert-text-error", page.Markup);
    }

    [Fact]
    public async Task Staff_without_report_rights_are_turned_away()
    {
        var s = await TicketSalesTests.SetUpAsync();
        var staff = await s.App.CreateUserAsync("staff@example.com", s.Theater.Id);
        await s.App.GrantAsync(staff, SellAtGate);
        await using var host = new PageHost(s.App).SignIn(staff);

        host.Render<ManageReports>(p => p.Add(x => x.Id, s.Theater.Id));

        Assert.EndsWith("Account/AccessDenied", host.Nav.Uri);
    }
}
