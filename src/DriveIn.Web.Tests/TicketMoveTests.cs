using System.Diagnostics.Metrics;
using System.Security.Claims;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using static DriveIn.Web.Authorization.TheaterPermissions;
using static DriveIn.Web.Tests.TicketSalesTests;
using static DriveIn.Web.Tests.VehicleSizeTests;

namespace DriveIn.Web.Tests;

public class TicketMoveTests
{
    // 5 PM in Chicago on the day of the 8 PM showing (see TicketSalesTests.SetUpAsync).
    private static readonly DateTimeOffset ShowDayAfternoon = new(2026, 9, 5, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_move_is_counted()
    {
        var (s, sold) = await SetUpWithSaleAsync();
        await using var _ = s;
        using var moved = new MetricCollector<long>(s.App.Get<IMeterFactory>(), DriveInMetrics.MeterName, "drivein.tickets.moved");

        await s.Sales.MoveAsync(s.OwnerPrincipal, sold.Code, 1, 3, VehicleSize.Standard);

        Assert.Equal(1, Assert.Single(moved.GetMeasurementSnapshot()).Value);
    }

    // Row B takes large vehicles; a buyer's car has spot A1, sold online.
    private static async Task<(Setup S, TicketView Ticket)> SetUpWithSaleAsync()
    {
        var s = await SetUpAsync();
        await MarkLargeAsync(s, RowBLarge);
        var buyer = await BuyerAsync(s.App);
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        var result = await s.Sales.PurchaseAsync(buyer, hold.TicketId, Buy(s.Single), TestApp.BaseUri);
        s.App.Time.SetUtcNow(ShowDayAfternoon);
        return (s, (await s.Sales.GetByCodeAsync(buyer, result.Code)).View);
    }

    private static async Task<ClaimsPrincipal> StaffAsync(Setup s, params string[] permissions)
    {
        var employee = await s.App.CreateUserAsync($"staff{Guid.NewGuid():N}@example.com", employeeTheaterId: s.Theater.Id);
        if (permissions.Length > 0)
            await s.App.GrantAsync(employee, permissions);
        return Principals.For(employee);
    }

    private static async Task AddTicketAsync(Setup s, int row, int spot, TicketStatus status, DateTimeOffset? heldUntil = null)
    {
        await using var db = s.App.Db();
        db.Tickets.Add(new Ticket
        {
            ShowtimeId = s.Showing.Id, Row = row, Spot = spot, SpotLabel = SpotLabels.Spot(SpotLabelScheme.LetterNumber, row, spot),
            Status = status, HeldUntil = heldUntil, CreatedAt = s.App.Time.GetUtcNow(),
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Gate_staff_move_a_car_that_turns_out_to_be_large_to_a_large_spot()
    {
        var (s, sold) = await SetUpWithSaleAsync();
        await using var _ = s;
        var staff = await StaffAsync(s, AdmitGuests, MoveTickets);
        var changes = new List<int>();
        s.App.Events.Changed += changes.Add;

        var moved = await s.Sales.MoveAsync(staff, sold.Code, 2, 3, VehicleSize.Large);

        var t = moved.Ticket;
        Assert.Equal((2, 3, "B3", VehicleSize.Large), (t.Row, t.Spot, t.SpotLabel, t.VehicleSize));
        Assert.Equal((sold.Code, sold.Ticket.ShortCode, sold.Ticket.Total), (t.Code, t.ShortCode, t.Total));
        Assert.Equal([s.Showing.Id], changes);
        var buyer = await BuyerAsync(s.App, "someone@example.com");
        var spots = await s.Sales.GetAvailabilityAsync(buyer, s.Showing.Id);
        Assert.Equal((SpotState.Available, SpotState.Sold), (spots[1, 1], spots[2, 3]));

        await using var db = s.App.Db();
        var log = await db.TicketMoves.SingleAsync();
        Assert.Equal((sold.Ticket.Id, "A1", VehicleSize.Standard, "B3", VehicleSize.Large, ShowDayAfternoon),
            (log.TicketId, log.FromLabel, log.FromVehicleSize, log.ToLabel, log.ToVehicleSize, log.MovedAt));
        Assert.Equal(staff.FindFirstValue(ClaimTypes.NameIdentifier), log.MovedById);

        // Checking in still works, at the new spot.
        await s.Sales.AdmitAsync(staff, sold.Code);
    }

    [Fact]
    public async Task A_car_already_checked_in_can_still_be_moved_and_stays_checked_in()
    {
        var (s, sold) = await SetUpWithSaleAsync();
        await using var _ = s;
        await s.Sales.AdmitAsync(s.OwnerPrincipal, sold.Code);

        var found = Assert.Single(await s.Sales.FindAtGateAsync(s.OwnerPrincipal, s.Theater.Id, sold.Code));
        Assert.NotNull(found.AdmitProblem); // already used
        Assert.True(found.CanMove);
        Assert.Null(found.MoveProblem);

        var moved = await s.Sales.MoveAsync(s.OwnerPrincipal, sold.Code, 1, 3, VehicleSize.Standard);
        Assert.Equal(("A3", ShowDayAfternoon), (moved.Ticket.SpotLabel, moved.Ticket.AdmittedAt));
    }

    [Fact]
    public async Task Moving_needs_the_move_tickets_permission()
    {
        var (s, sold) = await SetUpWithSaleAsync();
        await using var _ = s;
        var admitOnly = await StaffAsync(s, AdmitGuests, SellAtGate);
        var moveOnly = await StaffAsync(s, MoveTickets);
        var nothing = await StaffAsync(s);

        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.MoveAsync(admitOnly, sold.Code, 1, 2, VehicleSize.Standard));
        Assert.False(Assert.Single(await s.Sales.FindAtGateAsync(admitOnly, s.Theater.Id, sold.Code)).CanMove);

        // Moving alone is enough to look the ticket up, but not to check it in.
        var found = Assert.Single(await s.Sales.FindAtGateAsync(moveOnly, s.Theater.Id, sold.Code));
        Assert.Equal((false, true), (found.CanAdmit, found.CanMove));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.FindAtGateAsync(nothing, s.Theater.Id, sold.Code));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.AdmitAsync(moveOnly, sold.Code));

        await s.Sales.MoveAsync(moveOnly, sold.Code, 1, 2, VehicleSize.Standard);
    }

    [Fact]
    public async Task Another_theaters_staff_cant_move_a_ticket()
    {
        var (s, sold) = await SetUpWithSaleAsync();
        await using var _ = s;
        var otherOwner = await s.App.CreateUserAsync("other@example.com");
        await s.App.CreateTheaterAsync("Other", otherOwner.Id);

        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            s.Sales.MoveAsync(Principals.For(otherOwner), sold.Code, 1, 2, VehicleSize.Standard));
    }

