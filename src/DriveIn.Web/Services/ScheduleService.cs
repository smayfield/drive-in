using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

public sealed record FilmInput(string Title, string? Rating, int RuntimeMinutes);

// Local times are in the theater's time zone.
public sealed record ShowtimeView(
    int Id, int ScreenId, string ScreenName, int FilmId, string FilmTitle, string? Rating,
    DateTime StartsLocal, DateTime EndsLocal);

// A theater's films and the showtimes scheduled on its screens. Any member may view the schedule;
// changing it requires ManageSchedule.
public sealed class ScheduleService(IDbContextFactory<ApplicationDbContext> dbFactory, IAuthorizationService auth, TimeProvider time)
{
    public static DateTimeOffset EndsAt(Showtime showtime) => showtime.StartsAt.AddMinutes(showtime.Film!.RuntimeMinutes);

    // --- Films ---

    public async Task<List<Film>> ListFilmsAsync(ClaimsPrincipal user, int theaterId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        Guard.RequireMember(user, await FindTheaterAsync(db, theaterId));
        return await db.Films.AsNoTracking().Where(f => f.TheaterId == theaterId).OrderBy(f => f.Title).ToListAsync();
    }

    public async Task<Film> AddFilmAsync(ClaimsPrincipal user, int theaterId, FilmInput input)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await auth.RequireAsync(user, await FindTheaterAsync(db, theaterId), TheaterPermissions.ManageSchedule);
        var film = new Film { TheaterId = theaterId };
        Apply(input, film);
        db.Films.Add(film);
        await db.SaveChangesAsync();
        return film;
    }

    // A longer runtime must not make any upcoming showtime run into the next one on its screen.
    public async Task UpdateFilmAsync(ClaimsPrincipal user, int filmId, FilmInput input)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var film = await LoadFilmAuthorizedAsync(db, user, filmId);
        Apply(input, film);
        var now = time.GetUtcNow();
        var upcoming = await db.Showtimes.Include(s => s.Screen)
            .Where(s => s.FilmId == filmId && s.StartsAt > now.AddMinutes(-Film.MaxRuntimeMinutes))
            .ToListAsync();
        foreach (var showtime in upcoming.Where(s => EndsAt(s) > now))
            await EnsureFreeAsync(db, film.Theater!, showtime.ScreenId, showtime.Screen!.Name, showtime.StartsAt, EndsAt(showtime), showtime.Id);
        await db.SaveChangesAsync();
    }

    // Past showtimes go with the film; a film with upcoming showtimes can't be deleted.
    public async Task DeleteFilmAsync(ClaimsPrincipal user, int filmId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var film = await LoadFilmAuthorizedAsync(db, user, filmId);
        var showtimes = await db.Showtimes.Where(s => s.FilmId == filmId).ToListAsync();
        if (showtimes.Any(s => EndsAt(s) > time.GetUtcNow()))
            throw new AppValidationException($"\"{film.Title}\" has upcoming showtimes. Remove them from the schedule first.");
        db.Showtimes.RemoveRange(showtimes);
        db.Films.Remove(film);
        await db.SaveChangesAsync();
    }

    // --- Showtimes ---

    // Showtimes that haven't ended yet, soonest first; optionally for one screen.
    public async Task<List<ShowtimeView>> ListUpcomingAsync(ClaimsPrincipal user, int theaterId, int? screenId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await FindTheaterAsync(db, theaterId);
        Guard.RequireMember(user, theater);
        var now = time.GetUtcNow();
        var query = db.Showtimes.AsNoTracking().Include(s => s.Film).Include(s => s.Screen)
            .Where(s => s.Screen!.TheaterId == theaterId && s.StartsAt > now.AddMinutes(-Film.MaxRuntimeMinutes));
        if (screenId is int id)
            query = query.Where(s => s.ScreenId == id);
        var showtimes = await query.ToListAsync();
        return showtimes.Where(s => EndsAt(s) > now)
            .OrderBy(s => s.StartsAt).ThenBy(s => s.Screen!.SortOrder)
            .Select(s => new ShowtimeView(s.Id, s.ScreenId, s.Screen!.Name, s.FilmId, s.Film!.Title, s.Film.Rating,
                TheaterTime.ToLocal(theater, s.StartsAt), TheaterTime.ToLocal(theater, EndsAt(s))))
            .ToList();
    }

    public async Task<Showtime> AddShowtimeAsync(ClaimsPrincipal user, int screenId, int filmId, DateOnly date, TimeOnly startTime)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var screen = await db.Screens.Include(s => s.Theater).FirstOrDefaultAsync(s => s.Id == screenId)
            ?? throw new NotFoundException("Screen not found.");
        await auth.RequireAsync(user, screen.Theater!, TheaterPermissions.ManageSchedule);
        var film = await db.Films.FirstOrDefaultAsync(f => f.Id == filmId && f.TheaterId == screen.TheaterId)
            ?? throw new NotFoundException("Film not found.");

        var startsAt = TheaterTime.ToUtc(screen.Theater!, date, startTime);
        if (startsAt <= time.GetUtcNow())
            throw new AppValidationException("Showtimes must be in the future.");
        var endsAt = startsAt.AddMinutes(film.RuntimeMinutes);
        await EnsureFreeAsync(db, screen.Theater!, screenId, screen.Name, startsAt, endsAt, exceptId: null);

        var showtime = new Showtime { ScreenId = screenId, FilmId = filmId, StartsAt = startsAt };
        db.Showtimes.Add(showtime);
        await db.SaveChangesAsync();
        return showtime;
    }

    public async Task DeleteShowtimeAsync(ClaimsPrincipal user, int showtimeId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var showtime = await db.Showtimes.Include(s => s.Screen!.Theater).FirstOrDefaultAsync(s => s.Id == showtimeId)
            ?? throw new NotFoundException("Showtime not found.");
        await auth.RequireAsync(user, showtime.Screen!.Theater!, TheaterPermissions.ManageSchedule);
        db.Showtimes.Remove(showtime);
        await db.SaveChangesAsync();
    }

    private static async Task EnsureFreeAsync(ApplicationDbContext db, Theater theater, int screenId, string screenName,
        DateTimeOffset startsAt, DateTimeOffset endsAt, int? exceptId)
    {
        // Anything that could overlap starts less than the longest runtime before this one.
        var candidates = await db.Showtimes.Include(s => s.Film)
            .Where(s => s.ScreenId == screenId && s.Id != exceptId
                && s.StartsAt < endsAt && s.StartsAt > startsAt.AddMinutes(-Film.MaxRuntimeMinutes))
            .ToListAsync();
        var clash = candidates.OrderBy(s => s.StartsAt).FirstOrDefault(s => EndsAt(s) > startsAt);
        if (clash is not null)
            throw new AppValidationException(
                $"That overlaps \"{clash.Film!.Title}\" on {screenName}, which runs until {TheaterTime.ToLocal(theater, EndsAt(clash)):ddd, MMM d 'at' h:mm tt}.");
    }

    private static async Task<Theater> FindTheaterAsync(ApplicationDbContext db, int theaterId) =>
        await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");

    private async Task<Film> LoadFilmAuthorizedAsync(ApplicationDbContext db, ClaimsPrincipal user, int filmId)
    {
        var film = await db.Films.Include(f => f.Theater).FirstOrDefaultAsync(f => f.Id == filmId)
            ?? throw new NotFoundException("Film not found.");
        await auth.RequireAsync(user, film.Theater!, TheaterPermissions.ManageSchedule);
        return film;
    }

    private static void Apply(FilmInput input, Film film)
    {
        var title = (input.Title ?? "").Trim();
        if (title.Length == 0)
            throw new AppValidationException("Give the film a title.");
        if (title.Length > 200)
            throw new AppValidationException("Film titles can be at most 200 characters.");
        var rating = string.IsNullOrWhiteSpace(input.Rating) ? null : input.Rating.Trim();
        if (rating?.Length > 10)
            throw new AppValidationException("Ratings can be at most 10 characters.");
        if (input.RuntimeMinutes is < 1 or > Film.MaxRuntimeMinutes)
            throw new AppValidationException($"Runtime must be 1 to {Film.MaxRuntimeMinutes} minutes.");
        film.Title = title;
        film.Rating = rating;
        film.RuntimeMinutes = input.RuntimeMinutes;
    }
}
