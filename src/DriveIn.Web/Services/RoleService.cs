using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

public sealed record RoleSummary(int Id, string Name, string? Description, List<string> Permissions, int MemberCount);

// Owner-defined roles within a theater. Requires ManageRoles; a non-owner can only create, change or
// delete roles whose permissions (before and after the change) they hold themselves.
public sealed class RoleService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IAuthorizationService auth,
    TheaterAccess access,
    TimeProvider time)
{
    public async Task<List<RoleSummary>> ListAsync(ClaimsPrincipal actor, int theaterId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await RequireAsync(db, actor, theaterId);
        var roles = await db.TheaterRoles.AsNoTracking()
            .Where(r => r.TheaterId == theaterId)
            .OrderBy(r => r.Name)
            .Select(r => new RoleSummary(r.Id, r.Name, r.Description,
                r.Permissions.Select(p => p.Permission).ToList(), r.Members.Count))
            .ToListAsync();
        // Show permissions in catalog order and hide any retired keys.
        return roles.Select(r => r with { Permissions = Ordered(r.Permissions) }).ToList();
    }

    public async Task<TheaterRole> CreateAsync(ClaimsPrincipal actor, int theaterId, string name, string? description,
        IEnumerable<string> permissions)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await RequireAsync(db, actor, theaterId);
        var keys = Validate(permissions);
        var trimmed = await ValidateNameAsync(db, theaterId, name, exceptId: null);
        Guard.RequireWithinAuthority(await access.GetPermissionsAsync(actor, theater), keys,
            "You can only grant permissions you have yourself.");

        var role = new TheaterRole
        {
            TheaterId = theaterId,
            Name = trimmed,
            Description = Clean(description),
            CreatedAt = time.GetUtcNow(),
            Permissions = keys.Select(k => new TheaterRolePermission { Permission = k }).ToList(),
        };
        db.TheaterRoles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    public async Task UpdateAsync(ClaimsPrincipal actor, int roleId, string name, string? description,
        IEnumerable<string> permissions)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var role = await db.TheaterRoles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == roleId)
            ?? throw new NotFoundException("Role not found.");
        var theater = await RequireAsync(db, actor, role.TheaterId);
        var keys = Validate(permissions);
        var trimmed = await ValidateNameAsync(db, role.TheaterId, name, exceptId: role.Id);
        Guard.RequireWithinAuthority(await access.GetPermissionsAsync(actor, theater),
            keys.Concat(role.Permissions.Select(p => p.Permission)),
            "You can only change roles whose permissions you have yourself.");

        role.Name = trimmed;
        role.Description = Clean(description);
        db.TheaterRolePermissions.RemoveRange(role.Permissions.Where(p => !keys.Contains(p.Permission)));
        var existing = role.Permissions.Select(p => p.Permission).ToHashSet();
        role.Permissions.AddRange(keys.Where(k => !existing.Contains(k)).Select(k => new TheaterRolePermission { Permission = k }));
        await db.SaveChangesAsync();
    }

    // Removes the role from everyone who has it.
    public async Task DeleteAsync(ClaimsPrincipal actor, int roleId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var role = await db.TheaterRoles.Include(r => r.Permissions).Include(r => r.Members)
            .FirstOrDefaultAsync(r => r.Id == roleId) ?? throw new NotFoundException("Role not found.");
        var theater = await RequireAsync(db, actor, role.TheaterId);
        Guard.RequireWithinAuthority(await access.GetPermissionsAsync(actor, theater),
            role.Permissions.Select(p => p.Permission),
            "You can only delete roles whose permissions you have yourself.");

        db.EmployeeRoles.RemoveRange(role.Members);
        db.TheaterRolePermissions.RemoveRange(role.Permissions);
        db.TheaterRoles.Remove(role);
        await db.SaveChangesAsync();
    }

    private async Task<Theater> RequireAsync(ApplicationDbContext db, ClaimsPrincipal actor, int theaterId)
    {
        var theater = await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(actor, theater, TheaterPermissions.ManageRoles);
        return theater;
    }

    private static HashSet<string> Validate(IEnumerable<string> permissions)
    {
        var keys = permissions.ToHashSet();
        var unknown = keys.Where(k => !TheaterPermissions.IsKnown(k)).ToList();
        if (unknown.Count > 0)
            throw new AppValidationException($"Unknown permission: {string.Join(", ", unknown)}.");
        return keys;
    }

    private static async Task<string> ValidateNameAsync(ApplicationDbContext db, int theaterId, string name, int? exceptId)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
            throw new AppValidationException("Give the role a name.");
        if (trimmed.Length > 60)
            throw new AppValidationException("Role names can be at most 60 characters.");
        var lower = trimmed.ToLower();
        if (await db.TheaterRoles.AnyAsync(r => r.TheaterId == theaterId && r.Id != exceptId && r.Name.ToLower() == lower))
            throw new AppValidationException($"There's already a role named \"{trimmed}\".");
        return trimmed;
    }

    private static string? Clean(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim()[..Math.Min(description.Trim().Length, 300)];

    private static List<string> Ordered(IEnumerable<string> keys)
    {
        var set = keys.ToHashSet();
        return TheaterPermissions.All.Where(p => set.Contains(p.Key)).Select(p => p.Key).ToList();
    }
}
