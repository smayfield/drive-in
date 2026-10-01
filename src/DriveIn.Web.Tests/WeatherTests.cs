using System.Net;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Tests;

public class WeatherTests
{
    // Midnight UTC on the TestApp clock's day (the clock itself reads 2026-09-01 12:00 UTC).
    private static readonly DateTimeOffset Today = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    // Hourly for 16 days from today; the evening of Sep 2 (UTC) cools from 25°C by a degree an hour, with rain from 22:00.
    private static HourlyForecast Forecast() => new(Enumerable.Range(0, 16 * 24).Select(i =>
    {
        var at = Today.AddHours(i);
        var evening = at >= Today.AddDays(1).AddHours(20) && at <= Today.AddDays(2).AddHours(1);
        var hoursIn = (at - Today.AddDays(1).AddHours(20)).TotalHours;
        return evening
            ? new ForecastHour(at, 25 - hoursIn, hoursIn >= 2 ? 60 : 10, hoursIn >= 2 ? 61 : 0, hoursIn * 5)
            : new ForecastHour(at, 20, 0, 1, 5);
    }).ToList());

    private static ShowtimeView Showing(int id, DateTimeOffset startsUtc, int minutes = 120) =>
        new(id, 1, "Screen 1", [], 0, startsUtc.UtcDateTime, startsUtc.AddMinutes(minutes).UtcDateTime, null, null,
            startsUtc, startsUtc.AddMinutes(minutes));

    private static async Task<Theater> TheaterAsync(TestApp app, GeoPoint? at, TheaterMode mode = TheaterMode.Live, string? country = "US",
        string? ownerId = null)
    {
        var theater = await app.CreateTheaterAsync("Starlight", ownerId);
        await using var db = app.Db();
        var t = await db.Theaters.SingleAsync(x => x.Id == theater.Id);
        (t.Latitude, t.Longitude, t.Mode, t.Country) = (at?.Latitude, at?.Longitude, mode, country);
        await db.SaveChangesAsync();
        return t;
    }

    [Fact]
    public void Summarize_covers_the_hours_from_start_to_end()
    {
        // 20:30 to 01:30 UTC, across midnight: the hours 20:00 through 01:00.
        var starts = Today.AddDays(1).AddHours(20).AddMinutes(30);
        var weather = WeatherService.Summarize(Forecast(), starts, starts.AddHours(5), usUnits: false)!;

        Assert.Equal(25, weather.StartC);
        Assert.Equal(20, weather.EndC);
        Assert.Equal(60, weather.PrecipitationChance);
        Assert.Equal(61, weather.WeatherCode); // the worst hour, not the first
        Assert.Equal(25, weather.MaxWindKmh);
        Assert.True(weather.IsRough);
        Assert.Equal("Rain · 25°C → 20°C · 60% chance of rain · wind to 25 km/h", weather.Summary);
    }

    [Fact]
    public void Summarize_leaves_out_the_hour_a_showing_ends_exactly_on()
    {
        // 20:00 to 22:00: the hours 20:00 and 21:00, not 22:00 (when the rain starts).
        var starts = Today.AddDays(1).AddHours(20);
        var weather = WeatherService.Summarize(Forecast(), starts, starts.AddHours(2), usUnits: false)!;
        Assert.Equal(24, weather.EndC);
        Assert.Equal(10, weather.PrecipitationChance);
        Assert.Null(WeatherService.Summarize(Forecast(), starts, starts, usUnits: false));
    }

