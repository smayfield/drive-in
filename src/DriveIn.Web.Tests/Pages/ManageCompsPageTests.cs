using DriveIn.Web.Components.Pages.Manage;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests.Pages;

public class ManageCompsPageTests
{
    private static async Task<TicketSalesTests.Setup> SetUpAsync(bool enabled = true, bool requiresApproval = false)
    {
        var s = await TicketSalesTests.SetUpAsync();
        if (enabled)
            await s.App.Get<TheaterService>().UpdateFreeAdmissionSettingsAsync(s.OwnerPrincipal, s.Theater.Id, true, requiresApproval,
                false, null, null);
        return s;
    }

    private static IRenderedComponent<ManageComps> Open(PageHost host, TicketSalesTests.Setup s, string waitFor = "Log")
    {
        var page = host.Render<ManageComps>(p => p.Add(x => x.Id, s.Theater.Id));
        page.WaitForText(waitFor);
        return page;
    }

    private static void PickSpot(IRenderedComponent<ManageComps> page, string label)
    {
        page.Find("button.gate-showing").Click();
        page.WaitForText("Pick a green spot for the guest's car.");
        page.Find($"g[aria-label='Spot {label}: available']").Click();
        page.WaitForText($"Spot {label}");
    }

    [Fact]
    public async Task The_owner_gives_a_guest_free_admission_and_it_is_logged()
    {
        var s = await SetUpAsync();
        await using var host = new PageHost(s.App).SignIn(s.Owner);
        var page = Open(host, s);
        Assert.Contains("Nothing yet.", page.Text());

        PickSpot(page, "A2");
        page.SetField("Guest name", "Aunt May");
        page.SetField("Reason (optional)", "Family");
        page.ClickButton("Give free admission");

        page.WaitForText("Free admission given to Aunt May.");
        var text = page.Text();
        Assert.Contains("Aunt May: spot A2 on North", text);
        Assert.Contains("My free admissions", text);
        Assert.Contains("Given", text);
        await using var db = s.App.Db();
        Assert.True((await db.Tickets.SingleAsync()).IsComp);
    }

    [Fact]
    public async Task Picking_a_spot_can_be_cancelled()
    {
        var s = await SetUpAsync();
        await using var host = new PageHost(s.App).SignIn(s.Owner);
        var page = Open(host, s);
        PickSpot(page, "A2");

        page.ClickButton("Cancel");

        page.WaitForText("Pick a green spot for the guest's car.");
    }

    [Fact]
    public async Task A_request_waits_for_a_manager_who_can_approve_or_deny_it()
    {
        var s = await SetUpAsync(requiresApproval: true);
        var staff = await s.App.CreateUserAsync("staff@example.com", s.Theater.Id);
        await s.App.GrantAsync(staff, OfferFreeAdmission);
        await s.Sales.OfferFreeAdmissionAsync(Principals.For(staff), s.Showing.Id, 1, 1, "Uncle Ben", null, null, TestApp.BaseUri);
        await s.Sales.OfferFreeAdmissionAsync(Principals.For(staff), s.Showing.Id, 1, 2, "Cousin Flash", null, null, TestApp.BaseUri);
        await using var host = new PageHost(s.App).SignIn(s.Owner);
        var page = Open(host, s, "Waiting for approval");

        page.ClickButton("Approve");
        page.WaitForText("Approved: Uncle Ben, spot A1");
        page.ClickButton("Deny");

        page.WaitForText("Denied. The spot is back on sale.");
        Assert.DoesNotContain("Waiting for approval", page.Text());
    }

    [Fact]
    public async Task Staff_who_need_approval_request_a_spot_and_can_withdraw_it()
    {
        var s = await SetUpAsync(requiresApproval: true);
        var staff = await s.App.CreateUserAsync("staff@example.com", s.Theater.Id);
        await s.App.GrantAsync(staff, OfferFreeAdmission);
        await using var host = new PageHost(s.App).SignIn(staff);
        var page = Open(host, s, "Reserve a free spot");
        Assert.Contains("A manager has to approve it", page.Text());
        Assert.DoesNotContain("Log", page.Text());

        PickSpot(page, "B1");
        page.SetField("Guest name", "Uncle Ben");
        page.ClickButton("Request free admission");
        page.WaitForText("Requested a free spot for Uncle Ben.");
        Assert.Contains("Waiting for approval", page.Text());

        page.ClickButton("Withdraw");

        page.WaitForText("Withdrawn. The spot is back on sale.");
        await using var db = s.App.Db();
        Assert.Empty(await db.Tickets.ToListAsync());
    }

    [Fact]
    public async Task When_free_admission_is_off_the_owner_is_told_where_to_turn_it_on()
    {
        var s = await SetUpAsync(enabled: false);
        await using var host = new PageHost(s.App).SignIn(s.Owner);

        var page = Open(host, s);

        Assert.Contains("Turn it on under Free admission on the Overview page.", page.Text());
        Assert.DoesNotContain("Reserve a free spot", page.Text());
    }

    [Fact]
    public async Task Staff_without_free_admission_rights_are_turned_away()
    {
        var s = await SetUpAsync();
        var staff = await s.App.CreateUserAsync("staff@example.com", s.Theater.Id);
        await s.App.GrantAsync(staff, SellAtGate);
        await using var host = new PageHost(s.App).SignIn(staff);

        host.Render<ManageComps>(p => p.Add(x => x.Id, s.Theater.Id));

        Assert.EndsWith("Account/AccessDenied", host.Nav.Uri);
    }
}
