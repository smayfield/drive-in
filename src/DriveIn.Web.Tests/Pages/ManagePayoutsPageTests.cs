using DriveIn.Web.Authorization;
using DriveIn.Web.Components.Pages.Manage;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Tests.TicketSalesTests;

namespace DriveIn.Web.Tests.Pages;

public class ManagePayoutsPageTests
{
    [Fact]
    public async Task The_owner_starts_setup_and_is_sent_to_the_processor()
    {
        await using var s = await SetUpAsync();
        s.App.Payments.RequiresPayoutAccount = true;
        await using var host = new PageHost(s.App).SignIn(s.Owner);

        var page = host.Render<ManagePayouts>(p => p.Add(x => x.Id, s.Theater.Id));

        page.WaitForText("Not set up");
        Assert.Contains("can't take card payments online or at the gate", page.Text());
        page.ClickButton("Set up payouts");
        page.WaitForAssertion(() => Assert.Equal("https://connect.example.test/setup/acct_fake1", host.Nav.Uri));
    }

    [Fact]
    public async Task Coming_back_from_onboarding_checks_the_status()
    {
        await using var s = await SetUpAsync();
        await s.App.Get<PayoutService>().StartOnboardingAsync(s.OwnerPrincipal, s.Theater.Id, TestApp.BaseUri);
        s.App.Payouts.Accounts["acct_fake1"] = PayoutStatus.Enabled;
        await using var host = new PageHost(s.App).SignIn(s.Owner);
        host.Nav.NavigateTo($"manage/{s.Theater.Id}/payouts?done=1");

        var page = host.Render<ManagePayouts>(p => p.Add(x => x.Id, s.Theater.Id));

        page.WaitForText("Your payout account is ready.");
        Assert.Contains("Ready", page.Text());
        Assert.DoesNotContain("Continue setup", page.Text());
        await using var db = s.App.Db();
        Assert.Equal(PayoutStatus.Enabled, (await db.Theaters.SingleAsync(t => t.Id == s.Theater.Id)).PayoutStatus);
    }

    [Fact]
    public async Task Without_a_processor_that_pays_theaters_theres_nothing_to_do()
    {
        await using var s = await SetUpAsync();
        s.App.Payouts.IsAvailable = false;
        await using var host = new PageHost(s.App).SignIn(s.Owner);

        var page = host.Render<ManagePayouts>(p => p.Add(x => x.Id, s.Theater.Id));

        page.WaitForText("There's nothing to set up yet");
        Assert.DoesNotContain("Set up payouts", page.Text());
    }

    [Fact]
    public async Task The_owner_has_a_payouts_tab()
    {
        await using var s = await SetUpAsync();
        await using var host = new PageHost(s.App).SignIn(s.Owner);

        var page = host.Render<ManagePayouts>(p => p.Add(x => x.Id, s.Theater.Id));

        page.WaitForText("Payout account");
        Assert.Contains($"href=\"manage/{s.Theater.Id}/payouts\"", page.Markup);
    }

    [Fact]
    public async Task A_manager_without_ManagePayouts_is_turned_away()
    {
        await using var s = await SetUpAsync();
        var manager = await s.App.CreateUserAsync("manager@example.com", employeeTheaterId: s.Theater.Id);
        await s.App.GrantAsync(manager, DefaultTheaterRoles.All.Single(r => r.Name == "Manager").Permissions);
        await using var host = new PageHost(s.App).SignIn(manager);

        host.Render<ManagePayouts>(p => p.Add(x => x.Id, s.Theater.Id));

        Assert.EndsWith("Account/AccessDenied", host.Nav.Uri);
    }
}
