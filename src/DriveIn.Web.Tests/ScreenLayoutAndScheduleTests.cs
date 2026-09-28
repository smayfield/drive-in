using DriveIn.Web.Authorization;
using DriveIn.Web.Components.Shared;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests;

public class ScreenLayoutTests
{
    [Fact]
    public async Task New_theaters_start_with_one_screen()
    {
        await using var app = new TestApp();
        var admin = Principals.For(await app.CreateUserAsync("admin@example.com", admin: true), admin: true);

        var theater = await app.Get<TheaterService>().CreateAsync(admin, new Theater { Name = "Starlight", Slug = "starlight" });

        Assert.Equal(["Screen 1"], (await app.Get<TheaterService>().GetForManageAsync(admin, theater.Id)).Screens.Select(s => s.Name));
    }

    [Fact]
    public async Task A_theater_has_one_to_four_screens()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var me = Principals.For(owner);
        var screens = app.Get<ScreenService>();

        var first = await screens.AddAsync(me, theater.Id, "One");
        for (var i = 2; i <= Screen.MaxPerTheater; i++)
            await screens.AddAsync(me, theater.Id, $"Screen {i}");
        await Assert.ThrowsAsync<AppValidationException>(() => screens.AddAsync(me, theater.Id, "Fifth"));
        await Assert.ThrowsAsync<AppValidationException>(() => screens.AddAsync(me, theater.Id, "  "));

        await using (var db = app.Db())
        {
            foreach (var id in await db.Screens.Where(s => s.Id != first.Id).Select(s => s.Id).ToListAsync())
                await screens.DeleteAsync(me, id);
        }
        var ex = await Assert.ThrowsAsync<AppValidationException>(() => screens.DeleteAsync(me, first.Id));
        Assert.Contains("at least one", ex.Message);
    }

    [Fact]
    public async Task Layout_is_saved_and_validated()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var me = Principals.For(owner);
        var screens = app.Get<ScreenService>();
        var screen = await screens.AddAsync(me, theater.Id, "Main");

        await screens.UpdateAsync(me, screen.Id, new ScreenLayoutInput(" North ", SpotLabelScheme.Numeric, [10, 12, 14]));

        var saved = await screens.GetAsync(me, screen.Id);
        Assert.Equal(("North", SpotLabelScheme.Numeric, 36), (saved.Name, saved.LabelScheme, saved.SpotCount));
        Assert.Equal([10, 12, 14], saved.RowSpots);

        await Assert.ThrowsAsync<AppValidationException>(() =>
            screens.UpdateAsync(me, screen.Id, new ScreenLayoutInput("Main", SpotLabelScheme.LetterNumber, [10, 0])));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            screens.UpdateAsync(me, screen.Id, new ScreenLayoutInput("Main", SpotLabelScheme.LetterNumber, [Screen.MaxSpotsPerRow + 1])));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            screens.UpdateAsync(me, screen.Id, new ScreenLayoutInput("Main", SpotLabelScheme.LetterNumber, Enumerable.Repeat(5, Screen.MaxRows + 1).ToList())));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            screens.UpdateAsync(me, screen.Id, new ScreenLayoutInput("Main", (SpotLabelScheme)42, [5])));
    }

    [Fact]
    public async Task Members_view_layouts_but_need_ManageScreens_to_change_them()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var screen = await app.Get<ScreenService>().AddAsync(Principals.For(owner), theater.Id, "Main");
        var employeeUser = await app.CreateUserAsync("emp@example.com", employeeTheaterId: theater.Id);
        var employee = Principals.For(employeeUser);
        var outsider = Principals.For(await app.CreateUserAsync("user@example.com"));
        var screens = app.Get<ScreenService>();
        var layout = new ScreenLayoutInput("Main", SpotLabelScheme.NumberLetter, [4]);

        Assert.Equal("Main", (await screens.GetAsync(employee, screen.Id)).Name);
        await Assert.ThrowsAsync<AccessDeniedException>(() => screens.GetAsync(outsider, screen.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => screens.UpdateAsync(employee, screen.Id, layout));

        await app.GrantAsync(employeeUser, ManageScreens);
        await screens.UpdateAsync(employee, screen.Id, layout);
        Assert.Equal(4, (await screens.GetAsync(employee, screen.Id)).SpotCount);
    }

    [Fact]
    public async Task Screens_with_upcoming_showtimes_cannot_be_deleted()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var me = Principals.For(owner);
        var screens = app.Get<ScreenService>();
        await screens.AddAsync(me, theater.Id, "Keep");
        var doomed = await screens.AddAsync(me, theater.Id, "Doomed");
        var film = await app.Get<ScheduleService>().AddFilmAsync(me, theater.Id, new FilmInput("Jaws", "PG", 124));
        await app.Get<ScheduleService>().AddShowtimeAsync(me, doomed.Id, film.Id, new DateOnly(2026, 9, 2), new TimeOnly(20, 0));

        await Assert.ThrowsAsync<AppValidationException>(() => screens.DeleteAsync(me, doomed.Id));

        app.Time.Advance(TimeSpan.FromDays(3)); // the showtime is now in the past
        await screens.DeleteAsync(me, doomed.Id);
        await using var db = app.Db();
        Assert.Empty(await db.Showtimes.ToListAsync());
        Assert.Equal(["Keep"], await db.Screens.Select(s => s.Name).ToListAsync());
    }

    [Theory]
    [InlineData(SpotLabelScheme.LetterNumber, 1, 1, "A1")]
    [InlineData(SpotLabelScheme.LetterNumber, 2, 12, "B12")]
    [InlineData(SpotLabelScheme.LetterNumber, 27, 3, "AA3")]
    [InlineData(SpotLabelScheme.NumberLetter, 1, 1, "1A")]
    [InlineData(SpotLabelScheme.NumberLetter, 3, 28, "3AB")]
    [InlineData(SpotLabelScheme.Numeric, 1, 1, "101")]
    [InlineData(SpotLabelScheme.Numeric, 2, 2, "202")]
    [InlineData(SpotLabelScheme.Numeric, 12, 45, "1245")]
    public void Spot_labels_follow_the_scheme(SpotLabelScheme scheme, int row, int spot, string expected) =>
        Assert.Equal(expected, SpotLabels.Spot(scheme, row, spot));

    [Fact]
    public void Row_labels_follow_the_scheme()
    {
        Assert.Equal("C", SpotLabels.Row(SpotLabelScheme.LetterNumber, 3));
        Assert.Equal("3", SpotLabels.Row(SpotLabelScheme.NumberLetter, 3));
        Assert.Equal("3", SpotLabels.Row(SpotLabelScheme.Numeric, 3));
        Assert.Equal("Z", SpotLabels.Letters(26));
        Assert.Equal("AZ", SpotLabels.Letters(52));
    }
}

