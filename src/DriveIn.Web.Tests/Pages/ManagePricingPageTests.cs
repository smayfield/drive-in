using DriveIn.Web.Components.Pages.Manage;
using DriveIn.Web.Data;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests.Pages;

public class ManagePricingPageTests
{
    private static async Task<(PageHost Host, TicketSalesTests.Setup S, IRenderedComponent<ManagePricing> Page)> OpenAsync()
    {
        var s = await TicketSalesTests.SetUpAsync();
        var host = new PageHost(s.App).SignIn(s.Owner);
        var page = host.Render<ManagePricing>(p => p.Add(x => x.Id, s.Theater.Id));
        page.WaitForText("Price schedules");
        return (host, s, page);
    }

    [Fact]
    public async Task Schedules_and_add_ons_are_listed()
    {
        var (host, _, page) = await OpenAsync();
        await using var _ = host;

        var text = page.Text();
        Assert.Contains("Standard Default", text);
        Assert.Contains("One occupant $10.00", text);
        Assert.Contains("Car load $25.00", text);
        Assert.Contains("Outside food", text);
        Assert.Contains("Discount (%)", text);
    }

    [Fact]
    public async Task A_new_schedule_can_start_as_a_copy_and_be_edited()
    {
        var (host, s, page) = await OpenAsync();
        await using var _ = host;

        page.SetField("New schedule", "3D");
        host.Select(page, "Start from", "Copy of Standard");
        page.ClickButton("Create");
        page.WaitForText("Add option");
        page.ClickButton("Remove option"); // One occupant
        page.SetField("Price", "12", index: 0);
        page.ClickButton("Add option");
        page.SetField("Option name", "Truck", index: 1);
        page.SetField("Price", "30", index: 1);
        page.ClickButton("Save");

        page.WaitForText("Price schedule saved.");
        await using var db = s.App.Db();
        var threeD = await db.PriceSchedules.Include(p => p.Options).SingleAsync(p => p.Name == "3D");
        Assert.Equal([("Car load", 12m), ("Truck", 30m)], threeD.Options.OrderBy(o => o.SortOrder).Select(o => (o.Name, o.Price)));
    }

    [Fact]
    public async Task Another_schedule_can_be_made_the_default_and_the_old_one_deleted()
    {
        var (host, s, page) = await OpenAsync();
        await using var _ = host;
        page.SetField("New schedule", "Bargain");
        page.ClickButton("Create");
        page.WaitForText("Add option");
        page.ClickButton("Cancel");

        page.ClickButton("Make default");
        page.WaitForText("Bargain is now the default.");
        page.ClickButton("Delete");
        page.ClickButton("Confirm delete");

        page.WaitForAssertion(() => Assert.DoesNotContain("Standard", page.Text()));
        await using var db = s.App.Db();
        Assert.Equal(["Bargain"], await db.PriceSchedules.Select(p => p.Name).ToListAsync());
    }

    [Fact]
    public async Task Add_ons_can_be_added_edited_and_deleted()
    {
        var (host, s, page) = await OpenAsync();
        await using var _ = host;

        page.SetField("Add-on name", "Student");
        host.Select(page, "Add-on type", "Discount (%)");
        page.SetField("Amount", "15");
        page.ClickButton("Add");
        page.WaitForText("Student");

        var row = page.FindAll("tr").Single(r => r.TextContent.Contains("Student"));
        row.QuerySelectorAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        page.SetField("Add-on name", "College student");
        page.ClickButton("Save");
        page.WaitForText("College student");

        page.FindAll("tr").Single(r => r.TextContent.Contains("College student"))
            .QuerySelectorAll("button").First(b => b.TextContent.Trim() == "Delete").Click();
        page.ClickButton("Confirm delete");

        page.WaitForAssertion(() => Assert.DoesNotContain("College student", page.Text()));
        await using var db = s.App.Db();
        Assert.Equal(3, await db.AddOns.CountAsync());
    }

    [Fact]
    public async Task Staff_who_cant_manage_pricing_only_see_it()
    {
        var s = await TicketSalesTests.SetUpAsync();
        var staff = await s.App.CreateUserAsync("staff@example.com", s.Theater.Id);
        await s.App.GrantAsync(staff, ViewReports);
        await using var host = new PageHost(s.App).SignIn(staff);

        var page = host.Render<ManagePricing>(p => p.Add(x => x.Id, s.Theater.Id));

        page.WaitForText("One occupant");
        Assert.DoesNotContain("Make default", page.Markup);
        Assert.DoesNotContain("New schedule", page.Markup);
        Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent.Trim() == "Edit");
    }
}
