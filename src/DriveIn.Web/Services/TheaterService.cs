using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

public sealed record TheaterSummary(
    int Id, string Name, string Slug, string? City, string? State, bool IsActive,
    string? OwnerEmail, int ScreenCount, int EmployeeCount);

public sealed class TheaterService(IDbContextFactory<ApplicationDbContext> dbFactory, IAuthorizationService auth, TimeProvider time)
{
    // --- Browsing (any signed-in user) ---

    public async Task<List<Theater>> ListActiveAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Theaters.AsNoTracking()
            .Where(t => t.IsActive)
            .Include(t => t.Screens)
            .OrderBy(t => t.Name)
            .ToListAsync();
    }

    public async Task<Theater?> GetBySlugAsync(string slug)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.AsNoTracking()
            .Include(t => t.Screens)
            .FirstOrDefaultAsync(t => t.Slug == slug && t.IsActive);
        theater?.Screens.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
        return theater;
    }

    // --- Owner / employee management ---

    // Theaters this user may operate: all for admins, otherwise owned + the one they work at.
    public async Task<List<Theater>> ListManagedAsync(ClaimsPrincipal user)
    {
        var userId = Guard.RequireUserId(user);
        var employeeTheaterId = user.GetEmployeeTheaterId();
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.Theaters.AsNoTracking();
        if (!user.IsAdmin())
            query = query.Where(t => t.OwnerId == userId || t.Id == employeeTheaterId);
        return await query.OrderBy(t => t.Name).ToListAsync();
    }

    // For the manage pages: any member (admin, owner, or its employee). What they can change is per permission.
    public async Task<Theater> GetForManageAsync(ClaimsPrincipal user, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.AsNoTracking()
            .Include(t => t.Screens)
            .Include(t => t.Owner)
            .FirstOrDefaultAsync(t => t.Id == id) ?? throw new NotFoundException("Theater not found.");
        Guard.RequireMember(user, theater);
        theater.Screens.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
        return theater;
    }

    // Requires EditProfile; slug, active flag and owner are admin-only.
    public async Task UpdateProfileAsync(ClaimsPrincipal user, Theater input)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.FirstOrDefaultAsync(t => t.Id == input.Id)
            ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(user, theater, TheaterPermissions.EditProfile);
        CopyProfile(input, theater);
        theater.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
    }

    // --- Admin ---

    public async Task<List<TheaterSummary>> AdminListAsync(ClaimsPrincipal user)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Theaters.AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new TheaterSummary(t.Id, t.Name, t.Slug, t.City, t.State, t.IsActive,
                t.Owner != null ? t.Owner.Email : null, t.Screens.Count, t.Employees.Count))
            .ToListAsync();
    }

    public async Task<Theater> AdminGetAsync(ClaimsPrincipal user, int id)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Theaters.AsNoTracking().Include(t => t.Owner).FirstOrDefaultAsync(t => t.Id == id)
            ?? throw new NotFoundException("Theater not found.");
    }

    public async Task<Theater> CreateAsync(ClaimsPrincipal user, Theater input)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        await EnsureSlugFreeAsync(db, input.Slug, exceptId: null);
        var theater = new Theater { Slug = input.Slug, IsActive = input.IsActive, CreatedAt = time.GetUtcNow(), UpdatedAt = time.GetUtcNow() };
        CopyProfile(input, theater);
        db.Theaters.Add(theater);
        db.TheaterRoles.AddRange(DefaultTheaterRoles.CreateFor(theater, time.GetUtcNow()));
        db.Screens.Add(new Screen { Theater = theater, Name = "Screen 1" });
        db.PriceSchedules.Add(new PriceSchedule { Theater = theater, Name = PricingService.DefaultScheduleName, IsDefault = true });
        await db.SaveChangesAsync();
        return theater;
    }

    public async Task AdminUpdateAsync(ClaimsPrincipal user, Theater input)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.FirstOrDefaultAsync(t => t.Id == input.Id)
            ?? throw new NotFoundException("Theater not found.");
        await EnsureSlugFreeAsync(db, input.Slug, exceptId: theater.Id);
        CopyProfile(input, theater);
        theater.Slug = input.Slug;
        theater.IsActive = input.IsActive;
        theater.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
    }

    // Deletes the theater with its screens, schedule, pricing, invitations, roles, and employee accounts
    // (employee accounts are only valid for this theater, so they go too).
    public async Task DeleteAsync(ClaimsPrincipal user, int id)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var theater = await db.Theaters
            .Include(t => t.Screens)
            .Include(t => t.Employees)
            .FirstOrDefaultAsync(t => t.Id == id) ?? throw new NotFoundException("Theater not found.");
        db.Invitations.RemoveRange(db.Invitations.Where(i => i.TheaterId == id));
        var roleIds = db.TheaterRoles.Where(r => r.TheaterId == id).Select(r => r.Id);
        db.EmployeeRoles.RemoveRange(db.EmployeeRoles.Where(m => roleIds.Contains(m.RoleId)));
        db.TheaterRolePermissions.RemoveRange(db.TheaterRolePermissions.Where(p => roleIds.Contains(p.RoleId)));
        db.TheaterRoles.RemoveRange(db.TheaterRoles.Where(r => r.TheaterId == id));
        db.Users.RemoveRange(theater.Employees);
        db.ShowtimeFeatures.RemoveRange(db.ShowtimeFeatures.Where(f => f.Showtime!.Screen!.TheaterId == id));
        db.Showtimes.RemoveRange(db.Showtimes.Where(s => s.Screen!.TheaterId == id));
        db.Films.RemoveRange(db.Films.Where(f => f.TheaterId == id));
        db.AddOns.RemoveRange(db.AddOns.Where(a => a.TheaterId == id));
        db.PriceOptions.RemoveRange(db.PriceOptions.Where(o => o.Schedule!.TheaterId == id));
        db.PriceSchedules.RemoveRange(db.PriceSchedules.Where(s => s.TheaterId == id));
        db.Screens.RemoveRange(theater.Screens);
        db.Theaters.Remove(theater);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private static async Task EnsureSlugFreeAsync(ApplicationDbContext db, string slug, int? exceptId)
    {
        if (await db.Theaters.AnyAsync(t => t.Slug == slug && t.Id != exceptId))
            throw new AppValidationException($"The slug \"{slug}\" is already used by another theater.");
    }

    // Showtimes are entered in the theater's time zone, so it has to be one we can resolve.
    private static void CopyProfile(Theater from, Theater to)
    {
        var timeZone = string.IsNullOrWhiteSpace(from.TimeZone) ? null : from.TimeZone.Trim();
        if (timeZone is not null && !TheaterTime.IsValidZone(timeZone))
            throw new AppValidationException($"\"{timeZone}\" isn't a time zone. Use an IANA name such as America/Chicago.");
        to.Name = from.Name.Trim();
        to.AddressLine1 = from.AddressLine1;
        to.AddressLine2 = from.AddressLine2;
        to.City = from.City;
        to.State = from.State;
        to.PostalCode = from.PostalCode;
        to.Country = from.Country;
        to.Phone = from.Phone;
        to.Website = from.Website;
        to.Description = from.Description;
        to.TimeZone = timeZone;
    }
}
