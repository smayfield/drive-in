using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

public sealed record RoleRef(int Id, string Name);

public sealed record EmployeeSummary(
    string Id, string Email, string? DisplayName, bool HasPassword, bool IsLockedOut, DateTimeOffset CreatedAt,
    List<RoleRef> Roles);

// Management of a theater's employee accounts and their role assignments. Inviting lives in InvitationService.
public sealed class EmployeeService(
    IServiceScopeFactory scopeFactory,
    IAuthorizationService auth,
    TheaterAccess access,
    IEmailSender<ApplicationUser> identityEmail,
    TimeProvider time)
{
    public static readonly string[] ListingPermissions =
        [TheaterPermissions.ViewEmployees, TheaterPermissions.ManageEmployees, TheaterPermissions.ManageRoles];

    public async Task<List<EmployeeSummary>> ListAsync(ClaimsPrincipal actor, int theaterId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var theater = await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        // Anyone who can act on employees needs to see them.
        var permissions = await access.GetPermissionsAsync(actor, theater);
        if (!ListingPermissions.Any(permissions.Contains))
            throw new AccessDeniedException();
        var now = time.GetUtcNow();
        return await db.Users.AsNoTracking()
            .Where(u => u.EmployeeTheaterId == theaterId)
            .OrderBy(u => u.Email)
            .Select(u => new EmployeeSummary(u.Id, u.Email!, u.DisplayName, u.PasswordHash != null,
                u.LockoutEnd != null && u.LockoutEnd > now, u.CreatedAt,
                u.EmployeeRoles.Where(m => m.Role!.TheaterId == theaterId).OrderBy(m => m.Role!.Name).Select(m => new RoleRef(m.RoleId, m.Role!.Name)).ToList()))
            .ToListAsync();
    }

    public async Task SendPasswordResetAsync(ClaimsPrincipal actor, int theaterId, string userId, string baseUri)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        await RequireAsync(db, actor, theaterId, TheaterPermissions.ManageEmployees);
        var user = await LoadEmployeeAsync(users, theaterId, userId);
        var token = await users.GeneratePasswordResetTokenAsync(user);
        await identityEmail.SendPasswordResetLinkAsync(user, user.Email!,
            System.Net.WebUtility.HtmlEncode(AccountLinks.PasswordReset(baseUri, token)));
    }

    public async Task DeleteAsync(ClaimsPrincipal actor, int theaterId, string userId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var theater = await RequireAsync(db, actor, theaterId, TheaterPermissions.ManageEmployees);
        var user = await LoadEmployeeAsync(users, theaterId, userId);

        // Can't remove someone with authority you don't have yourself.
        var targetPermissions = await db.EmployeeRoles
            .Where(m => m.UserId == userId && m.Role!.TheaterId == theaterId)
            .SelectMany(m => m.Role!.Permissions.Select(p => p.Permission))
            .ToListAsync();
        Guard.RequireWithinAuthority(await access.GetPermissionsAsync(actor, theater), targetPermissions,
            "You can't delete an employee who has permissions you don't have.");

        var result = await users.DeleteAsync(user);
        if (!result.Succeeded)
            throw new AppValidationException(string.Join(" ", result.Errors.Select(e => e.Description)));
    }

    // Replaces the employee's roles. Every role added or removed must be within the actor's own permissions.
    public async Task SetRolesAsync(ClaimsPrincipal actor, int theaterId, string userId, IEnumerable<int> roleIds)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var theater = await RequireAsync(db, actor, theaterId, TheaterPermissions.ManageRoles);
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.EmployeeTheaterId == theaterId))
            throw new NotFoundException("Employee not found.");

        var wanted = roleIds.ToHashSet();
        var theaterRoles = await db.TheaterRoles
            .Where(r => r.TheaterId == theaterId)
            .Select(r => new { r.Id, r.Name, Permissions = r.Permissions.Select(p => p.Permission).ToList() })
            .ToListAsync();
        if (wanted.Except(theaterRoles.Select(r => r.Id)).Any())
            throw new NotFoundException("Role not found.");

        var current = await db.EmployeeRoles.Where(m => m.UserId == userId && m.Role!.TheaterId == theaterId).ToListAsync();
        var currentIds = current.Select(m => m.RoleId).ToHashSet();
        var changed = theaterRoles.Where(r => wanted.Contains(r.Id) != currentIds.Contains(r.Id)).ToList();

        var actorPermissions = await access.GetPermissionsAsync(actor, theater);
        foreach (var role in changed)
            Guard.RequireWithinAuthority(actorPermissions, role.Permissions,
                $"You can't assign or remove the \"{role.Name}\" role because it has permissions you don't have.");

        db.EmployeeRoles.RemoveRange(current.Where(m => !wanted.Contains(m.RoleId)));
        db.EmployeeRoles.AddRange(wanted.Except(currentIds).Select(id => new EmployeeRole { UserId = userId, RoleId = id }));
        await db.SaveChangesAsync();
    }

    private async Task<Theater> RequireAsync(ApplicationDbContext db, ClaimsPrincipal actor, int theaterId, string permission)
    {
        var theater = await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(actor, theater, permission);
        return theater;
    }

    // Only accounts employed by this theater; nobody can touch another theater's accounts from here.
    private static async Task<ApplicationUser> LoadEmployeeAsync(UserManager<ApplicationUser> users, int theaterId, string userId)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null || user.EmployeeTheaterId != theaterId)
            throw new NotFoundException("Employee not found.");
        return user;
    }
}
