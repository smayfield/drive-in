using System.Security.Claims;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests;

public class OnboardingTests
{
    private static NewTheaterInput Input(string name = "Starlight Drive-In", int screens = 2, bool accept = true, string? slug = null) =>
        new(name, "Austin", "TX", "America/Chicago", screens, accept, slug);

    private static async Task<(TestApp App, ApplicationUser Owner, Theater Theater)> SignUpAsync()
    {
        var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.Get<OnboardingService>().CreateDemoTheaterAsync(Principals.For(owner), Input());
        return (app, owner, theater);
    }

    // --- Sign-up ---

    [Fact]
    public async Task Signing_up_creates_a_private_demo_theater_ready_to_try()
    {
        var (app, owner, theater) = await SignUpAsync();
        await using var _ = app;

        await using var db = app.Db();
        var saved = await db.Theaters.Include(t => t.Screens).SingleAsync(t => t.Id == theater.Id);
        Assert.Equal(("Starlight Drive-In", "starlight-drive-in", TheaterMode.Demo), (saved.Name, saved.Slug, saved.Mode));
        Assert.Equal(owner.Id, saved.OwnerId);
        Assert.Equal("America/Chicago", saved.TimeZone);
        Assert.Equal(app.Time.GetUtcNow(), saved.TermsAcceptedAt);
        Assert.NotNull(saved.TermsVersion);
        Assert.False(saved.IsPublic);
        Assert.Equal(["Screen 1", "Screen 2"], saved.Screens.OrderBy(s => s.SortOrder).Select(s => s.Name));
        Assert.All(saved.Screens, s => Assert.Equal(120, s.SpotCount)); // a starter layout
        var prices = await db.PriceSchedules.Include(p => p.Options).SingleAsync(p => p.TheaterId == theater.Id);
        Assert.True(prices.IsDefault);
        Assert.Equal(3, prices.Options.Count);
        Assert.Equal(4, await db.TheaterRoles.CountAsync(r => r.TheaterId == theater.Id));
    }

    [Fact]
    public async Task Web_addresses_come_from_the_name_and_stay_unique()
    {
        var (app, owner, _) = await SignUpAsync();
        await using var __ = app;
        var other = Principals.For(await app.CreateUserAsync("other@example.com"));
        var onboarding = app.Get<OnboardingService>();

        var second = await onboarding.CreateDemoTheaterAsync(other, Input());
        Assert.Equal("starlight-drive-in-2", second.Slug);

        var chosen = await onboarding.CreateDemoTheaterAsync(other, Input("Moonlight", slug: "moonlight-atx"));
        Assert.Equal("moonlight-atx", chosen.Slug);
        await Assert.ThrowsAsync<AppValidationException>(() => onboarding.CreateDemoTheaterAsync(other, Input("Moon", slug: "moonlight-atx")));
        await Assert.ThrowsAsync<AppValidationException>(() => onboarding.CreateDemoTheaterAsync(Principals.For(owner), Input("X", slug: "Not Valid!")));
    }

    [Theory]
    [InlineData("Starlight Drive-In", "starlight-drive-in")]
    [InlineData("  The Café  Twin #2 ", "the-cafe-twin-2")]
    [InlineData("!!!", "theater")]
    public void Names_become_web_addresses(string name, string slug) => Assert.Equal(slug, OnboardingService.Slugify(name));

    [Fact]
    public async Task Sign_up_checks_its_input()
    {
        await using var app = new TestApp();
        var user = Principals.For(await app.CreateUserAsync("owner@example.com"));
        var onboarding = app.Get<OnboardingService>();

        Assert.Contains("Terms", (await Assert.ThrowsAsync<AppValidationException>(() => onboarding.CreateDemoTheaterAsync(user, Input(accept: false)))).Message);
        await Assert.ThrowsAsync<AppValidationException>(() => onboarding.CreateDemoTheaterAsync(user, Input(name: " ")));
        await Assert.ThrowsAsync<AppValidationException>(() => onboarding.CreateDemoTheaterAsync(user, Input(screens: 0)));
        await Assert.ThrowsAsync<AppValidationException>(() => onboarding.CreateDemoTheaterAsync(user, Input(screens: 5)));
        await Assert.ThrowsAsync<AppValidationException>(() => onboarding.CreateDemoTheaterAsync(user, Input() with { TimeZone = "Mars/Olympus" }));
        await Assert.ThrowsAsync<AppValidationException>(() => onboarding.CreateDemoTheaterAsync(user, Input() with { TimeZone = "" }));
        await Assert.ThrowsAsync<AccessDeniedException>(() => onboarding.CreateDemoTheaterAsync(Principals.Anonymous, Input()));
        await using var db = app.Db();
        Assert.Empty(db.Theaters);
    }

