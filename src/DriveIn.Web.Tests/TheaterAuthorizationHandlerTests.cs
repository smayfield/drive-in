using DriveIn.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests;

public class TheaterAuthorizationHandlerTests
{
    [Fact]
    public async Task Admin_and_owner_have_every_permission_employees_only_their_roles()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var admin = await app.CreateUserAsync("admin@example.com", admin: true);
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var other = await app.CreateTheaterAsync("Other");
        var ops = await app.CreateUserAsync("ops@example.com", employeeTheaterId: theater.Id);
        var noRoles = await app.CreateUserAsync("new@example.com", employeeTheaterId: theater.Id);
        var elsewhere = await app.CreateUserAsync("else@example.com", employeeTheaterId: other.Id);
        var regular = await app.CreateUserAsync("user@example.com");
        await app.GrantAsync(ops, EditProfile, ManageScreens);
        await app.GrantAsync(elsewhere, EditProfile, ManageScreens, ManageRoles);

        Assert.Equal(AllKeys, await app.PermissionsAsync(admin, theater, admin: true));
        Assert.Equal(AllKeys, await app.PermissionsAsync(owner, theater));
        Assert.Equal(new[] { EditProfile, ManageScreens }.Order(), (await app.PermissionsAsync(ops, theater)).Order());
        Assert.Empty(await app.PermissionsAsync(noRoles, theater));
        Assert.Empty(await app.PermissionsAsync(elsewhere, theater)); // roles at another theater don't count
        Assert.Empty(await app.PermissionsAsync(regular, theater));
    }

    [Fact]
    public async Task Employee_rights_are_the_union_of_their_roles()
    {
        await using var app = new TestApp();
        var theater = await app.CreateTheaterAsync("Starlight");
        var employee = await app.CreateUserAsync("e@example.com", employeeTheaterId: theater.Id);
        await app.GrantAsync(employee, EditProfile);
        await app.GrantAsync(employee, ManageScreens, ViewEmployees);

        Assert.Equal(new[] { EditProfile, ViewEmployees, ManageScreens }.Order(), (await app.PermissionsAsync(employee, theater)).Order());
    }

    [Fact]
    public async Task Handler_grants_requirement_only_with_the_permission()
    {
        await using var app = new TestApp();
        var theater = await app.CreateTheaterAsync("Starlight");
        var employee = await app.CreateUserAsync("e@example.com", employeeTheaterId: theater.Id);
        await app.GrantAsync(employee, ManageScreens);

        await using var scope = app.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var user = Principals.For(employee);
        Assert.True((await auth.AuthorizeAsync(user, theater, new TheaterPermissionRequirement(ManageScreens))).Succeeded);
        Assert.False((await auth.AuthorizeAsync(user, theater, new TheaterPermissionRequirement(EditProfile))).Succeeded);
        Assert.False((await auth.AuthorizeAsync(Principals.Anonymous, theater, new TheaterPermissionRequirement(ManageScreens))).Succeeded);
    }

    [Fact]
    public void Catalog_keys_are_unique_and_default_roles_use_only_known_keys()
    {
        Assert.Equal(All.Count, AllKeys.Count);
        Assert.All(DefaultTheaterRoles.All.SelectMany(r => r.Permissions), k => Assert.True(IsKnown(k), k));
        Assert.Equal(AllKeys, DefaultTheaterRoles.All.Single(r => r.Name == "Manager").Permissions.ToHashSet());
    }
}
