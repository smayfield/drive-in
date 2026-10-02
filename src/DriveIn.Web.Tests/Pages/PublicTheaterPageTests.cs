using DriveIn.Web.Components.Pages.Theaters;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using TheaterIndex = DriveIn.Web.Components.Pages.Theaters.Index;

namespace DriveIn.Web.Tests.Pages;

// The public theater list and a theater's page.
public class PublicTheaterPageTests
{
    private static readonly GeoPoint Austin = new(30.27, -97.74);

    // The setup's theater, placed in Austin.
    private static async Task<TicketSalesTests.Setup> PlacedAsync()
    {
        var s = await TicketSalesTests.SetUpAsync();
        await using var db = s.App.Db();
        var t = await db.Theaters.SingleAsync();
        (t.Latitude, t.Longitude, t.City, t.State, t.Description) = (Austin.Latitude, Austin.Longitude, "Austin", "TX", "Under the stars.");
        await db.SaveChangesAsync();
        return s;
    }

    private static async Task<PageHost> BuyerHostAsync(TicketSalesTests.Setup s) =>
        new PageHost(s.App).SignIn(await TicketSalesTests.BuyerAsync(s.App));

    // Answers the page's driveIn.getPosition call (its result type is private to the page).
    private static void SetPosition(PageHost host, double? lat, double? lon, string? error)
    {
        var type = typeof(TheaterIndex).GetNestedType("BrowserPosition", System.Reflection.BindingFlags.NonPublic)!;
        var setup = typeof(BunitJSInteropSetupExtensions).GetMethods()
            .Single(m => m.Name == "Setup" && m.GetParameters() is [_, { ParameterType: var id }, { ParameterType: var args }]
                && id == typeof(string) && args == typeof(object[]))
            .MakeGenericMethod(type)
            .Invoke(null, [host.Context.JSInterop, "driveIn.getPosition", Array.Empty<object>()])!;
        setup.GetType().GetMethod("SetResult")!.Invoke(setup, [Activator.CreateInstance(type, lat, lon, error)]);
    }

    [Fact]
    public async Task Every_open_theater_is_listed()
    {
        var s = await PlacedAsync();
        await using var host = await BuyerHostAsync(s);

        var page = host.Render<TheaterIndex>();

        page.WaitForText("Starlight");
        Assert.Contains("Austin, TX 1 screen · 7 spots", page.Text());
        Assert.Contains("theaters/starlight", page.Markup);
    }

    [Fact]
    public async Task A_search_goes_into_the_link_and_lists_theaters_nearest_first()
    {
        var s = await PlacedAsync();
        s.App.Geocoder.Places["78701"] = new GeoPoint(30.30, -97.70);
        await using var host = await BuyerHostAsync(s);
        var page = host.Render<TheaterIndex>();
        page.WaitForText("Starlight");

        page.SetField("ZIP code or city", "78701");
        page.Find("form").Submit();

        Assert.Contains("theaters?near=78701&radius=100", host.Nav.Uri);
        var results = host.Render<TheaterIndex>();
        results.WaitForText("1 theater within 100 miles of 78701, nearest first.");
        Assert.Contains("3 miles away", results.Text());
    }

    [Fact]
    public async Task An_empty_search_shows_every_theater_again()
    {
        var s = await PlacedAsync();
        await using var host = await BuyerHostAsync(s);
        var page = host.Render<TheaterIndex>();
        page.WaitForText("Starlight");

        page.Find("form").Submit();

        Assert.EndsWith("/theaters", host.Nav.Uri);
    }

    [Fact]
    public async Task A_place_that_cant_be_found_says_so()
    {
        var s = await PlacedAsync();
        await using var host = await BuyerHostAsync(s);
        host.Nav.NavigateTo("theaters?near=Atlantis&radius=25");

        var page = host.Render<TheaterIndex>();

        page.WaitForText("We couldn't find \"Atlantis\". Try a ZIP code, or a city and state.");
        Assert.DoesNotContain("Starlight", page.Text());
    }

    [Fact]
    public async Task A_far_search_finds_nothing_and_suggests_a_larger_distance()
    {
        var s = await PlacedAsync();
        await using var host = await BuyerHostAsync(s);
        host.Nav.NavigateTo("theaters?lat=40.71&lon=-74.01&radius=25");

        var page = host.Render<TheaterIndex>();

        page.WaitForText("No theaters within 25 miles of your location. Try a larger distance.");
    }

    [Fact]
    public async Task Half_a_location_in_the_link_is_invalid()
    {
        var s = await PlacedAsync();
        await using var host = await BuyerHostAsync(s);
        host.Nav.NavigateTo("theaters?lat=30.2");

        var page = host.Render<TheaterIndex>();

        page.WaitForText("That location isn't valid.");
    }

    [Fact]
    public async Task My_location_searches_near_it()
    {
        var s = await PlacedAsync();
        await using var host = await BuyerHostAsync(s);
        SetPosition(host, 30.2712, -97.7431, null);
        var page = host.Render<TheaterIndex>();
        page.WaitForText("Starlight");

        page.ClickButton("Use my location");

        page.WaitForAssertion(() => Assert.Contains("theaters?lat=30.27&lon=-97.74&radius=100", host.Nav.Uri));
    }

    [Fact]
    public async Task A_refused_location_explains_what_to_do()
    {
        var s = await PlacedAsync();
        await using var host = await BuyerHostAsync(s);
        SetPosition(host, null, null, "denied");
        var page = host.Render<TheaterIndex>();
        page.WaitForText("Starlight");

        page.ClickButton("Use my location");

        page.WaitForText("Your browser didn't share your location.");
    }

