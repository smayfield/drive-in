using DriveIn.Web.Components.Pages.Manage;

namespace DriveIn.Web.Tests.Pages;

public class ManagePageTests
{
    [Fact]
    public async Task Manage_lists_the_theaters_a_person_owns()
    {
        var s = await TicketSalesTests.SetUpAsync();
        await using var host = new PageHost(s.App).SignIn(s.Owner);

        var page = host.Render<ManageIndex>();

        page.WaitForText("Starlight");
        Assert.Contains("Owner", page.Text());
        Assert.Contains($"manage/{s.Theater.Id}", page.Markup);
    }

    [Fact]
    public async Task The_lot_map_lists_each_screen_with_its_rows_and_spots()
    {
        var s = await TicketSalesTests.SetUpAsync();
        await using var host = new PageHost(s.App).SignIn(s.Owner);

        var page = host.Render<ManageLot>(p => p.Add(x => x.Id, s.Theater.Id));

        page.WaitForText("Lot map");
        Assert.Contains("1 screen · 7 spots", page.Text());
    }

    [Fact]
    public async Task A_stranger_is_sent_to_access_denied()
    {
        var s = await TicketSalesTests.SetUpAsync();
        var stranger = await s.App.CreateUserAsync("stranger@example.com");
        await using var host = new PageHost(s.App).SignIn(stranger);

        host.Render<ManageLot>(p => p.Add(x => x.Id, s.Theater.Id));

        Assert.EndsWith("Account/AccessDenied", host.Nav.Uri);
    }
}
