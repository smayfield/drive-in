using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests;

public class RoleTests
{
    [Fact]
    public async Task New_theaters_get_the_default_roles()
    {
        await using var app = new TestApp();
        var admin = Principals.For(await app.CreateUserAsync("admin@example.com", admin: true), admin: true);

        var theater = await app.Get<TheaterService>().CreateAsync(admin, new Theater { Name = "Starlight", Slug = "starlight" });

        var roles = await app.Get<RoleService>().ListAsync(admin, theater.Id);
        Assert.Equal(["Concessions", "Manager", "Operations", "Ticketing"], roles.Select(r => r.Name));
        Assert.Equal(AllKeys, roles.Single(r => r.Name == "Manager").Permissions.ToHashSet());
        Assert.Equal([AdmitGuests, MoveTickets, SellAtGate], roles.Single(r => r.Name == "Ticketing").Permissions.Order());
    }

    [Fact]
    public async Task Owner_creates_edits_and_deletes_roles()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var employee = await app.CreateUserAsync("e@example.com", employeeTheaterId: theater.Id);
        var roles = app.Get<RoleService>();
        var me = Principals.For(owner);

        var role = await roles.CreateAsync(me, theater.Id, " Night Crew ", "Late shift", [ManageScreens]);
        await app.Get<EmployeeService>().SetRolesAsync(me, theater.Id, employee.Id, [role.Id]);
        Assert.Equal([ManageScreens], await app.PermissionsAsync(employee, theater));

        await roles.UpdateAsync(me, role.Id, "Night Crew", null, [EditProfile, ManageScreens]);
        Assert.Equal(new[] { EditProfile, ManageScreens }.Order(), (await app.PermissionsAsync(employee, theater)).Order());

