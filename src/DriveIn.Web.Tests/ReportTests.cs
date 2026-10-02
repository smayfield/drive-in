using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;
using static DriveIn.Web.Tests.TicketSalesTests;

namespace DriveIn.Web.Tests;

public class ReportTests
{
    // The setup's showing: Jaws, 8 PM Sep 5 in Chicago (01:00 UTC Sep 6), on a 7-spot screen.
    private static readonly DateOnly ShowDay = new(2026, 9, 5);
    private static readonly DateTimeOffset AfterTheShow = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private const string Visa = "pm_test_visa_4242_0001"; // a test card token (see TestCardTokens)

    private static ReportService Reports(Setup s) => s.App.Get<ReportService>();

    // A ticket written straight to the database, so each state and channel can be set up exactly.
    private static async Task AddTicketAsync(Setup s, int showtimeId, int row, int spot, decimal total, TicketStatus status = TicketStatus.Sold,
        decimal giftCard = 0m, bool gate = false, bool comp = false, bool admitted = false, string? option = "One occupant",
        params (string Name, decimal Effect)[] addOns)
    {
        await using var db = s.App.Db();
        db.Tickets.Add(new Ticket
        {
            ShowtimeId = showtimeId, Row = row, Spot = spot, SpotLabel = $"{row}-{spot}", Status = status,
            CreatedAt = s.App.Time.GetUtcNow(), HeldUntil = status == TicketStatus.Held ? s.App.Time.GetUtcNow().AddMinutes(10) : null,
            SoldAt = status == TicketStatus.Sold ? s.App.Time.GetUtcNow() : null,
            OptionName = comp ? null : option, OptionPrice = comp ? 0m : total - addOns.Sum(a => a.Effect), Total = total,
            GiftCardAmount = giftCard, SoldAtGate = gate, IsComp = comp, AdmittedAt = admitted ? s.App.Time.GetUtcNow() : null,
            AddOns = addOns.Select((a, i) => new TicketAddOn { Position = i, Name = a.Name, Kind = AddOnKind.Fee, Amount = a.Effect, Effect = a.Effect }).ToList(),
        });
        await db.SaveChangesAsync();
    }

    private static async Task<ClaimsPrincipal> EmployeeAsync(Setup s, params string[] permissions)
    {
        var employee = await s.App.CreateUserAsync($"emp{Guid.NewGuid():N}@example.com", employeeTheaterId: s.Theater.Id);
        if (permissions.Length > 0)
            await s.App.GrantAsync(employee, permissions);
        return Principals.For(employee);
    }

    // --- Who can see reports ---

