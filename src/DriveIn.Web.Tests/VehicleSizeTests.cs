using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;
using static DriveIn.Web.Tests.TicketSalesTests;

namespace DriveIn.Web.Tests;

public class VehicleSizeTests
{
    // The test screen has rows A (3 spots) and B (4 spots); these mark row B for large vehicles.
    internal static readonly int[] RowBLarge = [201, 202, 203, 204];

    internal static Task MarkLargeAsync(Setup s, params int[] keys) =>
        s.App.Get<ScreenService>().UpdateAsync(s.OwnerPrincipal, s.Screen.Id,
            new ScreenLayoutInput("North", SpotLabelScheme.LetterNumber, [3, 4], keys));

    [Fact]
    public async Task A_large_vehicle_can_only_hold_a_spot_marked_for_large_vehicles()
    {
        await using var s = await SetUpAsync();
        await MarkLargeAsync(s, RowBLarge);
        var buyer = await BuyerAsync(s.App);

        var ex = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 2, VehicleSize.Large));
        Assert.Contains("A2", ex.Message);
        Assert.Contains("marked L", ex.Message);

        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 2, 2, VehicleSize.Large);
        Assert.Equal(VehicleSize.Large, hold.VehicleSize);
        var result = await s.Sales.PurchaseAsync(buyer, hold.TicketId, Buy(s.Single), TestApp.BaseUri);
        var ticket = (await s.Sales.GetByCodeAsync(buyer, result.Code)).View.Ticket;
        Assert.Equal(("B2", VehicleSize.Large), (ticket.SpotLabel, ticket.VehicleSize));
        Assert.Contains("(large vehicle)", s.App.Email.Sent.Single().Body);
    }

    [Fact]
    public async Task A_standard_vehicle_can_park_anywhere()
    {
        await using var s = await SetUpAsync();
        await MarkLargeAsync(s, RowBLarge);
        var buyer = await BuyerAsync(s.App);

        var front = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        var back = await s.Sales.HoldAsync(buyer, s.Showing.Id, 2, 1, VehicleSize.Standard);

        Assert.Equal(VehicleSize.Standard, front.VehicleSize);
        Assert.Equal(("B1", VehicleSize.Standard), (back.SpotLabel, back.VehicleSize));
    }

    [Fact]
    public async Task Holding_your_own_spot_again_with_another_vehicle_updates_it_if_it_fits()
    {
        await using var s = await SetUpAsync();
        await MarkLargeAsync(s, RowBLarge);
        var buyer = await BuyerAsync(s.App);
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 2, 1);

        var again = await s.Sales.HoldAsync(buyer, s.Showing.Id, 2, 1, VehicleSize.Large);

        Assert.Equal((hold.TicketId, hold.HeldUntil, VehicleSize.Large), (again.TicketId, again.HeldUntil, again.VehicleSize));
    }

    [Fact]
    public async Task A_screen_with_no_large_spots_turns_large_vehicles_away()
    {
        await using var s = await SetUpAsync();
        var buyer = await BuyerAsync(s.App);

        var ex = await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.HoldAsync(buyer, s.Showing.Id, 2, 2, VehicleSize.Large));

        Assert.Contains("no spots for large vehicles", ex.Message);
    }

    [Fact]
    public async Task The_gate_and_free_admission_check_the_vehicle_too()
    {
        await using var s = await SetUpAsync();
        await MarkLargeAsync(s, RowBLarge);
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 9, 5, 22, 0, 0, TimeSpan.Zero));
        await s.App.Get<TheaterService>().UpdateFreeAdmissionSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true, false, false, null, null);

        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.HoldAtGateAsync(s.OwnerPrincipal, s.Showing.Id, 1, 1, VehicleSize.Large));
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.OfferFreeAdmissionAsync(
            s.OwnerPrincipal, s.Showing.Id, 1, 1, "Sam", null, null, TestApp.BaseUri, VehicleSize.Large));

        var gate = await s.Sales.HoldAtGateAsync(s.OwnerPrincipal, s.Showing.Id, 2, 4, VehicleSize.Large);
        var sold = await s.Sales.SellAtGateAsync(s.OwnerPrincipal, gate.TicketId, s.Single.Id, []);
        var comp = await s.Sales.OfferFreeAdmissionAsync(s.OwnerPrincipal, s.Showing.Id, 2, 3, "Sam", null, null, TestApp.BaseUri, VehicleSize.Large);
        Assert.Equal(VehicleSize.Large, sold.Ticket.VehicleSize);
        Assert.Equal(VehicleSize.Large, comp!.Ticket.VehicleSize);
    }

    // --- Laying out spots ---

    [Fact]
    public async Task Marks_are_saved_kept_when_omitted_and_dropped_with_the_spots_they_were_on()
    {
        await using var s = await SetUpAsync();
        var screens = s.App.Get<ScreenService>();
        await MarkLargeAsync(s, 204, 201, 201);
        Assert.Equal([201, 204], (await screens.GetAsync(s.OwnerPrincipal, s.Screen.Id)).LargeSpots);

        // No marks given: the current ones stay, less any on spots the new layout removes.
        await screens.UpdateAsync(s.OwnerPrincipal, s.Screen.Id, new ScreenLayoutInput("North", SpotLabelScheme.LetterNumber, [3, 3]));

        var screen = await screens.GetAsync(s.OwnerPrincipal, s.Screen.Id);
        Assert.Equal([201], screen.LargeSpots);
        Assert.True(screen.HasSizeLimits);
    }

    [Fact]
    public async Task A_mark_must_be_on_a_spot_in_the_layout()
    {
        await using var s = await SetUpAsync();

        await Assert.ThrowsAsync<AppValidationException>(() => MarkLargeAsync(s, 104)); // row A has 3 spots
        await Assert.ThrowsAsync<AppValidationException>(() => MarkLargeAsync(s, 301)); // no row C
    }

    [Fact]
    public async Task A_spot_a_large_vehicle_has_a_ticket_for_stays_marked_until_the_showing_passes()
    {
        await using var s = await SetUpAsync();
        await MarkLargeAsync(s, RowBLarge);
        var buyer = await BuyerAsync(s.App);
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 2, 3, VehicleSize.Large);
        await s.Sales.PurchaseAsync(buyer, hold.TicketId, Buy(s.Single), TestApp.BaseUri);

        var ex = await Assert.ThrowsAsync<AppValidationException>(() => MarkLargeAsync(s, 201, 202));
        Assert.Contains("B3", ex.Message);

        await MarkLargeAsync(s, 203); // unmarking the others is fine
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero)); // after the showing
        await MarkLargeAsync(s);
        Assert.Empty((await s.App.Get<ScreenService>().GetAsync(s.OwnerPrincipal, s.Screen.Id)).LargeSpots);
    }

    [Fact]
    public async Task Only_screen_managers_mark_spots()
    {
        await using var s = await SetUpAsync();
        var employee = await s.App.CreateUserAsync("staff@example.com", employeeTheaterId: s.Theater.Id);
        await s.App.GrantAsync(employee, EditProfile, ManageSchedule);

        await Assert.ThrowsAsync<AccessDeniedException>(() => s.App.Get<ScreenService>().UpdateAsync(Principals.For(employee), s.Screen.Id,
            new ScreenLayoutInput("North", SpotLabelScheme.LetterNumber, [3, 4], RowBLarge)));
    }

    [Fact]
    public void The_default_marks_the_back_half_of_the_rows()
    {
        Assert.Equal([301, 302, 401], Screen.BackHalfLarge([2, 2, 2, 1]));
        Assert.Equal([301, 401, 501], Screen.BackHalfLarge([1, 1, 1, 1, 1])); // an odd middle row goes to the front
        Assert.Equal([101, 102], Screen.BackHalfLarge([2]));
        Assert.Empty(Screen.BackHalfLarge([]));
    }

    [Fact]
    public async Task A_signed_up_theaters_sample_screens_take_large_vehicles_in_the_back_rows()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.Get<OnboardingService>().CreateDemoTheaterAsync(Principals.For(owner),
            new NewTheaterInput("Starlight Drive-In", "Austin", "TX", "America/Chicago", 1, true, null));
        await using var db = app.Db();

        var screen = await db.Screens.SingleAsync(x => x.TheaterId == theater.Id);

        Assert.Equal(60, screen.LargeSpots.Count); // rows 5 to 8 of 8, 15 spots each
        Assert.True(screen.AllowsLarge(5, 1));
        Assert.False(screen.AllowsLarge(4, 15));
    }
}
