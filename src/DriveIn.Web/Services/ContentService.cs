using System.Security.Claims;
using System.Text.RegularExpressions;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

public sealed record PageInput(
    string Title, string? Slug, string? Summary, string? BodyHtml, int? CoverImageId = null, string? CoverAlt = null,
    bool ShowInMenu = true, bool IsPinned = false, DateTimeOffset? EventStartsAt = null);

public sealed record PageListItem(
    int Id, PageKind Kind, string Title, string Slug, PageStatus Status, DateTimeOffset? PublishAt,
    DateTimeOffset? UnpublishAt, bool ShowInMenu, int SortOrder, bool IsPinned, DateTimeOffset? EventStartsAt,
    DateTimeOffset UpdatedAt, string? UpdatedBy, string PublicHref);

public sealed record ImageView(
    int Id, string Url, string FileName, string? AltText, int Width, int Height, int ByteSize, DateTimeOffset UploadedAt,
    List<string> UsedIn);

public sealed record MenuLink(string Title, string Href);

// What a theater's menu bar lists: its live menu pages in order, and whether it has news to link to.
public sealed record TheaterMenu(List<MenuLink> Pages, bool HasPosts)
{
    public static readonly TheaterMenu Empty = new([], false);
}

public sealed record PostCard(
    int Id, string Title, string Href, string Summary, DateTimeOffset PublishedAt, DateTimeOffset? EventStartsAt,
    bool IsPinned, string? CoverUrl, string? CoverAlt);

public sealed record PublicPage(
    Theater Theater, int Id, PageKind Kind, string Title, string Description, string BodyHtml, string? CoverUrl,
    string? CoverAlt, int CoverWidth, int CoverHeight, DateTimeOffset? PublishAt, DateTimeOffset? EventStartsAt,
    PageStatus Status, bool IsPreview, string? ShareImageUrl);

