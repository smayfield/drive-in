using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Tests;

public class SeasonTests
{
    // An owned theater in Chicago time with one screen and one film. The clock starts at 2026-09-01 12:00 UTC.
    private static async Task<(TestApp App, ApplicationUser Owner, Theater Theater, Screen Screen, Film Film)> SetUpAsync()
    {
        var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        await using (var db = app.Db())
        {
            (await db.Theaters.SingleAsync(t => t.Id == theater.Id)).TimeZone = "America/Chicago";
            await db.SaveChangesAsync();
        }
        var me = Principals.For(owner);
        var screen = await app.Get<ScreenService>().AddAsync(me, theater.Id, "North");
        var film = await app.Get<ScheduleService>().AddFilmAsync(me, theater.Id, new FilmInput("Jaws", "PG", 124));
        return (app, owner, theater, screen, film);
    }

    private static async Task SetSeasonAsync(TestApp app, ApplicationUser owner, int theaterId, DateOnly? opens, DateOnly? closes)
    {
        var theaters = app.Get<TheaterService>();
        var profile = (await theaters.GetForManageAsync(Principals.For(owner), theaterId)).CopyForEdit();
        profile.SeasonOpensOn = opens;
        profile.SeasonClosesOn = closes;
        await theaters.UpdateProfileAsync(Principals.For(owner), profile);
    }

    [Fact]
    public async Task Showings_can_only_be_scheduled_within_the_season()
    {
        var (app, owner, theater, screen, film) = await SetUpAsync();
        await using var _ = app;
        await SetSeasonAsync(app, owner, theater.Id, new DateOnly(2026, 4, 15), new DateOnly(2026, 9, 30));
        var schedule = app.Get<ScheduleService>();
        var me = Principals.For(owner);

        await schedule.AddShowtimeAsync(me, screen.Id, film.Id, new DateOnly(2026, 9, 30), new TimeOnly(20, 0)); // closing night
        var ex = await Assert.ThrowsAsync<AppValidationException>(() =>
            schedule.AddShowtimeAsync(me, screen.Id, film.Id, new DateOnly(2026, 10, 1), new TimeOnly(20, 0)));
        Assert.Contains("outside the theater's season (Apr 15, 2026 – Sep 30, 2026)", ex.Message);
    }

    [Fact]
    public async Task Either_end_of_the_season_can_be_open()
    {
        var (app, owner, theater, screen, film) = await SetUpAsync();
        await using var _ = app;
        await SetSeasonAsync(app, owner, theater.Id, new DateOnly(2026, 9, 10), null);
        var schedule = app.Get<ScheduleService>();
        var me = Principals.For(owner);

        await Assert.ThrowsAsync<AppValidationException>(() =>
            schedule.AddShowtimeAsync(me, screen.Id, film.Id, new DateOnly(2026, 9, 9), new TimeOnly(20, 0)));
        await schedule.AddShowtimeAsync(me, screen.Id, film.Id, new DateOnly(2027, 6, 1), new TimeOnly(20, 0));

        await using var db = app.Db();
        var saved = await db.Theaters.SingleAsync(t => t.Id == theater.Id);
        Assert.Equal("from Sep 10, 2026", Seasons.Describe(saved));
    }

    [Fact]
    public async Task The_season_cannot_close_before_it_opens()
    {
        var (app, owner, theater, _, _) = await SetUpAsync();
        await using var _ = app;

        await Assert.ThrowsAsync<AppValidationException>(() =>
            SetSeasonAsync(app, owner, theater.Id, new DateOnly(2026, 9, 30), new DateOnly(2026, 5, 1)));
    }

    [Fact]
    public async Task The_season_cannot_be_changed_to_leave_out_scheduled_showings()
    {
        var (app, owner, theater, screen, film) = await SetUpAsync();
        await using var _ = app;
        // 11 PM Chicago on Sep 20 is already Sep 21 in UTC: the season is in local dates.
        await app.Get<ScheduleService>().AddShowtimeAsync(Principals.For(owner), screen.Id, film.Id, new DateOnly(2026, 9, 20), new TimeOnly(23, 0));

        var ex = await Assert.ThrowsAsync<AppValidationException>(() =>
            SetSeasonAsync(app, owner, theater.Id, null, new DateOnly(2026, 9, 19)));
        Assert.Contains("Sep 20, 2026", ex.Message);
        await SetSeasonAsync(app, owner, theater.Id, null, new DateOnly(2026, 9, 20));
    }

    [Fact]
    public async Task Only_those_who_can_edit_the_profile_can_set_the_season()
    {
        var (app, owner, theater, _, _) = await SetUpAsync();
        await using var _ = app;
        var employee = await app.CreateUserAsync("staff@example.com", employeeTheaterId: theater.Id);
        await app.GrantAsync(employee, DriveIn.Web.Authorization.TheaterPermissions.ManageSchedule);
        var profile = (await app.Get<TheaterService>().GetForManageAsync(Principals.For(employee), theater.Id)).CopyForEdit();
        profile.SeasonOpensOn = new DateOnly(2026, 5, 1);

        await Assert.ThrowsAsync<AccessDeniedException>(() => app.Get<TheaterService>().UpdateProfileAsync(Principals.For(employee), profile));
    }
}
