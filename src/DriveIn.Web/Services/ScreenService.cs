using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

public sealed class ScreenService(IDbContextFactory<ApplicationDbContext> dbFactory, IAuthorizationService auth)
{
    public async Task<Screen> AddAsync(ClaimsPrincipal user, int theaterId, string name, int carCapacity)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(user, theater, TheaterPermissions.ManageScreens);

        var nextOrder = await db.Screens.Where(s => s.TheaterId == theaterId)
            .Select(s => (int?)s.SortOrder).MaxAsync() ?? -1;
        var screen = new Screen { TheaterId = theaterId, Name = name.Trim(), CarCapacity = carCapacity, SortOrder = nextOrder + 1 };
        db.Screens.Add(screen);
        await db.SaveChangesAsync();
        return screen;
    }

    public async Task UpdateAsync(ClaimsPrincipal user, int screenId, string name, int carCapacity)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var screen = await LoadAuthorizedAsync(db, user, screenId);
        screen.Name = name.Trim();
        screen.CarCapacity = carCapacity;
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(ClaimsPrincipal user, int screenId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var screen = await LoadAuthorizedAsync(db, user, screenId);
        db.Screens.Remove(screen);
        await db.SaveChangesAsync();
    }

    // Swaps the screen with its neighbor; direction is -1 (up) or +1 (down).
    public async Task MoveAsync(ClaimsPrincipal user, int screenId, int direction)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var screen = await LoadAuthorizedAsync(db, user, screenId);
        var siblings = await db.Screens.Where(s => s.TheaterId == screen.TheaterId)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToListAsync();
        var index = siblings.FindIndex(s => s.Id == screenId);
        var target = index + Math.Sign(direction);
        if (target < 0 || target >= siblings.Count)
            return;
        (siblings[index], siblings[target]) = (siblings[target], siblings[index]);
        for (var i = 0; i < siblings.Count; i++)
            siblings[i].SortOrder = i;
        await db.SaveChangesAsync();
    }

    private async Task<Screen> LoadAuthorizedAsync(ApplicationDbContext db, ClaimsPrincipal user, int screenId)
    {
        var screen = await db.Screens.Include(s => s.Theater).FirstOrDefaultAsync(s => s.Id == screenId)
            ?? throw new NotFoundException("Screen not found.");
        await auth.RequireAsync(user, screen.Theater!, TheaterPermissions.ManageScreens);
        return screen;
    }
}
