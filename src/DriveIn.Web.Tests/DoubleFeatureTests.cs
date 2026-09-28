using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests;

public class DoubleFeatureTests
{
    private static readonly DateOnly Day = new(2026, 9, 5);

    // An owned theater in Chicago time (default intermission 15 min) with two screens and two films.
    private static async Task<(TestApp App, ApplicationUser Owner, Theater Theater, Screen North, Screen South, Film Jaws, Film Alien)> SetUpAsync()
    {
        var app = new TestApp(); // 2026-09-01 12:00 UTC
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        await using (var db = app.Db())
        {
            (await db.Theaters.SingleAsync(t => t.Id == theater.Id)).TimeZone = "America/Chicago";
            await db.SaveChangesAsync();
        }
        var me = Principals.For(owner);
        var north = await app.Get<ScreenService>().AddAsync(me, theater.Id, "North");
        var south = await app.Get<ScreenService>().AddAsync(me, theater.Id, "South");
        var jaws = await app.Get<ScheduleService>().AddFilmAsync(me, theater.Id, new FilmInput("Jaws", "PG", 124));
        var alien = await app.Get<ScheduleService>().AddFilmAsync(me, theater.Id, new FilmInput("Alien", "R", 117));
        return (app, owner, theater, north, south, jaws, alien);
    }

    private static ShowtimeInput Input(Screen screen, int hour, int minute, params Film[] films) =>
        new(screen.Id, films.Select(f => f.Id).ToList(), Day, new TimeOnly(hour, minute));

    [Fact]
    public async Task A_double_feature_is_one_showing_with_the_default_intermission_between_films()
    {
        var (app, owner, theater, north, _, jaws, alien) = await SetUpAsync();
        await using var _ = app;
        var schedule = app.Get<ScheduleService>();

        var showing = await schedule.AddShowtimeAsync(Principals.For(owner), Input(north, 20, 0, jaws, alien));

        Assert.Equal(15, showing.IntermissionMinutes);
        // 20:00 CDT = 01:00 UTC; 124 + 15 + 117 = 256 minutes.
        Assert.Equal(new DateTimeOffset(2026, 9, 6, 5, 16, 0, TimeSpan.Zero), showing.EndsAt);
        var view = Assert.Single(await schedule.ListUpcomingAsync(Principals.For(owner), theater.Id));
        Assert.Equal("Jaws + Alien", view.Title);
        Assert.True(view.IsMultiFeature);
        Assert.Equal([new DateTime(2026, 9, 5, 20, 0, 0), new DateTime(2026, 9, 5, 22, 19, 0)], view.Features.Select(f => f.StartsLocal));
        Assert.Equal(new DateTime(2026, 9, 6, 0, 16, 0), view.EndsLocal);
    }

    [Fact]
    public async Task The_intermission_can_be_overridden_and_changing_the_default_leaves_existing_showings_alone()
    {
        var (app, owner, theater, north, south, jaws, alien) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var schedule = app.Get<ScheduleService>();

        var custom = await schedule.AddShowtimeAsync(me, Input(north, 20, 0, jaws, alien) with { IntermissionMinutes = 30 });
        Assert.Equal(new DateTimeOffset(2026, 9, 6, 5, 31, 0, TimeSpan.Zero), custom.EndsAt);

        var standard = await schedule.AddShowtimeAsync(me, Input(south, 20, 0, jaws, alien));
        await schedule.SetDefaultIntermissionAsync(me, theater.Id, 20);
        var later = await schedule.AddShowtimeAsync(me, new ShowtimeInput(south.Id, [jaws.Id, alien.Id], Day.AddDays(1), new TimeOnly(20, 0)));

        await using var db = app.Db();
        Assert.Equal(15, (await db.Showtimes.SingleAsync(s => s.Id == standard.Id)).IntermissionMinutes);
        Assert.Equal(20, (await db.Showtimes.SingleAsync(s => s.Id == later.Id)).IntermissionMinutes);
        Assert.Equal(20, (await db.Theaters.SingleAsync(t => t.Id == theater.Id)).DefaultIntermissionMinutes);
    }

