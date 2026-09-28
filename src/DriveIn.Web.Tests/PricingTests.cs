using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests;

public class PricingTests
{
    // An owned theater (Chicago time) with its default "Standard" schedule, one screen and one film.
    private static async Task<(TestApp App, ApplicationUser Owner, Theater Theater, PriceSchedule Standard, Screen Screen, Film Film)> SetUpAsync()
    {
        var app = new TestApp(); // 2026-09-01 12:00 UTC
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var standard = new PriceSchedule { TheaterId = theater.Id, Name = "Standard", IsDefault = true };
        await using (var db = app.Db())
        {
            (await db.Theaters.SingleAsync(t => t.Id == theater.Id)).TimeZone = "America/Chicago";
            db.PriceSchedules.Add(standard);
            await db.SaveChangesAsync();
        }
        var me = Principals.For(owner);
        await app.Get<PricingService>().UpdateScheduleAsync(me, standard.Id, "Standard",
            [new(null, "1 occupant", null, 10m), new(null, "2 occupants", null, 15m), new(null, "Car load", "Up to 6", 25m)]);
        var screen = await app.Get<ScreenService>().AddAsync(me, theater.Id, "North");
        var film = await app.Get<ScheduleService>().AddFilmAsync(me, theater.Id, new FilmInput("Jaws", "PG", 124));
        return (app, owner, theater, standard, screen, film);
    }

    [Fact]
    public async Task New_theaters_start_with_a_default_Standard_schedule()
    {
        await using var app = new TestApp();
        var admin = Principals.For(await app.CreateUserAsync("admin@example.com", admin: true), admin: true);

        var theater = await app.Get<TheaterService>().CreateAsync(admin, new Theater { Name = "Starlight", Slug = "starlight" });

        var pricing = await app.Get<PricingService>().GetAsync(admin, theater.Id);
        Assert.Equal("Standard", Assert.Single(pricing.Schedules).Name);
        Assert.Equal("Standard", pricing.Default?.Name);
    }

