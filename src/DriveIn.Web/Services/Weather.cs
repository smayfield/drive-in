using System.Globalization;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Serialization;
using DriveIn.Web.Data;
using Microsoft.Extensions.Caching.Memory;

namespace DriveIn.Web.Services;

// Hour-by-hour forecast for one place, in UTC and metric units. Hours are the start of each hour.
public sealed record HourlyForecast(IReadOnlyList<ForecastHour> Hours);

public sealed record ForecastHour(DateTimeOffset At, double TemperatureC, int? PrecipitationChance, int WeatherCode, double WindKmh);

// Fetches forecasts. Implementations never throw for provider failures: a forecast that can't be had is null.
public interface IWeatherForecaster
{
    // False when weather is turned off (Weather:Provider = None), so pages don't promise a forecast later.
    bool IsEnabled => true;

    Task<HourlyForecast?> GetHourlyAsync(GeoPoint at, CancellationToken ct = default);
}

public sealed class WeatherOptions
{
    public const string Section = "Weather";

    // "OpenMeteo", or "None" to show no weather.
    public string Provider { get; set; } = "OpenMeteo";
    public string BaseUrl { get; set; } = "https://api.open-meteo.com/";
}

public sealed class NullWeatherForecaster : IWeatherForecaster
{
    public bool IsEnabled => false;

    public Task<HourlyForecast?> GetHourlyAsync(GeoPoint at, CancellationToken ct = default) => Task.FromResult<HourlyForecast?>(null);
}

// Open-Meteo: free, no key, 16 days ahead. A forecast is cached for an hour per place (to about a kilometer), so a
// theater's pages share one request.
public sealed class OpenMeteoForecaster(IHttpClientFactory httpFactory, IMemoryCache cache, ILogger<OpenMeteoForecaster> logger)
    : IWeatherForecaster
{
    public const string HttpClientName = "open-meteo";
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);

    public async Task<HourlyForecast?> GetHourlyAsync(GeoPoint at, CancellationToken ct = default)
    {
        var (lat, lon) = (Math.Round(at.Latitude, 2), Math.Round(at.Longitude, 2));
        var key = $"forecast:{lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)}";
        if (cache.TryGetValue(key, out HourlyForecast? cached))
            return cached;
        try
        {
            var url = string.Create(CultureInfo.InvariantCulture,
                $"v1/forecast?latitude={lat}&longitude={lon}&hourly=temperature_2m,precipitation_probability,weather_code,wind_speed_10m" +
                $"&timezone=UTC&timeformat=unixtime&forecast_days={WeatherService.ForecastDays}");
            var response = await httpFactory.CreateClient(HttpClientName).GetFromJsonAsync<OpenMeteoResponse>(url, ct);
            var forecast = response?.Hourly?.ToForecast();
            if (forecast is not null)
                cache.Set(key, forecast, CacheFor);
            return forecast;
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Fetching the forecast for {Latitude},{Longitude} failed", lat, lon);
            return null;
        }
    }

    private sealed record OpenMeteoResponse([property: JsonPropertyName("hourly")] OpenMeteoHourly? Hourly);

    // What DateTimeOffset can represent; anything else in a response is garbage and skipped rather than thrown on.
    private const long MinUnixSeconds = -62135596800, MaxUnixSeconds = 253402300799;

    private sealed record OpenMeteoHourly(
        [property: JsonPropertyName("time")] long[]? Time,
        [property: JsonPropertyName("temperature_2m")] double?[]? Temperature,
        [property: JsonPropertyName("precipitation_probability")] int?[]? PrecipitationChance,
        [property: JsonPropertyName("weather_code")] int?[]? WeatherCode,
        [property: JsonPropertyName("wind_speed_10m")] double?[]? Wind)
    {
        // Hours missing a temperature or condition (the far end of the range can be patchy) are left out.
        public HourlyForecast? ToForecast()
        {
            if (Time is null || Temperature is null || WeatherCode is null)
                return null;
            var hours = new List<ForecastHour>();
            for (var i = 0; i < Time.Length; i++)
            {
                if (Temperature.ElementAtOrDefault(i) is not double temp || WeatherCode.ElementAtOrDefault(i) is not int code
                    || Time[i] is < MinUnixSeconds or > MaxUnixSeconds)
                    continue;
                hours.Add(new ForecastHour(DateTimeOffset.FromUnixTimeSeconds(Time[i]), temp,
                    PrecipitationChance?.ElementAtOrDefault(i), code, Wind?.ElementAtOrDefault(i) ?? 0));
            }
            return hours.Count == 0 ? null : new HourlyForecast(hours);
        }
    }
}

// WeatherService.ForShowingsAsync's answer: forecasts by showing id, and the showings too far off for one yet.
public sealed record ShowingForecasts(IReadOnlyDictionary<int, ShowingWeather> Weather, IReadOnlySet<int> Later)
{
    public static readonly ShowingForecasts None = new(new Dictionary<int, ShowingWeather>(), new HashSet<int>());

