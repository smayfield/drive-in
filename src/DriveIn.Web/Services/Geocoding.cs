using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DriveIn.Web.Data;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DriveIn.Web.Services;

public sealed record GeoPoint(double Latitude, double Longitude)
{
    public static bool IsValid(double latitude, double longitude) =>
        latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;
}

public static class Geo
{
    private const double EarthRadiusMiles = 3958.8;

    // Great-circle (haversine) distance.
    public static double DistanceMiles(GeoPoint a, GeoPoint b)
    {
        static double Rad(double deg) => deg * Math.PI / 180;
        var dLat = Rad(b.Latitude - a.Latitude);
        var dLon = Rad(b.Longitude - a.Longitude);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(Rad(a.Latitude)) * Math.Cos(Rad(b.Latitude)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusMiles * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    public static GeoPoint? Of(Theater theater) =>
        theater is { Latitude: double lat, Longitude: double lon } ? new GeoPoint(lat, lon) : null;

    // The theater's address as one line for a geocoder; null when there's nothing to look up.
    public static string? AddressQuery(Theater theater)
    {
        var parts = new[] { theater.AddressLine1, theater.City, theater.State, theater.PostalCode, theater.Country }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList();
        // A country alone would land in the middle of it.
        return parts.Count == 0 || (parts.Count == 1 && !string.IsNullOrWhiteSpace(theater.Country)) ? null : string.Join(", ", parts);
    }

    public static bool SameAddress(Theater a, Theater b) =>
        Same(a.AddressLine1, b.AddressLine1) && Same(a.City, b.City) && Same(a.State, b.State)
        && Same(a.PostalCode, b.PostalCode) && Same(a.Country, b.Country);

    private static bool Same(string? x, string? y) =>
        string.Equals((x ?? "").Trim(), (y ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}

// Turns an address or a place a customer typed (a ZIP code, "Austin, TX") into coordinates. Implementations never
// throw for lookup failures: a place that can't be found, or a provider that's down, is null.
public interface IGeocoder
{
    Task<GeoPoint?> GeocodeAsync(string query, CancellationToken ct = default);
}

public sealed class GeocodingOptions
{
    public const string Section = "Geocoding";

    // "Nominatim" (OpenStreetMap), or "None" to turn lookups off.
    public string Provider { get; set; } = "Nominatim";
    public string BaseUrl { get; set; } = "https://nominatim.openstreetmap.org/";
    // Nominatim's usage policy asks for a contact address in the User-Agent. Falls back to Company:ContactEmail.
    public string? ContactEmail { get; set; }
}

public sealed class NullGeocoder : IGeocoder
{
    public Task<GeoPoint?> GeocodeAsync(string query, CancellationToken ct = default) => Task.FromResult<GeoPoint?>(null);
}

// OpenStreetMap's Nominatim. The public server allows at most one request a second, so calls are serialized and spaced
// out, and results (misses too) are cached for a day. The cache is its own and capped, since customers choose the
// queries.
public sealed partial class NominatimGeocoder(
    IHttpClientFactory httpFactory, TimeProvider time, ILogger<NominatimGeocoder> logger) : IGeocoder, IDisposable
{
    public const string HttpClientName = "nominatim";
    private const int MaxCachedPlaces = 10_000;
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(24);

    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = MaxCachedPlaces });

    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset lastCall = DateTimeOffset.MinValue;

    public async Task<GeoPoint?> GeocodeAsync(string query, CancellationToken ct = default)
    {
        var normalized = Whitespace().Replace(query.Trim(), " ");
        if (normalized.Length is 0 or > 300)
            return null;
        var key = $"geocode:{normalized.ToUpperInvariant()}";
        if (cache.TryGetValue(key, out GeoPoint? cached))
            return cached;

        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out cached))
                return cached;
            var wait = lastCall + MinInterval - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, time, ct);
            lastCall = time.GetUtcNow();
            var point = await LookUpAsync(normalized, ct);
            cache.Set(key, point, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheFor, Size = 1 });
            return point;
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) && !ct.IsCancellationRequested)
        {
            // Not cached, so the next try asks again.
            logger.LogWarning(ex, "Geocoding \"{Query}\" failed", normalized);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        cache.Dispose();
        gate.Dispose();
    }

    private async Task<GeoPoint?> LookUpAsync(string query, CancellationToken ct)
    {
        // A bare US ZIP code as free text matches postcodes anywhere in the world, so ask for it as a US postcode.
        var zip = UsZip().Match(query);
        var url = zip.Success
            ? $"search?format=jsonv2&limit=1&countrycodes=us&postalcode={Uri.EscapeDataString(zip.Groups[1].Value)}"
            : $"search?format=jsonv2&limit=1&q={Uri.EscapeDataString(query)}";
        var client = httpFactory.CreateClient(HttpClientName);
        var results = await client.GetFromJsonAsync<List<NominatimPlace>>(url, ct);
        var first = results?.FirstOrDefault();
        if (first is null
            || !double.TryParse(first.Lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(first.Lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            || !GeoPoint.IsValid(lat, lon))
            return null;
        return new GeoPoint(lat, lon);
    }

    private sealed record NominatimPlace([property: JsonPropertyName("lat")] string? Lat, [property: JsonPropertyName("lon")] string? Lon);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(\d{5})(-\d{4})?$")]
    private static partial Regex UsZip();
}

// Looks up any active theater that has an address but no coordinates, once at startup (theaters saved before
// geocoding existed, or whose lookup failed while the provider was down).
public sealed class TheaterGeocodingBackfill(IServiceScopeFactory scopes, ILogger<TheaterGeocodingBackfill> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var located = await scope.ServiceProvider.GetRequiredService<TheaterService>().LocateMissingAsync(stoppingToken);
            if (located > 0)
                logger.LogInformation("Found coordinates for {Count} theaters", located);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Geocoding theaters failed");
        }
    }
}