public class LotPlanTests
{
    private static Screen Screen(int id, params int[] rows) =>
        new() { Id = id, SortOrder = id, Name = $"Screen {id}", RowSpots = rows.ToList() };

    [Fact]
    public void Rows_are_centered_on_the_screen_nearest_first()
    {
        var field = LotField.For(Screen(1, 4, 6));

        foreach (var row in field.Rows)
            Assert.Equal(-row.Spots[0].X, row.Spots[^1].X + LotPlan.SpotWidth, 6);
        Assert.True(field.Rows[0].Y < field.Rows[1].Y);
        Assert.Equal(["A1", "A2", "A3", "A4"], field.Rows[0].Spots.Select(s => s.Label));
        Assert.True(field.Rows[0].Spots[0].X < field.Rows[0].Spots[1].X); // spot 1 on the left
    }

    [Fact]
    public void Theater_view_puts_screens_1_and_2_opposite_and_3_and_4_opposite_at_90_degrees()
    {
        var plan = LotPlan.ForTheater([Screen(1, 20, 22), Screen(2, 18), Screen(3, 30, 30, 30), Screen(4, 10)]);

        Assert.Equal([0, 180, 90, 270], plan.Fields.Select(f => f.Angle));
        var b = plan.Building!.Value;
        var (north, south, east, west) = (plan.Fields[0].Bounds, plan.Fields[1].Bounds, plan.Fields[2].Bounds, plan.Fields[3].Bounds);
        Assert.True(north.Bottom <= b.Y && south.Y >= b.Bottom && east.X >= b.Right && west.Right <= b.X);

        var all = plan.Fields.Select(f => f.Bounds).Append(b).ToList();
        for (var i = 0; i < all.Count; i++)
            for (var j = i + 1; j < all.Count; j++)
                Assert.False(all[i].Intersects(all[j]), $"Areas {i} and {j} overlap");
        Assert.All(all, r => Assert.True(plan.ViewBox.Union(r) == plan.ViewBox));
    }

