using System.Diagnostics.Metrics;
using System.Security.Claims;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using static DriveIn.Web.Authorization.TheaterPermissions;
using static DriveIn.Web.Tests.TicketSalesTests;

namespace DriveIn.Web.Tests;

// The offline gate page's admit list and the sync of check-ins made without a signal.
public class OfflineGateTests
{
    // 5 PM in Chicago on the day of the 8 PM showing: gates are open (see TicketSalesTests.SetUpAsync).
    private static readonly DateTimeOffset GatesOpen = new(2026, 9, 5, 22, 0, 0, TimeSpan.Zero);

    private static async Task<ClaimsPrincipal> StaffAsync(Setup s, params string[] permissions)
    {
        var employee = await s.App.CreateUserAsync($"gate{Guid.NewGuid():N}@example.com", employeeTheaterId: s.Theater.Id);
        if (permissions.Length > 0)
            await s.App.GrantAsync(employee, permissions);
        return Principals.For(employee);
    }

    private sealed record Sold(Ticket Ticket, string Code);

    private static async Task<Sold> BuyOnlineAsync(Setup s, int row = 1, int spot = 1)
    {
        var buyer = await BuyerAsync(s.App, $"buyer{Guid.NewGuid():N}@example.com");
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, row, spot);
        var code = (await s.Sales.PurchaseAsync(buyer, hold.TicketId, Buy(s.Single), TestApp.BaseUri)).Code;
        await using var db = s.App.Db();
        return new Sold(await db.Tickets.AsNoTracking().SingleAsync(t => t.Code == code), code);
    }

    private static OfflineAdmission Admission(Sold sold, DateTimeOffset at, Guid? id = null) =>
        new(sold.Ticket.Id, id ?? Guid.NewGuid(), at, sold.Ticket.SpotLabel);

    // --- The admit list ---

    [Fact]
    public async Task The_list_has_todays_showings_and_their_sold_tickets_with_hashed_codes_only()
    {
        await using var s = await SetUpAsync();
        var sold = await BuyOnlineAsync(s, 2, 3);
        s.App.Time.SetUtcNow(GatesOpen);
        var attendant = await StaffAsync(s, AdmitGuests);

        var list = await s.Sales.GetOfflineGateListAsync(attendant, s.Theater.Id);

        Assert.Equal(s.Theater.Name, list.Theater);
        var showing = Assert.Single(list.Showings);
        Assert.Equal(s.Showing.Id, showing.Id);
        Assert.Equal(s.Showing.StartsAt.AddHours(-TicketSalesService.AdmitOpensHoursBefore), showing.GatesOpenAt);
        Assert.Equal("5:00 PM", showing.GatesOpenLabel);
        var ticket = Assert.Single(list.Tickets);
        Assert.Equal((sold.Ticket.Id, "B3", "online", false), (ticket.Id, ticket.Spot, ticket.Kind, ticket.Large));
        Assert.Equal(sold.Ticket.ShortCode, ticket.ShortCode);
        Assert.Equal(TicketSalesService.HashTicketCode(sold.Code), ticket.CodeHash);
        Assert.Equal(64, ticket.CodeHash.Length);
        Assert.DoesNotContain(sold.Code, System.Text.Json.JsonSerializer.Serialize(list));
        Assert.Null(ticket.AdmittedAt);
    }

    [Fact]
    public async Task The_list_leaves_out_other_days_showings_holds_and_other_theaters()
    {
        await using var s = await SetUpAsync();
        await BuyOnlineAsync(s);
        var buyer = await BuyerAsync(s.App, "holder@example.com");
        await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 2);
        var attendant = await StaffAsync(s, AdmitGuests);

        // Sep 1: the Sep 5 showing isn't today.
        var early = await s.Sales.GetOfflineGateListAsync(attendant, s.Theater.Id);
        Assert.Empty(early.Showings);
        Assert.Empty(early.Tickets);

        s.App.Time.SetUtcNow(GatesOpen);
        var today = await s.Sales.GetOfflineGateListAsync(attendant, s.Theater.Id);
        Assert.Single(today.Tickets); // the hold isn't a ticket to admit

        var other = await s.App.CreateTheaterAsync("Elsewhere");
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.GetOfflineGateListAsync(attendant, other.Id));
    }

    [Fact]
    public async Task The_list_and_sync_need_admit_guests()
    {
        await using var s = await SetUpAsync();
        var sold = await BuyOnlineAsync(s);
        s.App.Time.SetUtcNow(GatesOpen);
        var seller = await StaffAsync(s, SellAtGate, MoveTickets);
        var customer = await BuyerAsync(s.App, "customer@example.com");

        foreach (var user in new[] { seller, customer, Principals.Anonymous })
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.GetOfflineGateListAsync(user, s.Theater.Id));
            await Assert.ThrowsAsync<AccessDeniedException>(() =>
                s.Sales.SyncOfflineAdmissionsAsync(user, s.Theater.Id, [Admission(sold, GatesOpen)]));
        }
        await using var db = s.App.Db();
        Assert.Null((await db.Tickets.SingleAsync(t => t.Id == sold.Ticket.Id)).AdmittedAt);
    }

    // --- Syncing check-ins ---

    [Fact]
    public async Task A_check_in_made_offline_is_applied_at_the_time_the_car_came_in_even_after_the_showing_ended()
    {
        await using var s = await SetUpAsync();
        var sold = await BuyOnlineAsync(s);
        var attendant = await StaffAsync(s, AdmitGuests);
        var cameIn = GatesOpen.AddHours(2);
        // The signal came back after the showing ended.
        s.App.Time.SetUtcNow(s.Showing.EndsAt.AddMinutes(30));

        var result = Assert.Single(await s.Sales.SyncOfflineAdmissionsAsync(attendant, s.Theater.Id, [Admission(sold, cameIn)]));

        Assert.Equal(OfflineSyncOutcomes.Admitted, result.Outcome);
        Assert.Null(result.CurrentSpot);
        await using var db = s.App.Db();
        Assert.Equal(cameIn, (await db.Tickets.SingleAsync(t => t.Id == sold.Ticket.Id)).AdmittedAt);
    }

    [Fact]
    public async Task Syncing_the_same_check_in_again_is_harmless()
    {
        await using var s = await SetUpAsync();
        var sold = await BuyOnlineAsync(s);
        s.App.Time.SetUtcNow(GatesOpen.AddHours(1));
        var attendant = await StaffAsync(s, AdmitGuests);
        var admission = Admission(sold, GatesOpen.AddMinutes(30));
        using var synced = new MetricCollector<long>(s.App.Get<IMeterFactory>(), DriveInMetrics.MeterName, "drivein.gate.offline_admissions");
        using var admitted = new MetricCollector<long>(s.App.Get<IMeterFactory>(), DriveInMetrics.MeterName, "drivein.tickets.admitted");

        await s.Sales.SyncOfflineAdmissionsAsync(attendant, s.Theater.Id, [admission]);
        // The answer was lost, so the device sends it again.
        var again = Assert.Single(await s.Sales.SyncOfflineAdmissionsAsync(attendant, s.Theater.Id, [admission]));

        Assert.Equal(OfflineSyncOutcomes.AlreadySynced, again.Outcome);
        Assert.Null(again.Message);
        var count = Assert.Single(synced.GetMeasurementSnapshot());
        Assert.Equal("synced", count.Tags["outcome"]);
        Assert.Equal("offline", Assert.Single(admitted.GetMeasurementSnapshot()).Tags["how"]);
    }

    [Fact]
    public async Task A_ticket_already_used_elsewhere_is_a_conflict_and_keeps_the_first_check_in()
    {
        await using var s = await SetUpAsync();
        var sold = await BuyOnlineAsync(s);
        s.App.Time.SetUtcNow(GatesOpen.AddHours(1));
        var attendant = await StaffAsync(s, AdmitGuests);
        await s.Sales.AdmitAsync(attendant, sold.Code); // the online gate, at 6 PM
        using var synced = new MetricCollector<long>(s.App.Get<IMeterFactory>(), DriveInMetrics.MeterName, "drivein.gate.offline_admissions");

        var result = Assert.Single(await s.Sales.SyncOfflineAdmissionsAsync(attendant, s.Theater.Id,
            [Admission(sold, GatesOpen.AddMinutes(45))]));

        Assert.Equal(OfflineSyncOutcomes.AlreadyUsed, result.Outcome);
        Assert.Contains("Already used", result.Message);
        Assert.Contains("6:00 PM", result.Message);
        Assert.Equal("conflict", Assert.Single(synced.GetMeasurementSnapshot()).Tags["outcome"]);
        await using var db = s.App.Db();
        Assert.Equal(GatesOpen.AddHours(1), (await db.Tickets.SingleAsync(t => t.Id == sold.Ticket.Id)).AdmittedAt);
    }

    [Fact]
    public async Task Two_devices_checking_in_the_same_ticket_admit_it_once()
    {
        await using var s = await SetUpAsync();
        var sold = await BuyOnlineAsync(s);
        s.App.Time.SetUtcNow(GatesOpen.AddHours(1));
        var attendant = await StaffAsync(s, AdmitGuests);

        var first = await s.Sales.SyncOfflineAdmissionsAsync(attendant, s.Theater.Id, [Admission(sold, GatesOpen.AddMinutes(10))]);
        var second = await s.Sales.SyncOfflineAdmissionsAsync(attendant, s.Theater.Id, [Admission(sold, GatesOpen.AddMinutes(20))]);

        Assert.Equal(OfflineSyncOutcomes.Admitted, Assert.Single(first).Outcome);
        Assert.Equal(OfflineSyncOutcomes.AlreadyUsed, Assert.Single(second).Outcome);
    }

    [Fact]
    public async Task A_check_in_outside_the_ticket_window_is_a_conflict()
    {
        await using var s = await SetUpAsync();
        var sold = await BuyOnlineAsync(s);
        s.App.Time.SetUtcNow(GatesOpen.AddHours(1));
        var attendant = await StaffAsync(s, AdmitGuests);

        // A device clock a day behind: before the gates opened.
        var result = Assert.Single(await s.Sales.SyncOfflineAdmissionsAsync(attendant, s.Theater.Id,
            [Admission(sold, GatesOpen.AddDays(-1))]));

        Assert.Equal(OfflineSyncOutcomes.NotValid, result.Outcome);
        Assert.StartsWith("When it was checked in: Not valid today", result.Message);
        await using var db = s.App.Db();
        Assert.Null((await db.Tickets.SingleAsync(t => t.Id == sold.Ticket.Id)).AdmittedAt);
    }

    [Fact]
    public async Task A_device_clock_ahead_of_the_server_admits_as_of_now()
    {
        await using var s = await SetUpAsync();
        var sold = await BuyOnlineAsync(s);
        s.App.Time.SetUtcNow(GatesOpen.AddHours(1));
        var attendant = await StaffAsync(s, AdmitGuests);

        var result = Assert.Single(await s.Sales.SyncOfflineAdmissionsAsync(attendant, s.Theater.Id,
            [Admission(sold, GatesOpen.AddHours(5))]));

        Assert.Equal(OfflineSyncOutcomes.Admitted, result.Outcome);
        await using var db = s.App.Db();
        Assert.Equal(GatesOpen.AddHours(1), (await db.Tickets.SingleAsync(t => t.Id == sold.Ticket.Id)).AdmittedAt);
    }

    [Fact]
    public async Task A_moved_ticket_is_admitted_and_reports_its_new_spot()
    {
        await using var s = await SetUpAsync();
        var sold = await BuyOnlineAsync(s);
        s.App.Time.SetUtcNow(GatesOpen.AddHours(1));
        var attendant = await StaffAsync(s, AdmitGuests);
        await using (var db = s.App.Db())
        {
            var ticket = await db.Tickets.SingleAsync(t => t.Id == sold.Ticket.Id);
            (ticket.Row, ticket.Spot, ticket.SpotLabel) = (2, 4, "B4");
            await db.SaveChangesAsync();
        }

        var result = Assert.Single(await s.Sales.SyncOfflineAdmissionsAsync(attendant, s.Theater.Id,
            [Admission(sold, GatesOpen.AddMinutes(30))]));

        Assert.Equal((OfflineSyncOutcomes.Admitted, "B4"), (result.Outcome, result.CurrentSpot));
    }

    [Fact]
    public async Task A_ticket_that_is_gone_or_another_theaters_is_not_found()
    {
        await using var s = await SetUpAsync();
        var sold = await BuyOnlineAsync(s);
        s.App.Time.SetUtcNow(GatesOpen.AddHours(1));
        var attendant = await StaffAsync(s, AdmitGuests);

        var other = await s.App.CreateTheaterAsync("Elsewhere", s.Owner.Id);
        var results = await s.Sales.SyncOfflineAdmissionsAsync(s.OwnerPrincipal, other.Id, [Admission(sold, GatesOpen)]);
        Assert.Equal(OfflineSyncOutcomes.NotFound, Assert.Single(results).Outcome);

        await using (var db = s.App.Db())
        {
            db.Tickets.Remove(await db.Tickets.SingleAsync(t => t.Id == sold.Ticket.Id));
            await db.SaveChangesAsync();
        }
        results = await s.Sales.SyncOfflineAdmissionsAsync(attendant, s.Theater.Id, [Admission(sold, GatesOpen)]);
        Assert.Equal(OfflineSyncOutcomes.NotFound, Assert.Single(results).Outcome);
    }

    [Fact]
    public async Task A_sync_is_limited_in_size_and_every_check_in_needs_an_id()
    {
        await using var s = await SetUpAsync();
        var sold = await BuyOnlineAsync(s);
        var attendant = await StaffAsync(s, AdmitGuests);

        var tooMany = Enumerable.Range(0, TicketSalesService.MaxOfflineSyncBatch + 1).Select(_ => Admission(sold, GatesOpen)).ToList();
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.SyncOfflineAdmissionsAsync(attendant, s.Theater.Id, tooMany));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            s.Sales.SyncOfflineAdmissionsAsync(attendant, s.Theater.Id, [Admission(sold, GatesOpen, Guid.Empty)]));
    }
}