    [Fact]
    public async Task With_no_theaters_open_the_list_says_so()
    {
        await using var app = new TestApp();
        await using var host = new PageHost(app).SignIn(await TicketSalesTests.BuyerAsync(app));

        var page = host.Render<TheaterIndex>();

        page.WaitForText("No theaters are open yet.");
    }

    // --- A theater's page ---

    [Fact]
    public async Task A_theaters_page_shows_its_showings_weather_and_details()
    {
        var s = await PlacedAsync();
        await s.App.Get<TheaterService>().UpdateGiftCardSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true);
        // Clear and mild all week, every hour.
        var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        s.App.Weather.Forecast = new HourlyForecast(Enumerable.Range(0, 16 * 24)
            .Select(i => new ForecastHour(start.AddHours(i), 22, 0, 0, 5)).ToList());
        await using var host = await BuyerHostAsync(s);

        var page = host.Render<Details>(p => p.Add(x => x.Slug, "starlight"));

        page.WaitForText("Forecast:");
        var text = page.Text();
        Assert.Contains("Under the stars.", text);
        Assert.Contains("Saturday, September 5", text);
        Assert.Contains("Jaws", text);
        Assert.Contains("8:00 PM ends 10:04 PM", text);
        Assert.Contains("Buy a gift card", text);
        Assert.Contains("North 7", text);
        Assert.Contains($"theaters/starlight/showings/{s.Showing.Id}", page.Markup);
    }

    [Fact]
    public async Task A_theater_with_nothing_scheduled_says_to_check_back()
    {
        await using var app = new TestApp();
        await app.CreateTheaterAsync("Moonlight");
        await using var host = new PageHost(app).SignIn(await TicketSalesTests.BuyerAsync(app));

        var page = host.Render<Details>(p => p.Add(x => x.Slug, "moonlight"));

        page.WaitForText("No upcoming showings are scheduled. Check back soon.");
        Assert.Contains("No screens listed.", page.Text());
    }

    [Fact]
    public async Task An_unknown_theater_is_not_found()
    {
        await using var app = new TestApp();
        await using var host = new PageHost(app).SignIn(await TicketSalesTests.BuyerAsync(app)).UseRequest();

        var page = host.Render<Details>(p => p.Add(x => x.Slug, "nowhere"));

        page.WaitForText("Theater not found");
        Assert.Equal(404, host.Request.Response.StatusCode);
    }

    // Visitors and crawlers read these without a live connection to the server (no Blazor circuit).
    [Theory]
    [InlineData(typeof(Details))]
    [InlineData(typeof(TheaterContentPage))]
    [InlineData(typeof(TheaterNews))]
    public void The_public_theater_pages_are_statically_rendered(Type page)
    {
        Assert.NotNull(page.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.ExcludeFromInteractiveRoutingAttribute), false).SingleOrDefault());
        var layout = (Microsoft.AspNetCore.Components.LayoutAttribute)page.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.LayoutAttribute), false).Single();
        Assert.Equal(typeof(DriveIn.Web.Components.Layout.PublicLayout), layout.LayoutType);
    }

    [Fact]
    public async Task A_visitor_who_isnt_signed_in_sees_the_page_and_is_asked_to_sign_in_to_buy()
    {
        var s = await PlacedAsync();
        await using var host = new PageHost(s.App).UseRequest();

        var page = host.Render<Details>(p => p.Add(x => x.Slug, "starlight"));

        page.WaitForText("Jaws");
        Assert.Contains("Sign in to buy", page.Text());
        Assert.Contains($"theaters/starlight/showings/{s.Showing.Id}", page.Markup);
        Assert.Contains("Message the theater", page.Text());
    }

    [Fact]
    public async Task The_showings_dont_wait_for_a_slow_forecast()
    {
        var s = await PlacedAsync();
        var pending = new TaskCompletionSource<HourlyForecast?>();
        s.App.Weather.Pending = pending;
        await using var host = await BuyerHostAsync(s);

        var page = host.Render<Details>(p => p.Add(x => x.Slug, "starlight"));

        page.WaitForText("Jaws");
        Assert.DoesNotContain("Forecast:", page.Text());

        var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        pending.SetResult(new HourlyForecast(Enumerable.Range(0, 16 * 24)
            .Select(i => new ForecastHour(start.AddHours(i), 22, 0, 0, 5)).ToList()));
        page.WaitForText("Forecast:");
    }

    [Fact]
    public async Task A_visitor_who_leaves_stops_the_forecast_request()
    {
        var s = await PlacedAsync();
        s.App.Weather.Pending = new TaskCompletionSource<HourlyForecast?>();
        await using var host = await BuyerHostAsync(s);
        using var leaving = new CancellationTokenSource();
        host.Request.RequestAborted = leaving.Token;

        var page = host.Render<Details>(p => p.Add(x => x.Slug, "starlight"));
        page.WaitForText("Jaws");
        Assert.Empty(page.FindComponents<DriveIn.Web.Components.Shared.WeatherLine>());

        leaving.Cancel();
        // The streamed line settles (its WeatherLine renders, empty) instead of waiting forever or failing the render.
        page.WaitForAssertion(() => Assert.Single(page.FindComponents<DriveIn.Web.Components.Shared.WeatherLine>()));
        Assert.DoesNotContain("Forecast:", page.Text());
    }

    [Fact]
    public async Task A_failing_forecast_leaves_the_showings_without_one()
    {
        var s = await PlacedAsync();
        s.App.Weather.Failure = new InvalidOperationException("weather is down");
        await using var host = await BuyerHostAsync(s);

        var page = host.Render<Details>(p => p.Add(x => x.Slug, "starlight"));

        page.WaitForText("Jaws");
        Assert.Contains("Choose a spot", page.Text());
        Assert.DoesNotContain("Forecast:", page.Text());
    }
}
