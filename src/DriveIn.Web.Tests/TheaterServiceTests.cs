using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Tests;

public class TheaterServiceTests
{
    [Fact]
    public async Task Delete_removes_screens_schedule_invitations_and_employee_accounts_only()
    {
        await using var app = new TestApp();
        var admin = await app.CreateUserAsync("admin@example.com", admin: true);
        var owner = await app.CreateUserAsync("owner@example.com");
        var doomed = await app.CreateTheaterAsync("Doomed", owner.Id);
        var kept = await app.CreateTheaterAsync("Kept", owner.Id);
        await app.CreateUserAsync("emp1@example.com", employeeTheaterId: doomed.Id);
        await app.CreateUserAsync("emp2@example.com", employeeTheaterId: kept.Id);
        await using (var db = app.Db())
        {
            db.Screens.AddRange(new Screen { TheaterId = doomed.Id, Name = "A" }, new Screen { TheaterId = kept.Id, Name = "B" });
            db.Invitations.Add(new Invitation { TheaterId = doomed.Id, Email = "x@example.com", TokenHash = "h", ExpiresAt = DateTimeOffset.MaxValue });
            await db.SaveChangesAsync();
            var screenA = await db.Screens.SingleAsync(s => s.Name == "A");
            db.Showtimes.Add(new Showtime
            {
                Screen = screenA, StartsAt = DateTimeOffset.UtcNow.AddDays(1), EndsAt = DateTimeOffset.UtcNow.AddDays(1).AddMinutes(124),
                Features = [new ShowtimeFeature { Position = 1, Film = new Film { TheaterId = doomed.Id, Title = "Jaws", RuntimeMinutes = 124 } }],
            });
            await db.SaveChangesAsync();
        }

        await app.Get<TheaterService>().DeleteAsync(Principals.For(admin, admin: true), doomed.Id);

        await using var check = app.Db();
        Assert.Equal(["Kept"], await check.Theaters.Select(t => t.Name).ToListAsync());
        Assert.Equal(["B"], await check.Screens.Select(s => s.Name).ToListAsync());
        Assert.Empty(await check.Invitations.ToListAsync());
        Assert.Empty(await check.Films.ToListAsync());
        Assert.Empty(await check.Showtimes.ToListAsync());
        Assert.Empty(await check.ShowtimeFeatures.ToListAsync());
        Assert.Equal(["admin@example.com", "emp2@example.com", "owner@example.com"],
            await check.Users.Select(u => u.Email!).OrderBy(e => e).ToListAsync());
    }

    [Fact]
    public async Task Only_admins_create_or_delete_theaters()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var service = app.Get<TheaterService>();

        await Assert.ThrowsAsync<AccessDeniedException>(() => service.CreateAsync(Principals.For(owner), new Theater { Name = "X", Slug = "x" }));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.DeleteAsync(Principals.For(owner), theater.Id));
    }

    [Fact]
    public async Task Employees_with_EditProfile_edit_only_their_theater_profile_and_not_admin_fields()
    {
        await using var app = new TestApp();
        var mine = await app.CreateTheaterAsync("Mine");
        var other = await app.CreateTheaterAsync("Other");
        var employee = await app.CreateUserAsync("emp@example.com", employeeTheaterId: mine.Id);
        var service = app.Get<TheaterService>();
        await Assert.ThrowsAsync<AccessDeniedException>(() => // no roles yet
            service.UpdateProfileAsync(Principals.For(employee), new Theater { Id = mine.Id, Name = "Nope" }));
        await app.GrantAsync(employee, TheaterPermissions.EditProfile);

        await service.UpdateProfileAsync(Principals.For(employee),
            new Theater { Id = mine.Id, Name = "Mine Renamed", Slug = "hijacked", IsActive = false, City = "Austin" });
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            service.UpdateProfileAsync(Principals.For(employee), new Theater { Id = other.Id, Name = "Nope" }));

        await using var db = app.Db();
        var saved = await db.Theaters.SingleAsync(t => t.Id == mine.Id);
        Assert.Equal(("Mine Renamed", "Austin", "mine", true), (saved.Name, saved.City, saved.Slug, saved.IsActive));
        Assert.Equal("Other", (await db.Theaters.SingleAsync(t => t.Id == other.Id)).Name);
    }

    [Fact]
    public async Task Slugs_must_be_unique()
    {
        await using var app = new TestApp();
        var admin = Principals.For(await app.CreateUserAsync("admin@example.com", admin: true), admin: true);
        await app.CreateTheaterAsync("Starlight");
        var service = app.Get<TheaterService>();

        await Assert.ThrowsAsync<AppValidationException>(() => service.CreateAsync(admin, new Theater { Name = "Dup", Slug = "starlight" }));
    }

    [Fact]
    public async Task ListManaged_returns_owned_and_employer_theaters()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        await app.CreateTheaterAsync("A", owner.Id);
        await app.CreateTheaterAsync("B", owner.Id);
        var c = await app.CreateTheaterAsync("C");
        var employee = await app.CreateUserAsync("emp@example.com", employeeTheaterId: c.Id);
        var regular = await app.CreateUserAsync("user@example.com");
        var service = app.Get<TheaterService>();

        Assert.Equal(["A", "B"], (await service.ListManagedAsync(Principals.For(owner))).Select(t => t.Name));
        Assert.Equal(["C"], (await service.ListManagedAsync(Principals.For(employee))).Select(t => t.Name));
        Assert.Empty(await service.ListManagedAsync(Principals.For(regular)));
    }
}