    [Fact]
    public async Task Employee_accounts_cannot_sign_up_theaters()
    {
        var (app, _, theater) = await SignUpAsync();
        await using var __ = app;
        var employee = await app.CreateUserAsync("staff@example.com", employeeTheaterId: theater.Id);

        var ex = await Assert.ThrowsAsync<AppValidationException>(() =>
            app.Get<OnboardingService>().CreateDemoTheaterAsync(Principals.For(employee), Input("Mine")));
        Assert.Contains("Employee accounts", ex.Message);
    }

    [Fact]
    public async Task An_account_can_sign_up_a_limited_number_of_theaters()
    {
        var (app, owner, _) = await SignUpAsync(); // limit is 3
        await using var __ = app;
        var onboarding = app.Get<OnboardingService>();
        await onboarding.CreateDemoTheaterAsync(Principals.For(owner), Input("Two"));
        await onboarding.CreateDemoTheaterAsync(Principals.For(owner), Input("Three"));

        await Assert.ThrowsAsync<AppValidationException>(() => onboarding.CreateDemoTheaterAsync(Principals.For(owner), Input("Four")));
    }

    [Fact]
    public async Task Admin_created_theaters_are_live()
    {
        await using var app = new TestApp();
        var admin = Principals.For(await app.CreateUserAsync("admin@example.com", admin: true), admin: true);

        var theater = await app.Get<TheaterService>().CreateAsync(admin, new Theater { Name = "Starlight", Slug = "starlight" });

        Assert.Equal(TheaterMode.Live, theater.Mode);
        Assert.NotNull(theater.LiveSince);
    }

    // --- Demo privacy and test sales ---

    // A showing tomorrow evening at the theater (8 PM Chicago, Sep 2).
    private static async Task<Showtime> ShowingAsync(TestApp app, ClaimsPrincipal owner, Theater theater)
    {
        var film = await app.Get<ScheduleService>().AddFilmAsync(owner, theater.Id, new FilmInput("Jaws", "PG", 124));
        await using var db = app.Db();
        var screenId = await db.Screens.Where(s => s.TheaterId == theater.Id).OrderBy(s => s.SortOrder).Select(s => s.Id).FirstAsync();
        return await app.Get<ScheduleService>().AddShowtimeAsync(owner, screenId, film.Id, new DateOnly(2026, 9, 2), new TimeOnly(20, 0));
    }

