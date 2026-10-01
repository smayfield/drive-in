using DriveIn.Web.Components.Pages.Admin;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Tests.Pages;

public class AdminTheaterPageTests
{
    private sealed record Admin(TestApp App, ApplicationUser User) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static async Task<Admin> AdminAsync()
    {
        var app = new TestApp();
        return new Admin(app, await app.CreateUserAsync("admin@example.com", admin: true));
    }

    // A demo theater whose owner has asked to go live.
    private static async Task<Theater> GoLiveRequestAsync(TestApp app)
    {
        var owner = await app.CreateUserAsync("owner@example.com");
        var onboarding = app.Get<OnboardingService>();
        var theater = await onboarding.CreateDemoTheaterAsync(Principals.For(owner),
            new NewTheaterInput("Starlight", "Austin", "TX", "America/Chicago", 1, true));
        await onboarding.RequestGoLiveAsync(Principals.For(owner), theater.Id, true, TestApp.BaseUri);
        return theater;
    }

    [Fact]
    public async Task Theaters_and_go_live_requests_are_listed_and_a_request_is_activated()
    {
        await using var a = await AdminAsync();
        await GoLiveRequestAsync(a.App);
        await using var host = new PageHost(a.App).SignIn(a.User, admin: true);
        var page = host.Render<AdminTheaters>();
        page.WaitForText("Go-live requests");
        Assert.Contains("Starlight Demo Wants to go live starlight Austin, TX owner@example.com", page.Text());

        page.ClickButton("Activate");

        page.WaitForText("Starlight is live. The owner has been emailed.");
        Assert.DoesNotContain("Go-live requests", page.Text());
        await using var db = a.App.Db();
        Assert.Equal(TheaterMode.Live, (await db.Theaters.SingleAsync()).Mode);
    }

    [Fact]
    public async Task A_go_live_request_is_declined_with_a_note()
    {
        await using var a = await AdminAsync();
        await GoLiveRequestAsync(a.App);
        await using var host = new PageHost(a.App).SignIn(a.User, admin: true);
        var page = host.Render<AdminTheaters>();
        page.WaitForText("Go-live requests");

        page.ClickButton("Decline…");
        page.SetField("Note to the owner", "Add showings first.");
        page.ClickButton("Decline and email owner");

        page.WaitForText("Declined Starlight's request. The owner has been emailed.");
        Assert.Contains(a.App.Email.Sent, m => m.To == "owner@example.com" && m.Body.Contains("Add showings first."));
    }

    [Fact]
    public async Task A_theater_is_deleted_after_confirming()
    {
        await using var a = await AdminAsync();
        var theater = await a.App.CreateTheaterAsync("Starlight");
        await a.App.CreateUserAsync("staff@example.com", theater.Id);
        await using var host = new PageHost(a.App).SignIn(a.User, admin: true);
        var page = host.Render<AdminTheaters>();
        page.WaitForText("Starlight");

        page.ClickButton("Delete");
        page.ClickButton("Cancel");
        page.ClickButton("Delete");
        page.ClickButton("Delete theater and 1 employee account");

        page.WaitForText("Deleted Starlight.");
        Assert.Contains("No theaters yet.", page.Text());
    }

    [Fact]
    public async Task Non_admins_are_turned_away()
    {
        await using var a = await AdminAsync();
        var user = await a.App.CreateUserAsync("someone@example.com");
        await using var host = new PageHost(a.App).SignIn(user);

        host.Render<AdminTheaters>();

        Assert.EndsWith("Account/AccessDenied", host.Nav.Uri);
    }

    [Fact]
    public async Task A_new_theater_is_created_and_opened()
    {
        await using var a = await AdminAsync();
        await using var host = new PageHost(a.App).SignIn(a.User, admin: true);
        var page = host.Render<AdminTheaterEdit>();
        page.WaitForText("Create theater");

        page.SetField("Name", "Moonlight");
        page.SetField("Slug (public URL)", "moonlight");
        page.SetField("Time zone", "America/Denver");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Matches(@"admin/theaters/\d+$", host.Nav.Uri));
        await using var db = a.App.Db();
        var created = await db.Theaters.SingleAsync();
        Assert.Equal(("Moonlight", "moonlight", "America/Denver"), (created.Name, created.Slug, created.TimeZone));
        Assert.EndsWith($"admin/theaters/{created.Id}", host.Nav.Uri);
    }

    [Fact]
    public async Task A_theater_is_edited_and_given_an_owner()
    {
        await using var a = await AdminAsync();
        var theater = await a.App.CreateTheaterAsync("Starlight");
        await a.App.CreateUserAsync("owner@example.com");
        await using var host = new PageHost(a.App).SignIn(a.User, admin: true);
        var page = host.Render<AdminTheaterEdit>(p => p.Add(x => x.Id, theater.Id));
        page.WaitForText("Current owner: none");
        Assert.Contains("No subscription: this theater isn't billed.", page.Text());

        page.SetField("City", "Austin");
        page.FindAll("form")[0].Submit();
        page.WaitForText("Saved.");

        page.Fields("Email").Single().Change("owner@example.com");
        page.FindAll("form")[1].Submit();

        page.WaitForText("owner@example.com is now the owner.");
        Assert.Contains("Current owner: owner@example.com", page.Text());
        await using var db = a.App.Db();
        Assert.Equal("Austin", (await db.Theaters.SingleAsync()).City);
    }

    [Fact]
    public async Task A_new_owner_without_an_account_is_invited()
    {
        await using var a = await AdminAsync();
        var theater = await a.App.CreateTheaterAsync("Starlight");
        await using var host = new PageHost(a.App).SignIn(a.User, admin: true);
        var page = host.Render<AdminTheaterEdit>(p => p.Add(x => x.Id, theater.Id));
        page.WaitForText("Current owner: none");

        page.FindAll("form")[1].Submit();
        page.WaitForText("Enter an email address.");
        page.Fields("Email").Single().Change("new@example.com");
        page.FindAll("form")[1].Submit();
        page.WaitForText("Invitation sent to new@example.com. They'll become the owner when they accept.");
        Assert.Contains("new@example.com Owner", page.Text());

        page.ClickButton("Resend");
        page.WaitForText("Invitation re-sent to new@example.com.");
        page.ClickButton("Revoke");

        page.WaitForText("Invitation to new@example.com revoked.");
        Assert.DoesNotContain("Pending invitations", page.Text());
    }

    [Fact]
    public async Task A_billed_theater_shows_its_subscription()
    {
        await using var b = await PageData.BilledTheaterAsync();
        await using var host = new PageHost(b.App).SignIn(b.AdminUser, admin: true);

        var page = host.Render<AdminTheaterEdit>(p => p.Add(x => x.Id, b.Theater.Id));

        page.WaitForText("Standard plan, $49.00 per screen, since Sep 1, 2026");
    }
}
