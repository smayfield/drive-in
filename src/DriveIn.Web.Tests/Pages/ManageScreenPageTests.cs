using DriveIn.Web.Components.Pages.Manage;
using DriveIn.Web.Data;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests.Pages;

public class ManageScreenPageTests
{
    private static async Task<(PageHost Host, TicketSalesTests.Setup S, IRenderedComponent<ManageScreen> Page)> OpenAsync()
    {
        var s = await TicketSalesTests.SetUpAsync();
        var host = new PageHost(s.App).SignIn(s.Owner);
        var page = host.Render<ManageScreen>(p => p.Add(x => x.Id, s.Theater.Id).Add(x => x.ScreenId, s.Screen.Id));
        page.WaitForText("Preview");
        return (host, s, page);
    }

    private static async Task<Screen> ReloadAsync(TicketSalesTests.Setup s)
    {
        await using var db = s.App.Db();
        return await db.Screens.SingleAsync(x => x.Id == s.Screen.Id);
    }

    [Fact]
    public async Task The_layout_preview_and_upcoming_showtimes_are_shown()
    {
        var (host, _, page) = await OpenAsync();
        await using var _ = host;

        var text = page.Text();
        Assert.Contains("2 rows · 7 spots", text);
        Assert.Contains("Sat, Sep 5 · 8:00 PM — Jaws", text);
        Assert.Contains("A1–A3", text);
        Assert.Contains("No spot takes large vehicles", text);
    }

    [Fact]
    public async Task Filling_a_grid_marks_the_back_half_for_large_vehicles_and_saves()
    {
        var (host, s, page) = await OpenAsync();
        await using var _ = host;

        page.SetField("Number of rows", "4");
        page.SetField("Spots per row", "5");
        page.ClickButton("Fill");
        page.WaitForText("10 of 20 spots take large vehicles.");
        Assert.Contains("Unsaved changes", page.Text());
        page.ClickButton("Save");

        page.WaitForText("Screen saved.");
        var screen = await ReloadAsync(s);
        Assert.Equal([5, 5, 5, 5], screen.RowSpots);
        Assert.Equal(10, screen.LargeSpots.Count);
    }

    [Fact]
    public async Task Rows_can_be_added_resized_marked_and_removed()
    {
        var (host, s, page) = await OpenAsync();
        await using var _ = host;

        page.ClickButton("Add row");
        page.SetField("Spots in row 3", "6");
        page.Find("input[aria-label='Row 3 takes large vehicles']").Change(true);
        page.ClickButton("Remove row 1");
        page.SetField("Name", "Big Screen");
        page.ClickButton("Save");

        page.WaitForText("Screen saved.");
        var screen = await ReloadAsync(s);
        Assert.Equal("Big Screen", screen.Name);
        Assert.Equal([4, 6], screen.RowSpots);
        Assert.Equal(6, screen.LargeSpots.Count); // all of the old row 3, now row 2
    }

    [Fact]
    public async Task Clicking_a_spot_marks_it_and_revert_undoes_it()
    {
        var (host, _, page) = await OpenAsync();
        await using var _ = host;

        page.FindAll("g.lot-clickable")[0].Click();
        page.WaitForText("1 of 7 spots take large vehicles.");
        page.ClickButton("All spots");
        page.WaitForText("7 of 7 spots take large vehicles.");

        page.ClickButton("Revert");

        page.WaitForText("No spot takes large vehicles");
        Assert.DoesNotContain("Unsaved changes", page.Text());
    }

    [Fact]
    public async Task The_label_scheme_can_be_changed()
    {
        var (host, s, page) = await OpenAsync();
        await using var _ = host;
        var before = (await ReloadAsync(s)).LabelScheme;

        page.FindAll(".mud-radio").First(r => !r.QuerySelector("input")!.HasAttribute("checked")).QuerySelector("input")!.Click();
        page.ClickButton("Save");

        page.WaitForText("Screen saved.");
        Assert.NotEqual(before, (await ReloadAsync(s)).LabelScheme);
    }

    [Fact]
    public async Task A_screen_from_another_theater_is_not_found()
    {
        var s = await TicketSalesTests.SetUpAsync();
        var other = await s.App.CreateTheaterAsync("Other", s.Owner.Id);
        await using var host = new PageHost(s.App).SignIn(s.Owner);

        var page = host.Render<ManageScreen>(p => p.Add(x => x.Id, other.Id).Add(x => x.ScreenId, s.Screen.Id));

        page.WaitForText("Screen not found.");
    }

    [Fact]
    public async Task Staff_who_cant_manage_screens_see_the_layout_only()
    {
        var s = await TicketSalesTests.SetUpAsync();
        var staff = await s.App.CreateUserAsync("staff@example.com", s.Theater.Id);
        await s.App.GrantAsync(staff, ViewReports);
        await using var host = new PageHost(s.App).SignIn(staff);

        var page = host.Render<ManageScreen>(p => p.Add(x => x.Id, s.Theater.Id).Add(x => x.ScreenId, s.Screen.Id));

        page.WaitForText("Layout");
        Assert.Contains("Spot labels:", page.Text());
        Assert.DoesNotContain("Fill", page.Markup);
        Assert.Empty(page.FindAll("g.lot-clickable"));
    }
}
