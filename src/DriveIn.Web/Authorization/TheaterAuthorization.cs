using System.Security.Claims;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Authorization;

public static class Roles
{
    public const string Admin = "Admin";
}

public static class Policies
{
    public const string Admin = "Admin";
}

public static class AppClaims
{
    // Present only on employee accounts; value is the theater id.
    public const string EmployeeTheater = "drivein:employee_theater";
}

public static class ClaimsPrincipalExtensions
{
    public static string? GetUserId(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier);

    public static int? GetEmployeeTheaterId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(AppClaims.EmployeeTheater), out var id) ? id : null;

    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(Roles.Admin);
}

// Requires one TheaterPermissions action on a Theater resource.
public sealed record TheaterPermissionRequirement(string Permission) : IAuthorizationRequirement;

// Resolves what a user may do at a theater:
//   admin or the theater's owner  -> every permission
//   an employee of the theater    -> the union of their roles' permissions (none by default)
//   anyone else                   -> nothing
// Role permissions are read from the database on each check, so changes apply immediately.
public sealed class TheaterAccess(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    // Admins, the owner, and the theater's own employees (even with no roles) may open its manage pages.
    public static bool IsMember(ClaimsPrincipal user, Theater theater) =>
        user.GetUserId() is string userId
        && (user.IsAdmin() || theater.OwnerId == userId || user.GetEmployeeTheaterId() == theater.Id);

    public static bool HasFullAccess(ClaimsPrincipal user, Theater theater) =>
        user.GetUserId() is string userId && (user.IsAdmin() || theater.OwnerId == userId);

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(ClaimsPrincipal user, Theater theater)
    {
        if (HasFullAccess(user, theater))
            return TheaterPermissions.AllKeys;
        if (user.GetUserId() is not string userId || user.GetEmployeeTheaterId() != theater.Id)
            return None;

        await using var db = await dbFactory.CreateDbContextAsync();
        var keys = await db.EmployeeRoles
            .Where(m => m.UserId == userId && m.Role!.TheaterId == theater.Id)
            .SelectMany(m => m.Role!.Permissions.Select(p => p.Permission))
            .Distinct()
            .ToListAsync();
        return keys.Where(TheaterPermissions.IsKnown).ToHashSet();
    }
}

public sealed class TheaterAuthorizationHandler(TheaterAccess access)
    : AuthorizationHandler<TheaterPermissionRequirement, Theater>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, TheaterPermissionRequirement requirement, Theater theater)
    {
        if ((await access.GetPermissionsAsync(context.User, theater)).Contains(requirement.Permission))
            context.Succeed(requirement);
    }
}