    [Fact]
    public async Task A_demo_theater_is_only_visible_to_its_members()
    {
        var (app, owner, theater) = await SignUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var showing = await ShowingAsync(app, me, theater);
        var stranger = Principals.For(await app.CreateUserAsync("guest@example.com"));
        var staff = await app.CreateUserAsync("staff@example.com", employeeTheaterId: theater.Id);
        var theaters = app.Get<TheaterService>();
        var sales = app.Get<TicketSalesService>();

        Assert.Empty(await theaters.ListActiveAsync(stranger));
        Assert.Null(await theaters.GetBySlugAsync(stranger, theater.Slug));
        await Assert.ThrowsAsync<NotFoundException>(() => sales.ListOnSaleAsync(stranger, theater.Slug));
        await Assert.ThrowsAsync<NotFoundException>(() => sales.GetShowingAsync(stranger, showing.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => sales.HoldAsync(stranger, showing.Id, 1, 1));

        foreach (var member in new[] { me, Principals.For(staff) })
        {
            Assert.Single(await theaters.ListActiveAsync(member));
            Assert.NotNull(await theaters.GetBySlugAsync(member, theater.Slug));
            Assert.Single((await sales.ListOnSaleAsync(member, theater.Slug)).Showings);
        }
    }

    [Fact]
    public async Task Demo_sales_are_test_sales_even_without_a_payment_processor()
    {
        var (app, owner, theater) = await SignUpAsync();
        await using var _ = app;
        app.Payments.IsAvailable = false; // like production today
        var me = Principals.For(owner);
        var showing = await ShowingAsync(app, me, theater);
        var sales = app.Get<TicketSalesService>();
        var option = (await sales.GetShowingAsync(me, showing.Id)).Prices.Options[0];

        Assert.True((await sales.GetShowingAsync(me, showing.Id)).OnSale);
        var hold = await sales.HoldAsync(me, showing.Id, 1, 1);
        await sales.PurchaseAsync(me, hold.TicketId, new PurchaseInput(option.Id, [],
            new CardInput("Owner", "4242 4242 4242 4242", 12, 2030, "123")), TestApp.BaseUri);

        Assert.Empty(app.Payments.Charges); // the real processor was never used
        await using var db = app.Db();
        Assert.True((await db.Tickets.SingleAsync()).IsTest);
        Assert.StartsWith("[TEST]", app.Email.Sent.Single().Subject);
        Assert.Contains("Test ticket", app.Email.Sent.Single().Body);
    }

    // --- Setup checklist ---

    [Fact]
    public async Task The_setup_checklist_tracks_what_is_left()
    {
        var (app, owner, theater) = await SignUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var onboarding = app.Get<OnboardingService>();

        var steps = (await onboarding.GetSetupStepsAsync(me, theater.Id)).ToDictionary(s => s.Title, s => s.Done);
        Assert.False(steps["Profile and time zone"]); // no address yet
        Assert.False(steps["Operating season"]);
        Assert.True(steps["Screens and spots"]);       // starter layout
        Assert.True(steps["Ticket prices"]);           // starter prices
        Assert.False(steps["Films and showings"]);

        await ShowingAsync(app, me, theater);
        Assert.True((await onboarding.GetSetupStepsAsync(me, theater.Id)).Single(s => s.Title == "Films and showings").Done);
        var stranger = Principals.For(await app.CreateUserAsync("x@example.com"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => onboarding.GetSetupStepsAsync(stranger, theater.Id));
    }

    // --- Going live ---

    [Fact]
    public async Task The_owner_asks_to_go_live_and_admins_are_emailed()
    {
        var (app, owner, theater) = await SignUpAsync();
        await using var _ = app;
        var admin = Principals.For(await app.CreateUserAsync("admin@example.com", admin: true), admin: true);
        var manager = await app.CreateUserAsync("manager@example.com", employeeTheaterId: theater.Id);
        await app.GrantAsync(manager, All.Select(p => p.Key).ToArray());
        var onboarding = app.Get<OnboardingService>();

        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            onboarding.RequestGoLiveAsync(Principals.For(manager), theater.Id, acceptBilling: true, TestApp.BaseUri));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            onboarding.RequestGoLiveAsync(Principals.For(owner), theater.Id, acceptBilling: false, TestApp.BaseUri));

        await onboarding.RequestGoLiveAsync(Principals.For(owner), theater.Id, acceptBilling: true, TestApp.BaseUri);

        var mail = Assert.Single(app.Email.Sent);
        Assert.Equal("admin@example.com", mail.To);
        Assert.Contains($"{TestApp.BaseUri}admin/theaters/{theater.Id}", mail.Body);
        Assert.Equal(theater.Id, Assert.Single(await onboarding.ListGoLiveRequestsAsync(admin)).Id);
        await Assert.ThrowsAsync<AppValidationException>(() =>
            onboarding.RequestGoLiveAsync(Principals.For(owner), theater.Id, acceptBilling: true, TestApp.BaseUri));
        await Assert.ThrowsAsync<AccessDeniedException>(() => onboarding.ListGoLiveRequestsAsync(Principals.For(owner)));
    }

