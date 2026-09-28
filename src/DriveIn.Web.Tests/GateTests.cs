using System.Security.Claims;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;
using static DriveIn.Web.Tests.TicketSalesTests;

namespace DriveIn.Web.Tests;

public class GateTests
{
    // 5 PM in Chicago on the day of the 8 PM showing (see TicketSalesTests.SetUpAsync).
    private static readonly DateTimeOffset ShowDayAfternoon = new(2026, 9, 5, 22, 0, 0, TimeSpan.Zero);

    private static async Task<ClaimsPrincipal> AttendantAsync(Setup s, params string[] permissions)
    {
        var employee = await s.App.CreateUserAsync($"gate{Guid.NewGuid():N}@example.com", employeeTheaterId: s.Theater.Id);
        if (permissions.Length > 0)
            await s.App.GrantAsync(employee, permissions);
        return Principals.For(employee);
    }

    private static async Task<TicketView> SellAtGateAsync(Setup s, ClaimsPrincipal attendant, int row = 1, int spot = 1, int? showtimeId = null)
    {
        var hold = await s.Sales.HoldAtGateAsync(attendant, showtimeId ?? s.Showing.Id, row, spot);
        return await s.Sales.SellAtGateAsync(attendant, hold.TicketId, s.Single.Id, []);
    }

    // --- Selling ---

    [Fact]
    public async Task An_attendant_sells_a_spot_at_the_gate_and_the_car_is_checked_in()
    {
        await using var s = await SetUpAsync();
        s.App.Time.SetUtcNow(ShowDayAfternoon);
        var attendant = await AttendantAsync(s, SellAtGate);
        var changes = new List<int>();
        s.App.Events.Changed += changes.Add;

        var hold = await s.Sales.HoldAtGateAsync(attendant, s.Showing.Id, 2, 2);
        var sold = await s.Sales.SellAtGateAsync(attendant, hold.TicketId, s.CarLoad.Id, [s.OutsideFood.Id]);

        var charge = Assert.Single(s.App.Payments.Charges);
        Assert.Equal(30m, charge.Amount);
        Assert.Null(charge.Card); // card-present, on the terminal
        var ticket = sold.Ticket;
        Assert.Equal((TicketStatus.Sold, "B2", 30m), (ticket.Status, ticket.SpotLabel, ticket.Total));
        Assert.Null(ticket.UserId);
        Assert.Equal(attendant.FindFirstValue(ClaimTypes.NameIdentifier), ticket.SoldById);
        Assert.Equal(ShowDayAfternoon, ticket.AdmittedAt);
        Assert.Matches($"^[{ShortCodes.Alphabet}]{{4}}$", ticket.ShortCode);
        Assert.Equal("Paid by card at the gate", TicketReceipt.PaidWith(ticket));
        Assert.Empty(s.App.Email.Sent);

        var buyer = await BuyerAsync(s.App);
        Assert.Equal(SpotState.Sold, (await s.Sales.GetAvailabilityAsync(buyer, s.Showing.Id))[2, 2]);
        Assert.Empty(await s.Sales.ListMyTicketsAsync(attendant));
        Assert.Equal([s.Showing.Id, s.Showing.Id], changes);
    }

    [Fact]
    public async Task The_gate_and_online_buyers_compete_for_the_same_spots()
    {
        await using var s = await SetUpAsync();
        s.App.Time.SetUtcNow(ShowDayAfternoon);
        var attendant = await AttendantAsync(s, SellAtGate);
        var buyer = await BuyerAsync(s.App);

        await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        var ex = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.HoldAtGateAsync(attendant, s.Showing.Id, 1, 1));
        Assert.Contains("choose another spot", ex.Message);

