using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

public sealed record UserSummary(
    string Id, string Email, string? DisplayName, bool EmailConfirmed, bool IsAdmin, bool IsLockedOut,
    string? EmployeeTheaterName, List<string> OwnedTheaters, List<string> Logins, bool HasPassword, DateTimeOffset CreatedAt);

// Site-admin user management. Each call runs in its own scope (see InvitationService).
public sealed class UserAdminService(IServiceScopeFactory scopeFactory, IEmailSender<ApplicationUser> identityEmail)
{
    public async Task<List<UserSummary>> ListAsync(ClaimsPrincipal actor, string? search)
    {
        Guard.RequireAdmin(actor);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var query = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(u => u.NormalizedEmail!.Contains(term)
                || (u.DisplayName != null && u.DisplayName.ToUpper().Contains(term)));
        }

        var adminRoleId = await db.Roles.Where(r => r.Name == Roles.Admin).Select(r => r.Id).FirstOrDefaultAsync();
        var now = DateTimeOffset.UtcNow;
        var rows = await query
            .OrderBy(u => u.Email)
            .Take(500)
            .Select(u => new
            {
                u.Id, u.Email, u.DisplayName, u.EmailConfirmed, u.CreatedAt,
                HasPassword = u.PasswordHash != null,
                IsLockedOut = u.LockoutEnd != null && u.LockoutEnd > now,
                IsAdmin = db.UserRoles.Any(r => r.UserId == u.Id && r.RoleId == adminRoleId),
                Employer = u.EmployeeTheater != null ? u.EmployeeTheater.Name : null,
                Owned = u.OwnedTheaters.OrderBy(t => t.Name).Select(t => t.Name).ToList(),
                Logins = db.UserLogins.Where(l => l.UserId == u.Id).Select(l => l.ProviderDisplayName ?? l.LoginProvider).ToList(),
            })
            .ToListAsync();

        return rows.Select(r => new UserSummary(r.Id, r.Email ?? "", r.DisplayName, r.EmailConfirmed, r.IsAdmin,
            r.IsLockedOut, r.Employer, r.Owned, r.Logins, r.HasPassword, r.CreatedAt)).ToList();
    }

    // Creates a confirmed account with no password and emails a link to set one.
    public async Task CreateAsync(ClaimsPrincipal actor, string email, string? displayName, bool makeAdmin, string baseUri)
    {
        Guard.RequireAdmin(actor);
        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var address = Guard.NormalizeEmail(email);
        if (await users.FindByEmailAsync(address) is not null)
            throw new AppValidationException($"{address} already has an account.");

        var user = new ApplicationUser
        {
            UserName = address,
            Email = address,
            EmailConfirmed = true,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
        };
        Check(await users.CreateAsync(user));
        if (makeAdmin)
            Check(await users.AddToRoleAsync(user, Roles.Admin));

        var token = await users.GeneratePasswordResetTokenAsync(user);
        await identityEmail.SendPasswordResetLinkAsync(user, address,
            System.Net.WebUtility.HtmlEncode(AccountLinks.PasswordReset(baseUri, token)));
    }

    public async Task UpdateDisplayNameAsync(ClaimsPrincipal actor, string userId, string? displayName)
    {
        Guard.RequireAdmin(actor);
        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await LoadAsync(users, userId);
        user.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        Check(await users.UpdateAsync(user));
    }

    public async Task SetAdminAsync(ClaimsPrincipal actor, string userId, bool isAdmin)
    {
        Guard.RequireAdmin(actor);
        if (!isAdmin && userId == actor.GetUserId())
            throw new AppValidationException("You can't remove your own admin access.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await LoadAsync(users, userId);
        if (isAdmin && user.EmployeeTheaterId is not null)
            throw new AppValidationException("Employee accounts can't be admins.");

        var already = await users.IsInRoleAsync(user, Roles.Admin);
        if (isAdmin && !already)
            Check(await users.AddToRoleAsync(user, Roles.Admin));
        else if (!isAdmin && already)
            Check(await users.RemoveFromRoleAsync(user, Roles.Admin));
        // Force the user's existing sessions to pick up the change on next validation.
        await users.UpdateSecurityStampAsync(user);
    }

    public async Task SetLockedAsync(ClaimsPrincipal actor, string userId, bool locked)
    {
        Guard.RequireAdmin(actor);
        if (locked && userId == actor.GetUserId())
            throw new AppValidationException("You can't lock your own account.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await LoadAsync(users, userId);
        Check(await users.SetLockoutEnabledAsync(user, true));
        Check(await users.SetLockoutEndDateAsync(user, locked ? DateTimeOffset.MaxValue : null));
        if (!locked)
            await users.ResetAccessFailedCountAsync(user);
        await users.UpdateSecurityStampAsync(user);
    }

    public async Task SendPasswordResetAsync(ClaimsPrincipal actor, string userId, string baseUri)
    {
        Guard.RequireAdmin(actor);
        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await LoadAsync(users, userId);
        var token = await users.GeneratePasswordResetTokenAsync(user);
        await identityEmail.SendPasswordResetLinkAsync(user, user.Email!,
            System.Net.WebUtility.HtmlEncode(AccountLinks.PasswordReset(baseUri, token)));
    }

    public async Task DeleteAsync(ClaimsPrincipal actor, string userId)
    {
        Guard.RequireAdmin(actor);
        if (userId == actor.GetUserId())
            throw new AppValidationException("You can't delete your own account here.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await LoadAsync(users, userId);

        var owned = await db.Theaters.Where(t => t.OwnerId == userId).Select(t => t.Name).ToListAsync();
        if (owned.Count > 0)
            throw new AppValidationException($"Reassign the theaters this user owns first: {string.Join(", ", owned)}.");

        Check(await users.DeleteAsync(user));
    }

    private static async Task<ApplicationUser> LoadAsync(UserManager<ApplicationUser> users, string userId) =>
        await users.FindByIdAsync(userId) ?? throw new NotFoundException("User not found.");

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new AppValidationException(string.Join(" ", result.Errors.Select(e => e.Description)));
    }
}
