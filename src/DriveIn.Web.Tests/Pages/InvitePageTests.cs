using System.Security.Claims;
using DriveIn.Web.Components.Pages;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Tests.Pages;

// Accepting an invitation on its (statically rendered) page.
public class InvitePageTests
{
    private sealed record Invited(TestApp App, ApplicationUser Owner, Theater Theater, string Token) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static async Task<Invited> InviteAsync(string email = "new@example.com", InvitationKind kind = InvitationKind.Employee)
    {
        var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var admin = await app.CreateUserAsync("admin@example.com", admin: true);
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var inviter = kind == InvitationKind.Owner ? Principals.For(admin, admin: true) : Principals.For(owner);
        await app.Get<InvitationService>().InviteAsync(inviter, theater.Id, email, kind, TestApp.BaseUri);
        return new Invited(app, owner, theater, Uri.UnescapeDataString(app.Email.LastInviteToken()));
    }

    private static IRenderedComponent<Invite> Open(PageHost host, string token) =>
        host.Render<Invite>(p => p.Add(x => x.Token, token));

    [Fact]
    public async Task A_new_employee_creates_their_account_and_is_signed_in()
    {
        await using var i = await InviteAsync();
        await using var host = new PageHost(i.App).UseRequest("POST");
        var page = Open(host, i.Token);
        Assert.Contains("You've been invited to Starlight as an employee. This invitation is for new@example.com.", page.Text());

        page.Find("[id='Input.DisplayName']").Change("Newbie");
        page.Find("[id='Input.Password']").Change("Password123!");
        page.Find("[id='Input.ConfirmPassword']").Change("Password123!");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith($"manage/{i.Theater.Id}", host.Nav.Uri));
        Assert.Contains(".AspNetCore.Identity.Application", host.ResponseCookies);
        await using var db = i.App.Db();
        var user = await db.Users.SingleAsync(u => u.Email == "new@example.com");
        Assert.Equal((i.Theater.Id, "Newbie"), (user.EmployeeTheaterId, user.DisplayName));
    }

    [Fact]
    public async Task Passwords_that_dont_match_are_refused()
    {
        await using var i = await InviteAsync();
        await using var host = new PageHost(i.App).UseRequest("POST");
        var page = Open(host, i.Token);

        page.Find("[id='Input.Password']").Change("Password123!");
        page.Find("[id='Input.ConfirmPassword']").Change("Different456!");
        page.Find("form").Submit();

        page.WaitForText("The passwords don't match.");
    }

    [Fact]
    public async Task A_password_the_service_refuses_is_explained()
    {
        await using var i = await InviteAsync();
        await using var host = new PageHost(i.App).UseRequest("POST");
        var page = Open(host, i.Token);

        page.Find("[id='Input.Password']").Change("password"); // long enough for the form, too weak for Identity
        page.Find("[id='Input.ConfirmPassword']").Change("password");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("alert-danger", page.Markup));
        Assert.DoesNotContain(".AspNetCore.Identity.Application", host.ResponseCookies);
    }

    [Fact]
    public async Task An_unknown_invitation_is_not_valid()
    {
        await using var i = await InviteAsync();
        await using var host = new PageHost(i.App).UseRequest();

        var page = Open(host, "nope");

        Assert.Contains("Invitation not valid", page.Text());
    }

    [Fact]
    public async Task Someone_signed_in_is_asked_to_sign_out_first()
    {
        await using var i = await InviteAsync();
        await using var host = new PageHost(i.App)
            .SignIn(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, "other@example.com")], "Test")))
            .UseRequest();

        var page = Open(host, i.Token);

        Assert.Contains("You're signed in as other@example.com. Employee invitations create a new account", page.Text());
        Assert.Contains("Sign out", page.Text());
    }

    [Fact]
    public async Task An_existing_account_accepts_an_owner_invitation()
    {
        await using var i = await InviteAsync("boss@example.com", InvitationKind.Owner);
        var boss = await i.App.CreateUserAsync("boss@example.com");
        var signedIn = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, boss.Id), new Claim(ClaimTypes.Email, "boss@example.com")], "Test"));
        await using var host = new PageHost(i.App).SignIn(signedIn).UseRequest("POST");
        var page = Open(host, i.Token);

        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith($"manage/{i.Theater.Id}", host.Nav.Uri));
        await using var db = i.App.Db();
        Assert.Equal(boss.Id, (await db.Theaters.SingleAsync()).OwnerId);
    }

    [Fact]
    public async Task An_owner_invitation_offers_signing_in_to_an_existing_account()
    {
        await using var i = await InviteAsync("boss@example.com", InvitationKind.Owner);
        await using var host = new PageHost(i.App).UseRequest();

        var page = Open(host, i.Token);

        Assert.Contains("as the owner", page.Text());
        Assert.Contains("Already have an account for boss@example.com?", page.Text());
    }
}
