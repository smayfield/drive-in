using System.Net;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Tests;

public class NearMeTests
{
    private static readonly GeoPoint Austin = new(30.27, -97.74);
    private static readonly GeoPoint RoundRock = new(30.51, -97.68); // ~17 miles north of Austin
    private static readonly GeoPoint SanAntonio = new(29.42, -98.49); // ~75 miles
    private static readonly GeoPoint Dallas = new(32.78, -96.80); // ~180 miles

    private static async Task<Theater> PlaceAsync(TestApp app, string name, GeoPoint? at, TheaterMode mode = TheaterMode.Live,
        bool active = true, string? ownerId = null)
    {
        var theater = await app.CreateTheaterAsync(name, ownerId);
        await using var db = app.Db();
        var t = await db.Theaters.SingleAsync(x => x.Id == theater.Id);
        (t.Latitude, t.Longitude, t.Mode, t.IsActive) = (at?.Latitude, at?.Longitude, mode, active);
        await db.SaveChangesAsync();
        return t;
    }

    [Fact]
    public void Distance_between_known_cities_is_about_right()
    {
        // New York to Los Angeles is about 2,450 miles as the crow flies.
        var miles = Geo.DistanceMiles(new GeoPoint(40.7128, -74.0060), new GeoPoint(34.0522, -118.2437));
        Assert.InRange(miles, 2420, 2480);
        Assert.Equal(0, Geo.DistanceMiles(Austin, Austin), 6);
    }

    [Fact]
    public async Task Near_lists_theaters_within_the_radius_nearest_first()
    {
        await using var app = new TestApp();
        var user = await app.CreateUserAsync("guest@example.com");
        await PlaceAsync(app, "Dallas", Dallas);
        await PlaceAsync(app, "San Antonio", SanAntonio);
        await PlaceAsync(app, "Round Rock", RoundRock);
        await PlaceAsync(app, "Nowhere", at: null);
        var theaters = app.Get<TheaterService>();

        var within100 = await theaters.ListNearAsync(Principals.For(user), Austin, 100);
        Assert.Equal(["Round Rock", "San Antonio"], within100.Select(x => x.Theater.Name));
        Assert.InRange(within100[0].Miles, 14, 20);

        var anyDistance = await theaters.ListNearAsync(Principals.For(user), Austin, null);
        Assert.Equal(["Round Rock", "San Antonio", "Dallas"], anyDistance.Select(x => x.Theater.Name));
    }

    [Fact]
    public async Task Near_hides_demo_theaters_from_outsiders_and_inactive_ones_from_everyone()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var guest = await app.CreateUserAsync("guest@example.com");
        await PlaceAsync(app, "Demo", RoundRock, TheaterMode.Demo, ownerId: owner.Id);
        await PlaceAsync(app, "Closed", RoundRock, active: false, ownerId: owner.Id);
        await PlaceAsync(app, "Open", SanAntonio);
        var theaters = app.Get<TheaterService>();

