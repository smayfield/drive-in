using System.Security.Claims;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;
using static DriveIn.Web.Tests.TicketSalesTests;

namespace DriveIn.Web.Tests;

public class FreeAdmissionTests
{
    // 5 PM in Chicago on the day of the 8 PM showing (see TicketSalesTests.SetUpAsync).
    private static readonly DateTimeOffset ShowDayAfternoon = new(2026, 9, 5, 22, 0, 0, TimeSpan.Zero);

    private static async Task<Setup> SetUpWithCompsAsync(bool requiresApproval = false, bool requiresReason = false,
        int? maxPerShowing = null, int? maxPerEmployee = null)
    {
        var s = await SetUpAsync();
        await s.App.Get<TheaterService>().UpdateFreeAdmissionSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true, requiresApproval,
            requiresReason, maxPerShowing, maxPerEmployee);
        return s;
    }

    private static async Task<ClaimsPrincipal> EmployeeAsync(Setup s, params string[] permissions)
    {
        var employee = await s.App.CreateUserAsync($"emp{Guid.NewGuid():N}@example.com", employeeTheaterId: s.Theater.Id);
        if (permissions.Length > 0)
            await s.App.GrantAsync(employee, permissions);
        return Principals.For(employee);
    }

    private static Task<TicketView?> OfferAsync(Setup s, ClaimsPrincipal who, int row = 1, int spot = 1, string guest = "Aunt May",
        string? email = null, string? reason = "Family") =>
        s.Sales.OfferFreeAdmissionAsync(who, s.Showing.Id, row, spot, guest, email, reason, TestApp.BaseUri);

    // --- Permissions and settings ---

    [Fact]
    public async Task Every_free_admission_action_needs_its_permission()
    {
        await using var s = await SetUpWithCompsAsync(requiresApproval: true);
        var none = await EmployeeAsync(s);
        var other = await EmployeeAsync(s, SellAtGate); // some role, but not these
        var offerer = await EmployeeAsync(s, OfferFreeAdmission);
        await OfferAsync(s, offerer); // a pending request
        var pending = Assert.Single(await GetTicketsAsync(s));
        foreach (var who in new[] { none, other })
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => OfferAsync(s, who));
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.ListCompShowingsAsync(who, s.Theater.Id));
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.GetCompScreenAsync(who, s.Showing.Id));
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.ListMyFreeAdmissionAsync(who, s.Theater.Id));
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.ListPendingFreeAdmissionAsync(who, s.Theater.Id));
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.GetFreeAdmissionLogAsync(who, s.Theater.Id));
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.ApproveFreeAdmissionAsync(who, pending.Id, TestApp.BaseUri));
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.DenyFreeAdmissionAsync(who, pending.Id, null));
            await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.CancelFreeAdmissionAsync(who, pending.Id, null));
        }
        // Offering alone doesn't allow approving or reading the log.
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.ApproveFreeAdmissionAsync(offerer, pending.Id, TestApp.BaseUri));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.DenyFreeAdmissionAsync(offerer, pending.Id, null));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.GetFreeAdmissionLogAsync(offerer, s.Theater.Id));
    }

    [Fact]
    public async Task Only_someone_who_can_edit_the_profile_changes_the_settings()
    {
        await using var s = await SetUpAsync();
        var employee = await EmployeeAsync(s, ManageSchedule);
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            s.App.Get<TheaterService>().UpdateFreeAdmissionSettingsAsync(employee, s.Theater.Id, true, true, true, null, null));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            s.App.Get<TheaterService>().UpdateFreeAdmissionSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true, true, true, 0, null));
    }

    [Fact]
    public async Task Free_admission_is_off_until_the_theater_turns_it_on()
    {
        await using var s = await SetUpAsync();
        Assert.False((await s.App.Db().Theaters.SingleAsync(t => t.Id == s.Theater.Id)).FreeAdmissionEnabled);
        var ex = await Assert.ThrowsAsync<AppValidationException>(() => OfferAsync(s, s.OwnerPrincipal));
        Assert.Contains("doesn't offer free admission", ex.Message);
    }

    // --- Giving free admission ---

    [Fact]
    public async Task Without_approval_the_guest_gets_a_free_ticket_and_it_is_logged()
    {
        await using var s = await SetUpWithCompsAsync();
        s.App.Time.SetUtcNow(ShowDayAfternoon);
        var employee = await EmployeeAsync(s, OfferFreeAdmission, AdmitGuests);

        var view = await OfferAsync(s, employee, 2, 3, "Aunt May", "may@example.com", "Family");

        Assert.NotNull(view);
        var t = view.Ticket;
        Assert.Equal((TicketStatus.Sold, "B3", 0m, true), (t.Status, t.SpotLabel, t.Total, t.IsComp));
        Assert.Equal(("Aunt May", "Family"), (t.GuestName, t.CompReason));
        Assert.Null(t.UserId);
        Assert.Equal(employee.FindFirstValue(ClaimTypes.NameIdentifier), t.SoldById);
        Assert.Matches($"^[{ShortCodes.Alphabet}]{{4}}$", t.ShortCode);
        Assert.Empty(s.App.Payments.Charges);
        Assert.Contains(s.App.Email.Sent, m => m.To == "may@example.com");

        var log = await s.Sales.GetFreeAdmissionLogAsync(s.OwnerPrincipal, s.Theater.Id);
        var row = Assert.Single(log.Rows).Event;
        Assert.Equal((CompAction.Issued, "Aunt May", "B3", "Family", "Jaws"), (row.Action, row.GuestName, row.SpotLabel, row.Reason, row.ShowingTitle));
        Assert.Equal(("emp", 1), (log.ByEmployee.Single().Name[..3], log.ByEmployee.Single().Count));

        // The gate admits them like any ticket.
        var found = Assert.Single(await s.Sales.FindAtGateAsync(employee, s.Theater.Id, t.ShortCode!));
        Assert.Null(found.AdmitProblem);
        await s.Sales.AdmitAsync(employee, t.Code!);
        var buyer = await BuyerAsync(s.App);
        Assert.Equal(SpotState.Sold, (await s.Sales.GetAvailabilityAsync(buyer, s.Showing.Id))[2, 3]);
    }

    [Fact]
    public async Task With_approval_an_offer_is_a_request_that_holds_the_spot_until_approved()
    {
        await using var s = await SetUpWithCompsAsync(requiresApproval: true);
        var offerer = await EmployeeAsync(s, OfferFreeAdmission);
        var manager = await EmployeeAsync(s, ApproveFreeAdmission, ViewFreeAdmission);

        Assert.Null(await OfferAsync(s, offerer, email: "may@example.com"));

        var buyer = await BuyerAsync(s.App);
        Assert.Equal(SpotState.Held, (await s.Sales.GetAvailabilityAsync(buyer, s.Showing.Id))[1, 1]);
        s.App.Time.Advance(TimeSpan.FromDays(1));
        await s.Sales.ReleaseExpiredHoldsAsync(); // never sweeps a pending request
        Assert.Equal(SpotState.Held, (await s.Sales.GetAvailabilityAsync(buyer, s.Showing.Id))[1, 1]);
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1));
        Assert.Empty(s.App.Email.Sent);

        var waiting = Assert.Single(await s.Sales.ListPendingFreeAdmissionAsync(manager, s.Theater.Id));
        Assert.True(waiting.Pending);
        Assert.Single(await s.Sales.ListMyFreeAdmissionAsync(offerer, s.Theater.Id));

        var view = await s.Sales.ApproveFreeAdmissionAsync(manager, waiting.TicketId, TestApp.BaseUri);

        Assert.Equal((TicketStatus.Sold, 0m), (view.Ticket.Status, view.Ticket.Total));
        Assert.NotNull(view.Ticket.Code);
        Assert.Contains(s.App.Email.Sent, m => m.To == "may@example.com");
        Assert.Empty(await s.Sales.ListPendingFreeAdmissionAsync(manager, s.Theater.Id));
        var actions = (await s.Sales.GetFreeAdmissionLogAsync(manager, s.Theater.Id)).Rows.Select(r => r.Event.Action).ToList();
        Assert.Equal([CompAction.Approved, CompAction.Requested], actions);
    }

    [Fact]
    public async Task Denying_a_request_frees_the_spot_and_is_logged()
    {
        await using var s = await SetUpWithCompsAsync(requiresApproval: true);
        var offerer = await EmployeeAsync(s, OfferFreeAdmission);
        await OfferAsync(s, offerer);
        var pending = Assert.Single(await GetTicketsAsync(s));

        await s.Sales.DenyFreeAdmissionAsync(s.OwnerPrincipal, pending.Id, "Not this week");

        Assert.Empty(await GetTicketsAsync(s));
        var buyer = await BuyerAsync(s.App);
        Assert.Equal(SpotState.Available, (await s.Sales.GetAvailabilityAsync(buyer, s.Showing.Id))[1, 1]);
        var denied = (await s.Sales.GetFreeAdmissionLogAsync(s.OwnerPrincipal, s.Theater.Id)).Rows.First().Event;
        Assert.Equal((CompAction.Denied, "Not this week", "Aunt May"), (denied.Action, denied.Note, denied.GuestName));
        Assert.NotNull(denied.OfferedByName);
    }

    [Fact]
    public async Task Someone_who_can_approve_skips_the_approval_step()
    {
        await using var s = await SetUpWithCompsAsync(requiresApproval: true);
        var manager = await EmployeeAsync(s, OfferFreeAdmission, ApproveFreeAdmission);

        var view = await OfferAsync(s, manager);

        Assert.Equal(TicketStatus.Sold, view!.Ticket.Status);
        Assert.Equal(CompAction.Issued, Assert.Single((await GetLogAsync(s)).Rows).Event.Action);
    }

    [Fact]
    public async Task A_reason_is_required_when_the_theater_says_so()
    {
        await using var s = await SetUpWithCompsAsync(requiresReason: true);
        var employee = await EmployeeAsync(s, OfferFreeAdmission);
        await Assert.ThrowsAsync<AppValidationException>(() => OfferAsync(s, employee, reason: "  "));
        await Assert.ThrowsAsync<AppValidationException>(() => OfferAsync(s, employee, guest: " "));
        Assert.NotNull(await OfferAsync(s, employee, reason: "Cousin"));
    }

    [Fact]
    public async Task Limits_per_showing_and_per_employee_apply()
    {
        await using var s = await SetUpWithCompsAsync(maxPerShowing: 3, maxPerEmployee: 2);
        var a = await EmployeeAsync(s, OfferFreeAdmission);
        var b = await EmployeeAsync(s, OfferFreeAdmission);
        await OfferAsync(s, a, 1, 1);
        await OfferAsync(s, a, 1, 2);
        var ex = await Assert.ThrowsAsync<AppValidationException>(() => OfferAsync(s, a, 1, 3));
        Assert.Contains("your limit of 2", ex.Message);
        await OfferAsync(s, b, 1, 3);
        ex = await Assert.ThrowsAsync<AppValidationException>(() => OfferAsync(s, b, 2, 1));
        Assert.Contains("limit of 3", ex.Message);
    }

    [Fact]
    public async Task A_taken_spot_cannot_be_given_away()
    {
        await using var s = await SetUpWithCompsAsync();
        var buyer = await BuyerAsync(s.App);
        await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        var employee = await EmployeeAsync(s, OfferFreeAdmission);

        await Assert.ThrowsAsync<AppValidationException>(() => OfferAsync(s, employee, 1, 1));
        await Assert.ThrowsAsync<NotFoundException>(() => OfferAsync(s, employee, 9, 9));
    }

    [Fact]
    public async Task Demo_theaters_mark_free_tickets_as_tests_and_clear_them_on_go_live()
    {
        await using var s = await SetUpWithCompsAsync();
        await using (var db = s.App.Db())
        {
            (await db.Theaters.SingleAsync(t => t.Id == s.Theater.Id)).Mode = TheaterMode.Demo;
            await db.SaveChangesAsync();
        }
        var view = await OfferAsync(s, s.OwnerPrincipal);
        Assert.True(view!.Ticket.IsTest);
        Assert.True(Assert.Single((await GetLogAsync(s)).Rows).Event.IsTest);

        var admin = Principals.For(await s.App.CreateUserAsync("admin@example.com", admin: true), admin: true);
        await s.App.Get<OnboardingService>().ActivateAsync(admin, s.Theater.Id, TestApp.BaseUri);

        Assert.Empty(await GetTicketsAsync(s));
        Assert.Empty((await GetLogAsync(s)).Rows);
    }

    // --- Withdrawing ---

    [Fact]
    public async Task An_approver_can_withdraw_an_unused_free_ticket_but_not_a_used_one()
    {
        await using var s = await SetUpWithCompsAsync();
        s.App.Time.SetUtcNow(ShowDayAfternoon);
        var offerer = await EmployeeAsync(s, OfferFreeAdmission, AdmitGuests);
        var manager = await EmployeeAsync(s, ApproveFreeAdmission);
        var first = (await OfferAsync(s, offerer, 1, 1))!.Ticket;
        var second = (await OfferAsync(s, offerer, 1, 2))!.Ticket;
        await s.Sales.AdmitAsync(offerer, second.Code!);

        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.CancelFreeAdmissionAsync(offerer, first.Id, null));
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.CancelFreeAdmissionAsync(manager, second.Id, null));
        await s.Sales.CancelFreeAdmissionAsync(manager, first.Id, "Changed plans");

        var buyer = await BuyerAsync(s.App);
        Assert.Equal(SpotState.Available, (await s.Sales.GetAvailabilityAsync(buyer, s.Showing.Id))[1, 1]);
        Assert.Equal(CompAction.Cancelled, (await GetLogAsync(s)).Rows.First().Event.Action);
    }

    [Fact]
    public async Task The_giver_can_withdraw_their_own_pending_request()
    {
        await using var s = await SetUpWithCompsAsync(requiresApproval: true);
        var offerer = await EmployeeAsync(s, OfferFreeAdmission);
        var other = await EmployeeAsync(s, OfferFreeAdmission);
        await OfferAsync(s, offerer);
        var pending = Assert.Single(await GetTicketsAsync(s));

        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Sales.CancelFreeAdmissionAsync(other, pending.Id, null));
        await s.Sales.CancelFreeAdmissionAsync(offerer, pending.Id, null);

        Assert.Empty(await GetTicketsAsync(s));
    }

    // --- Roles ---

    [Fact]
    public async Task The_default_Manager_role_gets_the_free_admission_actions_but_Ticketing_does_not()
    {
        await using var s = await SetUpAsync();
        var permissions = Authorization.DefaultTheaterRoles.All.ToDictionary(r => r.Name, r => r.Permissions);
        Assert.All([OfferFreeAdmission, ApproveFreeAdmission, ViewFreeAdmission], p => Assert.Contains(p, permissions["Manager"]));
        Assert.All([OfferFreeAdmission, ApproveFreeAdmission, ViewFreeAdmission], p => Assert.DoesNotContain(p, permissions["Ticketing"]));
    }

    // --- Helpers ---

    private static async Task<List<Ticket>> GetTicketsAsync(Setup s)
    {
        await using var db = s.App.Db();
        return await db.Tickets.Where(t => t.IsComp).ToListAsync();
    }

    private static Task<CompLog> GetLogAsync(Setup s) => s.Sales.GetFreeAdmissionLogAsync(s.OwnerPrincipal, s.Theater.Id);
}