    [Fact]
    public async Task Editing_a_schedule_keeps_option_ids_and_applies_order_adds_and_removals()
    {
        var (app, owner, theater, standard, _, _) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var pricing = app.Get<PricingService>();
        var before = (await pricing.GetAsync(me, theater.Id)).Default!.Options;
        var (one, two, carload) = (before[0], before[1], before[2]);

        await pricing.UpdateScheduleAsync(me, standard.Id, "Regular",
            [new(carload.Id, "Car load", "Up to 6", 30m), new(one.Id, "Single", null, 12.5m), new(null, "Walk-in", null, 5m)]);

        var after = (await pricing.GetAsync(me, theater.Id)).Default!;
        Assert.Equal("Regular", after.Name);
        Assert.Equal(["Car load", "Single", "Walk-in"], after.Options.Select(o => o.Name));
        Assert.Equal([30m, 12.5m, 5m], after.Options.Select(o => o.Price));
        Assert.Equal(carload.Id, after.Options[0].Id);
        Assert.Equal(one.Id, after.Options[1].Id);
        Assert.DoesNotContain(after.Options, o => o.Id == two.Id);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10.001)]
    [InlineData(10000)]
    public async Task Prices_must_be_whole_cents_within_range(decimal price)
    {
        var (app, owner, _, standard, _, _) = await SetUpAsync();
        await using var _ = app;

        await Assert.ThrowsAsync<AppValidationException>(() =>
            app.Get<PricingService>().UpdateScheduleAsync(Principals.For(owner), standard.Id, "Standard", [new(null, "X", null, price)]));
    }

    [Fact]
    public async Task Schedule_and_option_names_are_validated()
    {
        var (app, owner, theater, standard, _, _) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var pricing = app.Get<PricingService>();

        await Assert.ThrowsAsync<AppValidationException>(() => pricing.CreateScheduleAsync(me, theater.Id, "STANDARD"));
        await Assert.ThrowsAsync<AppValidationException>(() => pricing.CreateScheduleAsync(me, theater.Id, " "));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            pricing.UpdateScheduleAsync(me, standard.Id, "Standard", [new(null, "Car load", null, 1m), new(null, "car LOAD", null, 2m)]));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            pricing.UpdateScheduleAsync(me, standard.Id, "Standard",
                Enumerable.Range(1, PriceOption.MaxPerSchedule + 1).Select(i => new PriceOptionInput(null, $"O{i}", null, 1m)).ToList()));
    }

    [Fact]
    public async Task Options_from_another_schedule_cannot_be_edited_through_this_one()
    {
        var (app, owner, theater, standard, _, _) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var pricing = app.Get<PricingService>();
        var threeD = await pricing.CreateScheduleAsync(me, theater.Id, "3D", copyFromId: standard.Id);
        var foreignOption = (await pricing.GetAsync(me, theater.Id)).Default!.Options[0];

        await Assert.ThrowsAsync<NotFoundException>(() =>
            pricing.UpdateScheduleAsync(me, threeD.Id, "3D", [new(foreignOption.Id, "Hijacked", null, 0m)]));
    }

    [Fact]
    public async Task An_option_cannot_be_listed_twice()
    {
        var (app, owner, theater, standard, _, _) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var pricing = app.Get<PricingService>();
        var option = (await pricing.GetAsync(me, theater.Id)).Default!.Options[0];

        await Assert.ThrowsAsync<AppValidationException>(() =>
            pricing.UpdateScheduleAsync(me, standard.Id, "Standard", [new(option.Id, "A", null, 1m), new(option.Id, "B", null, 2m)]));
    }

    [Fact]
    public async Task New_schedules_can_copy_another_and_one_can_become_the_default()
    {
        var (app, owner, theater, standard, _, _) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var pricing = app.Get<PricingService>();

        var threeD = await pricing.CreateScheduleAsync(me, theater.Id, "3D", copyFromId: standard.Id);
        var copied = (await pricing.GetAsync(me, theater.Id)).Schedules.Single(s => s.Id == threeD.Id);
        Assert.Equal(["1 occupant", "2 occupants", "Car load"], copied.Options.Select(o => o.Name));
        Assert.False(copied.IsDefault);

        await pricing.SetDefaultScheduleAsync(me, threeD.Id);

        var schedules = (await pricing.GetAsync(me, theater.Id)).Schedules;
        Assert.Equal("3D", Assert.Single(schedules, s => s.IsDefault).Name);
    }

    [Fact]
    public async Task Showtimes_use_the_default_schedule_unless_overridden()
    {
        var (app, owner, theater, _, screen, film) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var pricing = app.Get<PricingService>();
        var schedule = app.Get<ScheduleService>();
        var special = await pricing.CreateScheduleAsync(me, theater.Id, "Special");
        await pricing.UpdateScheduleAsync(me, special.Id, "Special", [new(null, "Per car", null, 40m)]);
        var day = new DateOnly(2026, 9, 5);

        var regular = await schedule.AddShowtimeAsync(me, screen.Id, film.Id, day, new TimeOnly(19, 0));
        var premiere = await schedule.AddShowtimeAsync(me, screen.Id, film.Id, day, new TimeOnly(21, 30), special.Id);

        var regularPricing = await pricing.GetForShowtimeAsync(me, regular.Id);
        Assert.Equal(("Standard", false, 3), (regularPricing.Schedule.Name, regularPricing.IsOverride, regularPricing.Schedule.Options.Count));
        var premierePricing = await pricing.GetForShowtimeAsync(me, premiere.Id);
        Assert.Equal(("Special", true, 40m), (premierePricing.Schedule.Name, premierePricing.IsOverride, premierePricing.Schedule.Options.Single().Price));
        Assert.Equal([null, "Special"], (await schedule.ListUpcomingAsync(me, theater.Id)).Select(s => s.PriceScheduleName));

        await schedule.SetShowtimePricingAsync(me, premiere.Id, null);
        Assert.Equal("Standard", (await pricing.GetForShowtimeAsync(me, premiere.Id)).Schedule.Name);
    }

    [Fact]
    public async Task Showtimes_cannot_use_another_theaters_schedule()
    {
        var (app, owner, _, _, screen, film) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var elsewhere = await app.CreateTheaterAsync("Elsewhere", owner.Id);
        var foreign = await app.Get<PricingService>().CreateScheduleAsync(me, elsewhere.Id, "Theirs");
        var schedule = app.Get<ScheduleService>();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            schedule.AddShowtimeAsync(me, screen.Id, film.Id, new DateOnly(2026, 9, 5), new TimeOnly(20, 0), foreign.Id));
        var showtime = await schedule.AddShowtimeAsync(me, screen.Id, film.Id, new DateOnly(2026, 9, 5), new TimeOnly(20, 0));
        await Assert.ThrowsAsync<NotFoundException>(() => schedule.SetShowtimePricingAsync(me, showtime.Id, foreign.Id));
    }

    [Fact]
    public async Task Schedules_in_use_or_default_cannot_be_deleted_and_past_showtimes_fall_back_to_the_default()
    {
        var (app, owner, theater, standard, screen, film) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var pricing = app.Get<PricingService>();
        var threeD = await pricing.CreateScheduleAsync(me, theater.Id, "3D");
        var showtime = await app.Get<ScheduleService>().AddShowtimeAsync(me, screen.Id, film.Id, new DateOnly(2026, 9, 5), new TimeOnly(20, 0), threeD.Id);

        await Assert.ThrowsAsync<AppValidationException>(() => pricing.DeleteScheduleAsync(me, standard.Id));
        await Assert.ThrowsAsync<AppValidationException>(() => pricing.DeleteScheduleAsync(me, threeD.Id));

        app.Time.Advance(TimeSpan.FromDays(7));
        await pricing.DeleteScheduleAsync(me, threeD.Id);

        await using var db = app.Db();
        Assert.Null((await db.Showtimes.SingleAsync(s => s.Id == showtime.Id)).PriceScheduleId);
        Assert.Equal(["Standard"], await db.PriceSchedules.Select(s => s.Name).ToListAsync());
    }

    [Fact]
    public async Task Add_ons_are_fees_or_discounts_and_only_active_ones_are_offered()
    {
        var (app, owner, theater, _, screen, film) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var pricing = app.Get<PricingService>();

        var food = await pricing.AddAddOnAsync(me, theater.Id, new AddOnInput(" Outside food ", null, AddOnKind.Fee, 5m, true));
        await pricing.AddAddOnAsync(me, theater.Id, new AddOnInput("Veteran", "Show ID", AddOnKind.Discount, 2m, true));
        var senior = await pricing.AddAddOnAsync(me, theater.Id, new AddOnInput("Senior", null, AddOnKind.PercentDiscount, 15m, true));
        await pricing.UpdateAddOnAsync(me, senior.Id, new AddOnInput("Senior", null, AddOnKind.PercentDiscount, 15m, false));

        await Assert.ThrowsAsync<AppValidationException>(() => pricing.AddAddOnAsync(me, theater.Id, new AddOnInput("outside FOOD", null, AddOnKind.Fee, 1m, true)));
        await Assert.ThrowsAsync<AppValidationException>(() => pricing.AddAddOnAsync(me, theater.Id, new AddOnInput("Half off", null, AddOnKind.PercentDiscount, 150m, true)));
        await Assert.ThrowsAsync<AppValidationException>(() => pricing.AddAddOnAsync(me, theater.Id, new AddOnInput("Free", null, AddOnKind.Fee, 0m, true)));
        await Assert.ThrowsAsync<AppValidationException>(() => pricing.AddAddOnAsync(me, theater.Id, new AddOnInput("Weird", null, (AddOnKind)9, 1m, true)));

        var all = (await pricing.GetAsync(me, theater.Id)).AddOns;
        Assert.Equal(["Outside food", "Veteran", "Senior"], all.Select(a => a.Name));
        Assert.Equal(["+$5.00", "−$2.00", "−15%"], all.Select(a => a.Display));

        var showtime = await app.Get<ScheduleService>().AddShowtimeAsync(me, screen.Id, film.Id, new DateOnly(2026, 9, 5), new TimeOnly(20, 0));
        Assert.Equal(["Outside food", "Veteran"], (await pricing.GetForShowtimeAsync(me, showtime.Id)).AddOns.Select(a => a.Name));

        await pricing.DeleteAddOnAsync(me, food.Id);
        Assert.Equal(2, (await pricing.GetAsync(me, theater.Id)).AddOns.Count);
    }

    [Fact]
    public async Task Members_view_pricing_but_need_ManagePricing_to_change_it_and_ManageSchedule_to_apply_it()
    {
        var (app, owner, theater, standard, screen, film) = await SetUpAsync();
        await using var _ = app;
        var employeeUser = await app.CreateUserAsync("emp@example.com", employeeTheaterId: theater.Id);
        var employee = Principals.For(employeeUser);
        var outsider = Principals.For(await app.CreateUserAsync("user@example.com"));
        var pricing = app.Get<PricingService>();
        var schedule = app.Get<ScheduleService>();
        var special = await pricing.CreateScheduleAsync(Principals.For(owner), theater.Id, "Special");
        var showtime = await schedule.AddShowtimeAsync(Principals.For(owner), screen.Id, film.Id, new DateOnly(2026, 9, 5), new TimeOnly(20, 0));

        Assert.Equal(2, (await pricing.GetAsync(employee, theater.Id)).Schedules.Count);
        Assert.Equal("Standard", (await pricing.GetForShowtimeAsync(employee, showtime.Id)).Schedule.Name);
        await Assert.ThrowsAsync<AccessDeniedException>(() => pricing.GetAsync(outsider, theater.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => pricing.GetForShowtimeAsync(outsider, showtime.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => pricing.CreateScheduleAsync(employee, theater.Id, "Mine"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => pricing.UpdateScheduleAsync(employee, standard.Id, "Standard", []));
        await Assert.ThrowsAsync<AccessDeniedException>(() => pricing.SetDefaultScheduleAsync(employee, special.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => pricing.AddAddOnAsync(employee, theater.Id, new AddOnInput("X", null, AddOnKind.Fee, 1m, true)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => schedule.SetShowtimePricingAsync(employee, showtime.Id, special.Id));

        await app.GrantAsync(employeeUser, ManagePricing); // prices, not the schedule
        await pricing.AddAddOnAsync(employee, theater.Id, new AddOnInput("Outside food", null, AddOnKind.Fee, 5m, true));
        await Assert.ThrowsAsync<AccessDeniedException>(() => schedule.SetShowtimePricingAsync(employee, showtime.Id, special.Id));

        await app.GrantAsync(employeeUser, ManageSchedule);
        await schedule.SetShowtimePricingAsync(employee, showtime.Id, special.Id);
        Assert.Equal("Special", (await pricing.GetForShowtimeAsync(employee, showtime.Id)).Schedule.Name);
    }

    [Fact]
    public async Task Renaming_an_add_on_keeps_its_normalized_name_in_sync()
    {
        var (app, owner, theater, _, _, _) = await SetUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var pricing = app.Get<PricingService>();
        var addOn = await pricing.AddAddOnAsync(me, theater.Id, new AddOnInput("Seniors", null, AddOnKind.PercentDiscount, 10m, true));

        await pricing.UpdateAddOnAsync(me, addOn.Id, new AddOnInput(" Senior citizens ", null, AddOnKind.PercentDiscount, 10m, true));

        await using (var db = app.Db())
            Assert.Equal("SENIOR CITIZENS", (await db.AddOns.SingleAsync()).NormalizedName);
        await pricing.AddAddOnAsync(me, theater.Id, new AddOnInput("seniors", null, AddOnKind.Fee, 1m, true)); // old name is free again
        await Assert.ThrowsAsync<AppValidationException>(() =>
            pricing.AddAddOnAsync(me, theater.Id, new AddOnInput("SENIOR citizens", null, AddOnKind.Fee, 1m, true)));
    }

    [Fact]
    public async Task Deleting_a_theater_deletes_its_pricing()
    {
        await using var app = new TestApp();
        var admin = Principals.For(await app.CreateUserAsync("admin@example.com", admin: true), admin: true);
        var theater = await app.Get<TheaterService>().CreateAsync(admin, new Theater { Name = "Doomed", Slug = "doomed" });
        var pricing = app.Get<PricingService>();
        var standard = (await pricing.GetAsync(admin, theater.Id)).Default!;
        await pricing.UpdateScheduleAsync(admin, standard.Id, "Standard", [new(null, "Car load", null, 20m)]);
        await pricing.AddAddOnAsync(admin, theater.Id, new AddOnInput("Outside food", null, AddOnKind.Fee, 5m, true));

        await app.Get<TheaterService>().DeleteAsync(admin, theater.Id);

        await using var db = app.Db();
        Assert.Empty(await db.PriceSchedules.ToListAsync());
        Assert.Empty(await db.PriceOptions.ToListAsync());
        Assert.Empty(await db.AddOns.ToListAsync());
    }
}