// A theater's own pages and posts, and the images in them. Writing needs content.manage; reading follows the theater's
// own visibility (TheaterService.CanBrowse), signed in or not, and only shows what's live. Staff who can manage content
// also see drafts and scheduled items, as previews.
public sealed partial class ContentService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IAuthorizationService auth,
    TheaterAccess access,
    TimeProvider time,
    HtmlContent html,
    DriveInMetrics metrics)
{
    public const int FeedSize = 3;
    public const int NewsPageSize = 20;

    // Addresses a page can't take: they'd read like the theater's other pages.
    public static readonly IReadOnlySet<string> ReservedSlugs =
        new HashSet<string> { "news", "pages", "showings", "giftcards", "logo", "images", "new", "edit" };

    // --- Managing ---

    public async Task<List<PageListItem>> ListForManageAsync(ClaimsPrincipal user, int theaterId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await RequireManageAsync(db, user, theaterId);
        var now = time.GetUtcNow();
        // Only what the list shows: never the bodies.
        var pages = await db.TheaterPages.AsNoTracking()
            .Where(p => p.TheaterId == theaterId)
            .Select(p => new
            {
                p.Id, p.Kind, p.Title, p.Slug, p.PublishAt, p.UnpublishAt, p.ShowInMenu, p.SortOrder, p.IsPinned,
                p.EventStartsAt, p.UpdatedAt,
                UpdatedBy = p.UpdatedBy != null ? p.UpdatedBy.DisplayName ?? p.UpdatedBy.Email : null,
            })
            .ToListAsync();
        return pages
            .OrderBy(p => p.Kind)
            .ThenBy(p => p.Kind == PageKind.Page ? p.SortOrder : 0)
            .ThenByDescending(p => p.Kind == PageKind.Post ? p.PublishAt ?? p.UpdatedAt : default)
            .ThenBy(p => p.Title)
            .Select(p => new PageListItem(p.Id, p.Kind, p.Title, p.Slug, TheaterPage.Status(p.PublishAt, p.UnpublishAt, now),
                p.PublishAt, p.UnpublishAt, p.ShowInMenu, p.SortOrder, p.IsPinned, p.EventStartsAt, p.UpdatedAt,
                p.UpdatedBy, PublicHref(theater.Slug, p.Kind, p.Slug)))
            .ToList();
    }

    public async Task<(Theater Theater, TheaterPage Page)> GetForEditAsync(ClaimsPrincipal user, int pageId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var page = await db.TheaterPages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pageId)
            ?? throw new NotFoundException("Page not found.");
        var theater = await RequireManageAsync(db, user, page.TheaterId);
        return (theater, page);
    }

    // Saved as a draft. Publish separately.
    public async Task<TheaterPage> CreateAsync(ClaimsPrincipal user, int theaterId, PageKind kind, PageInput input)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await RequireManageAsync(db, user, theaterId);
        var now = time.GetUtcNow();
        var page = new TheaterPage
        {
            TheaterId = theaterId,
            Kind = kind,
            CreatedAt = now,
            SortOrder = kind == PageKind.Page
                ? (await db.TheaterPages.Where(p => p.TheaterId == theaterId && p.Kind == PageKind.Page)
                    .MaxAsync(p => (int?)p.SortOrder) ?? -1) + 1
                : 0,
        };
        await ApplyAsync(db, theater, page, input, user, now);
        db.TheaterPages.Add(page);
        await SaveAsync(db);
        return page;
    }

    public async Task<TheaterPage> UpdateAsync(ClaimsPrincipal user, int pageId, PageInput input)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var page = await db.TheaterPages.FirstOrDefaultAsync(p => p.Id == pageId)
            ?? throw new NotFoundException("Page not found.");
        var theater = await RequireManageAsync(db, user, page.TheaterId);
        await ApplyAsync(db, theater, page, input, user, time.GetUtcNow());
        await SaveAsync(db);
        return page;
    }

    private async Task ApplyAsync(ApplicationDbContext db, Theater theater, TheaterPage page, PageInput input,
        ClaimsPrincipal user, DateTimeOffset now)
    {
        var title = input.Title?.Trim() ?? "";
        if (title.Length == 0)
            throw new AppValidationException("Give it a title.");
        if (title.Length > TheaterPage.MaxTitleLength)
            throw new AppValidationException($"Keep the title to {TheaterPage.MaxTitleLength} characters.");

        var slug = string.IsNullOrWhiteSpace(input.Slug)
            ? await FreeSlugAsync(db, theater.Id, page, OnboardingService.Slugify(title))
            : input.Slug.Trim().ToLowerInvariant();
        if (slug.Length > TheaterPage.MaxSlugLength || !SlugPattern().IsMatch(slug))
            throw new AppValidationException("Use lowercase letters, digits and single hyphens in the web address.");
        if (page.Kind == PageKind.Page && ReservedSlugs.Contains(slug))
            throw new AppValidationException($"\"{slug}\" is reserved. Choose another web address.");
        if (await db.TheaterPages.AnyAsync(p => p.TheaterId == theater.Id && p.Kind == page.Kind && p.Slug == slug && p.Id != page.Id))
            throw new AppValidationException($"Another {(page.Kind == PageKind.Page ? "page" : "post")} already uses that web address.");

        var summary = string.IsNullOrWhiteSpace(input.Summary) ? null : input.Summary.Trim();
        if (summary?.Length > TheaterPage.MaxSummaryLength)
            throw new AppValidationException($"Keep the summary to {TheaterPage.MaxSummaryLength} characters.");

        if ((input.BodyHtml?.Length ?? 0) > TheaterPage.MaxBodyLength)
            throw new AppValidationException("That's too long for one page. Split it into several.");
        var body = html.Sanitize(input.BodyHtml, theater.Slug, await LibraryAsync(db, theater.Id));

        if (input.CoverImageId is int coverId && !await db.TheaterImages.AnyAsync(i => i.Id == coverId && i.TheaterId == theater.Id))
            throw new AppValidationException("Choose a cover image from this theater's images.");
        // Null: use the image's own description. Empty: decorative (the picker's "Decorative only").
        var coverAlt = input.CoverAlt?.Trim();
        if (coverAlt?.Length > TheaterImage.MaxAltLength)
            throw new AppValidationException($"Keep the image description to {TheaterImage.MaxAltLength} characters.");

        page.Title = title;
        page.Slug = slug;
        page.Summary = summary;
        page.BodyHtml = body;
        page.CoverImageId = input.CoverImageId;
        page.CoverAlt = input.CoverImageId is null ? null : coverAlt;
        page.ShowInMenu = page.Kind == PageKind.Page && input.ShowInMenu;
        page.IsPinned = page.Kind == PageKind.Post && input.IsPinned;
        page.EventStartsAt = page.Kind == PageKind.Post ? input.EventStartsAt : null;
        page.UpdatedAt = now;
        page.UpdatedById = user.GetUserId();
    }

    private static async Task<string> FreeSlugAsync(ApplicationDbContext db, int theaterId, TheaterPage page, string slug)
    {
        if (page.Kind == PageKind.Page && ReservedSlugs.Contains(slug))
            slug += "-page";
        var candidate = slug;
        for (var n = 2; await db.TheaterPages.AnyAsync(p => p.TheaterId == theaterId && p.Kind == page.Kind && p.Slug == candidate && p.Id != page.Id); n++)
            candidate = $"{slug}-{n}";
        return candidate;
    }

    // Shows it from publishAt (now if null) until unpublishAt (for good if null).
    public async Task PublishAsync(ClaimsPrincipal user, int pageId, DateTimeOffset? publishAt = null, DateTimeOffset? unpublishAt = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var page = await db.TheaterPages.FirstOrDefaultAsync(p => p.Id == pageId)
            ?? throw new NotFoundException("Page not found.");
        await RequireManageAsync(db, user, page.TheaterId);
        var now = time.GetUtcNow();
        var from = publishAt ?? now;
        if (unpublishAt is DateTimeOffset until && until <= from)
            throw new AppValidationException("The end must be after the start.");
        if (unpublishAt is DateTimeOffset end && end <= now)
            throw new AppValidationException("The end is already past. Choose a later end, or none.");
        if (HtmlContent.IsEffectivelyEmpty(page.BodyHtml) && page.CoverImageId is null)
            throw new AppValidationException("Write something before publishing.");
        var first = page.PublishAt is null;
        page.PublishAt = from;
        page.UnpublishAt = unpublishAt;
        page.UpdatedAt = now;
        page.UpdatedById = user.GetUserId();
        await db.SaveChangesAsync();
        if (first)
            metrics.ContentPublished(page.Kind);
    }

    // Back to a draft: hidden until published again.
    public async Task UnpublishAsync(ClaimsPrincipal user, int pageId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var page = await db.TheaterPages.FirstOrDefaultAsync(p => p.Id == pageId)
            ?? throw new NotFoundException("Page not found.");
        await RequireManageAsync(db, user, page.TheaterId);
        page.PublishAt = null;
        page.UnpublishAt = null;
        page.UpdatedAt = time.GetUtcNow();
        page.UpdatedById = user.GetUserId();
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(ClaimsPrincipal user, int pageId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var page = await db.TheaterPages.FirstOrDefaultAsync(p => p.Id == pageId)
            ?? throw new NotFoundException("Page not found.");
        await RequireManageAsync(db, user, page.TheaterId);
        db.TheaterPages.Remove(page);
        await db.SaveChangesAsync();
    }

    // The menu order: pageIds lists the theater's pages first to last (any it leaves out keep their place after them).
    public async Task ReorderPagesAsync(ClaimsPrincipal user, int theaterId, IReadOnlyList<int> pageIds)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await RequireManageAsync(db, user, theaterId);
        var pages = await db.TheaterPages.Where(p => p.TheaterId == theaterId && p.Kind == PageKind.Page)
            .OrderBy(p => p.SortOrder).ToListAsync();
        var ordered = pageIds.Select(id => pages.FirstOrDefault(p => p.Id == id)).OfType<TheaterPage>()
            .Concat(pages.Where(p => !pageIds.Contains(p.Id))).Distinct().ToList();
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].SortOrder = i;
        await db.SaveChangesAsync();
    }

    // --- Images ---

    public async Task<ImageView> UploadImageAsync(ClaimsPrincipal user, int theaterId, string fileName, byte[] data, string? altText = null)
    {
        if (data.Length == 0)
            throw new AppValidationException("Choose an image file.");
        if (data.Length > TheaterImage.MaxBytes)
            throw new AppValidationException($"Images can be at most {TheaterImage.MaxBytes / (1024 * 1024)} MB.");
        var contentType = UploadedImages.SniffContent(data)
            ?? throw new AppValidationException("Images must be JPG, PNG, GIF or WebP files.");
        var size = UploadedImages.Dimensions(data, contentType)
            ?? throw new AppValidationException("That image file looks damaged. Try saving it again.");
        if (size.Width <= 0 || size.Height <= 0)
            throw new AppValidationException("That image file looks damaged. Try saving it again.");
        var alt = string.IsNullOrWhiteSpace(altText) ? null : altText.Trim();
        if (alt?.Length > TheaterImage.MaxAltLength)
            throw new AppValidationException($"Keep the image description to {TheaterImage.MaxAltLength} characters.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await RequireManageAsync(db, user, theaterId);
        if (await db.TheaterImages.CountAsync(i => i.TheaterId == theaterId) >= TheaterImage.MaxPerTheater)
            throw new AppValidationException($"This theater has {TheaterImage.MaxPerTheater} images, the most it can keep. Delete some it no longer uses first.");
        // Just the file's own name: some browsers send a full Windows path, and on Linux Path.GetFileName doesn't split
        // on backslashes, so split on both.
        var name = (fileName ?? "").Split('/', '\\')[^1].Trim();
        var image = new TheaterImage
        {
            TheaterId = theaterId,
            FileName = name.Length == 0 ? "image" : name.Length > TheaterImage.MaxFileNameLength ? name[..TheaterImage.MaxFileNameLength] : name,
            AltText = alt,
            ContentType = contentType,
            Width = size.Width,
            Height = size.Height,
            ByteSize = data.Length,
            Data = data,
            UploadedAt = time.GetUtcNow(),
            UploadedById = user.GetUserId(),
        };
        db.TheaterImages.Add(image);
        await db.SaveChangesAsync();
        return new ImageView(image.Id, image.Url(theater.Slug), image.FileName, image.AltText, image.Width, image.Height,
            image.ByteSize, image.UploadedAt, []);
    }

    // The library, newest first, with where each image is used.
    public async Task<List<ImageView>> ListImagesAsync(ClaimsPrincipal user, int theaterId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await RequireManageAsync(db, user, theaterId);
        var images = await db.TheaterImages.AsNoTracking()
            .Where(i => i.TheaterId == theaterId)
            .OrderByDescending(i => i.UploadedAt).ThenByDescending(i => i.Id)
            .Select(i => new { i.Id, i.FileName, i.AltText, i.Width, i.Height, i.ByteSize, i.UploadedAt })
            .ToListAsync();
        var usage = await UsageAsync(db, theaterId);
        return images.Select(i => new ImageView(i.Id, TheaterImage.ImageUrl(theater.Slug, i.Id), i.FileName, i.AltText,
            i.Width, i.Height, i.ByteSize, i.UploadedAt, usage.GetValueOrDefault(i.Id) ?? [])).ToList();
    }

    public async Task UpdateImageAltAsync(ClaimsPrincipal user, int imageId, string? altText)
    {
        var alt = string.IsNullOrWhiteSpace(altText) ? null : altText.Trim();
        if (alt?.Length > TheaterImage.MaxAltLength)
            throw new AppValidationException($"Keep the image description to {TheaterImage.MaxAltLength} characters.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var image = await db.TheaterImages.FirstOrDefaultAsync(i => i.Id == imageId)
            ?? throw new NotFoundException("Image not found.");
        await RequireManageAsync(db, user, image.TheaterId);
        image.AltText = alt;
        await db.SaveChangesAsync();
    }

    // Only once no page uses it, so a page never shows a broken image.
    public async Task DeleteImageAsync(ClaimsPrincipal user, int imageId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var image = await db.TheaterImages.FirstOrDefaultAsync(i => i.Id == imageId)
            ?? throw new NotFoundException("Image not found.");
        await RequireManageAsync(db, user, image.TheaterId);
        if ((await UsageAsync(db, image.TheaterId)).GetValueOrDefault(imageId) is { Count: > 0 } usedIn)
            throw new AppValidationException($"This image is used in {string.Join(", ", usedIn)}. Remove it from there first.");
        db.TheaterImages.Remove(image);
        await db.SaveChangesAsync();
    }

    // Image id -> the titles of the pages and posts that use it (as their cover or in their text).
    private static async Task<Dictionary<int, List<string>>> UsageAsync(ApplicationDbContext db, int theaterId)
    {
        var pages = await db.TheaterPages.AsNoTracking().Where(p => p.TheaterId == theaterId)
            .Select(p => new { p.Title, p.CoverImageId, p.BodyHtml }).ToListAsync();
        var usage = new Dictionary<int, List<string>>();
        foreach (var page in pages)
        {
            var ids = HtmlContent.ImageIds(page.BodyHtml).ToHashSet();
            if (page.CoverImageId is int cover)
                ids.Add(cover);
            foreach (var id in ids)
            {
                if (!usage.TryGetValue(id, out var titles))
                    usage[id] = titles = [];
                titles.Add($"\"{page.Title}\"");
            }
        }
        return usage;
    }

    private static async Task<Dictionary<int, LibraryImage>> LibraryAsync(ApplicationDbContext db, int theaterId) =>
        await db.TheaterImages.AsNoTracking().Where(i => i.TheaterId == theaterId)
            .Select(i => new LibraryImage(i.Id, i.Width, i.Height, i.AltText))
            .ToDictionaryAsync(i => i.Id);

    // --- Reading (public) ---

    // The theater's live menu pages in order, and whether it has live posts. Empty for a theater the user can't browse.
    public async Task<TheaterMenu> GetMenuAsync(ClaimsPrincipal user, Theater theater)
    {
        if (!TheaterService.CanBrowse(user, theater))
            return TheaterMenu.Empty;
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = time.GetUtcNow();
        var live = Live(db.TheaterPages.AsNoTracking().Where(p => p.TheaterId == theater.Id), now);
        var pages = await live.Where(p => p.Kind == PageKind.Page && p.ShowInMenu)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Title)
            .Select(p => new MenuLink(p.Title, $"theaters/{theater.Slug}/pages/{p.Slug}"))
            .ToListAsync();
        var hasPosts = await live.AnyAsync(p => p.Kind == PageKind.Post);
        return new TheaterMenu(pages, hasPosts);
    }

    // Live posts: pinned first, then events still to come (soonest first), then the rest newest first.
    public async Task<(List<PostCard> Posts, int Total)> ListLivePostsAsync(ClaimsPrincipal user, Theater theater, int skip = 0, int take = FeedSize)
    {
        if (!TheaterService.CanBrowse(user, theater))
            return ([], 0);
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = time.GetUtcNow();
        var live = Live(db.TheaterPages.AsNoTracking().Where(p => p.TheaterId == theater.Id && p.Kind == PageKind.Post), now);
        var total = await live.CountAsync();
        // Ordered and paged in the database, so only one page of posts (and their text) is read. Each key is an
        // explicit value, so posts without an event date sort the same whatever the database does with nulls.
        var posts = await live
            .OrderByDescending(p => p.IsPinned)
            .ThenByDescending(p => p.EventStartsAt != null && p.EventStartsAt > now ? 1 : 0)
            .ThenBy(p => p.EventStartsAt != null && p.EventStartsAt > now ? p.EventStartsAt : null)
            .ThenByDescending(p => p.PublishAt)
            .ThenByDescending(p => p.Id)
            .Skip(skip)
            .Take(take)
            .Select(p => new
            {
                p.Id, p.Title, p.Slug, p.Summary, BodyHtml = p.Summary == null ? p.BodyHtml : null, p.PublishAt,
                p.EventStartsAt, p.IsPinned, p.CoverImageId,
                CoverAlt = p.CoverAlt ?? (p.CoverImage != null ? p.CoverImage.AltText : null),
            })
            .ToListAsync();
        var cards = posts.Select(p => new PostCard(p.Id, p.Title,
            $"theaters/{theater.Slug}/news/{p.Slug}",
            p.Summary ?? HtmlContent.Excerpt(HtmlContent.ToPlainText(p.BodyHtml), 200),
            p.PublishAt!.Value, p.EventStartsAt, p.IsPinned,
            p.CoverImageId is int id ? TheaterImage.ImageUrl(theater.Slug, id) : null, p.CoverAlt)).ToList();
        return (cards, total);
    }

    // A live page or post, or (for staff who manage content) any of them, as a preview. Null when there's no such
    // page this user may see.
    public async Task<PublicPage?> GetPageAsync(ClaimsPrincipal user, string theaterSlug, PageKind kind, string slug)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == theaterSlug);
        if (theater is null || !TheaterService.CanBrowse(user, theater))
            return null;
        var page = await db.TheaterPages.AsNoTracking().Include(p => p.CoverImage)
            .FirstOrDefaultAsync(p => p.TheaterId == theater.Id && p.Kind == kind && p.Slug == slug);
        if (page is null)
            return null;
        var now = time.GetUtcNow();
        var live = page.IsLive(now);
        if (!live && !await CanManageAsync(user, theater))
            return null;

        var body = html.Sanitize(page.BodyHtml, theater.Slug, await LibraryAsync(db, theater.Id));
        var description = page.Summary ?? HtmlContent.Excerpt(HtmlContent.ToPlainText(body), 200);
        var cover = page.CoverImage;
        var firstImage = HtmlContent.FirstImageId(body);
        var shareImage = cover is not null ? cover.Url(theater.Slug)
            : firstImage is int imageId ? TheaterImage.ImageUrl(theater.Slug, imageId)
            : theater.LogoUrl;
        return new PublicPage(theater, page.Id, page.Kind, page.Title, description, body,
            cover?.Url(theater.Slug), page.CoverAlt ?? cover?.AltText, cover?.Width ?? 0, cover?.Height ?? 0,
            page.PublishAt, page.EventStartsAt, page.StatusAt(now), !live, shareImage);
    }

    // An image for the image endpoint: the theater must be visible to the user. (Images aren't secret once uploaded,
    // the same as logos and posters.)
    public async Task<TheaterImage?> GetImageAsync(ClaimsPrincipal user, string theaterSlug, int imageId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == theaterSlug);
        if (theater is null || !TheaterService.CanBrowse(user, theater))
            return null;
        var image = await db.TheaterImages.AsNoTracking().FirstOrDefaultAsync(i => i.Id == imageId && i.TheaterId == theater.Id);
        if (image is not null)
            image.Theater = theater;
        return image;
    }

    public async Task<bool> CanManageAsync(ClaimsPrincipal user, Theater theater) =>
        user.GetUserId() is not null && (await access.GetPermissionsAsync(user, theater)).Contains(TheaterPermissions.ManageContent);

    private static IQueryable<TheaterPage> Live(IQueryable<TheaterPage> pages, DateTimeOffset now) =>
        pages.Where(p => p.PublishAt != null && p.PublishAt <= now && (p.UnpublishAt == null || p.UnpublishAt > now));

    public static string PublicHref(string theaterSlug, TheaterPage page) => PublicHref(theaterSlug, page.Kind, page.Slug);

    public static string PublicHref(string theaterSlug, PageKind kind, string slug) =>
        $"theaters/{theaterSlug}/{(kind == PageKind.Page ? "pages" : "news")}/{slug}";

    // --- Helpers ---

    private async Task<Theater> RequireManageAsync(ApplicationDbContext db, ClaimsPrincipal user, int theaterId)
    {
        var theater = await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(user, theater, TheaterPermissions.ManageContent);
        return theater;
    }

    // Two editors saving the same new address at once: the unique index turns the second into a clear message.
    private static async Task SaveAsync(ApplicationDbContext db)
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            throw new AppValidationException("Another page already uses that web address.");
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();
}