    [Fact]
    public async Task Activating_takes_the_theater_live_and_clears_its_test_tickets()
    {
        var (app, owner, theater) = await SignUpAsync();
        await using var _ = app;
        var me = Principals.For(owner);
        var admin = Principals.For(await app.CreateUserAsync("admin@example.com", admin: true), admin: true);
        var showing = await ShowingAsync(app, me, theater);
        var sales = app.Get<TicketSalesService>();
        var option = (await sales.GetShowingAsync(me, showing.Id)).Prices.Options[0];
        var hold = await sales.HoldAsync(me, showing.Id, 1, 1);
        await sales.PurchaseAsync(me, hold.TicketId, new PurchaseInput(option.Id, [], new CardInput("O", "4242424242424242", 12, 2030, "123")), TestApp.BaseUri);
        var onboarding = app.Get<OnboardingService>();
        await onboarding.RequestGoLiveAsync(me, theater.Id, acceptBilling: true, TestApp.BaseUri);

        await Assert.ThrowsAsync<AccessDeniedException>(() => onboarding.ActivateAsync(me, theater.Id, TestApp.BaseUri));
        await onboarding.ActivateAsync(admin, theater.Id, TestApp.BaseUri);

        await using (var db = app.Db())
        {
            var live = await db.Theaters.SingleAsync(t => t.Id == theater.Id);
            Assert.Equal((TheaterMode.Live, (DateTimeOffset?)app.Time.GetUtcNow(), (DateTimeOffset?)null), (live.Mode, live.LiveSince, live.GoLiveRequestedAt));
            Assert.Empty(db.Tickets);
            Assert.Empty(db.TicketAddOns);
        }
        Assert.Contains(app.Email.Sent, m => m.To == "owner@example.com" && m.Subject.Contains("is live"));
        Assert.Empty(await onboarding.ListGoLiveRequestsAsync(admin));

        // Now public, and selling for real.
        var guest = Principals.For(await app.CreateUserAsync("guest@example.com"));
        Assert.Single(await app.Get<TheaterService>().ListActiveAsync(guest));
        var guestHold = await sales.HoldAsync(guest, showing.Id, 1, 1);
        await sales.PurchaseAsync(guest, guestHold.TicketId, new PurchaseInput(option.Id, [], new CardInput("G", "4242424242424242", 12, 2030, "123")), TestApp.BaseUri);
        Assert.Single(app.Payments.Charges);
        await using (var db = app.Db())
            Assert.False((await db.Tickets.SingleAsync()).IsTest);
    }

    [Fact]
    public async Task Declining_clears_the_request_and_tells_the_owner()
    {
        var (app, owner, theater) = await SignUpAsync();
        await using var _ = app;
        var admin = Principals.For(await app.CreateUserAsync("admin@example.com", admin: true), admin: true);
        var onboarding = app.Get<OnboardingService>();
        await onboarding.RequestGoLiveAsync(Principals.For(owner), theater.Id, acceptBilling: true, TestApp.BaseUri);

        await onboarding.DeclineGoLiveAsync(admin, theater.Id, "Please lay out Screen 2 & add showings.");

        var mail = app.Email.Sent.Last();
        Assert.Equal("owner@example.com", mail.To);
        Assert.Contains("Please lay out Screen 2 &amp; add showings.", mail.Body);
        await using var db = app.Db();
        var t = await db.Theaters.SingleAsync(x => x.Id == theater.Id);
        Assert.Equal((TheaterMode.Demo, (DateTimeOffset?)null), (t.Mode, t.GoLiveRequestedAt));
        // They can ask again.
        await onboarding.RequestGoLiveAsync(Principals.For(owner), theater.Id, acceptBilling: true, TestApp.BaseUri);
    }

    // --- Seasonal billing ---

    [Theory]
    [InlineData(2026, 5, 15, 2026, 9, 10, 5)]  // May through September
    [InlineData(2026, 11, 20, 2027, 2, 1, 4)]  // across the new year
    [InlineData(2026, 6, 1, 2026, 6, 30, 1)]
    public void Each_calendar_month_the_season_touches_is_billed(int oy, int om, int od, int cy, int cm, int cd, int months)
    {
        var quote = PlanQuote.For(2, 49m, new DateOnly(oy, om, od), new DateOnly(cy, cm, cd));

        Assert.Equal((98m, (int?)months, (decimal?)(98m * months)), (quote.MonthlyTotal, quote.BilledMonths, quote.SeasonTotal));
    }

    [Fact]
    public void Without_a_season_or_a_price_the_estimate_is_open()
    {
        var noSeason = PlanQuote.For(1, 49m, new DateOnly(2026, 5, 1), null);
        Assert.Equal((49m, (int?)null, (decimal?)null), (noSeason.MonthlyTotal, noSeason.BilledMonths, noSeason.SeasonTotal));
        var noPrice = PlanQuote.For(1, null, new DateOnly(2026, 5, 1), new DateOnly(2026, 9, 30));
        Assert.Equal(((decimal?)null, (int?)5, (decimal?)null), (noPrice.MonthlyTotal, noPrice.BilledMonths, noPrice.SeasonTotal));
    }
}
