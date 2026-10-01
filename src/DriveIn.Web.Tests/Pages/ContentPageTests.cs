using Bunit;
using DriveIn.Web.Components.Pages.Manage;
using DriveIn.Web.Components.Pages.Theaters;
using DriveIn.Web.Components.Shared;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Tests.ContentTests;

namespace DriveIn.Web.Tests.Pages;

// Theaters' pages and posts: managing them, the editor and image picker, and the public pages.
public class ContentPageTests
{
    private static async Task<TheaterPage> PublishedAsync(World w, PageKind kind, string title, string body = "<p>Hello</p>", int? coverId = null)
    {
        var page = await w.Content.CreateAsync(w.OwnerUser, w.Theater.Id, kind, new PageInput(title, null, null, body, coverId));
        await w.Content.PublishAsync(w.OwnerUser, page.Id);
        return page;
    }

    // The editor's script: what getHtml returns stands for what was typed.
    private static BunitJSModuleInterop EditorModule(PageHost host, string typed)
    {
        var module = host.Context.JSInterop.SetupModule("./rich-text.js");
        module.Setup<int>("create", _ => true).SetResult(1);
        module.Setup<string?>("getHtml", _ => true).SetResult(typed);
        module.SetupVoid(_ => true).SetVoidResult();
        return module;
    }

    // --- Managing ---

    [Fact]
    public async Task The_list_shows_pages_and_posts_and_publishes_and_reorders_them()
    {
        await using var w = await SetUpAsync();
        var rules = await PublishedAsync(w, PageKind.Page, "Rules");
        var snacks = await w.Content.CreateAsync(w.OwnerUser, w.Theater.Id, PageKind.Page, Input("Snacks"));
        var post = await w.Content.CreateAsync(w.OwnerUser, w.Theater.Id, PageKind.Post,
            new PageInput("Movie marathon", null, null, "<p>x</p>", IsPinned: true, EventStartsAt: w.App.Time.GetUtcNow().AddDays(3)));
        await using var host = new PageHost(w.App).SignIn(w.Editor);

        var page = host.Render<ManageContent>(p => p.Add(x => x.Id, w.Theater.Id));
        page.WaitForText("Movie marathon");
        var text = page.Text();
        Assert.Contains("Rules Live", text);
        Assert.Contains("Snacks Draft", text);
        Assert.Contains("Pinned", text);
        Assert.Contains("owner@example.com", text);

        page.FindAll("button[aria-label='Move Snacks up']").Single().Click();
        page.WaitForAssertion(() => Assert.True(page.Markup.IndexOf("Snacks", StringComparison.Ordinal) < page.Markup.IndexOf(">Rules<", StringComparison.Ordinal)));

        page.FindAll("tr").Single(r => r.TextContent.Contains("Snacks")).QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Publish").Click();
        page.WaitForText("\"Snacks\" is live.");
        page.FindAll("tr").Single(r => r.TextContent.Contains("Rules")).QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Unpublish").Click();
        page.WaitForText("\"Rules\" is back to a draft.");

        page.FindAll("tr").Single(r => r.TextContent.Contains("Movie marathon")).QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Delete").Click();
        var dialogs = host.Dialogs!;
        dialogs.WaitForAssertion(() => Assert.Contains("Delete \"Movie marathon\"?", dialogs.Markup));
        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Delete").Click();
        page.WaitForText("Deleted \"Movie marathon\".");
        Assert.False(await w.App.Db().TheaterPages.AnyAsync(p => p.Id == post.Id));
        Assert.Equal(PageStatus.Live, (await w.App.Db().TheaterPages.SingleAsync(p => p.Id == snacks.Id)).StatusAt(w.App.Time.GetUtcNow()));
        _ = rules;
    }

    [Fact]
    public async Task Publishing_an_empty_page_from_the_list_explains_why_not()
    {
        await using var w = await SetUpAsync();
        await w.Content.CreateAsync(w.OwnerUser, w.Theater.Id, PageKind.Page, Input("Empty", "<p></p>"));
        await using var host = new PageHost(w.App).SignIn(w.Owner);

        var page = host.Render<ManageContent>(p => p.Add(x => x.Id, w.Theater.Id));
        page.WaitForText("Empty");
        page.ClickButton("Publish");

        page.WaitForText("Write something before publishing.");
    }