    public ShowingWeather? For(int showingId) => Weather.GetValueOrDefault(showingId);
    public bool IsLater(int showingId) => Later.Contains(showingId);
}

// The weather over one showing, from start to end.
public sealed record ShowingWeather(int WeatherCode, double StartC, double EndC, int? PrecipitationChance, double MaxWindKmh, bool UsUnits)
{
    // Rain likely, a thunderstorm, or strong wind: worth a heads-up before buying.
    public bool IsRough => PrecipitationChance >= 50 || WeatherCode >= 95 || MaxWindKmh >= 40;

    public string Condition => Wmo.Describe(WeatherCode);

    public string Temperatures =>
        Math.Round(Temp(StartC)) == Math.Round(Temp(EndC)) ? Degrees(StartC) : $"{Degrees(StartC)} → {Degrees(EndC)}";

    public string? Precipitation => PrecipitationChance is int p && p > 0 ? $"{p}% chance of rain" : null;

    public string? Wind => MaxWindKmh >= 25 ? $"wind to {(UsUnits ? $"{MaxWindKmh / 1.609:0} mph" : $"{MaxWindKmh:0} km/h")}" : null;

    // e.g. "Clear · 68°F → 61°F · 10% chance of rain"
    public string Summary => string.Join(" · ", new[] { Condition, Temperatures, Precipitation, Wind }.Where(s => s is not null));

    private double Temp(double c) => UsUnits ? c * 9 / 5 + 32 : c;
    private string Degrees(double c) => $"{Temp(c):0}°{(UsUnits ? "F" : "C")}";
}

// WMO weather interpretation codes, as Open-Meteo reports them.
internal static class Wmo
{
    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        >= 51 and <= 57 => "Drizzle",
        66 or 67 => "Freezing rain",
        >= 61 and <= 65 => "Rain",
        >= 71 and <= 77 => "Snow",
        >= 80 and <= 82 => "Rain showers",
        85 or 86 => "Snow showers",
        >= 95 => "Thunderstorms",
        _ => "Mixed",
    };
}

// The forecast for showings, for the public theater, showing and ticket pages. Weather is public, but it's only given
// for theaters the user may browse (TheaterService.CanBrowse).
public sealed class WeatherService(IWeatherForecaster forecaster, TimeProvider time)
{
    public const int ForecastDays = 16;

    // Forecasts for the showings that have one (not over yet, ending within the forecast range), and the showings too far
    // off for one yet. Both are empty when weather is off, the theater has no coordinates, or the user can't browse it
    // (empty rather than access denied, so a page that can show the showing anyway, like a ticket at a theater that has
    // since closed, still works).
    public async Task<ShowingForecasts> ForShowingsAsync(ClaimsPrincipal user, Theater theater,
        IEnumerable<ShowtimeView> showings, CancellationToken ct = default)
    {
        if (!forecaster.IsEnabled || !TheaterService.CanBrowse(user, theater) || Geo.Of(theater) is not GeoPoint at)
            return ShowingForecasts.None;
        var now = time.GetUtcNow();
        // The forecast runs ForecastDays whole days in UTC, starting today, so it ends at midnight UTC after the last one.
        var horizon = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(ForecastDays);
        var upcoming = showings.Where(s => s.EndsAt > now).ToList();
        var later = upcoming.Where(s => s.EndsAt > horizon).Select(s => s.Id).ToHashSet();
        var wanted = upcoming.Where(s => s.EndsAt <= horizon).ToList();
        var weather = new Dictionary<int, ShowingWeather>();
        if (wanted.Count > 0 && await forecaster.GetHourlyAsync(at, ct) is HourlyForecast forecast)
        {
            var us = string.IsNullOrWhiteSpace(theater.Country) || theater.Country.Trim().ToUpperInvariant() is "US" or "USA" or "UNITED STATES";
            foreach (var s in wanted)
            {
                if (Summarize(forecast, s.StartsAt, s.EndsAt, us) is ShowingWeather w)
                    weather[s.Id] = w;
            }
        }
        return new ShowingForecasts(weather, later);
    }

    // The hours the showing overlaps, [starts, ends): an end exactly on the hour doesn't take in the next one. Null unless
    // the forecast covers them all.
    internal static ShowingWeather? Summarize(HourlyForecast forecast, DateTimeOffset starts, DateTimeOffset ends, bool usUnits)
    {
        if (ends <= starts)
            return null;
        var first = Hour(starts);
        var last = Hour(ends.AddTicks(-1));
        var hours = forecast.Hours.Where(h => h.At >= first && h.At <= last).OrderBy(h => h.At).ToList();
        if (hours.Count == 0 || hours[0].At != first || hours[^1].At != last)
            return null;
        return new ShowingWeather(
            hours.Max(h => h.WeatherCode), hours[0].TemperatureC, hours[^1].TemperatureC,
            hours.Max(h => h.PrecipitationChance), hours.Max(h => h.WindKmh), usUnits);
    }

    private static DateTimeOffset Hour(DateTimeOffset t) => new(t.UtcDateTime.Date.AddHours(t.UtcDateTime.Hour), TimeSpan.Zero);
}
