using DriveIn.Web.Components.Pages.Manage;
using DriveIn.Web.Data;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests.Pages;

public class ManageSchedulePageTests
{
    private static async Task<(PageHost Host, TicketSalesTests.Setup S, IRenderedComponent<ManageSchedule> Page)> OpenAsync()
    {
        var s = await TicketSalesTests.SetUpAsync();
        var host = new PageHost(s.App).SignIn(s.Owner);
        var page = host.Render<ManageSchedule>(p => p.Add(x => x.Id, s.Theater.Id));
        page.WaitForText("Add a showing");
        return (host, s, page);
    }

    [Fact]
    public async Task Upcoming_showings_and_films_are_listed_in_the_theaters_time()
    {
        var (host, _, page) = await OpenAsync();
        await using var _ = host;

        var text = page.Text();
        Assert.Contains("Times are in the theater's time zone, America/Chicago.", text);
        Assert.Contains("Saturday, September 5, 2026", text);
        Assert.Contains("8:00 PM–10:04 PM", text);
        Assert.Contains("Jaws", text);
        Assert.Contains("8:30 PM Jaws → 10:34 PM", text); // the add form's preview
    }

    [Fact]
    public async Task Scheduling_a_showing_adds_it_to_the_list()
    {
        var (host, s, page) = await OpenAsync();
        await using var _ = host;

        page.ClickButton("Schedule");

        page.WaitForText("Showing scheduled.");
        Assert.Contains("Tuesday, September 1, 2026", page.Text());
        await using var db = s.App.Db();
        Assert.Equal(2, await db.Showtimes.CountAsync());
    }

    [Fact]
    public async Task A_double_feature_shows_both_films_and_the_intermission()
    {
        var (host, s, page) = await OpenAsync();
        await using var _ = host;
        page.SetField("New film title", "Alien");
        page.SetField("New film runtime in minutes", "117");
        page.ClickButton("Add film");
        page.WaitForText("Directed by"); // the new film opens for editing

        page.ClickButton("Make it a double feature");
        page.SetField("Intermission", "20");
        page.ClickButton("Schedule");

        page.WaitForText("Double feature");
        Assert.Contains("20 min intermission", page.Text());
        await using var db = s.App.Db();
        var showing = await db.Showtimes.Include(x => x.Features).OrderBy(x => x.Id).LastAsync();
        Assert.Equal(2, showing.Features.Count);
    }

    [Fact]
    public async Task Editing_a_showing_saves_the_changes()
    {
        var (host, s, page) = await OpenAsync();
        await using var _ = host;

        page.FindAll("tr").First(r => r.TextContent.Contains("Jaws") && r.TextContent.Contains("Remove"))
            .QuerySelectorAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        page.WaitForText("Change showing");
        page.ClickButton("Save changes");

        page.WaitForText("Showing updated.");
        Assert.Contains("Add a showing", page.Text());
        await using var db = s.App.Db();
        Assert.Equal(1, await db.Showtimes.CountAsync());
    }

    [Fact]
    public async Task Removing_a_showing_asks_first()
    {
        var (host, s, page) = await OpenAsync();
        await using var _ = host;

        page.ClickButton("Remove");
        page.ClickButton("Confirm remove");

        page.WaitForText("Nothing scheduled.");
        await using var db = s.App.Db();
        Assert.Equal(0, await db.Showtimes.CountAsync());
    }

    [Fact]
    public async Task A_showing_can_be_given_other_pricing()
    {
        var (host, s, page) = await OpenAsync();
        await using var _ = host;
        await using (var db = s.App.Db())
        {
            db.PriceSchedules.Add(new PriceSchedule { TheaterId = s.Theater.Id, Name = "Bargain night" });
            await db.SaveChangesAsync();
        }
        page.Dispose();
        page = host.Render<ManageSchedule>(p => p.Add(x => x.Id, s.Theater.Id));
        page.WaitForText("Add a showing");

        host.Select(page, "Pricing for this showing", "Bargain night");

        page.WaitForText("Pricing updated.");
        await using var check = s.App.Db();
        Assert.NotNull((await check.Showtimes.SingleAsync()).PriceScheduleId);
    }

    [Fact]
    public async Task Film_details_are_edited_in_place()
    {
        var (host, s, page) = await OpenAsync();
        await using var _ = host;

        page.FindAll("tr").First(r => r.TextContent.Contains("Jaws") && r.TextContent.Contains("Delete"))
            .QuerySelectorAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        page.SetField("Directed by", "Steven Spielberg");
        page.SetField("Year", "1975");
        page.ClickButton("Save", last: true);

        page.WaitForText("Directed by Steven Spielberg");
        Assert.Contains("(1975)", page.Text());
    }

    [Fact]
    public async Task A_film_with_showings_cant_be_deleted_but_an_unused_one_can()
    {
        var (host, s, page) = await OpenAsync();
        await using var _ = host;

        page.ClickButton("Delete");
        page.ClickButton("Confirm delete");
        page.WaitForAssertion(() => Assert.Contains("mud-alert-text-error", page.Markup));
        Assert.Contains("Jaws", page.Text());

        page.SetField("New film title", "Alien");
        page.ClickButton("Add film");
        page.WaitForText("Directed by");
        page.ClickButton("Cancel");
        page.ClickButton("Delete"); // films are listed by title
        page.ClickButton("Confirm delete");

        page.WaitForAssertion(() => Assert.DoesNotContain("Alien", page.Text()));
        await using var db = s.App.Db();
        Assert.Equal(["Jaws"], await db.Films.Select(f => f.Title).ToListAsync());
    }

    [Fact]
    public async Task The_default_intermission_is_saved()
    {
        var (host, s, page) = await OpenAsync();
        await using var _ = host;

        page.SetField("Default intermission in minutes", "25");
        page.ClickButton("Save");

        page.WaitForText("Default intermission saved.");
        await using var db = s.App.Db();
        Assert.Equal(25, (await db.Theaters.SingleAsync()).DefaultIntermissionMinutes);
    }

    [Fact]
    public async Task Staff_who_cant_manage_the_schedule_only_see_it()
    {
        var s = await TicketSalesTests.SetUpAsync();
        var staff = await s.App.CreateUserAsync("staff@example.com", s.Theater.Id);
        await s.App.GrantAsync(staff, ViewReports);
        await using var host = new PageHost(s.App).SignIn(staff);

        var page = host.Render<ManageSchedule>(p => p.Add(x => x.Id, s.Theater.Id));

        page.WaitForText("Default intermission between double-feature films");
        Assert.Contains("Jaws", page.Text());
        Assert.DoesNotContain("Add a showing", page.Markup);
        Assert.DoesNotContain("Add film", page.Markup);
    }

    [Fact]
    public async Task A_theater_without_a_time_zone_is_warned_and_asked_for_a_film_first()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        await using var host = new PageHost(app).SignIn(owner);

        var page = host.Render<ManageSchedule>(p => p.Add(x => x.Id, theater.Id));

        page.WaitForText("time zone isn't set");
        Assert.Contains("Add a film below first.", page.Text());
    }
}