    [Fact]
    public async Task The_forecast_range_ends_at_midnight_UTC_after_its_last_day()
    {
        await using var app = new TestApp();
        var user = await app.CreateUserAsync("guest@example.com");
        var theater = await TheaterAsync(app, new GeoPoint(30.27, -97.74));
        app.Weather.Forecast = Forecast(); // hours through 23:00 on Sep 16
        var weather = app.Get<WeatherService>();
        var lastNight = Showing(1, Today.AddDays(15).AddHours(21));  // Sep 16 21:00-23:00: covered
        var acrossTheEnd = Showing(2, Today.AddDays(15).AddHours(23)); // ends Sep 17 01:00: not
        var dayAfter = Showing(3, Today.AddDays(16).AddHours(10));     // before now + 16 days, but past the range

        var result = await weather.ForShowingsAsync(Principals.For(user), theater, [lastNight, acrossTheEnd, dayAfter]);

        Assert.Equal([1], result.Weather.Keys);
        Assert.Equal([2, 3], result.Later.Order());
    }

    [Fact]
    public void Summarize_needs_the_forecast_to_cover_the_whole_showing()
    {
        var lastHour = Forecast().Hours[^1].At;
        Assert.Null(WeatherService.Summarize(Forecast(), lastHour.AddMinutes(-30), lastHour.AddHours(2), usUnits: true));
        Assert.Null(WeatherService.Summarize(Forecast(), Today.AddHours(-3), Today.AddHours(-1), usUnits: true));
    }

    [Fact]
    public void Formats_in_US_units_and_leaves_out_calm_details()
    {
        var weather = new ShowingWeather(0, 20, 20.2, 0, 10, UsUnits: true);
        Assert.Equal("Clear · 68°F", weather.Summary);
        Assert.False(weather.IsRough);
        Assert.Equal("Thunderstorms · 68°F → 61°F · wind to 40 mph", new ShowingWeather(95, 20, 16, null, 64, true).Summary);
        Assert.True(new ShowingWeather(95, 20, 16, null, 0, true).IsRough);
        Assert.True(new ShowingWeather(3, 20, 16, 0, 45, true).IsRough);
    }

    [Theory]
    [InlineData(0, "Clear")]
    [InlineData(2, "Partly cloudy")]
    [InlineData(45, "Fog")]
    [InlineData(53, "Drizzle")]
    [InlineData(63, "Rain")]
    [InlineData(67, "Freezing rain")]
    [InlineData(75, "Snow")]
    [InlineData(81, "Rain showers")]
    [InlineData(96, "Thunderstorms")]
    public void Describes_WMO_codes(int code, string text) => Assert.Equal(text, Wmo.Describe(code));

    [Fact]
    public async Task Forecasts_upcoming_showings_within_range_only()
    {
        await using var app = new TestApp();
        var user = await app.CreateUserAsync("guest@example.com");
        var theater = await TheaterAsync(app, new GeoPoint(30.27, -97.74));
        app.Weather.Forecast = Forecast();
        var weather = app.Get<WeatherService>();
        var tonight = Showing(1, Today.AddHours(20));
        var past = Showing(2, Today.AddHours(-5));
        var farOff = Showing(3, Today.AddDays(20));

        var result = await weather.ForShowingsAsync(Principals.For(user), theater, [tonight, past, farOff]);

        Assert.Equal([1], result.Weather.Keys);
        Assert.Equal("Mostly clear · 68°F", result.For(1)!.Summary);
        Assert.Single(app.Weather.Requests); // one fetch for the theater
        Assert.True(result.IsLater(3));
        Assert.False(result.IsLater(1));
        Assert.False(result.IsLater(2)); // over, not later
    }

    [Fact]
    public async Task Nothing_without_coordinates_for_theaters_the_user_cant_browse_or_with_weather_off()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var guest = await app.CreateUserAsync("guest@example.com");
        app.Weather.Forecast = Forecast();
        var weather = app.Get<WeatherService>();
        ShowtimeView[] showings = [Showing(1, Today.AddHours(20)), Showing(2, Today.AddDays(20))];

        var unplaced = await TheaterAsync(app, at: null);
        Assert.Same(ShowingForecasts.None, await weather.ForShowingsAsync(Principals.For(guest), unplaced, showings));

