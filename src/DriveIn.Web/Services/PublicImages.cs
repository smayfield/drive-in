using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using Amazon.S3;
using Amazon.S3.Model;
using DriveIn.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DriveIn.Web.Services;

// Public theaters' images (logos, film posters, page and post images) are copied to an S3 bucket served by CloudFront
// (img.drive-in.online; infra/app.yml), so browsers fetch them from the CDN rather than from the server. The database
// stays the source of truth: the bytes, the backups, and the app's own image URLs, which redirect to the CDN copy when
// there's a current one (PublicImageLocator) and otherwise serve the bytes themselves, as they always have for demo
// and private theaters. PublicImagePublisher keeps the bucket in step. Off unless PublicImages:Bucket and :BaseUrl are
// set (they are in production; not in development or tests).
public sealed class PublicImagesOptions
{
    public const string Section = "PublicImages";

    public string? Bucket { get; set; }

    // The CDN's address, e.g. https://img.drive-in.online/
    public string? BaseUrl { get; set; }

    // The CloudFront distribution, so deleted copies are also dropped from its caches. Optional.
    public string? DistributionId { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Bucket) && !string.IsNullOrWhiteSpace(BaseUrl);

    public string UrlFor(string key) => BaseUrl!.TrimEnd('/') + "/" + key;

    // The CDN's origin (https://img.drive-in.online), for the Content-Security-Policy; null when off.
    public string? ImageOrigin => Enabled && Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
        ? uri.GetLeftPart(UriPartial.Authority)
        : null;
}

// Where the public copies live.
public interface IPublicImageStore
{
    Task PutAsync(string key, byte[] data, string contentType, CancellationToken ct = default);

    // Every key under prefix.
    Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default);

    // Deletes the copies and drops them from the CDN's caches.
    Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default);
}

public sealed class S3PublicImageStore(IAmazonS3 s3, IAmazonCloudFront cloudFront, IOptions<PublicImagesOptions> options)
    : IPublicImageStore
{
    private string Bucket => options.Value.Bucket!;

    public async Task PutAsync(string key, byte[] data, string contentType, CancellationToken ct = default)
    {
        using var body = new MemoryStream(data, writable: false);
        var request = new PutObjectRequest { BucketName = Bucket, Key = key, InputStream = body, ContentType = contentType };
        // A key names its bytes (CdnKeys), so a copy never changes: browsers and the CDN may keep it for a year.
        request.Headers.CacheControl = "public, max-age=31536000, immutable";
        await s3.PutObjectAsync(request, ct);
    }

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        var keys = new List<string>();
        var request = new ListObjectsV2Request { BucketName = Bucket, Prefix = prefix };
        await foreach (var obj in s3.Paginators.ListObjectsV2(request).S3Objects.WithCancellation(ct))
            keys.Add(obj.Key);
        return keys;
    }

    public async Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default)
    {
        foreach (var chunk in keys.Chunk(1000))
        {
            await s3.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = Bucket,
                Objects = chunk.Select(k => new KeyVersion { Key = k }).ToList(),
            }, ct);
            if (!string.IsNullOrWhiteSpace(options.Value.DistributionId))
            {
                await cloudFront.CreateInvalidationAsync(new CreateInvalidationRequest
                {
                    DistributionId = options.Value.DistributionId,
                    InvalidationBatch = new InvalidationBatch
                    {
                        CallerReference = Guid.NewGuid().ToString("N"),
                        Paths = new Paths { Items = chunk.Select(k => "/" + k).ToList(), Quantity = chunk.Length },
                    },
                }, ct);
            }
        }
    }
}

// The CDN address of an image's current public copy, or null to serve it from the app (no copy yet, a replaced logo or
// poster whose new copy isn't there yet, a theater that isn't public, or the CDN turned off). Anyone may see a public
// theater's images, so this needs no user.
public sealed class PublicImageLocator(IDbContextFactory<ApplicationDbContext> dbFactory, IOptions<PublicImagesOptions> options)
{
    public async Task<string?> LogoUrlAsync(string slug)
    {
        if (!options.Value.Enabled)
            return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var logo = await db.TheaterLogos.AsNoTracking()
            .Where(l => l.Theater!.Slug == slug && l.Theater.IsActive && l.Theater.Mode == TheaterMode.Live)
            .Select(l => new { l.TheaterId, l.Theater!.LogoUpdatedAt, l.CdnKey })
            .FirstOrDefaultAsync();
        return logo?.LogoUpdatedAt is DateTimeOffset version && CdnKeys.IsCurrent(logo.CdnKey, CdnKeys.LogoPrefix(logo.TheaterId, version))
            ? options.Value.UrlFor(logo.CdnKey!)
            : null;
    }