    [Fact]
    public async Task Reports_need_the_view_reports_permission()
    {
        await using var s = await SetUpAsync();
        var without = await EmployeeAsync(s, ViewGiftCards, SellAtGate);
        var with = await EmployeeAsync(s, ViewReports);
        var admin = Principals.For(await s.App.CreateUserAsync("admin@example.com", admin: true), admin: true);
        var stranger = Principals.For(await s.App.CreateUserAsync("owner2@example.com"));

        await Assert.ThrowsAsync<AccessDeniedException>(() => Reports(s).GetSalesReportAsync(without, s.Theater.Id, ShowDay, ShowDay));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Reports(s).GetGiftCardReportAsync(without, s.Theater.Id, ShowDay, ShowDay));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Reports(s).GetSalesReportAsync(stranger, s.Theater.Id, ShowDay, ShowDay));
        foreach (var user in new[] { with, admin, s.OwnerPrincipal })
        {
            await Reports(s).GetSalesReportAsync(user, s.Theater.Id, ShowDay, ShowDay);
            await Reports(s).GetGiftCardReportAsync(user, s.Theater.Id, ShowDay, ShowDay);
        }
    }

    [Fact]
    public void New_managers_get_view_reports()
    {
        Assert.Contains(ViewReports, DefaultTheaterRoles.All.Single(r => r.Name == "Manager").Permissions);
        Assert.DoesNotContain(ViewReports, DefaultTheaterRoles.All.Single(r => r.Name == "Ticketing").Permissions);
    }

    [Theory]
    [InlineData("2026-09-05", "2026-09-04")]
    [InlineData("2026-01-01", "2027-01-02")]
    public async Task The_range_must_run_forwards_and_be_at_most_366_days(string from, string to)
    {
        await using var s = await SetUpAsync();

        await Assert.ThrowsAsync<AppValidationException>(() =>
            Reports(s).GetSalesReportAsync(s.OwnerPrincipal, s.Theater.Id, DateOnly.Parse(from), DateOnly.Parse(to)));
        await Reports(s).GetSalesReportAsync(s.OwnerPrincipal, s.Theater.Id, new(2026, 1, 1), new(2027, 1, 1));
    }

    // --- Sales and attendance ---

    [Fact]
    public async Task Sold_tickets_are_counted_by_channel_payment_option_and_add_on()
    {
        await using var s = await SetUpAsync();
        var id = s.Showing.Id;
        await AddTicketAsync(s, id, 1, 1, 10m, admitted: true);
        await AddTicketAsync(s, id, 1, 2, 30m, giftCard: 15m, option: "Car load", addOns: ("Outside food", 5m));
        await AddTicketAsync(s, id, 1, 3, 10m, gate: true, admitted: true);
        await AddTicketAsync(s, id, 2, 1, 0m, comp: true);
        // Not sales: a hold, a free admission request and a payment still going through.
        await AddTicketAsync(s, id, 2, 2, 10m, TicketStatus.Held);
        await AddTicketAsync(s, id, 2, 3, 0m, TicketStatus.Pending, comp: true);
        await AddTicketAsync(s, id, 2, 4, 10m, TicketStatus.Paying);
        s.App.Time.SetUtcNow(AfterTheShow);

        var report = await Reports(s).GetSalesReportAsync(s.OwnerPrincipal, s.Theater.Id, ShowDay, ShowDay);

        var t = report.Totals;
        Assert.Equal((1, 4, 1, 2), (t.Showings, t.Cars, t.Free, t.Admitted));
        Assert.Equal((50m, 15m, 35m), (t.Gross, t.GiftCardPaid, t.CardPaid));
        Assert.Equal(0.5, t.AttendanceRate);
        Assert.False(t.HasTestSales);
        Assert.Equal([("Online", 2, 40m), ("At the gate", 1, 10m), ("Free admission", 1, 0m)],
            report.Channels.Select(c => (c.Name, c.Count, c.Amount)));
        Assert.Equal([("One occupant", 2, 20m), ("Car load", 1, 25m)], report.Options.Select(o => (o.Name, o.Count, o.Amount)));
        Assert.Equal([("Outside food", 1, 5m)], report.AddOns.Select(a => (a.Name, a.Count, a.Amount)));

        var row = Assert.Single(report.Showings);
        Assert.Equal(("North", "Jaws", 7, 4, 1, 2, (int?)2), (row.Screen, row.Films, row.Capacity, row.Cars, row.Free, row.Admitted, row.NoShows));
        Assert.Equal((50m, 15m), (row.Gross, row.GiftCardPaid));
        Assert.Equal(new DateTime(2026, 9, 5, 20, 0, 0), row.StartsLocal);
        var day = Assert.Single(report.Days);
        Assert.Equal((ShowDay, 1, 4, 2, 50m), (day.Date, day.Showings, day.Cars, day.Admitted, day.Gross));
    }

    [Fact]
    public async Task No_shows_and_attendance_wait_until_the_showing_has_ended()
    {
        await using var s = await SetUpAsync();
        await AddTicketAsync(s, s.Showing.Id, 1, 1, 10m);

        var report = await Reports(s).GetSalesReportAsync(s.OwnerPrincipal, s.Theater.Id, ShowDay, ShowDay);

        Assert.Null(Assert.Single(report.Showings).NoShows);
        Assert.Null(report.Totals.AttendanceRate);
        Assert.Equal(1, report.Totals.Cars);
    }

    [Fact]
    public async Task Dates_are_the_theaters_local_dates()
    {
        await using var s = await SetUpAsync();
        await AddTicketAsync(s, s.Showing.Id, 1, 1, 10m);

        // 8 PM in Chicago is already Sep 6 in UTC, but it's a Sep 5 showing.
        var sep5 = await Reports(s).GetSalesReportAsync(s.OwnerPrincipal, s.Theater.Id, ShowDay, ShowDay);
        var sep6 = await Reports(s).GetSalesReportAsync(s.OwnerPrincipal, s.Theater.Id, ShowDay.AddDays(1), ShowDay.AddDays(1));

        Assert.Equal(1, sep5.Totals.Cars);
        Assert.Equal(ShowDay, Assert.Single(sep5.Days).Date);
        Assert.Equal((0, 0), (sep6.Totals.Showings, sep6.Totals.Cars));
        Assert.Empty(sep6.Showings);
    }

    [Fact]
    public async Task A_double_feature_counts_toward_each_film_and_showings_without_sales_are_listed()
    {
        await using var s = await SetUpAsync();
        var schedule = s.App.Get<ScheduleService>();
        int jaws;
        await using (var db = s.App.Db())
            jaws = (await db.Films.SingleAsync(f => f.Title == "Jaws")).Id;
        var alien = await schedule.AddFilmAsync(s.OwnerPrincipal, s.Theater.Id, new FilmInput("Alien", "R", 117));
        var twoFilms = await schedule.AddShowtimeAsync(s.OwnerPrincipal,
            new ShowtimeInput(s.Screen.Id, [alien.Id, jaws], ShowDay.AddDays(1), new TimeOnly(20, 0)));
        await schedule.AddShowtimeAsync(s.OwnerPrincipal, s.Screen.Id, alien.Id, ShowDay.AddDays(2), new TimeOnly(20, 0));
        await AddTicketAsync(s, s.Showing.Id, 1, 1, 10m);
        await AddTicketAsync(s, twoFilms.Id, 1, 1, 25m);
        await AddTicketAsync(s, twoFilms.Id, 1, 2, 10m);

        var report = await Reports(s).GetSalesReportAsync(s.OwnerPrincipal, s.Theater.Id, ShowDay, ShowDay.AddDays(2));

        Assert.Equal((3, 3, 45m), (report.Totals.Showings, report.Totals.Cars, report.Totals.Gross));
        Assert.Equal(["Jaws", "Alien + Jaws", "Alien"], report.Showings.Select(r => r.Films));
        Assert.Equal([("Jaws", 2, 3, 45m), ("Alien", 2, 2, 35m)], report.Films.Select(f => (f.Title, f.Showings, f.Cars, f.Gross)));
        Assert.Equal([(ShowDay, 1), (ShowDay.AddDays(1), 2), (ShowDay.AddDays(2), 0)], report.Days.Select(d => (d.Date, d.Cars)));
    }

    [Fact]
    public async Task Other_theaters_sales_are_not_included()
    {
        await using var s = await SetUpAsync();
        var other = await s.App.CreateTheaterAsync("Moonlight", s.Owner.Id);
        await using (var db = s.App.Db())
        {
            var screen = new Screen { TheaterId = other.Id, Name = "Only", RowSpots = [5] };
            var showing = new Showtime { Screen = screen, StartsAt = s.Showing.StartsAt, EndsAt = s.Showing.EndsAt };
            db.Showtimes.Add(showing);
            await db.SaveChangesAsync();
            db.Tickets.Add(new Ticket { ShowtimeId = showing.Id, Row = 1, Spot = 1, SpotLabel = "1", Status = TicketStatus.Sold, Total = 99m });
            await db.SaveChangesAsync();
        }
        await AddTicketAsync(s, s.Showing.Id, 1, 1, 10m);

        var report = await Reports(s).GetSalesReportAsync(s.OwnerPrincipal, s.Theater.Id, ShowDay, ShowDay);

        Assert.Equal((1, 1, 10m), (report.Totals.Showings, report.Totals.Cars, report.Totals.Gross));
    }

    // --- Gift cards ---

    [Fact]
    public async Task Gift_card_report_reconciles_what_was_owed_and_lists_cards_with_a_balance()
    {
        await using var s = await SetUpAsync();
        await s.App.Get<TheaterService>().UpdateGiftCardSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true);
        async Task<GiftCard> BuyCardAsync(string email, decimal amount) =>
            (await s.Sales.PurchaseGiftCardAsync(await BuyerAsync(s.App, email), s.Theater.Id,
                new GiftCardPurchaseInput(amount, "Sam", null, null, Visa), TestApp.BaseUri)).Card;
        async Task<string> SpendAsync(string email, GiftCard card, PriceOption option, int spot)
        {
            var buyer = await BuyerAsync(s.App, email);
            var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 2, spot);
            return (await s.Sales.PurchaseAsync(buyer, hold.TicketId, new PurchaseInput(option.Id, [], Visa, card.Code), TestApp.BaseUri)).Code;
        }

        // Sep 1: bought before the range.
        var early = await BuyCardAsync("a@example.com", 50m);
        // Sep 3 (the range is Sep 2 to 4): another bought; $10 of the first spent; a $20 card spent in full.
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 9, 3, 15, 0, 0, TimeSpan.Zero));
        var later = await BuyCardAsync("b@example.com", 20m);
        var spent = await BuyCardAsync("c@example.com", 10m);
        await SpendAsync("d@example.com", early, s.Single, 1);
        await SpendAsync("e@example.com", spent, s.Single, 2);
        // The rest of a $25 ticket is declined, so the $20 taken off the later card is put back.
        s.App.Payments.DeclineWith = "declined";
        await Assert.ThrowsAsync<AppValidationException>(() => SpendAsync("f@example.com", later, s.CarLoad, 3));
        s.App.Payments.DeclineWith = null;
        // Sep 5: after the range.
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 9, 5, 15, 0, 0, TimeSpan.Zero));
        await BuyCardAsync("g@example.com", 5m);

        var report = await Reports(s).GetGiftCardReportAsync(s.OwnerPrincipal, s.Theater.Id, new(2026, 9, 2), new(2026, 9, 4));

        Assert.Equal(50m, report.OwedAtStart);
        Assert.Equal((2, 30m), (report.SoldCount, report.SoldAmount));
        Assert.Equal(20m, report.Redeemed);
        Assert.Equal(60m, report.OwedAtEnd);
        Assert.Equal(report.OwedAtEnd, report.OwedAtStart + report.SoldAmount - report.Redeemed);

        // Right now: everything but the card that was spent in full.
        Assert.Equal([(early.Last4, 40m), (later.Last4, 20m), (report.Outstanding[2].Last4, 5m)],
            report.Outstanding.Select(c => (c.Last4, c.Balance)));
        Assert.DoesNotContain(report.Outstanding, c => c.Id == spent.Id);
        Assert.Equal(65m, report.OutstandingTotal);
        Assert.Equal(new DateTime(2026, 9, 3, 10, 0, 0), report.Outstanding[0].LastUsedLocal);
        Assert.Null(report.Outstanding[2].LastUsedLocal);

        var csv = ReportCsv.GiftCards(report);
        Assert.DoesNotContain(early.Code, csv);
        Assert.Contains("…" + early.Last4, csv);
    }

    // --- CSV ---

    [Fact]
    public void Csv_quotes_cells_and_defuses_formulas()
    {
        var csv = ReportCsv.Build(["Name", "Amount", "When"],
        [
            ["Smith, \"Pat\"", 12.5m, new DateTime(2026, 9, 5, 20, 0, 0)],
            ["=HYPERLINK(\"x\")", -2m, null],
            ["@sum", 3, new DateOnly(2026, 9, 5)],
            ["  +1", 0, "\tplain"],
        ]);

        Assert.Equal(
            "Name,Amount,When\r\n" +
            "\"Smith, \"\"Pat\"\"\",12.50,2026-09-05 20:00\r\n" +
            "\"'=HYPERLINK(\"\"x\"\")\",-2.00,\r\n" +
            "'@sum,3,2026-09-05\r\n" +
            "'  +1,0,'\tplain\r\n",
            csv);
        var bytes = ReportCsv.ToBytes(csv + "é");
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal(csv + "é", System.Text.Encoding.UTF8.GetString(bytes[3..]));
    }
}
