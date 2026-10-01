using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

// The details after the runtime are optional and shown on the public theater page.
public sealed record FilmInput(
    string Title, string? Rating, int RuntimeMinutes, int? ReleaseYear = null, string? Overview = null,
    string? Directors = null, string? Cast = null, string? Genres = null);

// A showing: one or more films (in order) on a screen. IntermissionMinutes null uses the theater's default;
// PriceScheduleId null uses the theater's default prices.
public sealed record ShowtimeInput(
    int ScreenId, IReadOnlyList<int> FilmIds, DateOnly Date, TimeOnly StartTime,
    int? IntermissionMinutes = null, int? PriceScheduleId = null);

// The trailing fields are the film's optional details; null where the theater left them out.
public sealed record FeatureView(
    int FilmId, string Title, string? Rating, int RuntimeMinutes, DateTime StartsLocal, DateTime EndsLocal,
    string? PosterUrl = null, int? Year = null, string? Overview = null, string? Directors = null, string? Cast = null,
    string? Genres = null);

// Local times are in the theater's time zone. PriceScheduleId/Name are the showtime's own schedule; null means no
// override (it follows whatever the theater's default is), so a pinned schedule shows even if it's also the default.
public sealed record ShowtimeView(
    int Id, int ScreenId, string ScreenName, List<FeatureView> Features, int IntermissionMinutes,
    DateTime StartsLocal, DateTime EndsLocal, int? PriceScheduleId, string? PriceScheduleName,
    // The same times in UTC, for anything that can't round-trip local times (they're ambiguous when clocks fall back).
    DateTimeOffset StartsAt, DateTimeOffset EndsAt)
{
    public string Title => string.Join(" + ", Features.Select(f => f.Title));
    public bool IsMultiFeature => Features.Count > 1;
}