    [Fact]
    public void Theater_view_follows_screen_order()
    {
        var plan = LotPlan.ForTheater([Screen(2, 5), Screen(1, 5)]);

        Assert.Equal([1, 2], plan.Fields.Select(f => f.Field.Screen.Id));
    }
}

public class ScheduleTests
{
    private static async Task<(TestApp App, ApplicationUser Owner, Theater Theater, Screen Screen, Film Film)> SetUpAsync()
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
        var screen = await app.Get<ScreenService>().AddAsync(me, theater.Id, "North");
        var film = await app.Get<ScheduleService>().AddFilmAsync(me, theater.Id, new FilmInput(" Jaws ", " PG ", 124));
        return (app, owner, theater, screen, film);
    }

    [Fact]
    public async Task Showtimes_are_entered_and_listed_in_theater_time_and_stored_in_utc()
    {
        var (app, owner, theater, screen, film) = await SetUpAsync();
        await using var _ = app;
        var schedule = app.Get<ScheduleService>();

        var showtime = await schedule.AddShowtimeAsync(Principals.For(owner), screen.Id, film.Id, new DateOnly(2026, 9, 5), new TimeOnly(20, 30));

        Assert.Equal(new DateTimeOffset(2026, 9, 6, 1, 30, 0, TimeSpan.Zero), showtime.StartsAt); // CDT is UTC-5
        var listed = Assert.Single(await schedule.ListUpcomingAsync(Principals.For(owner), theater.Id));
        Assert.Equal((new DateTime(2026, 9, 5, 20, 30, 0), new DateTime(2026, 9, 5, 22, 34, 0), "Jaws", "PG", "North"),
            (listed.StartsLocal, listed.EndsLocal, listed.Title, listed.Features.Single().Rating, listed.ScreenName));
    }

    [Fact]
    public async Task Showtimes_on_a_screen_cannot_overlap_or_be_in_the_past()
    {
        var (app, owner, theater, screen, film) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var schedule = app.Get<ScheduleService>();
        var other = await app.Get<ScreenService>().AddAsync(me, theater.Id, "South");
        var day = new DateOnly(2026, 9, 5);
        await schedule.AddShowtimeAsync(me, screen.Id, film.Id, day, new TimeOnly(20, 30)); // until 22:34

        var ex = await Assert.ThrowsAsync<AppValidationException>(() =>
            schedule.AddShowtimeAsync(me, screen.Id, film.Id, day, new TimeOnly(22, 0)));
        Assert.Contains("10:34", ex.Message); // in theater time
        await Assert.ThrowsAsync<AppValidationException>(() =>
            schedule.AddShowtimeAsync(me, screen.Id, film.Id, day, new TimeOnly(19, 0)));
        await schedule.AddShowtimeAsync(me, screen.Id, film.Id, day, new TimeOnly(22, 34)); // back to back is fine
        await schedule.AddShowtimeAsync(me, other.Id, film.Id, day, new TimeOnly(21, 0)); // other screen is fine
        await Assert.ThrowsAsync<AppValidationException>(() =>
            schedule.AddShowtimeAsync(me, screen.Id, film.Id, new DateOnly(2026, 8, 31), new TimeOnly(20, 0)));

        Assert.Equal(3, (await schedule.ListUpcomingAsync(me, theater.Id)).Count);
        Assert.Equal(2, (await schedule.ListUpcomingAsync(me, theater.Id, screen.Id)).Count);
    }

    [Fact]
    public async Task Lengthening_a_film_cannot_make_its_showtimes_overlap()
    {
        var (app, owner, theater, screen, film) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var schedule = app.Get<ScheduleService>();
        var day = new DateOnly(2026, 9, 5);
        await schedule.AddShowtimeAsync(me, screen.Id, film.Id, day, new TimeOnly(20, 0));
        await schedule.AddShowtimeAsync(me, screen.Id, film.Id, day, new TimeOnly(22, 30));

        await schedule.UpdateFilmAsync(me, film.Id, new FilmInput("Jaws", "PG", 150));
        await Assert.ThrowsAsync<AppValidationException>(() => schedule.UpdateFilmAsync(me, film.Id, new FilmInput("Jaws", "PG", 151)));

        await using var db = app.Db();
        Assert.Equal(150, (await db.Films.SingleAsync()).RuntimeMinutes);
    }

    [Fact]
    public async Task Films_from_another_theater_cannot_be_scheduled()
    {
        var (app, owner, _, screen, _) = await SetUpAsync();
        await using var __ = app;
        var me = Principals.For(owner);
        var elsewhere = await app.CreateTheaterAsync("Elsewhere", owner.Id);
        var foreign = await app.Get<ScheduleService>().AddFilmAsync(me, elsewhere.Id, new FilmInput("Alien", "R", 117));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            app.Get<ScheduleService>().AddShowtimeAsync(me, screen.Id, foreign.Id, new DateOnly(2026, 9, 5), new TimeOnly(20, 0)));
    }

    [Fact]
    public async Task Films_with_upcoming_showtimes_cannot_be_deleted()
    {
        var (app, owner, _, screen, film) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var schedule = app.Get<ScheduleService>();
        await schedule.AddShowtimeAsync(me, screen.Id, film.Id, new DateOnly(2026, 9, 5), new TimeOnly(20, 0));

        await Assert.ThrowsAsync<AppValidationException>(() => schedule.DeleteFilmAsync(me, film.Id));
        app.Time.Advance(TimeSpan.FromDays(5));
        await schedule.DeleteFilmAsync(me, film.Id);

        await using var db = app.Db();
        Assert.Empty(await db.Films.ToListAsync());
        Assert.Empty(await db.Showtimes.ToListAsync());
    }

    [Fact]
    public async Task Members_view_the_schedule_but_need_ManageSchedule_to_change_it()
    {
        var (app, owner, theater, screen, film) = await SetUpAsync();
        await using var _ = app;
        var employeeUser = await app.CreateUserAsync("emp@example.com", employeeTheaterId: theater.Id);
        var employee = Principals.For(employeeUser);
        var outsider = Principals.For(await app.CreateUserAsync("user@example.com"));
        var schedule = app.Get<ScheduleService>();
        var day = new DateOnly(2026, 9, 5);
        var showtime = await schedule.AddShowtimeAsync(Principals.For(owner), screen.Id, film.Id, day, new TimeOnly(20, 0));

        Assert.Single(await schedule.ListUpcomingAsync(employee, theater.Id));
        Assert.Single(await schedule.ListFilmsAsync(employee, theater.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => schedule.ListUpcomingAsync(outsider, theater.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => schedule.AddFilmAsync(employee, theater.Id, new FilmInput("X", null, 90)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => schedule.AddShowtimeAsync(employee, screen.Id, film.Id, day, new TimeOnly(23, 0)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => schedule.DeleteShowtimeAsync(employee, showtime.Id));
        await app.GrantAsync(employeeUser, ManageScreens); // screens aren't the schedule
        await Assert.ThrowsAsync<AccessDeniedException>(() => schedule.DeleteShowtimeAsync(employee, showtime.Id));

        await app.GrantAsync(employeeUser, ManageSchedule);
        await schedule.DeleteShowtimeAsync(employee, showtime.Id);
        Assert.Empty(await schedule.ListUpcomingAsync(employee, theater.Id));
    }

    [Fact]
    public async Task Theater_time_zones_must_be_real()
    {
        var (app, owner, theater, _, _) = await SetUpAsync();
        await using var _ = app;
        var service = app.Get<TheaterService>();

        await Assert.ThrowsAsync<AppValidationException>(() =>
            service.UpdateProfileAsync(Principals.For(owner), new Theater { Id = theater.Id, Name = "Starlight", TimeZone = "Mars/Olympus" }));
        await service.UpdateProfileAsync(Principals.For(owner), new Theater { Id = theater.Id, Name = "Starlight", TimeZone = " America/Denver " });

        await using var db = app.Db();
        Assert.Equal("America/Denver", (await db.Theaters.SingleAsync(t => t.Id == theater.Id)).TimeZone);
    }
}