        await roles.DeleteAsync(me, role.Id);
        Assert.Empty(await app.PermissionsAsync(employee, theater));
        await using var db = app.Db();
        Assert.Empty(await db.EmployeeRoles.ToListAsync());
    }

    [Fact]
    public async Task Role_names_are_unique_per_theater_ignoring_case_and_permissions_must_be_known()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var other = await app.CreateTheaterAsync("Other", owner.Id);
        var roles = app.Get<RoleService>();
        var me = Principals.For(owner);

        await roles.CreateAsync(me, theater.Id, "Manager", null, []);
        await Assert.ThrowsAsync<AppValidationException>(() => roles.CreateAsync(me, theater.Id, "MANAGER", null, []));
        await Assert.ThrowsAsync<AppValidationException>(() => roles.CreateAsync(me, theater.Id, "  ", null, []));
        await Assert.ThrowsAsync<AppValidationException>(() => roles.CreateAsync(me, theater.Id, "Hacker", null, ["root.everything"]));
        await roles.CreateAsync(me, other.Id, "Manager", null, []); // other theater: fine
    }

    [Fact]
    public async Task Renaming_a_role_keeps_its_normalized_name_in_sync()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var roles = app.Get<RoleService>();
        var me = Principals.For(owner);
        var role = await roles.CreateAsync(me, theater.Id, "Night Crew", null, []);

        await roles.UpdateAsync(me, role.Id, "Late Crew", null, []);

        await using var db = app.Db();
        Assert.Equal("LATE CREW", (await db.TheaterRoles.SingleAsync(r => r.Id == role.Id)).NormalizedName);
        await roles.CreateAsync(me, theater.Id, "night crew", null, []); // old name is free again
        await Assert.ThrowsAsync<AppValidationException>(() => roles.CreateAsync(me, theater.Id, "LATE crew", null, []));
    }

    [Fact]
    public async Task Role_manager_cannot_grant_permissions_they_lack()
    {
        await using var app = new TestApp();
        var theater = await app.CreateTheaterAsync("Starlight");
        var lead = await app.CreateUserAsync("lead@example.com", employeeTheaterId: theater.Id);
        await app.GrantAsync(lead, ManageRoles, ManageScreens);
        var roles = app.Get<RoleService>();
        var me = Principals.For(lead);

        // Within their own authority: fine.
        var screens = await roles.CreateAsync(me, theater.Id, "Screens", null, [ManageScreens]);

        // Beyond it: rejected, whether creating or editing.
        await Assert.ThrowsAsync<AppValidationException>(() => roles.CreateAsync(me, theater.Id, "Boss", null, [ManageEmployees]));
        await Assert.ThrowsAsync<AppValidationException>(() => roles.UpdateAsync(me, screens.Id, "Screens", null, [ManageScreens, EditProfile]));
    }

    [Fact]
    public async Task Role_manager_cannot_change_or_delete_roles_above_them()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var lead = await app.CreateUserAsync("lead@example.com", employeeTheaterId: theater.Id);
        await app.GrantAsync(lead, ManageRoles, ManageScreens);
        var boss = await app.Get<RoleService>().CreateAsync(Principals.For(owner), theater.Id, "Boss", null, [ManageEmployees, ManageScreens]);
        var roles = app.Get<RoleService>();
        var me = Principals.For(lead);

        // Even removing a permission they lack counts as touching it.
        await Assert.ThrowsAsync<AppValidationException>(() => roles.UpdateAsync(me, boss.Id, "Boss", null, [ManageScreens]));
        await Assert.ThrowsAsync<AppValidationException>(() => roles.DeleteAsync(me, boss.Id));
    }

    [Fact]
    public async Task Role_manager_cannot_assign_or_remove_roles_above_them_including_on_themselves()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var lead = await app.CreateUserAsync("lead@example.com", employeeTheaterId: theater.Id);
        var staff = await app.CreateUserAsync("staff@example.com", employeeTheaterId: theater.Id);
        await app.GrantAsync(lead, ManageRoles, ManageScreens);
        var ownerRoles = app.Get<RoleService>();
        var boss = await ownerRoles.CreateAsync(Principals.For(owner), theater.Id, "Boss", null, [ManageEmployees]);
        var screens = await ownerRoles.CreateAsync(Principals.For(owner), theater.Id, "Screens", null, [ManageScreens]);
        var employees = app.Get<EmployeeService>();
        var me = Principals.For(lead);

        await Assert.ThrowsAsync<AppValidationException>(() => employees.SetRolesAsync(me, theater.Id, lead.Id, [boss.Id]));
        await Assert.ThrowsAsync<AppValidationException>(() => employees.SetRolesAsync(me, theater.Id, staff.Id, [boss.Id]));
        await employees.SetRolesAsync(me, theater.Id, staff.Id, [screens.Id]);
        Assert.Equal([ManageScreens], await app.PermissionsAsync(staff, theater));

        // The owner gives staff the Boss role; the lead can't take it away, but keeping it is fine.
        await employees.SetRolesAsync(Principals.For(owner), theater.Id, staff.Id, [screens.Id, boss.Id]);
        await Assert.ThrowsAsync<AppValidationException>(() => employees.SetRolesAsync(me, theater.Id, staff.Id, [screens.Id]));
        await employees.SetRolesAsync(me, theater.Id, staff.Id, [boss.Id]); // only removes Screens, which lead holds
    }

    [Fact]
    public async Task Roles_and_employees_must_belong_to_the_same_theater()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var mine = await app.CreateTheaterAsync("Mine", owner.Id);
        var other = await app.CreateTheaterAsync("Other", owner.Id);
        var mineEmployee = await app.CreateUserAsync("a@example.com", employeeTheaterId: mine.Id);
        var otherEmployee = await app.CreateUserAsync("b@example.com", employeeTheaterId: other.Id);
        var otherRole = await app.Get<RoleService>().CreateAsync(Principals.For(owner), other.Id, "X", null, []);
        var employees = app.Get<EmployeeService>();
        var me = Principals.For(owner);

        await Assert.ThrowsAsync<NotFoundException>(() => employees.SetRolesAsync(me, mine.Id, mineEmployee.Id, [otherRole.Id]));
        await Assert.ThrowsAsync<NotFoundException>(() => employees.SetRolesAsync(me, mine.Id, otherEmployee.Id, []));
    }

    [Fact]
    public async Task Staff_permissions_gate_employee_management()
    {
        await using var app = new TestApp();
        var theater = await app.CreateTheaterAsync("Starlight");
        var viewer = await app.CreateUserAsync("viewer@example.com", employeeTheaterId: theater.Id);
        var manager = await app.CreateUserAsync("manager@example.com", employeeTheaterId: theater.Id);
        var target = await app.CreateUserAsync("target@example.com", employeeTheaterId: theater.Id);
        await app.GrantAsync(viewer, ViewEmployees);
        await app.GrantAsync(manager, ManageEmployees, InviteEmployees);
        var employees = app.Get<EmployeeService>();
        var invitations = app.Get<InvitationService>();

        Assert.Equal(3, (await employees.ListAsync(Principals.For(viewer), theater.Id)).Count);
        await Assert.ThrowsAsync<AccessDeniedException>(() => employees.DeleteAsync(Principals.For(viewer), theater.Id, target.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            invitations.InviteAsync(Principals.For(viewer), theater.Id, "x@example.com", InvitationKind.Employee, TestApp.BaseUri));

        await invitations.InviteAsync(Principals.For(manager), theater.Id, "x@example.com", InvitationKind.Employee, TestApp.BaseUri);
        await employees.DeleteAsync(Principals.For(manager), theater.Id, target.Id);
        Assert.Equal(2, (await employees.ListAsync(Principals.For(manager), theater.Id)).Count);
    }

    [Fact]
    public async Task Cannot_delete_an_employee_with_more_authority()
    {
        await using var app = new TestApp();
        var theater = await app.CreateTheaterAsync("Starlight");
        var manager = await app.CreateUserAsync("manager@example.com", employeeTheaterId: theater.Id);
        var senior = await app.CreateUserAsync("senior@example.com", employeeTheaterId: theater.Id);
        await app.GrantAsync(manager, ManageEmployees);
        await app.GrantAsync(senior, ManageEmployees, ManageRoles);

        await Assert.ThrowsAsync<AppValidationException>(() =>
            app.Get<EmployeeService>().DeleteAsync(Principals.For(manager), theater.Id, senior.Id));
    }

    [Fact]
    public async Task Deleting_a_theater_removes_its_roles()
    {
        await using var app = new TestApp();
        var admin = Principals.For(await app.CreateUserAsync("admin@example.com", admin: true), admin: true);
        var theaters = app.Get<TheaterService>();
        var theater = await theaters.CreateAsync(admin, new Theater { Name = "Doomed", Slug = "doomed" });
        var employee = await app.CreateUserAsync("e@example.com", employeeTheaterId: theater.Id);
        await app.GrantAsync(employee, ManageScreens);

        await theaters.DeleteAsync(admin, theater.Id);

        await using var db = app.Db();
        Assert.Empty(await db.TheaterRoles.ToListAsync());
        Assert.Empty(await db.TheaterRolePermissions.ToListAsync());
        Assert.Empty(await db.EmployeeRoles.ToListAsync());
    }
}
