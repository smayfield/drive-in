using DriveIn.Web.Components.Pages.Manage;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Tests.Pages;

public class ManageTheaterPageTests
{
    private static async Task<(PageHost Host, TicketSalesTests.Setup S)> OwnerAsync()
    {
        var s = await TicketSalesTests.SetUpAsync();
        return (new PageHost(s.App).SignIn(s.Owner), s);
    }

    [Fact]
    public async Task The_owner_sees_the_screens_profile_and_plan()
    {
        var (host, s) = await OwnerAsync();
        await using var _ = host;

        var page = host.Render<ManageTheater>(p => p.Add(x => x.Id, s.Theater.Id));

        page.WaitForText("Save profile");
        var text = page.Text();
        Assert.Contains("North", text);
        Assert.Contains("Standard plan", text);
        Assert.Contains("Allow free admission", text);
        Assert.DoesNotContain("You don't have any permissions", text);
    }

    [Fact]
    public async Task Adding_a_screen_opens_its_setup()
    {
        var (host, s) = await OwnerAsync();
        await using var _ = host;
        var page = host.Render<ManageTheater>(p => p.Add(x => x.Id, s.Theater.Id));
        page.WaitForText("Add screen");

        page.SetField("New screen name", "South");
        page.ClickButton("Add screen");

        await using var db = s.App.Db();
        var south = await db.Screens.SingleAsync(x => x.Name == "South");
        Assert.EndsWith($"manage/{s.Theater.Id}/screens/{south.Id}", host.Nav.Uri);
    }

    [Fact]
    public async Task Deleting_a_screen_asks_for_confirmation_first()
    {
        var (host, s) = await OwnerAsync();
        await using var _ = host;
        await s.App.Get<ScreenService>().AddAsync(s.OwnerPrincipal, s.Theater.Id, "South");
        var page = host.Render<ManageTheater>(p => p.Add(x => x.Id, s.Theater.Id));
        page.WaitForText("South");

        page.FindAll("button").Last(b => b.TextContent.Trim() == "Delete").Click();
        page.ClickButton("Confirm delete");

        page.WaitForAssertion(() => Assert.DoesNotContain("South", page.Text()));
        await using var db = s.App.Db();
        Assert.Equal(["North"], await db.Screens.Select(x => x.Name).ToListAsync());
    }

    [Fact]
    public async Task Moving_a_screen_reorders_them()
    {
        var (host, s) = await OwnerAsync();
        await using var _ = host;
        await s.App.Get<ScreenService>().AddAsync(s.OwnerPrincipal, s.Theater.Id, "South");
        var page = host.Render<ManageTheater>(p => p.Add(x => x.Id, s.Theater.Id));
        page.WaitForText("South");

        page.FindAll("button[aria-label='Move up']").Last().Click();

        await using var db = s.App.Db();
        page.WaitForAssertion(() => Assert.True(page.Text().IndexOf("South", StringComparison.Ordinal) < page.Text().IndexOf("North", StringComparison.Ordinal)));
        Assert.Equal(["South", "North"], await db.Screens.OrderBy(x => x.SortOrder).Select(x => x.Name).ToListAsync());
    }

    [Fact]
    public async Task Saving_the_profile_updates_the_theater()
    {
        var (host, s) = await OwnerAsync();
        await using var _ = host;
        var page = host.Render<ManageTheater>(p => p.Add(x => x.Id, s.Theater.Id));
        page.WaitForText("Save profile");

        page.SetField("City", "Austin");
        page.Find("form").Submit();

        page.WaitForText("Profile saved.");
        await using var db = s.App.Db();
        Assert.Equal("Austin", (await db.Theaters.SingleAsync()).City);
    }

    [Fact]
    public async Task Free_admission_settings_are_saved()
    {
        var (host, s) = await OwnerAsync();
        await using var _ = host;
        var page = host.Render<ManageTheater>(p => p.Add(x => x.Id, s.Theater.Id));
        page.WaitForText("Allow free admission");

        page.Check("Allow free admission");
        page.WaitForText("Require a reason");
        page.Check("Require a reason", false);
        page.SetField("Max per showing (blank = no limit)", "4");
        page.ClickButton("Save free admission settings");

        page.WaitForText("Free admission settings saved.");
        await using var db = s.App.Db();
        var saved = await db.Theaters.SingleAsync();
        Assert.Equal((true, false, 4), (saved.FreeAdmissionEnabled, saved.FreeAdmissionRequiresReason, saved.FreeAdmissionMaxPerShowing));
    }

    [Fact]
    public async Task An_employee_without_roles_is_told_to_ask_for_permissions()
    {
        var s = await TicketSalesTests.SetUpAsync();
        var employee = await s.App.CreateUserAsync("staff@example.com", s.Theater.Id);
        await using var host = new PageHost(s.App).SignIn(employee);

        var page = host.Render<ManageTheater>(p => p.Add(x => x.Id, s.Theater.Id));

        page.WaitForText("You don't have any permissions at this theater yet.");
        Assert.DoesNotContain("Save profile", page.Markup);
        Assert.DoesNotContain("Add screen", page.Markup);
        Assert.DoesNotContain("Standard plan", page.Markup); // the subscription is the owner's business
    }

    [Fact]
    public async Task A_demo_theater_shows_its_setup_checklist_and_the_owner_can_ask_to_go_live()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.Get<OnboardingService>().CreateDemoTheaterAsync(Principals.For(owner),
            new NewTheaterInput("Starlight Drive-In", "Austin", "TX", "America/Chicago", 1, true, null));
        await using var host = new PageHost(app).SignIn(owner);
        var page = host.Render<ManageTheater>(p => p.Add(x => x.Id, theater.Id));
        page.WaitForText("Set up your theater");
        Assert.Contains("required steps done", page.Text());

        page.ClickButton("Ask to go live");
        page.WaitForAssertion(() => Assert.Contains("mud-alert-text-error", page.Markup));

        page.Check("I agree to the Standard plan");
        page.ClickButton("Ask to go live");

        page.WaitForText("You asked to go live");
        await using var db = app.Db();
        Assert.NotNull((await db.Theaters.SingleAsync()).GoLiveRequestedAt);
    }
}
