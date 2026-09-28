using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

public sealed record ScreenLayoutInput(string Name, SpotLabelScheme LabelScheme, IReadOnlyList<int> RowSpots);

// A theater's screens (1 to Screen.MaxPerTheater) and their spot layouts. Changes require ManageScreens.
public sealed class ScreenService(IDbContextFactory<ApplicationDbContext> dbFactory, IAuthorizationService auth, TimeProvider time)
{
    // Any member of the theater may view its screens.
    public async Task<Screen> GetAsync(ClaimsPrincipal user, int screenId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var screen = await db.Screens.AsNoTracking().Include(s => s.Theater).FirstOrDefaultAsync(s => s.Id == screenId)
            ?? throw new NotFoundException("Screen not found.");
        Guard.RequireMember(user, screen.Theater!);
        return screen;
    }

    public async Task<Screen> AddAsync(ClaimsPrincipal user, int theaterId, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(user, theater, TheaterPermissions.ManageScreens);
        var trimmed = ValidateName(name);

        var existing = await db.Screens.Where(s => s.TheaterId == theaterId).Select(s => s.SortOrder).ToListAsync();
        if (existing.Count >= Screen.MaxPerTheater)
            throw new AppValidationException($"A theater can have at most {Screen.MaxPerTheater} screens.");
        var screen = new Screen { TheaterId = theaterId, Name = trimmed, SortOrder = existing.DefaultIfEmpty(-1).Max() + 1 };
        db.Screens.Add(screen);
        await db.SaveChangesAsync();
        return screen;
    }

    public async Task UpdateAsync(ClaimsPrincipal user, int screenId, ScreenLayoutInput input)
    {
        var name = ValidateName(input.Name);
        if (!Enum.IsDefined(input.LabelScheme))
            throw new AppValidationException("Choose how spots are labeled.");
        if (input.RowSpots.Count > Screen.MaxRows)
            throw new AppValidationException($"A screen can have at most {Screen.MaxRows} rows.");
        if (input.RowSpots.Any(n => n is < 1 or > Screen.MaxSpotsPerRow))
            throw new AppValidationException($"Each row needs 1 to {Screen.MaxSpotsPerRow} spots.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var screen = await LoadAuthorizedAsync(db, user, screenId);
        // Spots sold (or held) for upcoming showings must stay where they are and keep the label on the buyer's ticket.
        var now = time.GetUtcNow();
        var ticketed = await TicketRecords.Active(db, now)
            .Where(t => t.Showtime!.ScreenId == screenId && t.Showtime.EndsAt > now)
            .Select(t => new { t.Row, t.Spot, t.SpotLabel })
            .ToListAsync();
        if (ticketed.Count > 0 && input.LabelScheme != screen.LabelScheme)
            throw new AppValidationException("Tickets are sold for upcoming showings on this screen, so its spot labels can't change until they've passed.");
        var removed = ticketed.Where(t => t.Row > input.RowSpots.Count || t.Spot > input.RowSpots[t.Row - 1])
            .Select(t => t.SpotLabel).Distinct().Order().ToList();
        if (removed.Count > 0)
            throw new AppValidationException($"Tickets are sold for upcoming showings in spots this layout removes: {string.Join(", ", removed.Take(10))}" +
                $"{(removed.Count > 10 ? " …" : "")}. Keep those spots until the showings have passed.");
        screen.Name = name;
        screen.LabelScheme = input.LabelScheme;
        screen.RowSpots = input.RowSpots.ToList();
        await db.SaveChangesAsync();
    }

    // Past showtimes go with the screen; a screen with upcoming showtimes can't be deleted. They're removed
    // explicitly rather than left to the FK cascade so every provider (including tests) sees the same result;
    // volume is small (a few showtimes per screen per night) and deleting a screen is rare.
    public async Task DeleteAsync(ClaimsPrincipal user, int screenId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var screen = await LoadAuthorizedAsync(db, user, screenId);
        if (!await db.Screens.AnyAsync(s => s.TheaterId == screen.TheaterId && s.Id != screenId))
            throw new AppValidationException("A theater needs at least one screen.");
        var showtimes = await db.Showtimes.Include(s => s.Features).Where(s => s.ScreenId == screenId).ToListAsync();
        if (showtimes.Any(s => s.EndsAt > time.GetUtcNow()))
            throw new AppValidationException($"{screen.Name} has upcoming showtimes. Remove them from the schedule first.");
        await TicketRecords.RemoveHoldsOrThrowAsync(db, showtimes.Select(s => s.Id).ToList(),
            $"Tickets were sold for showings on {screen.Name}, so it's kept with those sales records.");
        db.ShowtimeFeatures.RemoveRange(showtimes.SelectMany(s => s.Features));
        db.Showtimes.RemoveRange(showtimes);
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

    private static string ValidateName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
            throw new AppValidationException("Give the screen a name.");
        if (trimmed.Length > 100)
            throw new AppValidationException("Screen names can be at most 100 characters.");
        return trimmed;
    }
}