    [Fact]
    public async Task Staff_without_content_rights_get_no_tab_and_are_turned_away()
    {
        await using var w = await SetUpAsync();
        await using var host = new PageHost(w.App).SignIn(w.Editor);
        var header = host.Render<ManageHeader>(p => p.Add(x => x.Theater, w.Theater).Add(x => x.Title, "Probe"));
        header.WaitForText("Pages & posts");

        host.SignIn(w.Gate);
        var without = host.Render<ManageHeader>(p => p.Add(x => x.Theater, w.Theater).Add(x => x.Title, "Probe"));
        without.WaitForText("Gate");
        Assert.DoesNotContain("Pages & posts", without.Text());
        host.Render<ManageContent>(p => p.Add(x => x.Id, w.Theater.Id));
        Assert.EndsWith("Account/AccessDenied", host.Nav.Uri);
        host.Nav.NavigateTo("/");
        host.Render<ManageContentEdit>(p => p.Add(x => x.Id, w.Theater.Id));
        Assert.EndsWith("Account/AccessDenied", host.Nav.Uri);
    }

    [Fact]
    public async Task A_new_page_is_saved_from_the_editor_then_published()
    {
        await using var w = await SetUpAsync();
        await using var host = new PageHost(w.App).SignIn(w.Owner);
        EditorModule(host, "<p>Be <strong>kind</strong>.</p><script>x()</script>");
        host.Nav.NavigateTo($"manage/{w.Theater.Id}/content/new?kind=page");

        var page = host.Render<ManageContentEdit>(p => p.Add(x => x.Id, w.Theater.Id));
        page.WaitForText("New page");
        Assert.Contains("Show in the theater's menu", page.Text());
        page.SetField("Title", "House rules");
        page.ClickButton("Save draft");

        var saved = await w.App.Db().TheaterPages.SingleAsync();
        page.WaitForAssertion(() => Assert.EndsWith($"manage/{w.Theater.Id}/content/{saved.Id}", host.Nav.Uri));
        Assert.Equal(("house-rules", "<p>Be <strong>kind</strong>.</p>", null), (saved.Slug, saved.BodyHtml, saved.PublishAt));

        page.ClickButton("Publish now");
        page.WaitForText("Published. It's live now.");
        Assert.True((await w.App.Db().TheaterPages.SingleAsync()).IsLive(w.App.Time.GetUtcNow()));
        Assert.Contains("theaters/starlight/pages/house-rules", page.Markup);
    }

