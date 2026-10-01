using DriveIn.Web.Authorization;
using DriveIn.Web.Components.Pages.Manage;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests.Pages;

// The Employees and Roles pages.
public class ManageStaffPageTests
{
    private sealed record Staff(TestApp App, ApplicationUser Owner, Theater Theater, ApplicationUser Employee, RoleSummary GateCrew) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    // A theater with one employee (no roles yet) and a "Gate crew" role.
    private static async Task<Staff> SetUpAsync()
    {
        var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var employee = await app.CreateUserAsync("pat@example.com", theater.Id);
        var role = await app.Get<RoleService>().CreateAsync(Principals.For(owner), theater.Id, "Gate crew", "Works the gate", [AdmitGuests]);
        var gateCrew = (await app.Get<RoleService>().ListAsync(Principals.For(owner), theater.Id)).Single(r => r.Id == role.Id);
        return new Staff(app, owner, theater, employee, gateCrew);
    }

    private static IRenderedComponent<ManageEmployees> OpenEmployees(PageHost host, Staff s)
    {
        var page = host.Render<ManageEmployees>(p => p.Add(x => x.Id, s.Theater.Id));
        page.WaitForText("Pending invitations");
        return page;
    }

    [Fact]
    public async Task The_owner_invites_an_employee_then_resends_and_revokes_the_invitation()
    {
        await using var s = await SetUpAsync();
        await using var host = new PageHost(s.App).SignIn(s.Owner);
        var page = OpenEmployees(host, s);
        Assert.Contains("pat@example.com No roles", page.Text());

        page.SetField("Email", "new@example.com");
        page.Find("form").Submit();
        page.WaitForText("Invitation sent.");
        Assert.Contains("new@example.com Employee", page.Text());
        Assert.Contains(s.App.Email.Sent, m => m.To == "new@example.com");

        page.ClickButton("Resend");
        page.WaitForText("Invitation re-sent to new@example.com.");
        page.ClickButton("Revoke");

        page.WaitForText("Invitation to new@example.com revoked.");
        Assert.Contains("None.", page.Text());
    }

    [Fact]
    public async Task Inviting_without_an_email_is_refused()
    {
        await using var s = await SetUpAsync();
        await using var host = new PageHost(s.App).SignIn(s.Owner);
        var page = OpenEmployees(host, s);

        page.Find("form").Submit();

        page.WaitForText("Enter an email address.");
    }

    [Fact]
    public async Task Roles_are_assigned_to_an_employee()
    {
        await using var s = await SetUpAsync();
        await using var host = new PageHost(s.App).SignIn(s.Owner);
        var page = OpenEmployees(host, s);

        page.ClickButton("Edit");
        page.Check("Gate crew");
        page.ClickButton("Save roles");

        page.WaitForText("Roles updated for pat@example.com.");
        Assert.Contains("pat@example.com Gate crew", page.Text());
        await using var db = s.App.Db();
        Assert.Single(await db.EmployeeRoles.ToListAsync());
    }

    [Fact]
    public async Task An_employee_can_be_sent_a_password_reset_or_deleted()
    {
        await using var s = await SetUpAsync();
        await using var host = new PageHost(s.App).SignIn(s.Owner);
        var page = OpenEmployees(host, s);

        page.ClickButton("Send password reset");
        page.WaitForText("Password reset email sent to pat@example.com.");
        Assert.Contains(s.App.Email.Sent, m => m.To == "pat@example.com");

        page.ClickButton("Delete");
        page.ClickButton("Confirm delete");

        page.WaitForText("Deleted pat@example.com.");
        Assert.Contains("No employees yet.", page.Text());
    }

    [Fact]
    public async Task A_manager_cant_grant_roles_beyond_their_own_actions()
    {
        await using var s = await SetUpAsync();
        var manager = await s.App.CreateUserAsync("manager@example.com", s.Theater.Id);
        await s.App.GrantAsync(manager, ViewEmployees, TheaterPermissions.ManageRoles);
        await using var host = new PageHost(s.App).SignIn(manager);
        var page = OpenEmployees(host, s);
        Assert.DoesNotContain("Invite an employee", page.Text());
        Assert.DoesNotContain("Send password reset", page.Text());

        page.FindAll("tr").Single(r => r.TextContent.Contains("pat@example.com"))
            .QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Edit").Click();

        page.WaitForText("Gate crew (can't change)");
    }

    [Fact]
    public async Task Staff_without_employee_rights_are_turned_away()
    {
        await using var s = await SetUpAsync();
        await s.App.GrantAsync(s.Employee, SellAtGate);
        await using var host = new PageHost(s.App).SignIn(s.Employee);

        host.Render<ManageEmployees>(p => p.Add(x => x.Id, s.Theater.Id));

        Assert.EndsWith("Account/AccessDenied", host.Nav.Uri);
    }

    // --- Roles ---

    private static IRenderedComponent<ManageRoles> OpenRoles(PageHost host, Staff s)
    {
        var page = host.Render<ManageRoles>(p => p.Add(x => x.Id, s.Theater.Id));
        page.WaitForText("Gate crew");
        return page;
    }

    [Fact]
    public async Task The_owner_creates_a_role_with_actions()
    {
        await using var s = await SetUpAsync();
        await using var host = new PageHost(s.App).SignIn(s.Owner);
        var page = OpenRoles(host, s);

        page.ClickButton("New role");
        page.SetField("Name", "Box office");
        page.SetField("Description", "Sells tickets");
        page.Check("Sell tickets at the gate");
        page.Check("View reports");
        page.ClickButton("Create role");

        page.WaitForText("Role created.");
        Assert.Contains("Box office 0 employees Sells tickets", page.Text());
        await using var db = s.App.Db();
        var role = await db.TheaterRoles.Include(r => r.Permissions).SingleAsync(r => r.Name == "Box office");
        Assert.Equal(new[] { SellAtGate, ViewReports }.Order(), role.Permissions.Select(p => p.Permission).Order());
    }

    [Fact]
    public async Task A_role_is_edited_and_deleted()
    {
        await using var s = await SetUpAsync();
        await using var host = new PageHost(s.App).SignIn(s.Owner);
        var page = OpenRoles(host, s);

        page.ClickButton("Edit");
        page.WaitForText("Edit Gate crew");
        page.Check("Move tickets");
        page.Check("Admit guests", false);
        page.ClickButton("Save role");
        page.WaitForText("Role saved.");
        Assert.Contains("Move tickets", page.Text());

        page.ClickButton("Delete");
        page.ClickButton("Cancel");
        page.ClickButton("Delete");
        page.ClickButton("Confirm delete");

        page.WaitForText("Deleted the Gate crew role.");
        Assert.Contains("No roles yet.", page.Text());
    }

    [Fact]
    public async Task A_manager_only_sees_the_roles_they_can_change_as_editable()
    {
        await using var s = await SetUpAsync();
        var manager = await s.App.CreateUserAsync("manager@example.com", s.Theater.Id);
        await s.App.GrantAsync(manager, TheaterPermissions.ManageRoles);
        await using var host = new PageHost(s.App).SignIn(manager);

        var page = OpenRoles(host, s);

        var text = page.Text();
        Assert.Contains("You can only grant, change or remove actions you have yourself.", text);
        Assert.Contains("Has actions you don't have", text);
        page.ClickButton("New role");
        page.WaitForText("(you don't have this)");
    }
}
