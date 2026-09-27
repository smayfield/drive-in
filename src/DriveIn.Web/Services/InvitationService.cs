using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

public enum InviteOutcome
{
    // An invite email with a sign-up link was sent.
    Sent,
    // Owner invite for someone who already has an account: they were made owner right away.
    AssignedExistingOwner,
}

// Owner and employee invitations. Each operation runs in its own DI scope so UserManager
// and our tables share one DbContext (and one transaction) without holding a long-lived
// context inside a Blazor circuit.
public sealed class InvitationService(
    IServiceScopeFactory scopeFactory,
    IAuthorizationService auth,
    IAppEmailSender email,
    TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public async Task<InviteOutcome> InviteAsync(
        ClaimsPrincipal actor, int theaterId, string emailAddress, InvitationKind kind, string baseUri)
    {
        var address = Guard.NormalizeEmail(emailAddress);
        await using var scope = scopeFactory.CreateAsyncScope();
        var (db, users) = Resolve(scope);

        var theater = await db.Theaters.FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        await RequireCanManageAsync(actor, theater, kind);

        var existing = await users.FindByEmailAsync(address);
        if (kind == InvitationKind.Owner && existing is not null)
        {
            if (existing.EmployeeTheaterId is not null)
                throw new AppValidationException($"{address} is an employee account and can't own a theater.");
            theater.OwnerId = existing.Id;
            theater.UpdatedAt = time.GetUtcNow();
            await db.SaveChangesAsync();
            await email.SendAsync(address, $"You're now the owner of {theater.Name}",
                $"<p>You've been made the owner of <strong>{WebUtility.HtmlEncode(theater.Name)}</strong> on Drive-In Online.</p>" +
                $"<p><a href=\"{WebUtility.HtmlEncode(baseUri.TrimEnd('/') + "/manage/" + theater.Id)}\">Manage your theater</a></p>");
            return InviteOutcome.AssignedExistingOwner;
        }
        if (kind == InvitationKind.Employee && existing is not null)
            throw new AppValidationException(
                $"{address} already has an account. Employee accounts belong to a single theater, so invite a different email address.");

        // One pending invite per email/theater/kind: replace any earlier one.
        db.Invitations.RemoveRange(db.Invitations.Where(i =>
            i.TheaterId == theaterId && i.Email == address && i.Kind == kind && i.AcceptedAt == null));

        var token = NewToken();
        db.Invitations.Add(new Invitation
        {
            Email = address,
            TheaterId = theaterId,
            Kind = kind,
            TokenHash = Hash(token),
            InvitedById = actor.GetUserId(),
            CreatedAt = time.GetUtcNow(),
            ExpiresAt = time.GetUtcNow() + Lifetime,
        });
        await db.SaveChangesAsync();
        await SendInviteEmailAsync(address, theater.Name, kind, AccountLinks.Invite(baseUri, token));
        return InviteOutcome.Sent;
    }

    public async Task<List<Invitation>> ListPendingAsync(ClaimsPrincipal actor, int theaterId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var (db, _) = Resolve(scope);
        var theater = await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(actor, theater, TheaterOperations.ManageEmployees);

        var query = db.Invitations.AsNoTracking().Where(i => i.TheaterId == theaterId && i.AcceptedAt == null);
        if (!actor.IsAdmin())
            query = query.Where(i => i.Kind == InvitationKind.Employee);
        return await query.OrderByDescending(i => i.CreatedAt).ToListAsync();
    }

    public async Task RevokeAsync(ClaimsPrincipal actor, int invitationId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var (db, _) = Resolve(scope);
        var invite = await LoadForActorAsync(db, actor, invitationId);
        db.Invitations.Remove(invite);
        await db.SaveChangesAsync();
    }

    // Issues a fresh link (the old one stops working) and restarts the expiry clock.
    public async Task ResendAsync(ClaimsPrincipal actor, int invitationId, string baseUri)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var (db, _) = Resolve(scope);
        var invite = await LoadForActorAsync(db, actor, invitationId);
        var token = NewToken();
        invite.TokenHash = Hash(token);
        invite.ExpiresAt = time.GetUtcNow() + Lifetime;
        await db.SaveChangesAsync();
        await SendInviteEmailAsync(invite.Email, invite.Theater!.Name, invite.Kind, AccountLinks.Invite(baseUri, token));
    }

    // Null when the token is unknown, already used, or expired.
    public async Task<Invitation?> FindPendingAsync(string token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var (db, _) = Resolve(scope);
        return await FindPendingAsync(db, token, tracking: false);
    }

    // New account with a password. Following the emailed link proves the address, so it's confirmed.
    public async Task<ApplicationUser> AcceptWithPasswordAsync(string token, string? displayName, string password)
    {
        return await AcceptNewAccountAsync(token, displayName, async (users, user) => await users.CreateAsync(user, password));
    }

    // New account signing in with an external provider (Google); its email must match the invite.
    public async Task<ApplicationUser> AcceptWithExternalLoginAsync(string token, ExternalLoginInfo info)
    {
        var externalEmail = info.Principal.FindFirstValue(ClaimTypes.Email);
        var verified = info.Principal.FindFirstValue("email_verified");
        return await AcceptNewAccountAsync(token, info.Principal.FindFirstValue(ClaimTypes.Name), async (users, user) =>
        {
            if (externalEmail is null || !string.Equals(Guard.NormalizeEmail(externalEmail), user.Email, StringComparison.Ordinal))
                throw new AppValidationException(
                    $"This invitation was sent to {user.Email}, but you signed in to {info.ProviderDisplayName} as {externalEmail ?? "an account without an email"}.");
            if (string.Equals(verified, "false", StringComparison.OrdinalIgnoreCase))
                throw new AppValidationException($"Your {info.ProviderDisplayName} email address isn't verified.");

            var result = await users.CreateAsync(user);
            return result.Succeeded ? await users.AddLoginAsync(user, info) : result;
        });
    }

    // Owner invite accepted by someone who already has (and is signed in to) an account for that email.
    public async Task AcceptAsExistingUserAsync(string token, ClaimsPrincipal signedIn)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var (db, users) = Resolve(scope);
        await using var tx = await db.Database.BeginTransactionAsync();

        var invite = await FindPendingAsync(db, token, tracking: true)
            ?? throw new AppValidationException("This invitation is no longer valid.");
        var user = await users.FindByIdAsync(Guard.RequireUserId(signedIn))
            ?? throw new AccessDeniedException("You must be signed in.");

        if (!string.Equals(Guard.NormalizeEmail(user.Email ?? ""), invite.Email, StringComparison.Ordinal))
            throw new AppValidationException($"This invitation was sent to {invite.Email}. Sign out and sign in with that account.");
        if (invite.Kind != InvitationKind.Owner)
            throw new AppValidationException("Employee invitations create a new account and can't be accepted by an existing one.");
        if (user.EmployeeTheaterId is not null)
            throw new AppValidationException("Employee accounts can't own a theater.");

        invite.Theater!.OwnerId = user.Id;
        invite.Theater.UpdatedAt = time.GetUtcNow();
        invite.AcceptedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private async Task<ApplicationUser> AcceptNewAccountAsync(
        string token, string? displayName, Func<UserManager<ApplicationUser>, ApplicationUser, Task<IdentityResult>> create)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var (db, users) = Resolve(scope);
        await using var tx = await db.Database.BeginTransactionAsync();

        var invite = await FindPendingAsync(db, token, tracking: true)
            ?? throw new AppValidationException("This invitation is no longer valid.");
        if (await users.FindByEmailAsync(invite.Email) is not null)
            throw new AppValidationException(invite.Kind == InvitationKind.Owner
                ? "An account already exists for this email. Sign in, then open the invitation link again."
                : "An account already exists for this email.");

        var user = new ApplicationUser
        {
            UserName = invite.Email,
            Email = invite.Email,
            EmailConfirmed = true,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            EmployeeTheaterId = invite.Kind == InvitationKind.Employee ? invite.TheaterId : null,
            CreatedAt = time.GetUtcNow(),
        };
        var result = await create(users, user);
        if (!result.Succeeded)
            throw new AppValidationException(string.Join(" ", result.Errors.Select(e => e.Description)));

        if (invite.Kind == InvitationKind.Owner)
        {
            invite.Theater!.OwnerId = user.Id;
            invite.Theater.UpdatedAt = time.GetUtcNow();
        }
        invite.AcceptedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return user;
    }

    private async Task<Invitation?> FindPendingAsync(ApplicationDbContext db, string token, bool tracking)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        var hash = Hash(token);
        var query = tracking ? db.Invitations : db.Invitations.AsNoTracking();
        var invite = await query.Include(i => i.Theater).FirstOrDefaultAsync(i => i.TokenHash == hash);
        if (invite is null || invite.AcceptedAt is not null || invite.ExpiresAt <= time.GetUtcNow())
            return null;
        return invite;
    }

    private async Task<Invitation> LoadForActorAsync(ApplicationDbContext db, ClaimsPrincipal actor, int invitationId)
    {
        var invite = await db.Invitations.Include(i => i.Theater).FirstOrDefaultAsync(i => i.Id == invitationId)
            ?? throw new NotFoundException("Invitation not found.");
        await RequireCanManageAsync(actor, invite.Theater!, invite.Kind);
        return invite;
    }

    // Only admins assign owners; owners (and admins) invite employees.
    private async Task RequireCanManageAsync(ClaimsPrincipal actor, Theater theater, InvitationKind kind)
    {
        if (kind == InvitationKind.Owner)
            Guard.RequireAdmin(actor);
        else
            await auth.RequireAsync(actor, theater, TheaterOperations.ManageEmployees);
    }

    private Task SendInviteEmailAsync(string to, string theaterName, InvitationKind kind, string link)
    {
        var role = kind == InvitationKind.Owner ? "the owner" : "an employee";
        var name = WebUtility.HtmlEncode(theaterName);
        return email.SendAsync(to, $"You're invited to {theaterName} on Drive-In Online",
            $"<p>You've been invited to join <strong>{name}</strong> on Drive-In Online as {role}.</p>" +
            $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">Accept the invitation</a></p>" +
            $"<p>This link expires in {Lifetime.Days} days.</p>");
    }

    private static (ApplicationDbContext, UserManager<ApplicationUser>) Resolve(AsyncServiceScope scope) =>
        (scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
         scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());

    private static string NewToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    internal static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