    [Fact]
    public async Task A_post_is_scheduled_in_the_theaters_time_zone()
    {
        await using var w = await SetUpAsync();
        await using (var db = w.App.Db())
        {
            (await db.Theaters.SingleAsync()).TimeZone = "America/Chicago";
            await db.SaveChangesAsync();
        }
        var post = await w.Content.CreateAsync(w.OwnerUser, w.Theater.Id, PageKind.Post, Input("Halloween double feature"));
        await using var host = new PageHost(w.App).SignIn(w.Owner);
        EditorModule(host, "<p>Spooky!</p>");

        var page = host.Render<ManageContentEdit>(p => p.Add(x => x.Id, w.Theater.Id).Add(x => x.PageId, post.Id));
        page.WaitForText("Pin to the top");
        page.SetField("Show from", "10/25/2026");
        page.SetField("at", "06:00 PM");
        page.SetField("Event date", "10/31/2026");
        page.SetField("Starts at", "07:30 PM");
        page.ClickButton("Schedule");

        page.WaitForText("Scheduled.");
        var saved = await w.App.Db().TheaterPages.SingleAsync();
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero), saved.PublishAt); // 6 PM CDT
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 30, 0, TimeSpan.Zero), saved.EventStartsAt);
        Assert.Equal("<p>Spooky!</p>", saved.BodyHtml);

        page.ClickButton("Unpublish (back to draft)");
        page.WaitForText("It's a draft again");
        Assert.Null((await w.App.Db().TheaterPages.SingleAsync()).PublishAt);
    }

    [Fact]
    public async Task A_cover_is_chosen_from_the_library_and_the_page_can_be_deleted()
    {
        await using var w = await SetUpAsync();
        var image = await w.Content.UploadImageAsync(w.OwnerUser, w.Theater.Id, "screen.png", TestImages.Png(1600, 900), "The screen");
        var post = await w.Content.CreateAsync(w.OwnerUser, w.Theater.Id, PageKind.Post, Input("Opening night"));
        await using var host = new PageHost(w.App).SignIn(w.Owner);
        EditorModule(host, "<p>Hello</p>");

        var page = host.Render<ManageContentEdit>(p => p.Add(x => x.Id, w.Theater.Id).Add(x => x.PageId, post.Id));
        page.WaitForText("Choose a cover image");
        page.ClickButton("Choose a cover image");
        var dialogs = host.Dialogs!;
        dialogs.WaitForAssertion(() => Assert.NotEmpty(dialogs.FindAll(".image-tile")));
        dialogs.Find(".image-tile").Click();
        dialogs.WaitForAssertion(() => Assert.Equal("The screen", dialogs.Find("input:not([type=checkbox]):not([type=radio])").GetAttribute("value")));
        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Use as cover").Click();
        page.WaitForText("Change cover");
        page.ClickButton("Save draft");
        page.WaitForText("Saved.");
        Assert.Equal(image.Id, (await w.App.Db().TheaterPages.SingleAsync()).CoverImageId);

        page.ClickButton("Remove cover");
        page.ClickButton("Delete post");
        dialogs.WaitForAssertion(() => Assert.Contains("Delete \"Opening night\"?", dialogs.Markup));
        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Delete").Click();
        page.WaitForAssertion(() => Assert.EndsWith($"manage/{w.Theater.Id}/content", host.Nav.Uri));
        Assert.Empty(await w.App.Db().TheaterPages.ToListAsync());
    }

    [Fact]
    public async Task Reopening_a_decorative_cover_keeps_it_decorative()
    {
        await using var w = await SetUpAsync();
        var image = await w.Content.UploadImageAsync(w.OwnerUser, w.Theater.Id, "screen.png", TestImages.Png(), "The screen");
        var post = await w.Content.CreateAsync(w.OwnerUser, w.Theater.Id, PageKind.Post, new PageInput("Opening night", null, null, "<p>x</p>", image.Id, ""));
        await using var host = new PageHost(w.App).SignIn(w.Owner);
        EditorModule(host, "<p>x</p>");

        var page = host.Render<ManageContentEdit>(p => p.Add(x => x.Id, w.Theater.Id).Add(x => x.PageId, post.Id));
        page.WaitForText("Change cover");
        page.ClickButton("Change cover");

        var dialogs = host.Dialogs!;
        dialogs.WaitForText("Use as cover");
        Assert.True(dialogs.Find("input[type=checkbox]").HasAttribute("checked") || dialogs.Find("input[type=checkbox]").GetAttribute("aria-checked") == "true"
            || dialogs.FindAll(".mud-checkbox-true").Count > 0);
        Assert.False(dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Use as cover").HasAttribute("disabled"));
    }

    [Fact]
    public async Task The_editors_image_button_uploads_and_inserts_with_a_description_size_and_position()
    {
        await using var w = await SetUpAsync();
        await using var host = new PageHost(w.App).SignIn(w.Owner);
        var module = EditorModule(host, "");
        var editor = host.Render<RichTextEditor>(p => p.Add(x => x.Theater, w.Theater));
        editor.WaitForAssertion(() => Assert.NotEmpty(module.Invocations["create"]));

        var picking = editor.InvokeAsync(() => editor.Instance.OpenImagePicker());
        var dialogs = host.Dialogs!;
        dialogs.WaitForText("Add an image");
        dialogs.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(TestImages.Jpeg(1024, 768), "popcorn.jpg", contentType: "image/jpeg"));
        dialogs.WaitForAssertion(() => Assert.Single(dialogs.FindAll(".image-tile.selected")));
        Assert.True(dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Insert").HasAttribute("disabled")); // needs a description
        dialogs.Find("input:not([type=checkbox]):not([type=radio])").Input("Fresh popcorn");
        dialogs.FindAll(".mud-radio").First(r => r.TextContent.Contains("Small")).QuerySelector("input")!.Click();
        dialogs.FindAll(".mud-radio").First(r => r.TextContent.Contains("Right")).QuerySelector("input")!.Click();
        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Insert").Click();
        await picking;

        var image = await w.App.Db().TheaterImages.SingleAsync();
        var insert = Assert.Single(module.Invocations["insertImage"]);
        var arg = insert.Arguments[1]!;
        Assert.Equal($"theaters/starlight/images/{image.Id}", arg.GetType().GetProperty("src")!.GetValue(arg));
        Assert.Equal("Fresh popcorn", arg.GetType().GetProperty("alt")!.GetValue(arg));
        Assert.Equal("img-small img-right", arg.GetType().GetProperty("className")!.GetValue(arg));
        Assert.Equal(1024, arg.GetType().GetProperty("width")!.GetValue(arg));
    }

    [Fact]
    public async Task Clicking_an_image_in_the_editor_changes_or_removes_it()
    {
        await using var w = await SetUpAsync();
        await using var host = new PageHost(w.App).SignIn(w.Owner);
        var module = EditorModule(host, "");
        var editor = host.Render<RichTextEditor>(p => p.Add(x => x.Theater, w.Theater));
        editor.WaitForAssertion(() => Assert.NotEmpty(module.Invocations["create"]));
        var dialogs = host.Dialogs!;

        var editing = editor.InvokeAsync(() => editor.Instance.EditImage(new RichTextEditor.PlacedImage("theaters/starlight/images/1", "Old", "img-full img-center")));
        dialogs.WaitForText("Image settings");
        dialogs.Find("input:not([type=checkbox]):not([type=radio])").Input("New description");
        dialogs.FindAll(".mud-radio").First(r => r.TextContent.Contains("Left")).QuerySelector("input")!.Click();
        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Save").Click();
        await editing;
        var update = Assert.Single(module.Invocations["updateImage"]).Arguments[1]!;
        Assert.Equal("New description", update.GetType().GetProperty("alt")!.GetValue(update));
        Assert.Equal("img-full img-left", update.GetType().GetProperty("className")!.GetValue(update));

        var removing = editor.InvokeAsync(() => editor.Instance.EditImage(new RichTextEditor.PlacedImage("x", "", "")));
        dialogs.WaitForText("Remove image");
        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Remove image").Click();
        await removing;
        Assert.Null(module.Invocations["updateImage"][1].Arguments[1]);
    }

    [Fact]
    public async Task The_image_library_uploads_describes_and_deletes_unused_images()
    {
        await using var w = await SetUpAsync();
        var used = await w.Content.UploadImageAsync(w.OwnerUser, w.Theater.Id, "used.png", TestImages.Png());
        await PublishedAsync(w, PageKind.Page, "Rules", "<p>x</p>", used.Id);
        await using var host = new PageHost(w.App).SignIn(w.Editor);

        var page = host.Render<ManageContentImages>(p => p.Add(x => x.Id, w.Theater.Id));
        page.WaitForText("used.png");
        Assert.Contains("Used in \"Rules\"", page.Text());
        Assert.True(page.FindAll("button").Single(b => b.TextContent.Trim() == "Delete").HasAttribute("disabled"));

        page.FindComponent<InputFile>().UploadFiles(
            InputFileContent.CreateFromBinary(TestImages.Gif(), "spare.gif", contentType: "image/gif"),
            InputFileContent.CreateFromBinary(TestImages.WebP(), "wide.webp", contentType: "image/webp"));
        page.WaitForText("Uploaded 2 images.");
        Assert.Contains("3 of 250", page.Text());
        Assert.Contains("1200 × 630", page.Text());

        var spare = page.FindAll(".image-card").Single(c => c.TextContent.Contains("spare.gif"));
        spare.QuerySelector("input")!.Change("A spare");
        page.FindAll(".image-card").Single(c => c.TextContent.Contains("spare.gif")).QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Save").Click();
        page.WaitForText("Saved the description.");
        Assert.Equal("A spare", (await w.App.Db().TheaterImages.SingleAsync(i => i.FileName == "spare.gif")).AltText);

        page.FindAll(".image-card").Single(c => c.TextContent.Contains("spare.gif")).QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Delete").Click();
        var dialogs = host.Dialogs!;
        dialogs.WaitForText("Delete spare.gif?");
        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Delete").Click();
        page.WaitForText("Deleted spare.gif.");
        Assert.Equal(2, await w.App.Db().TheaterImages.CountAsync());

        page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("<svg/>", "evil.png", contentType: "image/png"));
        page.WaitForText("Images must be JPG, PNG, GIF or WebP files.");
    }

    // --- Public ---

    [Fact]
    public async Task Signed_out_visitors_see_the_theater_its_menu_and_news()
    {
        await using var w = await SetUpAsync();
        await PublishedAsync(w, PageKind.Page, "Rules");
        await w.Content.CreateAsync(w.OwnerUser, w.Theater.Id, PageKind.Page, Input("Secret draft"));
        var cover = await w.Content.UploadImageAsync(w.OwnerUser, w.Theater.Id, "c.png", TestImages.Png(), "Cover");
        await PublishedAsync(w, PageKind.Post, "Opening night", "<p>Come early for the best spots.</p>", cover.Id);
        await using var host = new PageHost(w.App);

        var page = host.Render<Details>(p => p.Add(x => x.Slug, "starlight"));

        page.WaitForText("News & events");
        var nav = page.Find(".theater-nav").TextContent;
        Assert.Contains("Showings", nav);
        Assert.Contains("Rules", nav);
        Assert.Contains("News & events", nav);
        Assert.DoesNotContain("Secret draft", page.Markup);
        Assert.Contains("Opening night", page.Text());
        Assert.Contains("Come early for the best spots.", page.Text());
        Assert.Contains(cover.Url, page.Markup);
        Assert.Contains("aria-current=\"page\"", page.Find(".theater-nav-link.active").OuterHtml);
    }

    [Fact]
    public async Task A_demo_theater_is_not_found_by_strangers()
    {
        await using var w = await SetUpAsync();
        await PublishedAsync(w, PageKind.Page, "Rules");
        await using (var db = w.App.Db())
        {
            (await db.Theaters.SingleAsync()).Mode = TheaterMode.Demo;
            await db.SaveChangesAsync();
        }
        await using var host = new PageHost(w.App);

        host.Render<Details>(p => p.Add(x => x.Slug, "starlight")).WaitForText("Theater not found");
        host.Render<TheaterContentPage>(p => p.Add(x => x.Slug, "starlight").Add(x => x.PageSlug, "rules")).WaitForText("Page not found");
        host.Render<TheaterNews>(p => p.Add(x => x.Slug, "starlight")).WaitForText("Theater not found");
    }

    [Fact]
    public async Task A_page_shows_its_sanitized_content_and_staff_preview_drafts()
    {
        await using var w = await SetUpAsync();
        var image = await w.Content.UploadImageAsync(w.OwnerUser, w.Theater.Id, "s.png", TestImages.Png(800, 600), "Snack bar");
        await PublishedAsync(w, PageKind.Page, "Snacks", $"<h2>Menu</h2><p><img src=\"{image.Url}\" class=\"img-small img-left\">Popcorn</p>");
        await w.Content.CreateAsync(w.OwnerUser, w.Theater.Id, PageKind.Page, Input("Coming soon"));
        await using var host = new PageHost(w.App);

        var live = host.Render<TheaterContentPage>(p => p.Add(x => x.Slug, "starlight").Add(x => x.PageSlug, "snacks"));
        live.WaitForText("Popcorn");
        Assert.Contains("<h2>Menu</h2>", live.Markup);
        Assert.Contains("alt=\"Snack bar\"", live.Markup);
        Assert.Contains("class=\"img-small img-left\"", live.Markup);
        Assert.DoesNotContain("Preview", live.Text());

        host.Render<TheaterContentPage>(p => p.Add(x => x.Slug, "starlight").Add(x => x.PageSlug, "coming-soon")).WaitForText("Page not found");

        host.SignIn(w.Editor);
        var preview = host.Render<TheaterContentPage>(p => p.Add(x => x.Slug, "starlight").Add(x => x.PageSlug, "coming-soon"));
        preview.WaitForText("Preview: only staff can see this. It's a draft.");
    }

    [Fact]
    public async Task A_post_shows_its_event_and_the_news_page_pages_through_posts()
    {
        await using var w = await SetUpAsync();
        var eventAt = w.App.Time.GetUtcNow().AddDays(5);
        var post = await w.Content.CreateAsync(w.OwnerUser, w.Theater.Id, PageKind.Post,
            new PageInput("Car show", null, null, "<p>Classic cars before the movie.</p>", EventStartsAt: eventAt));
        await w.Content.PublishAsync(w.OwnerUser, post.Id);
        for (var i = 0; i < ContentService.NewsPageSize; i++)
            await PublishedAsync(w, PageKind.Post, $"Update {i}");
        await using var host = new PageHost(w.App);

        var page = host.Render<TheaterContentPage>(p => p.Add(x => x.Slug, "starlight").Add(x => x.PostSlug, "car-show"));
        page.WaitForText("Classic cars before the movie.");
        Assert.Contains($"Event: {eventAt.UtcDateTime:dddd, MMMM d, yyyy}", page.Text());
        Assert.Contains("All news & events", page.Text());

        var news = host.Render<TheaterNews>(p => p.Add(x => x.Slug, "starlight"));
        news.WaitForText("Car show");
        Assert.Equal(ContentService.NewsPageSize, news.FindAll(".news-card").Count);
        Assert.Contains("Older", news.Text());

        host.Nav.NavigateTo("theaters/starlight/news?page=2");
        var second = host.Render<TheaterNews>(p => p.Add(x => x.Slug, "starlight"));
        second.WaitForText("Newer");
        Assert.Single(second.FindAll(".news-card"));
    }
}