// A theater's films and the showings scheduled on its screens. A showing is one or more films back to back
// (a double feature) with an intermission between them; showings on a screen never overlap. Any member may
// view the schedule; changing it requires ManageSchedule.
public sealed class ScheduleService(IDbContextFactory<ApplicationDbContext> dbFactory, IAuthorizationService auth, TimeProvider time)
{
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
        Apply(input, film, time.GetUtcNow().Year);
        db.Films.Add(film);
        await db.SaveChangesAsync();
        return film;
    }

    // Moves the end of every showing that includes the film. A longer runtime must not make an upcoming
    // showing run into the next one on its screen.
    public async Task UpdateFilmAsync(ClaimsPrincipal user, int filmId, FilmInput input)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var film = await LoadFilmAuthorizedAsync(db, user, filmId);
        Apply(input, film, time.GetUtcNow().Year);
        var now = time.GetUtcNow();
        var showtimes = await WithFeatures(db.Showtimes).Include(s => s.Screen)
            .Where(s => s.Features.Any(f => f.FilmId == filmId))
            .ToListAsync();
        foreach (var showtime in showtimes)
        {
            var wasUpcoming = showtime.EndsAt > now;
            showtime.EndsAt = Showtime.ComputeEndsAt(showtime.StartsAt, Runtimes(showtime), showtime.IntermissionMinutes);
            if (wasUpcoming)
                await EnsureFreeAsync(db, film.Theater!, showtime.ScreenId, showtime.Screen!.Name, showtime.StartsAt, showtime.EndsAt, showtime.Id);
        }
        await db.SaveChangesAsync();
    }

    // Past showings that include the film go with it; a film in an upcoming showing can't be deleted. Removed
    // explicitly for the same reason as in ScreenService.DeleteAsync.
    public async Task DeleteFilmAsync(ClaimsPrincipal user, int filmId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var film = await LoadFilmAuthorizedAsync(db, user, filmId);
        var showtimes = await db.Showtimes.Include(s => s.Features).Where(s => s.Features.Any(f => f.FilmId == filmId)).ToListAsync();
        if (showtimes.Any(s => s.EndsAt > time.GetUtcNow()))
            throw new AppValidationException($"\"{film.Title}\" is in upcoming showings. Remove them from the schedule first.");
        await TicketRecords.RemoveHoldsOrThrowAsync(db, showtimes.Select(s => s.Id).ToList(),
            $"Tickets were sold for showings of \"{film.Title}\", so it's kept with those sales records.");
        db.ShowtimeFeatures.RemoveRange(showtimes.SelectMany(s => s.Features));
        db.Showtimes.RemoveRange(showtimes);
        db.FilmPosters.RemoveRange(db.FilmPosters.Where(p => p.FilmId == filmId));
        db.Films.Remove(film);
        await db.SaveChangesAsync();
    }

    // --- Posters ---

    // Requires ManageSchedule. Replaces any existing poster. The type is taken from the file's bytes, and only JPEG,
    // GIF and PNG are accepted (not SVG, which can carry script).
    public async Task SetPosterAsync(ClaimsPrincipal user, int filmId, byte[] data)
    {
        if (data.Length == 0)
            throw new AppValidationException("Choose an image file.");
        if (data.Length > FilmPoster.MaxBytes)
            throw new AppValidationException($"Posters can be at most {FilmPoster.MaxBytes / (1024 * 1024)} MB.");
        var contentType = UploadedImages.Sniff(data)
            ?? throw new AppValidationException("Posters must be JPG, GIF or PNG images.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var film = await LoadFilmAuthorizedAsync(db, user, filmId);
        var poster = await db.FilmPosters.FirstOrDefaultAsync(p => p.FilmId == filmId);
        if (poster is null)
            db.FilmPosters.Add(poster = new FilmPoster { FilmId = filmId });
        poster.ContentType = contentType;
        poster.Data = data;
        film.PosterUpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
    }

    public async Task RemovePosterAsync(ClaimsPrincipal user, int filmId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var film = await LoadFilmAuthorizedAsync(db, user, filmId);
        db.FilmPosters.RemoveRange(db.FilmPosters.Where(p => p.FilmId == filmId));
        film.PosterUpdatedAt = null;
        await db.SaveChangesAsync();
    }

    // The poster of a film at a theater the user may browse (see TheaterService.CanBrowse); null when there's none
    // or it isn't visible to them.
    public async Task<FilmPoster?> GetPosterAsync(ClaimsPrincipal user, int filmId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var film = await db.Films.AsNoTracking().Include(f => f.Theater).FirstOrDefaultAsync(f => f.Id == filmId);
        if (film?.PosterUpdatedAt is null || !TheaterService.CanBrowse(user, film.Theater!))
            return null;
        return await db.FilmPosters.AsNoTracking().FirstOrDefaultAsync(p => p.FilmId == filmId);
    }

    // --- Intermission ---

    // The intermission prefilled for new showings. Existing showings keep theirs.
    public async Task SetDefaultIntermissionAsync(ClaimsPrincipal user, int theaterId, int minutes)
    {
        ValidateIntermission(minutes);
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.FirstOrDefaultAsync(t => t.Id == theaterId) ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(user, theater, TheaterPermissions.ManageSchedule);
        theater.DefaultIntermissionMinutes = minutes;
        await db.SaveChangesAsync();
    }

    // --- Showtimes ---

    // Showings that haven't ended yet, soonest first; optionally for one screen.
    public async Task<List<ShowtimeView>> ListUpcomingAsync(ClaimsPrincipal user, int theaterId, int? screenId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await FindTheaterAsync(db, theaterId);
        Guard.RequireMember(user, theater);
        var now = time.GetUtcNow();
        var query = WithFeatures(db.Showtimes.AsNoTracking()).Include(s => s.Screen).Include(s => s.PriceSchedule)
            .Where(s => s.Screen!.TheaterId == theaterId && s.EndsAt > now);
        if (screenId is int id)
            query = query.Where(s => s.ScreenId == id);
        var showtimes = await query.ToListAsync();
        return showtimes
            .OrderBy(s => s.StartsAt).ThenBy(s => s.Screen!.SortOrder)
            .Select(s => ToView(theater, s))
            .ToList();
    }

    // A single film; see the ShowtimeInput overload for double features.
    public Task<Showtime> AddShowtimeAsync(ClaimsPrincipal user, int screenId, int filmId, DateOnly date, TimeOnly startTime,
        int? priceScheduleId = null) =>
        AddShowtimeAsync(user, new ShowtimeInput(screenId, [filmId], date, startTime, PriceScheduleId: priceScheduleId));

    public async Task<Showtime> AddShowtimeAsync(ClaimsPrincipal user, ShowtimeInput input)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var showtime = new Showtime();
        await ApplyAsync(db, user, input, showtime);
        db.Showtimes.Add(showtime);
        await db.SaveChangesAsync();
        return showtime;
    }

    // Changes an upcoming showing: its screen, time, films, intermission and pricing.
    public async Task UpdateShowtimeAsync(ClaimsPrincipal user, int showtimeId, ShowtimeInput input)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var showtime = await LoadShowtimeAuthorizedAsync(db, user, showtimeId);
        if (showtime.StartsAt <= time.GetUtcNow())
            throw new AppValidationException("This showing has already started, so it can't be changed.");
        await ApplyAsync(db, user, input, showtime);
        await db.SaveChangesAsync();
    }

    public async Task DeleteShowtimeAsync(ClaimsPrincipal user, int showtimeId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var showtime = await LoadShowtimeAuthorizedAsync(db, user, showtimeId);
        await TicketRecords.RemoveHoldsOrThrowAsync(db, [showtimeId],
            "Tickets have been sold for this showing, so it can't be removed. Sales are final.");
        db.ShowtimeFeatures.RemoveRange(showtime.Features);
        db.Showtimes.Remove(showtime);
        await db.SaveChangesAsync();
    }

    // Overrides the showing's prices with another of the theater's price schedules; null goes back to the default.
    public async Task SetShowtimePricingAsync(ClaimsPrincipal user, int showtimeId, int? priceScheduleId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var showtime = await LoadShowtimeAuthorizedAsync(db, user, showtimeId);
        await EnsureScheduleAsync(db, showtime.Screen!.TheaterId, priceScheduleId);
        showtime.PriceScheduleId = priceScheduleId;
        await db.SaveChangesAsync();
    }

    // --- Helpers ---

    internal static IQueryable<Showtime> WithFeatures(IQueryable<Showtime> query) =>
        query.Include(s => s.Features.OrderBy(f => f.Position)).ThenInclude(f => f.Film);

    private static List<int> Runtimes(Showtime showtime) =>
        showtime.Features.OrderBy(f => f.Position).Select(f => f.Film!.RuntimeMinutes).ToList();

    internal static ShowtimeView ToView(Theater theater, Showtime s)
    {
        var features = s.Features.OrderBy(f => f.Position).ToList();
        var starts = Showtime.FeatureStarts(s.StartsAt, Runtimes(s), s.IntermissionMinutes).ToList();
        return new ShowtimeView(s.Id, s.ScreenId, s.Screen!.Name,
            features.Select((f, i) => new FeatureView(f.FilmId, f.Film!.Title, f.Film.Rating, f.Film.RuntimeMinutes,
                TheaterTime.ToLocal(theater, starts[i]), TheaterTime.ToLocal(theater, starts[i].AddMinutes(f.Film.RuntimeMinutes)),
                f.Film.PosterUrl, f.Film.ReleaseYear, f.Film.Overview, f.Film.Directors, f.Film.Cast, f.Film.Genres)).ToList(),
            s.IntermissionMinutes,
            TheaterTime.ToLocal(theater, s.StartsAt), TheaterTime.ToLocal(theater, s.EndsAt),
            s.PriceScheduleId, s.PriceSchedule?.Name, s.StartsAt, s.EndsAt);
    }

    // Validates the input and applies it to a new or tracked showtime (with its Features loaded).
    private async Task ApplyAsync(ApplicationDbContext db, ClaimsPrincipal user, ShowtimeInput input, Showtime showtime)
    {
        var screen = await db.Screens.Include(s => s.Theater).FirstOrDefaultAsync(s => s.Id == input.ScreenId)
            ?? throw new NotFoundException("Screen not found.");
        await auth.RequireAsync(user, screen.Theater!, TheaterPermissions.ManageSchedule);
        if (showtime.Id != 0 && showtime.Screen!.TheaterId != screen.TheaterId)
            throw new NotFoundException("Screen not found.");

        if (input.FilmIds.Count == 0)
            throw new AppValidationException("Choose a film.");
        if (input.FilmIds.Count > Showtime.MaxFeatures)
            throw new AppValidationException($"A showing can have at most {Showtime.MaxFeatures} films.");
        var ids = input.FilmIds.Distinct().ToList();
        var films = await db.Films.Where(f => ids.Contains(f.Id) && f.TheaterId == screen.TheaterId).ToDictionaryAsync(f => f.Id);
        if (films.Count != ids.Count)
            throw new NotFoundException("Film not found.");
        var intermission = input.IntermissionMinutes ?? screen.Theater!.DefaultIntermissionMinutes;
        ValidateIntermission(intermission);

        var startsAt = TheaterTime.ToUtc(screen.Theater!, input.Date, input.StartTime);
        if (startsAt <= time.GetUtcNow())
            throw new AppValidationException("Showings must be in the future.");
        if (!screen.Theater!.IsInSeason(input.Date))
            throw new AppValidationException($"{input.Date:ddd, MMM d, yyyy} is outside the theater's season ({Seasons.Describe(screen.Theater)}).");
        // Sold spots belong to the screen they were bought for.
        if (showtime.Id != 0 && showtime.ScreenId != screen.Id
            && await TicketRecords.Active(db, time.GetUtcNow()).AnyAsync(t => t.ShowtimeId == showtime.Id))
            throw new AppValidationException("Tickets have been sold for this showing, so it can't move to another screen.");
        var endsAt = Showtime.ComputeEndsAt(startsAt, input.FilmIds.Select(id => films[id].RuntimeMinutes).ToList(), intermission);
        await EnsureFreeAsync(db, screen.Theater!, screen.Id, screen.Name, startsAt, endsAt, showtime.Id == 0 ? null : showtime.Id);
        await EnsureScheduleAsync(db, screen.TheaterId, input.PriceScheduleId);

        showtime.ScreenId = screen.Id;
        showtime.StartsAt = startsAt;
        showtime.EndsAt = endsAt;
        showtime.IntermissionMinutes = intermission;
        showtime.PriceScheduleId = input.PriceScheduleId;
        // Update features in place by position: removing and re-adding the same (showtime, position) key would conflict.
        var byPosition = showtime.Features.ToDictionary(f => f.Position);
        for (var i = 0; i < input.FilmIds.Count; i++)
        {
            if (byPosition.TryGetValue(i + 1, out var feature))
                feature.FilmId = input.FilmIds[i];
            else
                showtime.Features.Add(new ShowtimeFeature { Position = i + 1, FilmId = input.FilmIds[i] });
        }
        foreach (var extra in showtime.Features.Where(f => f.Position > input.FilmIds.Count).ToList())
        {
            showtime.Features.Remove(extra);
            db.ShowtimeFeatures.Remove(extra);
        }
    }

    private static void ValidateIntermission(int minutes)
    {
        if (minutes is < 0 or > Showtime.MaxIntermissionMinutes)
            throw new AppValidationException($"Intermissions can be 0 to {Showtime.MaxIntermissionMinutes} minutes.");
    }

    private static async Task EnsureScheduleAsync(ApplicationDbContext db, int theaterId, int? priceScheduleId)
    {
        if (priceScheduleId is int id && !await db.PriceSchedules.AnyAsync(p => p.Id == id && p.TheaterId == theaterId))
            throw new NotFoundException("Price schedule not found.");
    }

    // The whole showing, from its first film's start to its last film's end (intermissions included), must be free.
    private static async Task EnsureFreeAsync(ApplicationDbContext db, Theater theater, int screenId, string screenName,
        DateTimeOffset startsAt, DateTimeOffset endsAt, int? exceptId)
    {
        var clash = await WithFeatures(db.Showtimes.AsNoTracking())
            .Where(s => s.ScreenId == screenId && s.Id != exceptId && s.StartsAt < endsAt && s.EndsAt > startsAt)
            .OrderBy(s => s.StartsAt)
            .FirstOrDefaultAsync();
        if (clash is not null)
            throw new AppValidationException(
                $"That overlaps \"{string.Join(" + ", clash.Features.OrderBy(f => f.Position).Select(f => f.Film!.Title))}\" on {screenName}, " +
                $"which runs {TheaterTime.ToLocal(theater, clash.StartsAt):ddd, MMM d h:mm tt}–{TheaterTime.ToLocal(theater, clash.EndsAt):h:mm tt}.");
    }

    private static async Task<Theater> FindTheaterAsync(ApplicationDbContext db, int theaterId) =>
        await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");

    private async Task<Showtime> LoadShowtimeAuthorizedAsync(ApplicationDbContext db, ClaimsPrincipal user, int showtimeId)
    {
        var showtime = await db.Showtimes.Include(s => s.Features).Include(s => s.Screen!.Theater)
            .FirstOrDefaultAsync(s => s.Id == showtimeId) ?? throw new NotFoundException("Showtime not found.");
        await auth.RequireAsync(user, showtime.Screen!.Theater!, TheaterPermissions.ManageSchedule);
        return showtime;
    }

    private async Task<Film> LoadFilmAuthorizedAsync(ApplicationDbContext db, ClaimsPrincipal user, int filmId)
    {
        var film = await db.Films.Include(f => f.Theater).FirstOrDefaultAsync(f => f.Id == filmId)
            ?? throw new NotFoundException("Film not found.");
        await auth.RequireAsync(user, film.Theater!, TheaterPermissions.ManageSchedule);
        return film;
    }

    private static string? Detail(string? value, int max, string name)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (text?.Length > max)
            throw new AppValidationException($"{name} can be at most {max} characters.");
        return text;
    }

    private static void Apply(FilmInput input, Film film, int currentYear)
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
        if (input.ReleaseYear is int year && (year < Film.MinReleaseYear || year > currentYear + 5))
            throw new AppValidationException($"The year must be between {Film.MinReleaseYear} and {currentYear + 5}.");
        film.Title = title;
        film.Rating = rating;
        film.RuntimeMinutes = input.RuntimeMinutes;
        film.ReleaseYear = input.ReleaseYear;
        film.Overview = Detail(input.Overview, 2000, "The description");
        film.Directors = Detail(input.Directors, 300, "Directors");
        film.Cast = Detail(input.Cast, 500, "Cast");
        film.Genres = Detail(input.Genres, 200, "Genres");
    }
}