    [Fact]
    public async Task Showings_on_a_screen_cannot_overlap_any_part_of_a_double_feature()
    {
        var (app, owner, _, north, south, jaws, alien) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var schedule = app.Get<ScheduleService>();
        await schedule.AddShowtimeAsync(me, Input(north, 20, 0, jaws, alien)); // 20:00–00:16, intermission 22:04–22:19

        var during = await Assert.ThrowsAsync<AppValidationException>(() => schedule.AddShowtimeAsync(me, Input(north, 22, 5, jaws)));
        Assert.Contains("Jaws + Alien", during.Message);
        await Assert.ThrowsAsync<AppValidationException>(() => schedule.AddShowtimeAsync(me, Input(north, 23, 30, jaws))); // second feature
        await Assert.ThrowsAsync<AppValidationException>(() => schedule.AddShowtimeAsync(me, Input(north, 18, 0, jaws))); // runs into it
        await Assert.ThrowsAsync<AppValidationException>(() => schedule.AddShowtimeAsync(me, Input(north, 19, 0, jaws, alien))); // wraps it

        await schedule.AddShowtimeAsync(me, Input(south, 21, 0, jaws, alien)); // another screen
        await schedule.AddShowtimeAsync(me, Input(north, 17, 56, jaws)); // ends exactly at 20:00
    }

    [Fact]
    public async Task Showings_can_be_changed_and_changes_are_checked_for_overlaps()
    {
        var (app, owner, theater, north, south, jaws, alien) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var schedule = app.Get<ScheduleService>();
        var early = await schedule.AddShowtimeAsync(me, Input(north, 19, 0, jaws));
        await schedule.AddShowtimeAsync(me, Input(north, 22, 0, alien));

        // Adding Alien after Jaws would run past 22:00.
        await Assert.ThrowsAsync<AppValidationException>(() => schedule.UpdateShowtimeAsync(me, early.Id, Input(north, 19, 0, jaws, alien)));

        await schedule.UpdateShowtimeAsync(me, early.Id, Input(south, 19, 0, jaws, alien) with { IntermissionMinutes = 10 });
        var changed = (await schedule.ListUpcomingAsync(me, theater.Id, south.Id)).Single();
        Assert.Equal(("Jaws + Alien", 10), (changed.Title, changed.IntermissionMinutes));

        await schedule.UpdateShowtimeAsync(me, early.Id, Input(south, 19, 0, alien));
        Assert.Equal("Alien", (await schedule.ListUpcomingAsync(me, theater.Id, south.Id)).Single().Title);
        await using var db = app.Db();
        Assert.Equal([(1, alien.Id)], await db.ShowtimeFeatures.Where(f => f.ShowtimeId == early.Id).Select(f => ValueTuple.Create(f.Position, f.FilmId)).ToListAsync());
    }

    [Fact]
    public async Task Showings_that_have_started_cannot_be_changed()
    {
        var (app, owner, _, north, _, jaws, alien) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var schedule = app.Get<ScheduleService>();
        var showing = await schedule.AddShowtimeAsync(me, Input(north, 20, 0, jaws, alien));

        app.Time.SetUtcNow(new DateTimeOffset(2026, 9, 6, 2, 0, 0, TimeSpan.Zero)); // 21:00 CDT, during Jaws

        await Assert.ThrowsAsync<AppValidationException>(() => schedule.UpdateShowtimeAsync(me, showing.Id, Input(north, 20, 0, jaws)));
    }

