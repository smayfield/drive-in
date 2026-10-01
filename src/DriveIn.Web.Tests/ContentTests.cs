using System.Diagnostics.Metrics;
using System.Text;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests;

// Just enough of each format for the type and size to be read from the header.
public static class TestImages
{
    public static byte[] Png(int width = 640, int height = 480) =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        .. BigEndian(width), .. BigEndian(height), 8, 6, 0, 0, 0, 0, 0, 0, 0,
    ];

    public static byte[] Gif(int width = 320, int height = 200) =>
        [.. "GIF89a"u8, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0, 0, 0];

    // SOI, an APP0 segment to skip, then a baseline start-of-frame.
    public static byte[] Jpeg(int width = 1024, int height = 768) =>
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00,
        0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 0, 0, 0, 0,
    ];

    // Extended WebP: the canvas size as 24-bit width-1 and height-1.
    public static byte[] WebP(int width = 1200, int height = 630) =>
    [
        .. "RIFF"u8, 30, 0, 0, 0, .. "WEBP"u8, .. "VP8X"u8, 10, 0, 0, 0, 0, 0, 0, 0,
        (byte)(width - 1), (byte)((width - 1) >> 8), (byte)((width - 1) >> 16),
        (byte)(height - 1), (byte)((height - 1) >> 8), (byte)((height - 1) >> 16),
    ];

    private static byte[] BigEndian(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
}

public class ContentTests
{
    internal sealed class World : IAsyncDisposable
    {
        public required TestApp App { get; init; }
        public required Theater Theater { get; init; }
        public required ApplicationUser Owner { get; init; }
        public required ApplicationUser Editor { get; init; } // content.manage
        public required ApplicationUser Gate { get; init; }   // no content rights

        public ContentService Content => App.Get<ContentService>();
        public System.Security.Claims.ClaimsPrincipal OwnerUser => Principals.For(Owner);

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    internal static async Task<World> SetUpAsync()
    {
        var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var editor = await app.CreateUserAsync("editor@example.com", employeeTheaterId: theater.Id);
        await app.GrantAsync(editor, ManageContent);
        var gate = await app.CreateUserAsync("gate@example.com", employeeTheaterId: theater.Id);
        await app.GrantAsync(gate, AdmitGuests);
        return new World { App = app, Theater = theater, Owner = owner, Editor = editor, Gate = gate };
    }

    internal static PageInput Input(string title, string body = "<p>Hello</p>", string? slug = null) => new(title, slug, null, body);

    [Fact]
    public async Task Only_people_with_content_manage_can_write()
    {
        await using var w = await SetUpAsync();
        var gate = Principals.For(w.Gate);
        var customer = Principals.For(await w.App.CreateUserAsync("c@example.com"));

        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Content.CreateAsync(gate, w.Theater.Id, PageKind.Page, Input("Rules")));
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Content.CreateAsync(customer, w.Theater.Id, PageKind.Page, Input("Rules")));
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Content.ListForManageAsync(gate, w.Theater.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Content.UploadImageAsync(gate, w.Theater.Id, "a.png", TestImages.Png()));