        await SellAtGateAsync(s, attendant, 1, 2);
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 2));
    }

    [Fact]
    public async Task The_gate_keeps_selling_after_the_showing_starts_until_it_ends()
    {
        await using var s = await SetUpAsync();
        var attendant = await AttendantAsync(s, SellAtGate);
        var buyer = await BuyerAsync(s.App);
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 9, 6, 2, 0, 0, TimeSpan.Zero)); // an hour in

        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1));
        Assert.True((await s.Sales.GetGateShowingAsync(attendant, s.Showing.Id)).OnSale);
        await SellAtGateAsync(s, attendant);

        s.App.Time.SetUtcNow(s.Showing.EndsAt);
        var ex = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.HoldAtGateAsync(attendant, s.Showing.Id, 1, 2));
        Assert.Contains("ended", ex.Message);
    }

    [Fact]
    public async Task The_gate_lists_todays_showings_with_open_spots()
    {
        await using var s = await SetUpAsync();
        var me = s.OwnerPrincipal;
        var film = (await s.App.Get<ScheduleService>().ListFilmsAsync(me, s.Theater.Id)).Single();
        var tomorrow = await s.App.Get<ScheduleService>().AddShowtimeAsync(me, s.Screen.Id, film.Id, new DateOnly(2026, 9, 6), new TimeOnly(20, 0));
        s.App.Time.SetUtcNow(ShowDayAfternoon);
        var attendant = await AttendantAsync(s, SellAtGate);
        await SellAtGateAsync(s, attendant);

        var listed = Assert.Single(await s.Sales.ListGateShowingsAsync(attendant, s.Theater.Id));
        Assert.Equal(s.Showing.Id, listed.Showing.Id);
        Assert.Equal((7, 1, 6), (listed.SpotCount, listed.Taken, listed.Open));

        // Still listed while it runs; gone once it ends, and tomorrow's appears once it's today.
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 9, 6, 2, 0, 0, TimeSpan.Zero)); // 9 PM Sep 5 in Chicago
        Assert.Equal([s.Showing.Id], (await s.Sales.ListGateShowingsAsync(attendant, s.Theater.Id)).Select(g => g.Showing.Id));
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 9, 6, 5, 30, 0, TimeSpan.Zero)); // 12:30 AM Sep 6 in Chicago
        Assert.Equal([tomorrow.Id], (await s.Sales.ListGateShowingsAsync(attendant, s.Theater.Id)).Select(g => g.Showing.Id));
    }

    [Fact]
    public async Task Without_a_payment_processor_the_gate_says_so_in_gate_terms()
    {
        await using var s = await SetUpAsync();
        s.App.Time.SetUtcNow(ShowDayAfternoon);
        s.App.Payments.IsAvailable = false;
        var attendant = await AttendantAsync(s, SellAtGate);

        var sale = await s.Sales.GetGateShowingAsync(attendant, s.Showing.Id);
        Assert.Equal("Card payments aren't set up yet, so tickets can't be sold at the gate.", sale.NotOnSaleReason);
        var ex = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.HoldAtGateAsync(attendant, s.Showing.Id, 1, 1));
        Assert.DoesNotContain("online", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_declined_card_at_the_gate_keeps_the_spot_held()
    {
        await using var s = await SetUpAsync();
        s.App.Time.SetUtcNow(ShowDayAfternoon);
        var attendant = await AttendantAsync(s, SellAtGate);
        var hold = await s.Sales.HoldAtGateAsync(attendant, s.Showing.Id, 1, 1);
        s.App.Payments.DeclineWith = "card declined";

        var ex = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.SellAtGateAsync(attendant, hold.TicketId, s.Single.Id, []));

        Assert.Contains("Try another card", ex.Message);
        Assert.Equal(SpotState.Mine, (await s.Sales.GetAvailabilityAsync(attendant, s.Showing.Id))[1, 1]);
    }

    [Fact]
    public async Task Selling_at_the_gate_requires_permission()
    {
        await using var s = await SetUpAsync();
        s.App.Time.SetUtcNow(ShowDayAfternoon);
        var admitOnly = await AttendantAsync(s, AdmitGuests);
        var seller = await AttendantAsync(s, SellAtGate);
        var hold = await s.Sales.HoldAtGateAsync(seller, s.Showing.Id, 1, 1);

        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.ListGateShowingsAsync(admitOnly, s.Theater.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.GetGateShowingAsync(admitOnly, s.Showing.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.HoldAtGateAsync(admitOnly, s.Showing.Id, 1, 2));
        var buyer = await BuyerAsync(s.App);
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.HoldAtGateAsync(buyer, s.Showing.Id, 1, 2));
        // An online buyer can't turn their own hold into an unpaid gate sale.
        var online = await s.Sales.HoldAsync(buyer, s.Showing.Id, 2, 1);
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.SellAtGateAsync(buyer, online.TicketId, s.Single.Id, []));
        // Nor sell someone else's hold.
        var otherSeller = await AttendantAsync(s, SellAtGate);
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.SellAtGateAsync(otherSeller, hold.TicketId, s.Single.Id, []));
        Assert.Empty(s.App.Payments.Charges);
    }

    // --- Checking in ---

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_ticket_bought_online_is_found_by_its_gate_code_ticket_code_or_link_and_checked_in(bool useCode, bool useLink)
    {
        await using var s = await SetUpAsync();
        var code = await SellAsync(s, await BuyerAsync(s.App));
        s.App.Time.SetUtcNow(ShowDayAfternoon.AddHours(1)); // 6 PM: gates are open
        var attendant = await AttendantAsync(s, AdmitGuests);
        string shortCode;
        await using (var db = s.App.Db())
            shortCode = (await db.Tickets.SingleAsync()).ShortCode!;
        var input = useCode ? code : useLink ? $"{TestApp.BaseUri}tickets/{code}?new=sent" : $" {shortCode.ToLowerInvariant()[..2]} {shortCode.ToLowerInvariant()[2..]} ";

        var found = Assert.Single(await s.Sales.FindAtGateAsync(attendant, s.Theater.Id, input));
        Assert.Null(found.AdmitProblem);
        Assert.Equal("A1", found.View.Ticket.SpotLabel);
        Assert.Equal("Jaws", found.View.Showing.Title);

        await s.Sales.AdmitAsync(attendant, found.View.Code);
        var again = Assert.Single(await s.Sales.FindAtGateAsync(attendant, s.Theater.Id, input));
        Assert.StartsWith("Already used", again.AdmitProblem);
    }

    [Fact]
    public async Task A_ticket_for_another_date_is_not_valid()
    {
        await using var s = await SetUpAsync();
        var code = await SellAsync(s, await BuyerAsync(s.App));
        var attendant = await AttendantAsync(s, AdmitGuests);

        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 9, 4, 23, 30, 0, TimeSpan.Zero)); // the evening before
        var early = Assert.Single(await s.Sales.FindAtGateAsync(attendant, s.Theater.Id, code));
        Assert.Equal("Not valid today: this ticket is for Sat, Sep 5 at 8:00 PM.", early.AdmitProblem);
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.AdmitAsync(attendant, code));

        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 9, 6, 23, 30, 0, TimeSpan.Zero)); // the evening after
        var late = Assert.Single(await s.Sales.FindAtGateAsync(attendant, s.Theater.Id, code));
        Assert.StartsWith("Not valid: this ticket was for Sat, Sep 5", late.AdmitProblem);
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.AdmitAsync(attendant, code));
    }

    [Fact]
    public async Task Earlier_the_same_day_the_ticket_says_when_the_gates_open()
    {
        await using var s = await SetUpAsync();
        var code = await SellAsync(s, await BuyerAsync(s.App));
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 9, 5, 17, 0, 0, TimeSpan.Zero)); // noon in Chicago

        var found = Assert.Single(await s.Sales.FindAtGateAsync(await AttendantAsync(s, AdmitGuests), s.Theater.Id, code));

        Assert.Equal("Too early: this ticket is for the 8:00 PM showing, and gates open 5:00 PM.", found.AdmitProblem);
    }

    [Fact]
    public async Task A_ticket_for_another_theater_is_not_valid_here()
    {
        await using var s = await SetUpAsync();
        var code = await SellAsync(s, await BuyerAsync(s.App));
        var other = await s.App.CreateTheaterAsync("Moonlight", s.Owner.Id);
        var otherAttendant = await s.App.CreateUserAsync("moon@example.com", employeeTheaterId: other.Id);
        await s.App.GrantAsync(otherAttendant, AdmitGuests);
        string shortCode;
        await using (var db = s.App.Db())
            shortCode = (await db.Tickets.SingleAsync()).ShortCode!;

        var ex = await Assert.ThrowsAsync<AppValidationException>(() =>
            s.Sales.FindAtGateAsync(Principals.For(otherAttendant), other.Id, code));
        Assert.Equal("Not valid here: this ticket is for Starlight.", ex.Message);
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.FindAtGateAsync(Principals.For(otherAttendant), other.Id, shortCode));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.AdmitAsync(Principals.For(otherAttendant), code));
    }

    [Fact]
    public async Task Checking_in_requires_permission_and_unknown_codes_are_reported()
    {
        await using var s = await SetUpAsync();
        var code = await SellAsync(s, await BuyerAsync(s.App));
        var sellOnly = await AttendantAsync(s, SellAtGate);
        var attendant = await AttendantAsync(s, AdmitGuests);

        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.FindAtGateAsync(sellOnly, s.Theater.Id, code));
        var ex = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.FindAtGateAsync(attendant, s.Theater.Id, "ZZZZ"));
        Assert.Contains("No ticket matches", ex.Message);
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.FindAtGateAsync(attendant, s.Theater.Id, "not-a-code"));
    }

    // --- Gate codes ---

    [Fact]
    public async Task Online_tickets_get_a_gate_code_on_the_receipt()
    {
        await using var s = await SetUpAsync();
        await SellAsync(s, await BuyerAsync(s.App));

        await using var db = s.App.Db();
        var shortCode = (await db.Tickets.SingleAsync()).ShortCode!;
        Assert.Matches($"^[{ShortCodes.Alphabet}]{{4}}$", shortCode);
        Assert.Contains(shortCode, s.App.Email.Sent.Single().Body);
        Assert.Contains("gate code", s.App.Email.Sent.Single().Body);
    }

    [Fact]
    public async Task Gate_codes_differ_between_upcoming_tickets()
    {
        await using var s = await SetUpAsync();
        s.App.Time.SetUtcNow(ShowDayAfternoon);
        var attendant = await AttendantAsync(s, SellAtGate);
        for (var spot = 1; spot <= 4; spot++)
            await SellAtGateAsync(s, attendant, 2, spot);
        for (var spot = 1; spot <= 3; spot++)
            await SellAtGateAsync(s, attendant, 1, spot);

        await using var db = s.App.Db();
        var codes = await db.Tickets.Select(t => t.ShortCode).ToListAsync();
        Assert.Equal(7, codes.Distinct().Count());
    }

    [Theory]
    [InlineData("k7qm", "K7QM")]
    [InlineData(" K7 QM ", "K7QM")]
    [InlineData("K7-QM", "K7QM")]
    [InlineData("K7Q", null)]
    [InlineData("K7QMX", null)]
    [InlineData("K0QM", null)] // no zero in the alphabet
    public void Gate_codes_are_read_forgivingly(string input, string? expected) =>
        Assert.Equal(expected, ShortCodes.Normalize(input));
}