    [Fact]
    public async Task A_longer_runtime_moves_the_end_of_double_features_and_cannot_cause_overlaps()
    {
        var (app, owner, _, north, _, jaws, alien) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var schedule = app.Get<ScheduleService>();
        var showing = await schedule.AddShowtimeAsync(me, Input(north, 19, 0, jaws, alien)); // until 23:16
        await schedule.AddShowtimeAsync(me, Input(north, 23, 30, alien));

        await schedule.UpdateFilmAsync(me, jaws.Id, new FilmInput("Jaws", "PG", 130));
        await using (var db = app.Db())
            Assert.Equal(new DateTimeOffset(2026, 9, 6, 4, 22, 0, TimeSpan.Zero), (await db.Showtimes.SingleAsync(s => s.Id == showing.Id)).EndsAt);

        await Assert.ThrowsAsync<AppValidationException>(() => schedule.UpdateFilmAsync(me, jaws.Id, new FilmInput("Jaws", "PG", 140)));
    }

    [Fact]
    public async Task Showing_input_is_validated()
    {
        var (app, owner, theater, north, _, jaws, alien) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var schedule = app.Get<ScheduleService>();
        var elsewhere = await app.CreateTheaterAsync("Elsewhere", owner.Id);
        var foreign = await schedule.AddFilmAsync(me, elsewhere.Id, new FilmInput("Them!", null, 94));

        await Assert.ThrowsAsync<AppValidationException>(() => schedule.AddShowtimeAsync(me, Input(north, 20, 0)));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            schedule.AddShowtimeAsync(me, Input(north, 12, 0, Enumerable.Repeat(alien, Showtime.MaxFeatures + 1).ToArray())));
        await Assert.ThrowsAsync<NotFoundException>(() => schedule.AddShowtimeAsync(me, Input(north, 20, 0, jaws, foreign)));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            schedule.AddShowtimeAsync(me, Input(north, 20, 0, jaws, alien) with { IntermissionMinutes = -1 }));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            schedule.AddShowtimeAsync(me, Input(north, 20, 0, jaws, alien) with { IntermissionMinutes = Showtime.MaxIntermissionMinutes + 1 }));
        await Assert.ThrowsAsync<AppValidationException>(() => schedule.SetDefaultIntermissionAsync(me, theater.Id, Showtime.MaxIntermissionMinutes + 1));

        await schedule.AddShowtimeAsync(me, Input(north, 12, 0, Enumerable.Repeat(alien, Showtime.MaxFeatures).ToArray())); // a marathon is fine
    }

    [Fact]
    public async Task Setting_the_default_intermission_requires_ManageSchedule()
    {
        var (app, _, theater, _, _, _, _) = await SetUpAsync();
        await using var _ = app;
        var employeeUser = await app.CreateUserAsync("emp@example.com", employeeTheaterId: theater.Id);
        var schedule = app.Get<ScheduleService>();

        await Assert.ThrowsAsync<AccessDeniedException>(() => schedule.SetDefaultIntermissionAsync(Principals.For(employeeUser), theater.Id, 20));
        await app.GrantAsync(employeeUser, ManageSchedule);
        await schedule.SetDefaultIntermissionAsync(Principals.For(employeeUser), theater.Id, 20);
    }

    [Fact]
    public async Task Removing_showings_and_films_removes_their_features()
    {
        var (app, owner, _, north, _, jaws, alien) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var schedule = app.Get<ScheduleService>();
        var removed = await schedule.AddShowtimeAsync(me, Input(north, 20, 0, jaws, alien));
        await schedule.AddShowtimeAsync(me, new ShowtimeInput(north.Id, [jaws.Id, alien.Id], Day.AddDays(1), new TimeOnly(20, 0)));

        await schedule.DeleteShowtimeAsync(me, removed.Id);
        await Assert.ThrowsAsync<AppValidationException>(() => schedule.DeleteFilmAsync(me, alien.Id)); // still in an upcoming double feature

        app.Time.Advance(TimeSpan.FromDays(10));
        await schedule.DeleteFilmAsync(me, alien.Id); // the whole past double feature goes, not just Alien's half

        await using var db = app.Db();
        Assert.Empty(await db.Showtimes.ToListAsync());
        Assert.Empty(await db.ShowtimeFeatures.ToListAsync());
        Assert.Equal(["Jaws"], await db.Films.Select(f => f.Title).ToListAsync());
    }
}