        var page = await w.Content.CreateAsync(Principals.For(w.Editor), w.Theater.Id, PageKind.Page, Input("Rules"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Content.PublishAsync(gate, page.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Content.DeleteAsync(customer, page.Id));
        await w.Content.PublishAsync(w.OwnerUser, page.Id);
        await w.Content.UpdateAsync(Principals.For(await w.App.CreateUserAsync("a@example.com", admin: true), admin: true), page.Id, Input("Rules 2"));
        Assert.Equal("Rules 2", (await w.Content.GetForEditAsync(w.OwnerUser, page.Id)).Page.Title);
    }

    [Fact]
    public async Task Slugs_come_from_titles_and_are_unique_per_theater_and_kind()
    {
        await using var w = await SetUpAsync();
        var me = w.OwnerUser;

        Assert.Equal("snack-bar-menu", (await w.Content.CreateAsync(me, w.Theater.Id, PageKind.Page, Input("Snack Bar Menu!"))).Slug);
        Assert.Equal("snack-bar-menu-2", (await w.Content.CreateAsync(me, w.Theater.Id, PageKind.Page, Input("Snack bar menu"))).Slug);
        Assert.Equal("snack-bar-menu", (await w.Content.CreateAsync(me, w.Theater.Id, PageKind.Post, Input("Snack bar menu"))).Slug); // posts are separate
        Assert.Equal("news-page", (await w.Content.CreateAsync(me, w.Theater.Id, PageKind.Page, Input("News"))).Slug);

        await Assert.ThrowsAsync<AppValidationException>(() => w.Content.CreateAsync(me, w.Theater.Id, PageKind.Page, Input("X", slug: "snack-bar-menu")));
        await Assert.ThrowsAsync<AppValidationException>(() => w.Content.CreateAsync(me, w.Theater.Id, PageKind.Page, Input("X", slug: "news")));
        await Assert.ThrowsAsync<AppValidationException>(() => w.Content.CreateAsync(me, w.Theater.Id, PageKind.Page, Input("X", slug: "Bad Slug")));
        await Assert.ThrowsAsync<AppValidationException>(() => w.Content.CreateAsync(me, w.Theater.Id, PageKind.Page, Input(" ")));
        await Assert.ThrowsAsync<AppValidationException>(() => w.Content.CreateAsync(me, w.Theater.Id, PageKind.Page,
            new PageInput("X", null, new string('s', TheaterPage.MaxSummaryLength + 1), "<p>x</p>")));
        await Assert.ThrowsAsync<AppValidationException>(() => w.Content.CreateAsync(me, w.Theater.Id, PageKind.Page,
            Input("X", "<p>" + new string('b', TheaterPage.MaxBodyLength) + "</p>")));
    }

    [Fact]
    public async Task Bodies_are_sanitized_when_saved()
    {
        await using var w = await SetUpAsync();

        var page = await w.Content.CreateAsync(w.OwnerUser, w.Theater.Id, PageKind.Page,
            Input("Rules", "<p onclick=\"x()\">Hi<script>alert(1)</script></p><img src=\"https://evil.example/a.png\">"));

        Assert.Equal("<p>Hi</p>", page.BodyHtml);
    }

    [Fact]
    public async Task Publishing_states_over_time()
    {
        await using var w = await SetUpAsync();
        var me = w.OwnerUser;
        var now = w.App.Time.GetUtcNow();
        var post = await w.Content.CreateAsync(me, w.Theater.Id, PageKind.Post, Input("Movie night"));
        Status(PageStatus.Draft);

        await w.Content.PublishAsync(me, post.Id, now.AddHours(1), now.AddHours(3));
        Status(PageStatus.Scheduled);
        Assert.Null(await w.Content.GetPageAsync(Principals.Anonymous, "starlight", PageKind.Post, "movie-night"));

        w.App.Time.Advance(TimeSpan.FromHours(1));
        Status(PageStatus.Live);
        Assert.NotNull(await w.Content.GetPageAsync(Principals.Anonymous, "starlight", PageKind.Post, "movie-night"));

        w.App.Time.Advance(TimeSpan.FromHours(2));
        Status(PageStatus.Ended);
        Assert.Null(await w.Content.GetPageAsync(Principals.Anonymous, "starlight", PageKind.Post, "movie-night"));

        await w.Content.PublishAsync(me, post.Id);
        Status(PageStatus.Live);
        await w.Content.UnpublishAsync(me, post.Id);
        Status(PageStatus.Draft);

        var t = w.App.Time.GetUtcNow();
        await Assert.ThrowsAsync<AppValidationException>(() => w.Content.PublishAsync(me, post.Id, t.AddHours(2), t.AddHours(1)));
        await Assert.ThrowsAsync<AppValidationException>(() => w.Content.PublishAsync(me, post.Id, t.AddHours(-3), t.AddHours(-1)));
        var empty = await w.Content.CreateAsync(me, w.Theater.Id, PageKind.Page, Input("Empty", "<p><br></p>"));
        await Assert.ThrowsAsync<AppValidationException>(() => w.Content.PublishAsync(me, empty.Id));

        void Status(PageStatus expected) =>
            Assert.Equal(expected, w.App.Db().TheaterPages.AsNoTracking().Single(p => p.Id == post.Id).StatusAt(w.App.Time.GetUtcNow()));
    }

    [Fact]
    public async Task Staff_preview_drafts_and_everyone_else_sees_only_live_content()
    {
        await using var w = await SetUpAsync();
        await w.Content.CreateAsync(w.OwnerUser, w.Theater.Id, PageKind.Page, Input("Rules"));

        var preview = await w.Content.GetPageAsync(Principals.For(w.Editor), "starlight", PageKind.Page, "rules");
        Assert.True(preview!.IsPreview);
        Assert.Equal(PageStatus.Draft, preview.Status);
        Assert.Null(await w.Content.GetPageAsync(Principals.For(w.Gate), "starlight", PageKind.Page, "rules"));
        Assert.Null(await w.Content.GetPageAsync(Principals.Anonymous, "starlight", PageKind.Page, "rules"));
        Assert.Null(await w.Content.GetPageAsync(Principals.Anonymous, "starlight", PageKind.Page, "nope"));
        Assert.Null(await w.Content.GetPageAsync(Principals.Anonymous, "nowhere", PageKind.Page, "rules"));
    }

    [Fact]
    public async Task Demo_theaters_content_stays_private()
    {
        await using var w = await SetUpAsync();
        await using (var db = w.App.Db())
        {
            (await db.Theaters.SingleAsync()).Mode = TheaterMode.Demo;
            await db.SaveChangesAsync();
        }
        var page = await w.Content.CreateAsync(w.OwnerUser, w.Theater.Id, PageKind.Page, Input("Rules"));
        await w.Content.PublishAsync(w.OwnerUser, page.Id);
        var image = await w.Content.UploadImageAsync(w.OwnerUser, w.Theater.Id, "a.png", TestImages.Png());
        var demo = await w.App.Db().Theaters.AsNoTracking().SingleAsync();
        var outsider = Principals.For(await w.App.CreateUserAsync("c@example.com"));

        Assert.Null(await w.Content.GetPageAsync(Principals.Anonymous, "starlight", PageKind.Page, "rules"));
        Assert.Null(await w.Content.GetPageAsync(outsider, "starlight", PageKind.Page, "rules"));
        Assert.Null(await w.Content.GetImageAsync(Principals.Anonymous, "starlight", image.Id));
        Assert.Same(TheaterMenu.Empty, await w.Content.GetMenuAsync(outsider, demo));
        Assert.Empty((await w.Content.ListLivePostsAsync(outsider, demo)).Posts);
        Assert.False((await w.Content.GetPageAsync(Principals.For(w.Gate), "starlight", PageKind.Page, "rules"))!.IsPreview); // members see it
    }

    [Fact]
    public async Task The_menu_lists_live_menu_pages_in_order()
    {
        await using var w = await SetUpAsync();
        var me = w.OwnerUser;
        var rules = await w.Content.CreateAsync(me, w.Theater.Id, PageKind.Page, Input("Rules"));
        var snacks = await w.Content.CreateAsync(me, w.Theater.Id, PageKind.Page, Input("Snacks"));
        var hidden = await w.Content.CreateAsync(me, w.Theater.Id, PageKind.Page, new PageInput("Staff only", null, null, "<p>x</p>", ShowInMenu: false));
        await w.Content.CreateAsync(me, w.Theater.Id, PageKind.Page, Input("Draft"));
        foreach (var p in new[] { rules, snacks, hidden })
            await w.Content.PublishAsync(me, p.Id);

        var menu = await w.Content.GetMenuAsync(Principals.Anonymous, w.Theater);
        Assert.Equal(["Rules", "Snacks"], menu.Pages.Select(p => p.Title));
        Assert.Equal("theaters/starlight/pages/rules", menu.Pages[0].Href);
        Assert.False(menu.HasPosts);

        await w.Content.ReorderPagesAsync(me, w.Theater.Id, [snacks.Id, rules.Id]);
        Assert.Equal(["Snacks", "Rules"], (await w.Content.GetMenuAsync(Principals.Anonymous, w.Theater)).Pages.Select(p => p.Title));

        var post = await w.Content.CreateAsync(me, w.Theater.Id, PageKind.Post, Input("News"));
        await w.Content.PublishAsync(me, post.Id);
        Assert.True((await w.Content.GetMenuAsync(Principals.Anonymous, w.Theater)).HasPosts);

        var list = await w.Content.ListForManageAsync(me, w.Theater.Id);
        Assert.Equal(["Snacks", "Rules", "Staff only", "Draft", "News"], list.Select(i => i.Title));
    }

    [Fact]
    public async Task Posts_list_pinned_then_upcoming_events_then_newest()
    {
        await using var w = await SetUpAsync();
        var me = w.OwnerUser;
        var now = w.App.Time.GetUtcNow();
        async Task Post(string title, bool pinned = false, DateTimeOffset? eventAt = null)
        {
            var p = await w.Content.CreateAsync(me, w.Theater.Id, PageKind.Post, new PageInput(title, null, null, $"<p>{title} body</p>", IsPinned: pinned, EventStartsAt: eventAt));
            await w.Content.PublishAsync(me, p.Id);
            w.App.Time.Advance(TimeSpan.FromMinutes(1));
        }
        await Post("Old news");
        await Post("Past event", eventAt: now.AddDays(-1));
        await Post("Pinned", pinned: true);
        await Post("Later event", eventAt: now.AddDays(9));
        await Post("Soon event", eventAt: now.AddDays(2));
        await Post("Fresh news");

        var (posts, total) = await w.Content.ListLivePostsAsync(Principals.Anonymous, w.Theater, take: 10);

        Assert.Equal(6, total);
        Assert.Equal(["Pinned", "Soon event", "Later event", "Fresh news", "Past event", "Old news"], posts.Select(p => p.Title));
        Assert.Equal("Fresh news body", posts[3].Summary);
        Assert.Equal(2, (await w.Content.ListLivePostsAsync(Principals.Anonymous, w.Theater, skip: 4, take: 10)).Posts.Count);
    }

    [Fact]
    public async Task Uploads_check_the_type_size_and_count()
    {
        await using var w = await SetUpAsync();
        var me = w.OwnerUser;

        var png = await w.Content.UploadImageAsync(me, w.Theater.Id, @"C:\photos\screen.png", TestImages.Png(640, 480), " Our screen ");
        Assert.Equal(("screen.png", 640, 480, "Our screen", "theaters/starlight/images/" + png.Id), (png.FileName, png.Width, png.Height, png.AltText, png.Url));
        Assert.Equal((320, 200), Size(await w.Content.UploadImageAsync(me, w.Theater.Id, "a.gif", TestImages.Gif(320, 200))));
        Assert.Equal((1024, 768), Size(await w.Content.UploadImageAsync(me, w.Theater.Id, "a.jpg", TestImages.Jpeg(1024, 768))));
        Assert.Equal((1200, 630), Size(await w.Content.UploadImageAsync(me, w.Theater.Id, "a.webp", TestImages.WebP(1200, 630))));

        await Assert.ThrowsAsync<AppValidationException>(() => w.Content.UploadImageAsync(me, w.Theater.Id, "x.svg",
            Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")));
        await Assert.ThrowsAsync<AppValidationException>(() => w.Content.UploadImageAsync(me, w.Theater.Id, "x.png", Encoding.UTF8.GetBytes("<html>")));
        await Assert.ThrowsAsync<AppValidationException>(() => w.Content.UploadImageAsync(me, w.Theater.Id, "x.png", []));
        await Assert.ThrowsAsync<AppValidationException>(() => w.Content.UploadImageAsync(me, w.Theater.Id, "x.png", TestImages.Png()[..12])); // no size
        await Assert.ThrowsAsync<AppValidationException>(() => w.Content.UploadImageAsync(me, w.Theater.Id, "x.png",
            [.. TestImages.Png(), .. new byte[TheaterImage.MaxBytes]]));

        await using (var db = w.App.Db())
        {
            db.TheaterImages.AddRange(Enumerable.Range(0, TheaterImage.MaxPerTheater - 4).Select(_ => new TheaterImage
            {
                TheaterId = w.Theater.Id, FileName = "x.png", ContentType = "image/png", Width = 1, Height = 1, Data = [1],
            }));
            await db.SaveChangesAsync();
        }
        var full = await Assert.ThrowsAsync<AppValidationException>(() => w.Content.UploadImageAsync(me, w.Theater.Id, "one-more.png", TestImages.Png()));
        Assert.Contains("250", full.Message);

        static (int, int) Size(ImageView i) => (i.Width, i.Height);
    }

    [Fact]
    public async Task Images_in_use_are_listed_and_cant_be_deleted()
    {
        await using var w = await SetUpAsync();
        var me = w.OwnerUser;
        var cover = await w.Content.UploadImageAsync(me, w.Theater.Id, "cover.png", TestImages.Png(1600, 900), "The screen");
        var inline = await w.Content.UploadImageAsync(me, w.Theater.Id, "snack.jpg", TestImages.Jpeg());
        var spare = await w.Content.UploadImageAsync(me, w.Theater.Id, "spare.gif", TestImages.Gif());

        var page = await w.Content.CreateAsync(me, w.Theater.Id, PageKind.Page, new PageInput("Snacks", null, null,
            $"<p>Yum <img src=\"{inline.Url}\" alt=\"Popcorn\" class=\"img-small img-right\"></p>", cover.Id, null));

        Assert.Contains($"src=\"{inline.Url}\"", page.BodyHtml);
        Assert.Contains("width=\"1024\"", page.BodyHtml);
        var library = await w.Content.ListImagesAsync(me, w.Theater.Id);
        Assert.Equal(["spare.gif", "snack.jpg", "cover.png"], library.Select(i => i.FileName));
        Assert.Equal(["\"Snacks\""], library.Single(i => i.Id == cover.Id).UsedIn);
        Assert.Equal(["\"Snacks\""], library.Single(i => i.Id == inline.Id).UsedIn);
        Assert.Empty(library.Single(i => i.Id == spare.Id).UsedIn);

        var refused = await Assert.ThrowsAsync<AppValidationException>(() => w.Content.DeleteImageAsync(me, cover.Id));
        Assert.Contains("\"Snacks\"", refused.Message);
        await Assert.ThrowsAsync<AppValidationException>(() => w.Content.DeleteImageAsync(me, inline.Id));
        await w.Content.DeleteImageAsync(me, spare.Id);

        await w.Content.UpdateImageAltAsync(me, cover.Id, " Our big screen ");
        await w.Content.UpdateAsync(me, page.Id, new PageInput("Snacks", "snacks", null, "<p>No pictures now</p>"));
        await w.Content.DeleteImageAsync(me, cover.Id);
        await w.Content.DeleteImageAsync(me, inline.Id);
        Assert.Empty(await w.Content.ListImagesAsync(me, w.Theater.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => w.Content.DeleteImageAsync(me, cover.Id));
    }

    [Fact]
    public async Task Covers_must_be_the_theaters_own_and_show_on_the_page()
    {
        await using var w = await SetUpAsync();
        var me = w.OwnerUser;
        var other = await w.App.CreateTheaterAsync("Moonlight", w.Owner.Id);
        var theirs = await w.Content.UploadImageAsync(me, other.Id, "x.png", TestImages.Png());
        var ours = await w.Content.UploadImageAsync(me, w.Theater.Id, "screen.png", TestImages.Png(1600, 900), "The screen");

        await Assert.ThrowsAsync<AppValidationException>(() => w.Content.CreateAsync(me, w.Theater.Id, PageKind.Post,
            new PageInput("X", null, null, "<p>x</p>", theirs.Id)));
        var post = await w.Content.CreateAsync(me, w.Theater.Id, PageKind.Post, new PageInput("Opening night", null, "Come early!", "<p>x</p>", ours.Id));
        await w.Content.PublishAsync(me, post.Id);

        var page = await w.Content.GetPageAsync(Principals.Anonymous, "starlight", PageKind.Post, "opening-night");
        Assert.Equal((ours.Url, "The screen", 1600, 900, "Come early!", ours.Url), (page!.CoverUrl, page.CoverAlt, page.CoverWidth, page.CoverHeight, page.Description, page.ShareImageUrl));
        var card = Assert.Single((await w.Content.ListLivePostsAsync(Principals.Anonymous, w.Theater)).Posts);
        Assert.Equal((ours.Url, "The screen", "Come early!"), (card.CoverUrl, card.CoverAlt, card.Summary));

        var image = await w.Content.GetImageAsync(Principals.Anonymous, "starlight", ours.Id);
        Assert.Equal("image/png", image!.ContentType);
        Assert.Null(await w.Content.GetImageAsync(Principals.Anonymous, "starlight", theirs.Id)); // wrong theater

        await w.Content.UpdateAsync(me, post.Id, new PageInput("Opening night", "opening-night", null, "<p>x</p>", ours.Id, "Our big screen"));
        Assert.Equal("Our big screen", (await w.Content.GetPageAsync(Principals.Anonymous, "starlight", PageKind.Post, "opening-night"))!.CoverAlt);

        // Decorative: an empty description stays empty rather than falling back to the image's own.
        await w.Content.UpdateAsync(me, post.Id, new PageInput("Opening night", "opening-night", null, "<p>x</p>", ours.Id, ""));
        Assert.Equal("", (await w.Content.GetPageAsync(Principals.Anonymous, "starlight", PageKind.Post, "opening-night"))!.CoverAlt);
        Assert.Equal("", Assert.Single((await w.Content.ListLivePostsAsync(Principals.Anonymous, w.Theater)).Posts).CoverAlt);
    }

    [Fact]
    public async Task Deleting_removes_it_and_the_first_publish_is_counted()
    {
        await using var w = await SetUpAsync();
        using var published = new MetricCollector<long>(w.App.Get<IMeterFactory>(), DriveInMetrics.MeterName, "drivein.content.published");
        var me = w.OwnerUser;
        var page = await w.Content.CreateAsync(me, w.Theater.Id, PageKind.Page, Input("Rules"));

        await w.Content.PublishAsync(me, page.Id);
        await w.Content.PublishAsync(me, page.Id); // already published: not counted again
        var post = await w.Content.CreateAsync(me, w.Theater.Id, PageKind.Post, Input("News"));
        await w.Content.PublishAsync(me, post.Id);

        Assert.Equal(["page", "post"], published.GetMeasurementSnapshot().Select(m => m.Tags["kind"]));
        await w.Content.DeleteAsync(me, page.Id);
        Assert.Null(await w.Content.GetPageAsync(Principals.Anonymous, "starlight", PageKind.Page, "rules"));
        await Assert.ThrowsAsync<NotFoundException>(() => w.Content.GetForEditAsync(me, page.Id));
    }

    [Fact]
    public void Image_sizes_are_read_only_from_real_headers()
    {
        Assert.Null(UploadedImages.Dimensions(TestImages.Png()[..20], "image/png"));
        Assert.Null(UploadedImages.Dimensions([0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0], "image/jpeg"));
        Assert.Null(UploadedImages.Dimensions([.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, .. "ABCD"u8, .. new byte[20]], "image/webp"));
        Assert.Equal("image/webp", UploadedImages.SniffContent(TestImages.WebP()));
        Assert.Null(UploadedImages.Sniff(TestImages.WebP())); // logos and posters stay JPG, GIF, PNG
    }
}
