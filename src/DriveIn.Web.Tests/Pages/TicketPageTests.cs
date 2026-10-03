using DriveIn.Web.Components.Pages.Tickets;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests.Pages;

public class TicketPageTests
{
    private const string Visa = "pm_test_visa_4242_0001"; // a test card token (see TestCardTokens)

    [Fact]
    public async Task My_tickets_lists_upcoming_and_past_tickets_and_gift_cards()
    {
        await using var s = await TicketSalesTests.SetUpAsync();
        var buyer = await TicketSalesTests.BuyerAsync(s.App);
        var code = await TicketSalesTests.SellAsync(s, buyer);
        await s.App.Get<TheaterService>().UpdateGiftCardSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true);
        await s.Sales.PurchaseGiftCardAsync(buyer, s.Theater.Id, new GiftCardPurchaseInput(50m, "Sam", null, null, Visa), TestApp.BaseUri);
        await using var host = new PageHost(s.App).SignIn(buyer);

        var page = host.Render<MyTickets>();

        page.WaitForText("Upcoming");
        var text = page.Text();
        Assert.Contains("Jaws", text);
        Assert.Contains("Starlight · North", text);
        Assert.Contains("Spot A1", text);
        Assert.Contains("My gift cards", text);
        Assert.Contains("You $50.00 $50.00", text);
        Assert.Contains($"tickets/{code}", page.Markup);

        // After the showing it moves to Past.
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        var later = host.Render<MyTickets>();
        later.WaitForText("Past");
        Assert.Contains("No upcoming showings.", later.Text());
        Assert.Contains("Jaws · Starlight · Sep 5, 2026 · spot A1", later.Text());
    }

    [Fact]
    public async Task Someone_with_no_tickets_is_pointed_to_the_showings()
    {
        await using var app = new TestApp();
        await using var host = new PageHost(app).SignIn(await TicketSalesTests.BuyerAsync(app));

        var page = host.Render<MyTickets>();

        page.WaitForText("You haven't bought any tickets yet.");
    }

    [Fact]
    public async Task The_buyer_sees_their_ticket_with_its_qr_code_and_can_resend_the_receipt()
    {
        await using var s = await TicketSalesTests.SetUpAsync();
        var buyer = await TicketSalesTests.BuyerAsync(s.App);
        var code = await TicketSalesTests.SellAsync(s, buyer);
        await using var host = new PageHost(s.App).SignIn(buyer);
        host.Nav.NavigateTo($"tickets/{code}?new=sent");

        var page = host.Render<TicketDetails>(p => p.Add(x => x.Code, code));

        page.WaitForText("You're all set! We've emailed your receipt to buyer@example.com.");
        var text = page.Text();
        Assert.Contains("Saturday, September 5, 2026 · 8:00 PM", text);
        Assert.Contains("One occupant $10.00", text);
        Assert.Contains("Total $10.00", text);
        Assert.Contains("data:image/png;base64,", page.Markup);
        Assert.DoesNotContain("Gate check", text);

        s.App.Email.Sent.Clear();
        page.ClickButton("Email my receipt again");

        page.WaitForText("Receipt sent to buyer@example.com.");
        Assert.Single(s.App.Email.Sent);
    }

    [Fact]
    public async Task A_receipt_that_couldnt_be_sent_says_so_and_a_failed_resend_shows_an_error()
    {
        await using var s = await TicketSalesTests.SetUpAsync();
        var buyer = await TicketSalesTests.BuyerAsync(s.App);
        var code = await TicketSalesTests.SellAsync(s, buyer);
        await using var host = new PageHost(s.App).SignIn(buyer);
        host.Nav.NavigateTo($"tickets/{code}?new=unsent");
        var page = host.Render<TicketDetails>(p => p.Add(x => x.Code, code));
        page.WaitForText("we couldn't email your receipt just now");

        s.App.Email.FailWith = new InvalidOperationException("SES is down");
        page.ClickButton("Email my receipt again");

        page.WaitForText("We couldn't send the email just now. Please try again later.");
    }

    [Fact]
    public async Task Gate_staff_scanning_the_ticket_can_admit_the_car()
    {
        await using var s = await TicketSalesTests.SetUpAsync();
        var code = await TicketSalesTests.SellAsync(s, await TicketSalesTests.BuyerAsync(s.App));
        var gate = await s.App.CreateUserAsync("gate@example.com", s.Theater.Id);
        await s.App.GrantAsync(gate, AdmitGuests);
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 9, 5, 22, 0, 0, TimeSpan.Zero));
        await using var host = new PageHost(s.App).SignIn(gate);
        var page = host.Render<TicketDetails>(p => p.Add(x => x.Code, code));
        page.WaitForText("Valid: spot A1");
        Assert.DoesNotContain("data:image/png", page.Markup); // the QR is for the buyer

        page.ClickButton("Admit car");

        page.WaitForText("Admitted. Enjoy the show!");
        Assert.Contains("Don't admit", page.Text());
        Assert.Contains("Used: admitted Sep 5 5:00 PM", page.Text());
        await using var db = s.App.Db();
        Assert.NotNull((await db.Tickets.SingleAsync()).AdmittedAt);
    }

    [Fact]
    public async Task An_unknown_ticket_shows_an_error()
    {
        await using var app = new TestApp();
        await using var host = new PageHost(app).SignIn(await TicketSalesTests.BuyerAsync(app));

        var page = host.Render<TicketDetails>(p => p.Add(x => x.Code, "nope"));

        page.WaitForAssertion(() => Assert.Contains("mud-alert-text-error", page.Markup));
    }
}
