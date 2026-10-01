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
                if (Temperature.ElementAtOrDefault(i) is not double temp || WeatherCode.ElementAtOrDefault(i) is not int code)
                    continue;
                hours.Add(new ForecastHour(DateTimeOffset.FromUnixTimeSeconds(Time[i]), temp,
                    PrecipitationChance?.ElementAtOrDefault(i), code, Wind?.ElementAtOrDefault(i) ?? 0));
            }
            return hours.Count == 0 ? null : new HourlyForecast(hours);
        }
    }
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

    // Forecasts for the showings that have one: not over yet, within the forecast range, at a theater with coordinates.
    public async Task<Dictionary<int, ShowingWeather>> ForShowingsAsync(ClaimsPrincipal user, Theater theater,
        IEnumerable<ShowtimeView> showings, CancellationToken ct = default)
    {
        var result = new Dictionary<int, ShowingWeather>();
        // Nothing rather than access denied, so a page that can show the showing anyway (a ticket at a theater that has
        // since closed) still works.
        if (!TheaterService.CanBrowse(user, theater))
            return result;
        var now = time.GetUtcNow();
        var wanted = showings.Select(s => (s.Id, Starts: Utc(theater, s.StartsLocal), Ends: Utc(theater, s.EndsLocal)))
            .Where(s => s.Ends > now && s.Starts < now.AddDays(ForecastDays)).ToList();
        if (wanted.Count == 0 || Geo.Of(theater) is not GeoPoint at || await forecaster.GetHourlyAsync(at, ct) is not HourlyForecast forecast)
            return result;
        var us = string.IsNullOrWhiteSpace(theater.Country) || theater.Country.Trim().ToUpperInvariant() is "US" or "USA" or "UNITED STATES";
        foreach (var (id, starts, ends) in wanted)
        {
            if (Summarize(forecast, starts, ends, us) is ShowingWeather weather)
                result[id] = weather;
        }
        return result;
    }

    // Whether a showing is too far off to have a forecast yet.
    public bool IsBeyondForecast(Theater theater, ShowtimeView showing) =>
        Utc(theater, showing.StartsLocal) >= time.GetUtcNow().AddDays(ForecastDays);

    // The hours from the one the showing starts in through the one it ends in; null unless the forecast covers them all.
    internal static ShowingWeather? Summarize(HourlyForecast forecast, DateTimeOffset starts, DateTimeOffset ends, bool usUnits)
    {
        var first = Hour(starts);
        var last = Hour(ends < starts ? starts : ends);
        var hours = forecast.Hours.Where(h => h.At >= first && h.At <= last).OrderBy(h => h.At).ToList();
        if (hours.Count == 0 || hours[0].At != first || hours[^1].At != last)
            return null;
        return new ShowingWeather(
            hours.Max(h => h.WeatherCode), hours[0].TemperatureC, hours[^1].TemperatureC,
            hours.Max(h => h.PrecipitationChance), hours.Max(h => h.WindKmh), usUnits);
    }

    private static DateTimeOffset Hour(DateTimeOffset t) => new(t.UtcDateTime.Date.AddHours(t.UtcDateTime.Hour), TimeSpan.Zero);

    private static DateTimeOffset Utc(Theater theater, DateTime local) =>
        TheaterTime.ToUtc(theater, DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local));
}