        var demo = await TheaterAsync(app, new GeoPoint(30.27, -97.74), TheaterMode.Demo, ownerId: owner.Id);
        Assert.Same(ShowingForecasts.None, await weather.ForShowingsAsync(Principals.For(guest), demo, showings));
        var forOwner = await weather.ForShowingsAsync(Principals.For(owner), demo, showings);
        Assert.Single(forOwner.Weather);
        Assert.True(forOwner.IsLater(2));

        // Weather:Provider = None: no "available later" note either.
        var off = new WeatherService(new NullWeatherForecaster(), app.Time);
        Assert.Same(ShowingForecasts.None, await off.ForShowingsAsync(Principals.For(owner), demo, showings));
    }

    [Fact]
    public async Task Uses_metric_outside_the_US()
    {
        await using var app = new TestApp();
        var user = await app.CreateUserAsync("guest@example.com");
        var theater = await TheaterAsync(app, new GeoPoint(43.65, -79.38), country: "CA");
        app.Weather.Forecast = Forecast();

        var result = await app.Get<WeatherService>().ForShowingsAsync(Principals.For(user), theater, [Showing(1, Today.AddHours(20))]);

        Assert.Equal("Mostly clear · 20°C", result.For(1)!.Summary);
    }

    // --- OpenMeteoForecaster ---

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://open-meteo.test/") };
    }

    private static OpenMeteoForecaster OpenMeteo(StubHandler handler) =>
        new(new StubFactory(handler), new MemoryCache(new MemoryCacheOptions()), NullLogger<OpenMeteoForecaster>.Instance);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task OpenMeteo_parses_hourly_data_skips_gaps_and_caches()
    {
        var handler = new StubHandler(_ => Json("""
            {"hourly":{"time":[1788220800,1788224400,1788228000],
              "temperature_2m":[21.5,null,19.0],"precipitation_probability":[10,20,null],
              "weather_code":[0,1,3],"wind_speed_10m":[8.2,9.0,null]}}
            """));
        var forecaster = OpenMeteo(handler);

        var forecast = await forecaster.GetHourlyAsync(new GeoPoint(30.2672, -97.7431));
        await forecaster.GetHourlyAsync(new GeoPoint(30.2711, -97.7437)); // same place to 2 decimals

        Assert.NotNull(forecast);
        Assert.Equal(2, forecast.Hours.Count);
        Assert.Equal(new ForecastHour(DateTimeOffset.FromUnixTimeSeconds(1788220800), 21.5, 10, 0, 8.2), forecast.Hours[0]);
        Assert.Equal(new ForecastHour(DateTimeOffset.FromUnixTimeSeconds(1788228000), 19.0, null, 3, 0), forecast.Hours[1]);
        var request = Assert.Single(handler.Requests);
        Assert.Contains("latitude=30.27&longitude=-97.74", request.Query);
        Assert.Contains("timezone=UTC", request.Query);
    }

    [Fact]
    public async Task OpenMeteo_returns_null_on_failure_and_doesnt_cache_it()
    {
        var calls = 0;
        var handler = new StubHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : Json("""{"hourly":{"time":[1788220800],"temperature_2m":[20],"weather_code":[0]}}"""));
        var forecaster = OpenMeteo(handler);

        Assert.Null(await forecaster.GetHourlyAsync(new GeoPoint(30, -97)));
        Assert.NotNull(await forecaster.GetHourlyAsync(new GeoPoint(30, -97)));
        Assert.Null(await OpenMeteo(new StubHandler(_ => Json("{}"))).GetHourlyAsync(new GeoPoint(30, -97)));
        Assert.Null(await OpenMeteo(new StubHandler(_ => Json("nope"))).GetHourlyAsync(new GeoPoint(30, -97)));
        // A timestamp DateTimeOffset can't hold is skipped, not thrown on.
        var odd = await OpenMeteo(new StubHandler(_ => Json("""{"hourly":{"time":[999999999999999,1788220800],"temperature_2m":[20,21],"weather_code":[0,0]}}""")))
            .GetHourlyAsync(new GeoPoint(30, -97));
        Assert.Equal([21.0], odd!.Hours.Select(h => h.TemperatureC));
    }
}