    public async Task<string?> PosterUrlAsync(int filmId)
    {
        if (!options.Value.Enabled)
            return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var poster = await db.FilmPosters.AsNoTracking()
            .Where(p => p.FilmId == filmId && p.Film!.Theater!.IsActive && p.Film.Theater.Mode == TheaterMode.Live)
            .Select(p => new { p.Film!.TheaterId, p.Film.PosterUpdatedAt, p.CdnKey })
            .FirstOrDefaultAsync();
        return poster?.PosterUpdatedAt is DateTimeOffset version
            && CdnKeys.IsCurrent(poster.CdnKey, CdnKeys.PosterPrefix(poster.TheaterId, filmId, version))
            ? options.Value.UrlFor(poster.CdnKey!)
            : null;
    }

    public async Task<string?> ImageUrlAsync(string slug, int imageId)
    {
        if (!options.Value.Enabled)
            return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var image = await db.TheaterImages.AsNoTracking()
            .Where(i => i.Id == imageId && i.Theater!.Slug == slug && i.Theater.IsActive && i.Theater.Mode == TheaterMode.Live)
            .Select(i => new { i.TheaterId, i.CdnKey })
            .FirstOrDefaultAsync();
        return image is not null && CdnKeys.IsCurrent(image.CdnKey, CdnKeys.ImagePrefix(image.TheaterId, imageId))
            ? options.Value.UrlFor(image.CdnKey!)
            : null;
    }
}

