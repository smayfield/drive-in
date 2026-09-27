using System.Security.Claims;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DriveIn.Web.Tests;

public class InvitationServiceTests
{
    [Fact]
    public async Task Owner_invites_employee_who_accepts_with_password()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var invitations = app.Get<InvitationService>();

        var outcome = await invitations.InviteAsync(Principals.For(owner), theater.Id, " New.Hire@Example.com ", InvitationKind.Employee, TestApp.BaseUri);

        Assert.Equal(InviteOutcome.Sent, outcome);
        Assert.Equal("new.hire@example.com", app.Email.Sent.Single().To);
        var token = app.Email.LastInviteToken();

        var user = await invitations.AcceptWithPasswordAsync(token, "New Hire", "Password123!");

        Assert.Equal(theater.Id, user.EmployeeTheaterId);
        Assert.True(user.EmailConfirmed);
        Assert.Equal("New Hire", user.DisplayName);
        Assert.Null(await invitations.FindPendingAsync(token));
        await Assert.ThrowsAsync<AppValidationException>(() => invitations.AcceptWithPasswordAsync(token, null, "Password123!"));
    }

    [Fact]
    public async Task Invite_expires_after_seven_days()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var invitations = app.Get<InvitationService>();
        await invitations.InviteAsync(Principals.For(owner), theater.Id, "a@example.com", InvitationKind.Employee, TestApp.BaseUri);
        var token = app.Email.LastInviteToken();

        app.Time.Advance(TimeSpan.FromDays(6));
        Assert.NotNull(await invitations.FindPendingAsync(token));

        app.Time.Advance(TimeSpan.FromDays(1));
        Assert.Null(await invitations.FindPendingAsync(token));
        await Assert.ThrowsAsync<AppValidationException>(() => invitations.AcceptWithPasswordAsync(token, null, "Password123!"));
    }

    [Fact]
    public async Task Resend_replaces_the_old_link()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var invitations = app.Get<InvitationService>();
        await invitations.InviteAsync(Principals.For(owner), theater.Id, "a@example.com", InvitationKind.Employee, TestApp.BaseUri);
        var oldToken = app.Email.LastInviteToken();
        var invite = (await invitations.ListPendingAsync(Principals.For(owner), theater.Id)).Single();

        await invitations.ResendAsync(Principals.For(owner), invite.Id, TestApp.BaseUri);

        Assert.Null(await invitations.FindPendingAsync(oldToken));
        Assert.NotNull(await invitations.FindPendingAsync(app.Email.LastInviteToken()));
    }

    [Fact]
    public async Task Employee_invite_to_existing_account_is_rejected()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        await app.CreateUserAsync("taken@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);

        await Assert.ThrowsAsync<AppValidationException>(() => app.Get<InvitationService>()
            .InviteAsync(Principals.For(owner), theater.Id, "TAKEN@example.com", InvitationKind.Employee, TestApp.BaseUri));
    }

    [Fact]
    public async Task Employees_cannot_invite_and_owners_cannot_assign_owners()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var employee = await app.CreateUserAsync("emp@example.com", employeeTheaterId: theater.Id);
        var invitations = app.Get<InvitationService>();

        await Assert.ThrowsAsync<AccessDeniedException>(() => invitations
            .InviteAsync(Principals.For(employee), theater.Id, "x@example.com", InvitationKind.Employee, TestApp.BaseUri));
        await Assert.ThrowsAsync<AccessDeniedException>(() => invitations
            .InviteAsync(Principals.For(owner), theater.Id, "x@example.com", InvitationKind.Owner, TestApp.BaseUri));
        Assert.Empty(app.Email.Sent);
    }

    [Fact]
    public async Task Owner_invite_to_existing_account_assigns_immediately()
    {
        await using var app = new TestApp();
        var admin = await app.CreateUserAsync("admin@example.com", admin: true);
        var existing = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight");

        var outcome = await app.Get<InvitationService>()
            .InviteAsync(Principals.For(admin, admin: true), theater.Id, "owner@example.com", InvitationKind.Owner, TestApp.BaseUri);

        Assert.Equal(InviteOutcome.AssignedExistingOwner, outcome);
        await using var db = app.Db();
        Assert.Equal(existing.Id, (await db.Theaters.SingleAsync()).OwnerId);
        Assert.Empty(await db.Invitations.ToListAsync());
    }

    [Fact]
    public async Task Owner_invite_to_employee_account_is_rejected()
    {
        await using var app = new TestApp();
        var admin = await app.CreateUserAsync("admin@example.com", admin: true);
        var other = await app.CreateTheaterAsync("Other");
        await app.CreateUserAsync("emp@example.com", employeeTheaterId: other.Id);
        var theater = await app.CreateTheaterAsync("Starlight");

        await Assert.ThrowsAsync<AppValidationException>(() => app.Get<InvitationService>()
            .InviteAsync(Principals.For(admin, admin: true), theater.Id, "emp@example.com", InvitationKind.Owner, TestApp.BaseUri));
    }

    [Fact]
    public async Task New_owner_accepts_and_owns_the_theater()
    {
        await using var app = new TestApp();
        var admin = await app.CreateUserAsync("admin@example.com", admin: true);
        var theater = await app.CreateTheaterAsync("Starlight");
        var invitations = app.Get<InvitationService>();
        await invitations.InviteAsync(Principals.For(admin, admin: true), theater.Id, "boss@example.com", InvitationKind.Owner, TestApp.BaseUri);

        var owner = await invitations.AcceptWithPasswordAsync(app.Email.LastInviteToken(), null, "Password123!");

        Assert.Null(owner.EmployeeTheaterId);
        await using var db = app.Db();
        Assert.Equal(owner.Id, (await db.Theaters.SingleAsync()).OwnerId);
    }

    [Fact]
    public async Task External_login_must_match_the_invited_email()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var invitations = app.Get<InvitationService>();
        await invitations.InviteAsync(Principals.For(owner), theater.Id, "hire@example.com", InvitationKind.Employee, TestApp.BaseUri);
        var token = app.Email.LastInviteToken();

        await Assert.ThrowsAsync<AppValidationException>(() =>
            invitations.AcceptWithExternalLoginAsync(token, GoogleLogin("someone.else@gmail.com")));

        var user = await invitations.AcceptWithExternalLoginAsync(token, GoogleLogin("Hire@Example.com"));

        Assert.Equal(theater.Id, user.EmployeeTheaterId);
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Single(await users.GetLoginsAsync((await users.FindByIdAsync(user.Id))!));
    }

    [Fact]
    public async Task Existing_user_accepts_owner_invite_only_for_their_own_email()
    {
        await using var app = new TestApp();
        var admin = await app.CreateUserAsync("admin@example.com", admin: true);
        var theater = await app.CreateTheaterAsync("Starlight");
        var invitations = app.Get<InvitationService>();
        await invitations.InviteAsync(Principals.For(admin, admin: true), theater.Id, "late@example.com", InvitationKind.Owner, TestApp.BaseUri);
        var token = app.Email.LastInviteToken();
        // Registered after the invite went out.
        var late = await app.CreateUserAsync("late@example.com");
        var stranger = await app.CreateUserAsync("stranger@example.com");

        await Assert.ThrowsAsync<AppValidationException>(() => invitations.AcceptAsExistingUserAsync(token, Principals.For(stranger)));
        await invitations.AcceptAsExistingUserAsync(token, Principals.For(late));

        await using var db = app.Db();
        Assert.Equal(late.Id, (await db.Theaters.SingleAsync()).OwnerId);
    }

    private static ExternalLoginInfo GoogleLogin(string email)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Email, email),
            new Claim("email_verified", "true"),
            new Claim(ClaimTypes.Name, "Google User"),
        ], "Google"));
        return new ExternalLoginInfo(principal, "Google", "google-sub-" + email, "Google");
    }
}