        Assert.Equal(["Open"], (await theaters.ListNearAsync(Principals.For(guest), Austin, null)).Select(x => x.Theater.Name));
        Assert.Equal(["Demo", "Open"], (await theaters.ListNearAsync(Principals.For(owner), Austin, null)).Select(x => x.Theater.Name));
    }

    [Fact]
    public async Task FindPlace_uses_the_geocoder_and_requires_sign_in()
    {
        await using var app = new TestApp();
        var user = await app.CreateUserAsync("guest@example.com");
        app.Geocoder.Places["78701"] = Austin;
        var theaters = app.Get<TheaterService>();

        Assert.Equal(Austin, await theaters.FindPlaceAsync(Principals.For(user), "78701"));
        Assert.Null(await theaters.FindPlaceAsync(Principals.For(user), "Atlantis"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => theaters.FindPlaceAsync(Principals.Anonymous, "78701"));
    }

    [Fact]
    public async Task Saving_a_new_address_looks_up_its_coordinates()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        app.Geocoder.Places["1 Main St, Austin, TX, 78701, US"] = Austin;
        var theaters = app.Get<TheaterService>();

        var input = theater.CopyForEdit();
        (input.AddressLine1, input.City, input.State, input.PostalCode) = ("1 Main St", "Austin", "TX", "78701");
        await theaters.UpdateProfileAsync(Principals.For(owner), input);

        var saved = await theaters.GetForManageAsync(Principals.For(owner), theater.Id);
        Assert.Equal(Austin, Geo.Of(saved));

        // Changing only the name doesn't look it up again.
        var lookups = app.Geocoder.Queries.Count;
        var rename = saved.CopyForEdit();
        rename.Name = "Starlight Drive-In";
        await theaters.UpdateProfileAsync(Principals.For(owner), rename);
        Assert.Equal(lookups, app.Geocoder.Queries.Count);
        Assert.Equal(Austin, Geo.Of(await theaters.GetForManageAsync(Principals.For(owner), theater.Id)));
    }

    [Fact]
    public async Task An_address_that_cant_be_found_still_saves_and_clears_the_old_coordinates()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await PlaceAsync(app, "Starlight", Austin, ownerId: owner.Id);
        var theaters = app.Get<TheaterService>();

        var input = theater.CopyForEdit();
        (input.AddressLine1, input.City) = ("Nowhere Rd", "Atlantis");
        await theaters.UpdateProfileAsync(Principals.For(owner), input);

        var saved = await theaters.GetForManageAsync(Principals.For(owner), theater.Id);
        Assert.Equal("Atlantis", saved.City);
        Assert.Null(Geo.Of(saved));
    }

    [Fact]
    public async Task Coordinates_the_editor_enters_are_kept_and_checked()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        app.Geocoder.Places["1 Main St, Austin, US"] = Austin;
        var theaters = app.Get<TheaterService>();

        var input = theater.CopyForEdit();
        (input.AddressLine1, input.City, input.Latitude, input.Longitude) = ("1 Main St", "Austin", RoundRock.Latitude, RoundRock.Longitude);
        await theaters.UpdateProfileAsync(Principals.For(owner), input);
        Assert.Equal(RoundRock, Geo.Of(await theaters.GetForManageAsync(Principals.For(owner), theater.Id)));
        Assert.Empty(app.Geocoder.Queries);

        input.Latitude = 91;
        await Assert.ThrowsAsync<AppValidationException>(() => theaters.UpdateProfileAsync(Principals.For(owner), input));
        (input.Latitude, input.Longitude) = (30, null);
        await Assert.ThrowsAsync<AppValidationException>(() => theaters.UpdateProfileAsync(Principals.For(owner), input));
    }

    [Fact]
    public async Task Signing_up_looks_up_the_city()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        app.Geocoder.Places["Austin, TX, US"] = Austin;

        var theater = await app.Get<OnboardingService>().CreateDemoTheaterAsync(Principals.For(owner),
            new NewTheaterInput("Starlight", "Austin", "TX", "America/Chicago", 1, AcceptTerms: true));

        Assert.Equal(Austin, Geo.Of(theater));
    }

    [Fact]
    public async Task Backfill_locates_theaters_that_have_an_address_but_no_coordinates()
    {
        await using var app = new TestApp();
        var theater = await app.CreateTheaterAsync("Starlight");
        var bare = await app.CreateTheaterAsync("Bare");
        await using (var db = app.Db())
        {
            var t = await db.Theaters.SingleAsync(x => x.Id == theater.Id);
            t.City = "Austin";
            await db.SaveChangesAsync();
        }
        app.Geocoder.Places["Austin, US"] = Austin;

        Assert.Equal(1, await app.Get<TheaterService>().LocateMissingAsync(CancellationToken.None));

        await using var check = app.Db();
        Assert.Equal(Austin, Geo.Of(await check.Theaters.SingleAsync(x => x.Id == theater.Id)));
        Assert.Null(Geo.Of(await check.Theaters.SingleAsync(x => x.Id == bare.Id)));
    }

    // --- NominatimGeocoder ---

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
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://nominatim.test/") };
    }

    private static NominatimGeocoder Nominatim(StubHandler handler) =>
        new(new StubFactory(handler), new MemoryCache(new MemoryCacheOptions()), TimeProvider.System, NullLogger<NominatimGeocoder>.Instance);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Nominatim_parses_the_first_result_and_caches_it()
    {
        var handler = new StubHandler(_ => Json("""[{"lat":"30.2672","lon":"-97.7431","display_name":"Austin"}]"""));
        var geocoder = Nominatim(handler);

        Assert.Equal(new GeoPoint(30.2672, -97.7431), await geocoder.GeocodeAsync("Austin,  TX"));
        Assert.Equal(new GeoPoint(30.2672, -97.7431), await geocoder.GeocodeAsync("austin, tx"));
        var request = Assert.Single(handler.Requests);
        Assert.Contains("q=Austin%2C%20TX", request.Query);
    }

    [Fact]
    public async Task Nominatim_asks_for_a_zip_code_as_a_US_postcode()
    {
        var handler = new StubHandler(_ => Json("""[{"lat":"30.27","lon":"-97.74"}]"""));
        await Nominatim(handler).GeocodeAsync("78701");
        var query = Assert.Single(handler.Requests).Query;
        Assert.Contains("postalcode=78701", query);
        Assert.Contains("countrycodes=us", query);
    }

    [Fact]
    public async Task Nominatim_returns_null_for_no_match_or_a_failure()
    {
        Assert.Null(await Nominatim(new StubHandler(_ => Json("[]"))).GeocodeAsync("Atlantis"));
        Assert.Null(await Nominatim(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))).GeocodeAsync("Austin"));
        Assert.Null(await Nominatim(new StubHandler(_ => Json("not json"))).GeocodeAsync("Austin"));
        Assert.Null(await Nominatim(new StubHandler(_ => Json("""[{"lat":"999","lon":"0"}]"""))).GeocodeAsync("Austin"));
    }
}
