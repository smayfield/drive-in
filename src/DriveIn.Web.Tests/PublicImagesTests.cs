using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriveIn.Web.Tests;

// Public theaters' images are copied to the image CDN and the app's image URLs redirect there (PublicImages.cs).
public class PublicImagesTests
{
    private const string Cdn = "https://img.example.test/";

    private sealed class FakeStore : IPublicImageStore
    {
        public Dictionary<string, (byte[] Data, string ContentType)> Objects { get; } = [];
        public List<string> Deleted { get; } = [];

        public Task PutAsync(string key, byte[] data, string contentType, CancellationToken ct = default)
        {
            Objects[key] = (data, contentType);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList());

        public Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default)
        {
            foreach (var key in keys)
            {
                Objects.Remove(key);
                Deleted.Add(key);
            }
            return Task.CompletedTask;
        }
    }

    private sealed record World(TestApp App, Theater Theater, Film Film, ImageView Image, System.Security.Claims.ClaimsPrincipal Owner,
        FakeStore Store, PublicImagePublisher Publisher, PublicImageLocator Locator) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Publisher.Dispose();
            return App.DisposeAsync();
        }
    }

    private static async Task<World> SetUpAsync(TheaterMode mode = TheaterMode.Live, bool enabled = true)
    {
        var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        await using (var db = app.Db())
        {
            (await db.Theaters.SingleAsync(t => t.Id == theater.Id)).Mode = mode;
            await db.SaveChangesAsync();
        }
        var me = Principals.For(owner);
        await app.Get<TheaterService>().SetLogoAsync(me, theater.Id, TestImages.Png(200, 100));
        var film = await app.Get<ScheduleService>().AddFilmAsync(me, theater.Id, new FilmInput("Jaws", "PG", 124));
        await app.Get<ScheduleService>().SetPosterAsync(me, film.Id, TestImages.Png(300, 450));
        var image = await app.Get<ContentService>().UploadImageAsync(me, theater.Id, "screen.png", TestImages.Png());
        var options = Options.Create(new PublicImagesOptions { Bucket = enabled ? "images" : null, BaseUrl = Cdn });
        var store = new FakeStore();
        var publisher = new PublicImagePublisher(app.Get<IServiceScopeFactory>(), store, app.Time, app.Get<DriveInMetrics>(),
            AlwaysLeader.Instance, NullLogger<PublicImagePublisher>.Instance);
        var locator = new PublicImageLocator(app.Get<IDbContextFactory<ApplicationDbContext>>(), options);
        return new World(app, theater, film, image, me, store, publisher, locator);
    }

    private static async Task<(string? Logo, string? Poster, string? Image)> UrlsAsync(World w) =>
        (await w.Locator.LogoUrlAsync(w.Theater.Slug), await w.Locator.PosterUrlAsync(w.Film.Id),
            await w.Locator.ImageUrlAsync(w.Theater.Slug, w.Image.Id));

    private static async Task SetModeAsync(World w, TheaterMode mode)
    {
        await using var db = w.App.Db();
        (await db.Theaters.SingleAsync(t => t.Id == w.Theater.Id)).Mode = mode;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_public_theaters_images_are_copied_and_their_urls_redirect_to_the_copies()
    {
        await using var w = await SetUpAsync();
        Assert.Equal((null, null, null), await UrlsAsync(w)); // nothing copied yet: the app serves them

        var result = await w.Publisher.SyncAsync();

        Assert.Equal(3, result.Published);
        Assert.Equal(3, w.Store.Objects.Count);
        var (logo, poster, image) = await UrlsAsync(w);
        foreach (var url in new[] { logo, poster, image })
        {
            Assert.NotNull(url);
            Assert.StartsWith(Cdn + $"t/{w.Theater.Id}/", url);
            Assert.EndsWith(".png", url);
            var key = url![Cdn.Length..];
            Assert.Equal("image/png", w.Store.Objects[key].ContentType);
        }
        // Keys end with a hash of the bytes, so they can't be guessed from ids.
        Assert.Matches($@"/image-{w.Image.Id}-[0-9a-f]{{32}}\.png$", image);

        // A second run has nothing to do.
        Assert.Equal(new PublicImagePublisher.Result(0, 0, 0), await w.Publisher.SyncAsync());
    }

    [Fact]
    public async Task Demo_theaters_images_are_never_copied()
    {
        await using var w = await SetUpAsync(TheaterMode.Demo);

        await w.Publisher.SyncAsync();

        Assert.Empty(w.Store.Objects);
        Assert.Equal((null, null, null), await UrlsAsync(w));
    }

    [Fact]
    public async Task When_a_theater_stops_being_public_its_urls_stop_redirecting_at_once_and_the_copies_are_deleted()
    {
        await using var w = await SetUpAsync();
        await w.Publisher.SyncAsync();

        await SetModeAsync(w, TheaterMode.Demo);
        Assert.Equal((null, null, null), await UrlsAsync(w)); // before the publisher runs

        var result = await w.Publisher.SyncAsync();

        Assert.Equal(3, result.Forgotten);
        Assert.Equal(3, result.Deleted);
        Assert.Empty(w.Store.Objects);
        await using var db = w.App.Db();
        Assert.All(await db.TheaterImages.ToListAsync(), i => Assert.Null(i.CdnKey));
    }

    [Fact]
    public async Task A_replaced_logo_is_never_served_from_its_old_copy_and_the_old_copy_is_deleted()
    {
        await using var w = await SetUpAsync();
        await w.Publisher.SyncAsync();
        var oldLogo = await w.Locator.LogoUrlAsync(w.Theater.Slug);

        w.App.Time.Advance(TimeSpan.FromMinutes(1));
        await w.App.Get<TheaterService>().SetLogoAsync(w.Owner, w.Theater.Id, TestImages.Png(201, 100));
        Assert.Null(await w.Locator.LogoUrlAsync(w.Theater.Slug)); // the app serves the new one until it's copied

        await w.Publisher.SyncAsync();

        var newLogo = await w.Locator.LogoUrlAsync(w.Theater.Slug);
        Assert.NotNull(newLogo);
        Assert.NotEqual(oldLogo, newLogo);
        Assert.Equal([oldLogo![Cdn.Length..]], w.Store.Deleted);
    }

    [Fact]
    public async Task A_key_recorded_for_an_older_upload_is_ignored_and_replaced()
    {
        await using var w = await SetUpAsync();
        await w.Publisher.SyncAsync();
        // As if the logo were replaced while its copy was being made: the recorded key names the previous version.
        await using (var db = w.App.Db())
        {
            var theater = await db.Theaters.SingleAsync(t => t.Id == w.Theater.Id);
            theater.LogoUpdatedAt = theater.LogoUpdatedAt!.Value.AddSeconds(1);
            await db.SaveChangesAsync();
        }
        Assert.Null(await w.Locator.LogoUrlAsync(w.Theater.Slug));

        var result = await w.Publisher.SyncAsync();

        Assert.Equal(1, result.Published);
        Assert.Equal(1, result.Deleted);
        Assert.NotNull(await w.Locator.LogoUrlAsync(w.Theater.Slug));
    }

    [Fact]
    public async Task Deleted_images_lose_their_copies()
    {
        await using var w = await SetUpAsync();
        await w.Publisher.SyncAsync();
        var image = await w.Locator.ImageUrlAsync(w.Theater.Slug, w.Image.Id);

        await w.App.Get<ContentService>().DeleteImageAsync(w.Owner, w.Image.Id);
        await w.Publisher.SyncAsync();

        Assert.Equal([image![Cdn.Length..]], w.Store.Deleted);
        Assert.Equal(2, w.Store.Objects.Count);
    }

    [Fact]
    public async Task With_the_cdn_off_every_image_is_served_by_the_app()
    {
        await using var w = await SetUpAsync(enabled: false);
        await w.Publisher.SyncAsync(); // (never registered when off; even so, the locator ignores copies)

        Assert.Equal((null, null, null), await UrlsAsync(w));
    }

    [Fact]
    public void The_csp_allows_images_from_the_cdn_only_when_it_is_on()
    {
        var on = new PublicImagesOptions { Bucket = "images", BaseUrl = "https://img.drive-in.online/" };
        Assert.Equal("https://img.drive-in.online", on.ImageOrigin);
        Assert.Contains("img-src 'self' data: blob: https://img.drive-in.online;",
            SecurityHeaders.ContentSecurityPolicy("n", development: false, on.ImageOrigin));

        Assert.Null(new PublicImagesOptions().ImageOrigin);
        Assert.Contains("img-src 'self' data: blob:;", SecurityHeaders.ContentSecurityPolicy("n", development: false));
    }
}