    [Theory]
    [InlineData(TicketStatus.Sold)]
    [InlineData(TicketStatus.Held)]
    [InlineData(TicketStatus.Paying)]
    [InlineData(TicketStatus.Pending)]
    public async Task A_ticket_can_only_move_to_a_spot_thats_available_now(TicketStatus taken)
    {
        var (s, sold) = await SetUpWithSaleAsync();
        await using var _ = s;
        await AddTicketAsync(s, 2, 1, taken, taken is TicketStatus.Held or TicketStatus.Paying ? ShowDayAfternoon.AddMinutes(5) : null);

        var ex = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.MoveAsync(s.OwnerPrincipal, sold.Code, 2, 1, VehicleSize.Large));

        Assert.Contains("B1", ex.Message);
        await using var db = s.App.Db();
        Assert.Equal("A1", (await db.Tickets.SingleAsync(t => t.Id == sold.Ticket.Id)).SpotLabel);
        Assert.Empty(await db.TicketMoves.ToListAsync());
    }

    [Fact]
    public async Task A_spot_whose_hold_ran_out_is_available()
    {
        var (s, sold) = await SetUpWithSaleAsync();
        await using var _ = s;
        await AddTicketAsync(s, 2, 1, TicketStatus.Held, ShowDayAfternoon.AddMinutes(-1));

        var moved = await s.Sales.MoveAsync(s.OwnerPrincipal, sold.Code, 2, 1, VehicleSize.Large);

        Assert.Equal("B1", moved.Ticket.SpotLabel);
        await using var db = s.App.Db();
        Assert.Equal(sold.Ticket.Id, (await db.Tickets.SingleAsync(t => t.Row == 2 && t.Spot == 1)).Id);
    }

    [Fact]
    public async Task A_large_vehicle_can_only_move_to_a_large_spot()
    {
        var (s, sold) = await SetUpWithSaleAsync();
        await using var _ = s;

        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.MoveAsync(s.OwnerPrincipal, sold.Code, 1, 2, VehicleSize.Large));
        // Staying put isn't a move, and a large vehicle doesn't fit there either.
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.MoveAsync(s.OwnerPrincipal, sold.Code, 1, 1, VehicleSize.Standard));
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.MoveAsync(s.OwnerPrincipal, sold.Code, 1, 1, VehicleSize.Large));
        await Assert.ThrowsAsync<NotFoundException>(() => s.Sales.MoveAsync(s.OwnerPrincipal, sold.Code, 1, 4, VehicleSize.Standard));
    }

    [Fact]
    public async Task A_ticket_whose_showing_has_ended_cant_be_moved()
    {
        var (s, sold) = await SetUpWithSaleAsync();
        await using var _ = s;
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));

        var ex = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.MoveAsync(s.OwnerPrincipal, sold.Code, 1, 2, VehicleSize.Standard));

        Assert.Contains("ended", ex.Message);
    }
}
