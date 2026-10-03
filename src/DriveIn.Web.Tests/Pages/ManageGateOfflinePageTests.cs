using DriveIn.Web.Components.Pages.Manage;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests.Pages;

// The offline gate page's static shell; its script (wwwroot/gate-offline/) does the checking in.
public class ManageGateOfflinePageTests
{
    [Fact]
    public async Task Staff_who_admit_guests_get_the_shell_with_its_script_and_endpoints()
    {
        await using var s = await TicketSalesTests.SetUpAsync();
        var staff = await s.App.CreateUserAsync("gate@example.com", s.Theater.Id);
        await s.App.GrantAsync(staff, AdmitGuests);
        await using var host = new PageHost(s.App).UseRequest().SignIn(staff);

        var page = host.Render<ManageGateOffline>(p => p.Add(x => x.Id, s.Theater.Id));

        var shell = page.Find("#gate-offline");
        Assert.Equal($"/manage/{s.Theater.Id}/gate/offline", shell.GetAttribute("data-base"));
        Assert.Equal(s.Theater.Id.ToString(), shell.GetAttribute("data-theater-id"));
        Assert.Contains($"{s.Theater.Name}: gate", page.Find("h1").TextContent);
        Assert.NotNull(page.Find("input#gate-code"));
        Assert.NotNull(page.Find("[data-ref='conflicts-section'][hidden]"));
        Assert.Contains("gate-offline/gate-offline.js", page.Find("script[type='module']").GetAttribute("src"));
        Assert.Equal($"manage/{s.Theater.Id}/gate", page.Find("a.btn").GetAttribute("href"));
    }

    [Fact]
    public async Task Staff_without_admit_guests_are_told_and_get_no_script()
    {
        await using var s = await TicketSalesTests.SetUpAsync();
        var staff = await s.App.CreateUserAsync("seller@example.com", s.Theater.Id);
        await s.App.GrantAsync(staff, SellAtGate);
        await using var host = new PageHost(s.App).UseRequest().SignIn(staff);

        var page = host.Render<ManageGateOffline>(p => p.Add(x => x.Id, s.Theater.Id));

        Assert.Contains("needs the \"Admit guests\" action", page.Text());
        Assert.Empty(page.FindAll("#gate-offline"));
        Assert.Empty(page.FindAll("script"));
    }

    [Fact]
    public async Task Someone_from_another_theater_sees_not_found()
    {
        await using var s = await TicketSalesTests.SetUpAsync();
        var outsider = await s.App.CreateUserAsync("outsider@example.com");
        await using var host = new PageHost(s.App).UseRequest().SignIn(outsider);

        var page = host.Render<ManageGateOffline>(p => p.Add(x => x.Id, s.Theater.Id));

        Assert.Contains("Theater not found", page.Text());
        Assert.Empty(page.FindAll("#gate-offline"));
    }
}
