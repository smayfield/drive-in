using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DriveIn.Web.Tests;

public class UserAdminServiceTests
{
    [Fact]
    public async Task Cannot_delete_a_user_who_owns_theaters()
    {
        await using var app = new TestApp();
        var admin = Principals.For(await app.CreateUserAsync("admin@example.com", admin: true), admin: true);
        var owner = await app.CreateUserAsync("owner@example.com");
        await app.CreateTheaterAsync("Starlight", owner.Id);

        var ex = await Assert.ThrowsAsync<AppValidationException>(() => app.Get<UserAdminService>().DeleteAsync(admin, owner.Id));
        Assert.Contains("Starlight", ex.Message);
    }

    [Fact]
    public async Task Employee_accounts_cannot_be_admins()
    {
        await using var app = new TestApp();
        var admin = Principals.For(await app.CreateUserAsync("admin@example.com", admin: true), admin: true);
        var theater = await app.CreateTheaterAsync("Starlight");
        var employee = await app.CreateUserAsync("emp@example.com", employeeTheaterId: theater.Id);

        await Assert.ThrowsAsync<AppValidationException>(() => app.Get<UserAdminService>().SetAdminAsync(admin, employee.Id, true));
    }

    [Fact]
    public async Task Admins_cannot_demote_lock_or_delete_themselves()
    {
        await using var app = new TestApp();
        var self = await app.CreateUserAsync("admin@example.com", admin: true);
        var principal = Principals.For(self, admin: true);
        var service = app.Get<UserAdminService>();

        await Assert.ThrowsAsync<AppValidationException>(() => service.SetAdminAsync(principal, self.Id, false));
        await Assert.ThrowsAsync<AppValidationException>(() => service.SetLockedAsync(principal, self.Id, true));
        await Assert.ThrowsAsync<AppValidationException>(() => service.DeleteAsync(principal, self.Id));
    }

    [Fact]
    public async Task Non_admins_are_denied()
    {
        await using var app = new TestApp();
        var user = await app.CreateUserAsync("user@example.com");

        await Assert.ThrowsAsync<AccessDeniedException>(() => app.Get<UserAdminService>().ListAsync(Principals.For(user), null));
    }

    [Fact]
    public async Task Create_makes_confirmed_account_and_emails_reset_link()
    {
        await using var app = new TestApp();
        var admin = Principals.For(await app.CreateUserAsync("admin@example.com", admin: true), admin: true);
        var service = app.Get<UserAdminService>();

        await service.CreateAsync(admin, "New@Example.com", "New Person", makeAdmin: true, TestApp.BaseUri);

        var listed = (await service.ListAsync(admin, "new@")).Single();
        Assert.Equal(("new@example.com", true, true, false), (listed.Email, listed.EmailConfirmed, listed.IsAdmin, listed.HasPassword));
        Assert.Contains("Account/ResetPassword?code=", app.Email.Sent.Single().Body);
    }
}

public class EmployeeServiceTests
{
    [Fact]
    public async Task Owner_manages_only_their_own_employees()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var mine = await app.CreateTheaterAsync("Mine", owner.Id);
        var other = await app.CreateTheaterAsync("Other");
        var myEmployee = await app.CreateUserAsync("mine@example.com", employeeTheaterId: mine.Id);
        var theirEmployee = await app.CreateUserAsync("theirs@example.com", employeeTheaterId: other.Id);
        var service = app.Get<EmployeeService>();

        // Someone else's employee, addressed through my theater: not found.
        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteAsync(Principals.For(owner), mine.Id, theirEmployee.Id));
        // Someone else's theater: denied.
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.DeleteAsync(Principals.For(owner), other.Id, theirEmployee.Id));

        await service.SendPasswordResetAsync(Principals.For(owner), mine.Id, myEmployee.Id, TestApp.BaseUri);
        await service.DeleteAsync(Principals.For(owner), mine.Id, myEmployee.Id);

        Assert.Empty(await service.ListAsync(Principals.For(owner), mine.Id));
        Assert.Equal("mine@example.com", app.Email.Sent.Single().To);
    }

    [Fact]
    public async Task Employees_cannot_manage_employees()
    {
        await using var app = new TestApp();
        var theater = await app.CreateTheaterAsync("Starlight");
        var a = await app.CreateUserAsync("a@example.com", employeeTheaterId: theater.Id);
        var b = await app.CreateUserAsync("b@example.com", employeeTheaterId: theater.Id);
        var service = app.Get<EmployeeService>();

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(Principals.For(a), theater.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.DeleteAsync(Principals.For(a), theater.Id, b.Id));
    }
}

public class ScreenServiceTests
{
    [Fact]
    public async Task Employee_adds_and_reorders_screens_for_their_theater_only()
    {
        await using var app = new TestApp();
        var theater = await app.CreateTheaterAsync("Starlight");
        var other = await app.CreateTheaterAsync("Other");
        var employee = Principals.For(await app.CreateUserAsync("emp@example.com", employeeTheaterId: theater.Id));
        var service = app.Get<ScreenService>();

        await service.AddAsync(employee, theater.Id, "One", 100);
        await service.AddAsync(employee, theater.Id, "Two", 200);
        var three = await service.AddAsync(employee, theater.Id, "Three", 300);
        await service.MoveAsync(employee, three.Id, -1);
        await service.MoveAsync(employee, three.Id, -1);
        await service.MoveAsync(employee, three.Id, -1); // already first: no-op

        await using var db = app.Db();
        Assert.Equal(["Three", "One", "Two"],
            await db.Screens.Where(s => s.TheaterId == theater.Id).OrderBy(s => s.SortOrder).Select(s => s.Name).ToListAsync());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.AddAsync(employee, other.Id, "Nope", 1));
    }
}

public class AppClaimsPrincipalFactoryTests
{
    [Fact]
    public async Task Promotes_configured_admin_and_adds_employee_claim()
    {
        await using var app = new TestApp(adminEmail: "Boss@Example.com");
        var boss = await app.CreateUserAsync("boss@example.com");
        var theater = await app.CreateTheaterAsync("Starlight");
        var employee = await app.CreateUserAsync("emp@example.com", employeeTheaterId: theater.Id);

        await using var scope = app.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var bossPrincipal = await factory.CreateAsync((await users.FindByIdAsync(boss.Id))!);
        var employeePrincipal = await factory.CreateAsync((await users.FindByIdAsync(employee.Id))!);

        Assert.True(bossPrincipal.IsAdmin());
        Assert.False(employeePrincipal.IsAdmin());
        Assert.Equal(theater.Id, employeePrincipal.GetEmployeeTheaterId());
        Assert.Null(bossPrincipal.GetEmployeeTheaterId());
    }
}
