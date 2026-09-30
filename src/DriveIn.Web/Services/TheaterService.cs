using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

public sealed record TheaterSummary(
    int Id, string Name, string Slug, string? City, string? State, bool IsActive,
    string? OwnerEmail, int ScreenCount, int EmployeeCount, TheaterMode Mode = TheaterMode.Live, DateTimeOffset? GoLiveRequestedAt = null);

internal static class UploadedImages
{
    // The MIME type if the bytes start like a JPEG, GIF or PNG, else null.
    public static string? Sniff(ReadOnlySpan<byte> d) => d switch
    {
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'7' or (byte)'9', (byte)'a', ..] => "image/gif",
        _ => null,
    };
}

public sealed class TheaterService(
    IDbContextFactory<ApplicationDbContext> dbFactory, IAuthorizationService auth, TimeProvider time, IGeocoder geocoder)
{
    // --- Browsing (any signed-in user) ---

    // Live theaters, plus any demo theaters the user belongs to (so owners and staff can try them out).
    public async Task<List<Theater>> ListActiveAsync(ClaimsPrincipal user)
    {
        // CanBrowse as a query, so private demo theaters aren't loaded only to be filtered out.
        var userId = user.GetUserId();
        var admin = userId is not null && user.IsAdmin();
        var employeeTheaterId = userId is null ? null : user.GetEmployeeTheaterId();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Theaters.AsNoTracking()
            .Where(t => t.IsActive && (t.Mode == TheaterMode.Live || admin
                || (userId != null && t.OwnerId == userId) || t.Id == employeeTheaterId))
            .Include(t => t.Screens)
            .OrderBy(t => t.Name)
            .ToListAsync();
    }

    // Theaters the user may browse (as ListActiveAsync) within radiusMiles of a point, nearest first; null radius is
    // any distance. Theaters without coordinates can't be placed, so they're left out.
    public async Task<List<(Theater Theater, double Miles)>> ListNearAsync(ClaimsPrincipal user, GeoPoint from, double? radiusMiles)
    {
        if (!GeoPoint.IsValid(from.Latitude, from.Longitude))
            throw new AppValidationException("That location isn't valid.");
        // Few enough theaters to measure in memory.
        return (await ListActiveAsync(user))
            .Select(t => (Theater: t, At: Geo.Of(t)))
            .Where(x => x.At is not null)
            .Select(x => (x.Theater, Miles: Geo.DistanceMiles(from, x.At!)))
            .Where(x => radiusMiles is not double r || x.Miles <= r)
            .OrderBy(x => x.Miles).ThenBy(x => x.Theater.Name)
            .ToList();
    }

    // A place a customer typed ("Austin, TX", a ZIP code); null when it can't be found. Any signed-in user.
    public async Task<GeoPoint?> FindPlaceAsync(ClaimsPrincipal user, string place, CancellationToken ct = default)
    {
        Guard.RequireUserId(user);
        return string.IsNullOrWhiteSpace(place) ? null : await geocoder.GeocodeAsync(place, ct);
    }

    public async Task<Theater?> GetBySlugAsync(ClaimsPrincipal user, string slug)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.AsNoTracking()
            .Include(t => t.Screens)
            .FirstOrDefaultAsync(t => t.Slug == slug && t.IsActive);
        if (theater is null || !CanBrowse(user, theater))
            return null;
        theater.Screens.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
        return theater;
    }

    // Live theaters are public; a demo theater is only visible to its members (admins, owner, employees).
    public static bool CanBrowse(ClaimsPrincipal user, Theater theater) =>
        theater.IsActive && (theater.IsPublic || TheaterAccess.IsMember(user, theater));

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

    // Free admission rules for the theater. Requires EditProfile.
    public async Task UpdateFreeAdmissionSettingsAsync(ClaimsPrincipal user, int theaterId, bool enabled, bool requiresApproval,
        bool requiresReason, int? maxPerShowing, int? maxPerEmployeePerShowing)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(user, theater, TheaterPermissions.EditProfile);
        if (maxPerShowing is < 1 || maxPerEmployeePerShowing is < 1)
            throw new AppValidationException("Limits must be at least 1, or left blank for no limit.");
        theater.FreeAdmissionEnabled = enabled;
        theater.FreeAdmissionRequiresApproval = requiresApproval;
        theater.FreeAdmissionRequiresReason = requiresReason;
        theater.FreeAdmissionMaxPerShowing = maxPerShowing;
        theater.FreeAdmissionMaxPerEmployeePerShowing = maxPerEmployeePerShowing;
        theater.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
    }

    // Turns gift card sales on or off. Cards already sold stay spendable either way. Requires ManageGiftCards.
    public async Task UpdateGiftCardSettingsAsync(ClaimsPrincipal user, int theaterId, bool enabled)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(user, theater, TheaterPermissions.ManageGiftCards);
        theater.GiftCardsEnabled = enabled;
        theater.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
    }

    // Requires EditProfile; slug, active flag and owner are admin-only.
    public async Task UpdateProfileAsync(ClaimsPrincipal user, Theater input)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.FirstOrDefaultAsync(t => t.Id == input.Id)
            ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(user, theater, TheaterPermissions.EditProfile);
        var (addressChanged, coordinatesEdited) = (!Geo.SameAddress(theater, input), !SameCoordinates(theater, input));
        CopyProfile(input, theater);
        await EnsureSeasonCoversShowingsAsync(db, theater);
        await LocateAsync(geocoder, theater, addressChanged, coordinatesEdited);
        theater.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
    }

    // --- Logo ---

    // Requires EditProfile. Replaces any existing logo. The type is taken from the file's bytes, and only JPEG, GIF
    // and PNG are accepted (not SVG, which can carry script).
    public async Task SetLogoAsync(ClaimsPrincipal user, int theaterId, byte[] data)
    {
        if (data.Length == 0)
            throw new AppValidationException("Choose an image file.");
        if (data.Length > TheaterLogo.MaxBytes)
            throw new AppValidationException($"Logos can be at most {TheaterLogo.MaxBytes / (1024 * 1024)} MB.");
        var contentType = UploadedImages.Sniff(data)
            ?? throw new AppValidationException("Logos must be JPG, GIF or PNG images.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(user, theater, TheaterPermissions.EditProfile);
        var logo = await db.TheaterLogos.FirstOrDefaultAsync(l => l.TheaterId == theaterId);
        if (logo is null)
            db.TheaterLogos.Add(logo = new TheaterLogo { TheaterId = theaterId });
        logo.ContentType = contentType;
        logo.Data = data;
        theater.LogoUpdatedAt = time.GetUtcNow();
        theater.UpdatedAt = theater.LogoUpdatedAt.Value;
        await db.SaveChangesAsync();
    }

    public async Task RemoveLogoAsync(ClaimsPrincipal user, int theaterId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(user, theater, TheaterPermissions.EditProfile);
        db.TheaterLogos.RemoveRange(db.TheaterLogos.Where(l => l.TheaterId == theaterId));
        theater.LogoUpdatedAt = null;
        theater.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
    }

    // The logo of a theater the user may browse (see CanBrowse); null when there's none or it isn't visible to them.
    public async Task<TheaterLogo?> GetLogoAsync(ClaimsPrincipal user, string slug)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug);
        if (theater is null || theater.LogoUpdatedAt is null || !CanBrowse(user, theater))
            return null;
        return await db.TheaterLogos.AsNoTracking().FirstOrDefaultAsync(l => l.TheaterId == theater.Id);
    }

    // --- Admin ---

    public async Task<List<TheaterSummary>> AdminListAsync(ClaimsPrincipal user)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Theaters.AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new TheaterSummary(t.Id, t.Name, t.Slug, t.City, t.State, t.IsActive,
                t.Owner != null ? t.Owner.Email : null, t.Screens.Count, t.Employees.Count, t.Mode, t.GoLiveRequestedAt))
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
        // Admins set up theaters for owners they've signed, so these are live from the start.
        var theater = new Theater
        {
            Slug = input.Slug, IsActive = input.IsActive, CreatedAt = time.GetUtcNow(), UpdatedAt = time.GetUtcNow(),
            Mode = TheaterMode.Live, LiveSince = time.GetUtcNow(),
        };
        CopyProfile(input, theater);
        await LocateAsync(geocoder, theater, addressChanged: true, coordinatesEdited: Geo.Of(theater) is not null);
        AddStarterSetup(db, theater, time.GetUtcNow());
        await db.SaveChangesAsync();
        return theater;
    }

    // Adds a new theater with what every theater starts with: the default roles, its screens and a default price
    // schedule. With samples (self sign-up), the screens get a starter layout and the schedule starter prices, so a
    // demo theater can try a sale right away; the owner changes them during setup.
    internal static void AddStarterSetup(ApplicationDbContext db, Theater theater, DateTimeOffset now, int screens = 1, bool samples = false)
    {
        db.Theaters.Add(theater);
        db.TheaterRoles.AddRange(DefaultTheaterRoles.CreateFor(theater, now));
        for (var i = 1; i <= screens; i++)
        {
            List<int> rows = samples ? Enumerable.Repeat(15, 8).ToList() : [];
            db.Screens.Add(new Screen
            {
                Theater = theater, Name = $"Screen {i}", SortOrder = i - 1,
                RowSpots = rows, LargeSpots = Screen.BackHalfLarge(rows),
            });
        }
        db.PriceSchedules.Add(new PriceSchedule
        {
            Theater = theater, Name = PricingService.DefaultScheduleName, IsDefault = true,
            Options = samples
                ?
                [
                    new PriceOption { Name = "1 occupant", Price = 10m, SortOrder = 0 },
                    new PriceOption { Name = "2 occupants", Price = 15m, SortOrder = 1 },
                    new PriceOption { Name = "Car load", Description = "Up to 6 people", Price = 25m, SortOrder = 2 },
                ]
                : [],
        });
    }

    public async Task AdminUpdateAsync(ClaimsPrincipal user, Theater input)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.FirstOrDefaultAsync(t => t.Id == input.Id)
            ?? throw new NotFoundException("Theater not found.");
        await EnsureSlugFreeAsync(db, input.Slug, exceptId: theater.Id);
        var (addressChanged, coordinatesEdited) = (!Geo.SameAddress(theater, input), !SameCoordinates(theater, input));
        CopyProfile(input, theater);
        await EnsureSeasonCoversShowingsAsync(db, theater);
        await LocateAsync(geocoder, theater, addressChanged, coordinatesEdited);
        theater.Slug = input.Slug;
        theater.IsActive = input.IsActive;
        theater.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
    }

    // Deletes the theater with its screens, schedule, ticket sales, pricing, invitations, roles, subscription and draft
    // invoices (other invoices are kept), and employee accounts (employee accounts are only valid for this theater, so they go too).
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
        db.TicketAddOns.RemoveRange(db.TicketAddOns.Where(a => a.Ticket!.Showtime!.Screen!.TheaterId == id));
        db.TicketMoves.RemoveRange(db.TicketMoves.Where(m => m.Ticket!.Showtime!.Screen!.TheaterId == id));
        db.Tickets.RemoveRange(db.Tickets.Where(t => t.Showtime!.Screen!.TheaterId == id));
        db.ShowtimeFeatures.RemoveRange(db.ShowtimeFeatures.Where(f => f.Showtime!.Screen!.TheaterId == id));
        db.Showtimes.RemoveRange(db.Showtimes.Where(s => s.Screen!.TheaterId == id));
        db.FilmPosters.RemoveRange(db.FilmPosters.Where(p => p.Film!.TheaterId == id));
        db.Films.RemoveRange(db.Films.Where(f => f.TheaterId == id));
        db.TheaterLogos.RemoveRange(db.TheaterLogos.Where(l => l.TheaterId == id));
        db.AddOns.RemoveRange(db.AddOns.Where(a => a.TheaterId == id));
        db.PriceOptions.RemoveRange(db.PriceOptions.Where(o => o.Schedule!.TheaterId == id));
        db.PriceSchedules.RemoveRange(db.PriceSchedules.Where(s => s.TheaterId == id));
        // Every invoice past draft (issued, paid or void) is a record and is kept, detached from the theater; drafts and the
        // subscription go.
        foreach (var invoice in await db.Invoices.Where(i => i.TheaterId == id).ToListAsync())
        {
            if (invoice.Status == InvoiceStatus.Draft)
                db.Invoices.Remove(invoice);
            else
                (invoice.TheaterId, invoice.SubscriptionId) = (null, null);
        }
        db.Subscriptions.RemoveRange(db.Subscriptions.Where(x => x.TheaterId == id));
        db.Screens.RemoveRange(theater.Screens);
        db.Theaters.Remove(theater);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    internal static async Task EnsureSlugFreeAsync(ApplicationDbContext db, string slug, int? exceptId)
    {
        if (await db.Theaters.AnyAsync(t => t.Slug == slug && t.Id != exceptId))
            throw new AppValidationException($"The slug \"{slug}\" is already used by another theater.");
    }

    // Showtimes are entered in the theater's time zone, so it has to be one we can resolve.
    internal static void CopyProfile(Theater from, Theater to)
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
        if (from.Latitude.HasValue != from.Longitude.HasValue)
            throw new AppValidationException("Enter both latitude and longitude, or leave both blank to look them up from the address.");
        if (from is { Latitude: double lat, Longitude: double lon } && !GeoPoint.IsValid(lat, lon))
            throw new AppValidationException("Latitude must be between -90 and 90, and longitude between -180 and 180.");
        to.Latitude = from.Latitude;
        to.Longitude = from.Longitude;
        if (from.SeasonOpensOn > from.SeasonClosesOn)
            throw new AppValidationException("The season can't close before it opens.");
        to.SeasonOpensOn = from.SeasonOpensOn;
        to.SeasonClosesOn = from.SeasonClosesOn;
    }

    // --- Location ---

    // Looks up the coordinates from the address when it changed or there are none yet, unless the editor entered
    // them. A miss leaves them blank (the old ones belonged to the old address) and never blocks the save.
    internal static async Task LocateAsync(IGeocoder geocoder, Theater theater, bool addressChanged, bool coordinatesEdited)
    {
        if (coordinatesEdited || (!addressChanged && Geo.Of(theater) is not null))
            return;
        var query = Geo.AddressQuery(theater);
        var point = query is null ? null : await geocoder.GeocodeAsync(query);
        (theater.Latitude, theater.Longitude) = (point?.Latitude, point?.Longitude);
    }

    private static bool SameCoordinates(Theater a, Theater b) => a.Latitude == b.Latitude && a.Longitude == b.Longitude;

    // For TheaterGeocodingBackfill: locates active theaters that have an address but no coordinates. Returns how many
    // were found.
    internal async Task<int> LocateMissingAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var missing = await db.Theaters.Where(t => t.IsActive && (t.Latitude == null || t.Longitude == null)).ToListAsync(ct);
        var found = 0;
        foreach (var theater in missing)
        {
            if (Geo.AddressQuery(theater) is not string query || await geocoder.GeocodeAsync(query, ct) is not GeoPoint point)
                continue;
            (theater.Latitude, theater.Longitude) = (point.Latitude, point.Longitude);
            // Saved as each is found (lookups are a second apart), so a restart partway through keeps them.
            await db.SaveChangesAsync(ct);
            found++;
        }
        return found;
    }

    // The season can't be changed to leave out showings that are already scheduled (and may have tickets sold).
    private async Task EnsureSeasonCoversShowingsAsync(ApplicationDbContext db, Theater theater)
    {
        var now = time.GetUtcNow();
        var starts = await db.Showtimes.Where(s => s.Screen!.TheaterId == theater.Id && s.EndsAt > now)
            .Select(s => s.StartsAt).ToListAsync();
        var outside = starts.Select(s => DateOnly.FromDateTime(TheaterTime.ToLocal(theater, s)))
            .Where(d => !theater.IsInSeason(d)).Distinct().Order().ToList();
        if (outside.Count > 0)
            throw new AppValidationException(
                $"Showings are scheduled outside that season ({string.Join(", ", outside.Take(5).Select(d => d.ToString("MMM d, yyyy")))}" +
                $"{(outside.Count > 5 ? " …" : "")}). Move or remove them first.");
    }
}