// Keeps the CDN bucket matching the database, every couple of minutes, in the copy of the app that runs the background
// jobs: copies public theaters' images that have no current copy (which also fills the bucket the first time), forgets
// the copies of theaters that are no longer public, and deletes copies nothing refers to any more (replaced, removed,
// or no longer public). Until a copy exists the app serves the image itself, so nothing waits on this.
public sealed class PublicImagePublisher(IServiceScopeFactory scopes, IPublicImageStore store, TimeProvider time,
    DriveInMetrics metrics, IJobLeadership leadership, ILogger<PublicImagePublisher> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    public sealed record Result(int Published, int Forgotten, int Deleted);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                if (!await leadership.IsLeaderAsync(stoppingToken))
                    continue;
                var result = await SyncAsync(stoppingToken);
                if (result.Published + result.Forgotten + result.Deleted > 0)
                    logger.LogInformation("Public images: {Published} copied to the CDN, {Forgotten} withdrawn, {Deleted} deleted",
                        result.Published, result.Forgotten, result.Deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Couldn't sync public images with the CDN");
                metrics.JobFailed("public_images");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<Result> SyncAsync(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var forgotten = await ForgetNonPublicAsync(dbFactory, ct);
        var published = await PublishLogosAsync(dbFactory, ct) + await PublishPostersAsync(dbFactory, ct)
            + await PublishImagesAsync(dbFactory, ct);
        var deleted = await DeleteUnusedAsync(dbFactory, ct);
        return new Result(published, forgotten, deleted);
    }

    // A theater that went back to demo, was deactivated or deleted: its images stop redirecting at once (the locator
    // checks), and here their copies are let go, to be deleted below.
    private static async Task<int> ForgetNonPublicAsync(IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var logos = await db.TheaterLogos.Where(l => l.CdnKey != null && !(l.Theater!.IsActive && l.Theater.Mode == TheaterMode.Live))
            .ToListAsync(ct);
        var posters = await db.FilmPosters.Where(p => p.CdnKey != null && !(p.Film!.Theater!.IsActive && p.Film.Theater.Mode == TheaterMode.Live))
            .ToListAsync(ct);
        var images = await db.TheaterImages.Where(i => i.CdnKey != null && !(i.Theater!.IsActive && i.Theater.Mode == TheaterMode.Live))
            .ToListAsync(ct);
        logos.ForEach(l => l.CdnKey = null);
        posters.ForEach(p => p.CdnKey = null);
        images.ForEach(i => i.CdnKey = null);
        await db.SaveChangesAsync(ct);
        return logos.Count + posters.Count + images.Count;
    }

    private async Task<int> PublishLogosAsync(IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken ct)
    {
        List<int> due;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var rows = await db.TheaterLogos.AsNoTracking()
                .Where(l => l.Theater!.IsActive && l.Theater.Mode == TheaterMode.Live && l.Theater.LogoUpdatedAt != null)
                .Select(l => new { l.TheaterId, l.Theater!.LogoUpdatedAt, l.CdnKey })
                .ToListAsync(ct);
            due = rows.Where(r => !CdnKeys.IsCurrent(r.CdnKey, CdnKeys.LogoPrefix(r.TheaterId, r.LogoUpdatedAt!.Value)))
                .Select(r => r.TheaterId).ToList();
        }
        var count = 0;
        foreach (var theaterId in due)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            // The bytes and the version they belong to, read together.
            var logo = await db.TheaterLogos.Include(l => l.Theater).FirstOrDefaultAsync(l => l.TheaterId == theaterId, ct);
            if (logo?.Theater?.LogoUpdatedAt is not DateTimeOffset version)
                continue;
            count += await PublishAsync(db, logo, CdnKeys.Make(CdnKeys.LogoPrefix(theaterId, version), logo.Data, logo.ContentType),
                logo.Data, logo.ContentType, key => logo.CdnKey = key, ct);
        }
        return count;
    }

    private async Task<int> PublishPostersAsync(IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken ct)
    {
        List<int> due;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var rows = await db.FilmPosters.AsNoTracking()
                .Where(p => p.Film!.Theater!.IsActive && p.Film.Theater.Mode == TheaterMode.Live && p.Film.PosterUpdatedAt != null)
                .Select(p => new { p.FilmId, p.Film!.TheaterId, p.Film.PosterUpdatedAt, p.CdnKey })
                .ToListAsync(ct);
            due = rows.Where(r => !CdnKeys.IsCurrent(r.CdnKey, CdnKeys.PosterPrefix(r.TheaterId, r.FilmId, r.PosterUpdatedAt!.Value)))
                .Select(r => r.FilmId).ToList();
        }
        var count = 0;
        foreach (var filmId in due)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var poster = await db.FilmPosters.Include(p => p.Film).FirstOrDefaultAsync(p => p.FilmId == filmId, ct);
            if (poster?.Film?.PosterUpdatedAt is not DateTimeOffset version)
                continue;
            var key = CdnKeys.Make(CdnKeys.PosterPrefix(poster.Film.TheaterId, filmId, version), poster.Data, poster.ContentType);
            count += await PublishAsync(db, poster, key, poster.Data, poster.ContentType, k => poster.CdnKey = k, ct);
        }
        return count;
    }

    private async Task<int> PublishImagesAsync(IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken ct)
    {
        List<int> due;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var rows = await db.TheaterImages.AsNoTracking()
                .Where(i => i.Theater!.IsActive && i.Theater.Mode == TheaterMode.Live)
                .Select(i => new { i.Id, i.TheaterId, i.CdnKey })
                .ToListAsync(ct);
            due = rows.Where(r => !CdnKeys.IsCurrent(r.CdnKey, CdnKeys.ImagePrefix(r.TheaterId, r.Id))).Select(r => r.Id).ToList();
        }
        var count = 0;
        foreach (var imageId in due)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var image = await db.TheaterImages.FirstOrDefaultAsync(i => i.Id == imageId, ct);
            if (image is null)
                continue;
            var key = CdnKeys.Make(CdnKeys.ImagePrefix(image.TheaterId, image.Id), image.Data, image.ContentType);
            count += await PublishAsync(db, image, key, image.Data, image.ContentType, k => image.CdnKey = k, ct);
        }
        return count;
    }

    // Copies the bytes, then records the key. If the row changed meanwhile (removed, or replaced: the key then names the
    // old version and is ignored), the copy is left for DeleteUnusedAsync.
    private async Task<int> PublishAsync(ApplicationDbContext db, object row, string key, byte[] data, string contentType,
        Action<string> setKey, CancellationToken ct)
    {
        await store.PutAsync(key, data, contentType, ct);
        setKey(key);
        try
        {
            await db.SaveChangesAsync(ct);
            return 1;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("A public image changed while it was being copied; it'll be picked up on the next run");
            db.Entry(row).State = EntityState.Detached;
            return 0;
        }
    }

    private async Task<int> DeleteUnusedAsync(IDbContextFactory<ApplicationDbContext> dbFactory, CancellationToken ct)
    {
        HashSet<string> used;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            used = (await db.TheaterLogos.Where(l => l.CdnKey != null).Select(l => l.CdnKey!).ToListAsync(ct))
                .Concat(await db.FilmPosters.Where(p => p.CdnKey != null).Select(p => p.CdnKey!).ToListAsync(ct))
                .Concat(await db.TheaterImages.Where(i => i.CdnKey != null).Select(i => i.CdnKey!).ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
        }
        var unused = (await store.ListAsync("t/", ct)).Where(k => !used.Contains(k)).ToList();
        if (unused.Count > 0)
            await store.DeleteAsync(unused, ct);
        return unused.Count;
    }
}

// When the CDN is off: nothing is copied anywhere.
public sealed class NoPublicImageStore : IPublicImageStore
{
    public Task PutAsync(string key, byte[] data, string contentType, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default) => Task.CompletedTask;
}
