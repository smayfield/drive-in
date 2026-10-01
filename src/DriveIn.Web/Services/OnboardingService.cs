using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DriveIn.Web.Services;

public sealed record NewTheaterInput(
    string Name, string? City, string? State, string TimeZone, int Screens, bool AcceptTerms, string? Slug = null);

public sealed record SetupStep(string Title, string Detail, bool Done, string? Href, bool Optional = false);

// What the Standard plan would bill: screens × the monthly price, for each calendar month the season touches.
// BilledMonths is null when the theater has no season (billed every month).
public sealed record PlanQuote(int Screens, decimal? MonthlyTotal, int? BilledMonths, decimal? SeasonTotal)
{
    public static PlanQuote For(int screens, decimal? pricePerScreen, DateOnly? opens, DateOnly? closes)
    {
        int? months = opens is DateOnly o && closes is DateOnly c && c >= o
            ? (c.Year - o.Year) * 12 + c.Month - o.Month + 1
            : null;
        var monthly = pricePerScreen * screens;
        return new PlanQuote(screens, monthly, months, monthly * months);
    }
}

// Self-service sign-up and the path from demo to live. Any signed-in user (not an employee account) can sign a
// theater up; it starts in demo mode with them as owner. Going live is the owner's (or an admin's) request, since
// it starts billing, and an admin activates it, which starts the theater's subscription (BillingService).
public sealed partial class OnboardingService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IAppEmailSender email,
    IOptions<PlanOptions> plans,
    IOptions<CompanyOptions> company,
    BillingService billing,
    TimeProvider time,
    IGeocoder geocoder,
    DriveInMetrics metrics,
    ILogger<OnboardingService> logger)
{
    public const int MaxScreens = Screen.MaxPerTheater;

    // --- Sign-up ---

    public async Task<Theater> CreateDemoTheaterAsync(ClaimsPrincipal user, NewTheaterInput input)
    {
        var userId = Guard.RequireUserId(user);
        if (user.GetEmployeeTheaterId() is not null)
            throw new AppValidationException("Employee accounts belong to one theater and can't sign up a new one. " +
                "Sign out and create your own account to sign up a theater.");
        if (!input.AcceptTerms)
            throw new AppValidationException("Please accept the Terms of Service to continue.");
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0)
            throw new AppValidationException("Enter your theater's name.");
        if (name.Length > 200)
            throw new AppValidationException("Theater names can be at most 200 characters.");
        if (input.Screens is < 1 or > MaxScreens)
            throw new AppValidationException($"Choose 1 to {MaxScreens} screens. You can change this later.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var owned = await db.Theaters.CountAsync(t => t.OwnerId == userId);
        if (owned >= plans.Value.MaxTheatersPerOwner)
            throw new AppValidationException($"An account can sign up at most {plans.Value.MaxTheatersPerOwner} theaters. " +
                "Contact us if you need more.");

        var slug = string.IsNullOrWhiteSpace(input.Slug) ? await FreeSlugAsync(db, Slugify(name)) : input.Slug.Trim().ToLowerInvariant();
        if (!SlugPattern().IsMatch(slug) || slug.Length > 100)
            throw new AppValidationException("Web addresses can use lowercase letters, digits and single hyphens.");
        await TheaterService.EnsureSlugFreeAsync(db, slug, exceptId: null);

        var now = time.GetUtcNow();
        var theater = new Theater
        {
            Slug = slug, OwnerId = userId, IsActive = true, Mode = TheaterMode.Demo,
            CreatedAt = now, UpdatedAt = now, TermsAcceptedAt = now, TermsVersion = company.Value.TermsVersion,
        };
        // Validates the time zone the same way the profile form does.
        TheaterService.CopyProfile(new Theater
        {
            Name = name, City = Clean(input.City, 100), State = Clean(input.State, 100), Country = "US", TimeZone = input.TimeZone,
        }, theater);
        if (!TheaterTime.IsValidZone(theater.TimeZone))
            throw new AppValidationException("Choose your theater's time zone.");
        await TheaterService.LocateAsync(geocoder, theater, addressChanged: true, coordinatesEdited: false);
        TheaterService.AddStarterSetup(db, theater, now, input.Screens, samples: true);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            throw new AppValidationException($"The web address \"{slug}\" was just taken. Try another.");
        }
        metrics.TheaterSignedUp();
        return theater;
    }

    // A checklist of what a new theater still needs, for the manage page. Any member may see it.
    public async Task<List<SetupStep>> GetSetupStepsAsync(ClaimsPrincipal user, int theaterId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.AsNoTracking().Include(t => t.Screens).FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        Guard.RequireMember(user, theater);
        var now = time.GetUtcNow();
        var prices = await db.PriceOptions.AnyAsync(o => o.Schedule!.TheaterId == theaterId && o.Schedule.IsDefault);
        var showings = await db.Showtimes.AnyAsync(s => s.Screen!.TheaterId == theaterId && s.EndsAt > now);
        var staff = await db.Users.AnyAsync(u => u.EmployeeTheaterId == theaterId)
            || await db.Invitations.AnyAsync(i => i.TheaterId == theaterId);
        var testSale = await db.Tickets.AnyAsync(t => t.Showtime!.Screen!.TheaterId == theaterId && t.Status == TicketStatus.Sold);
        var manage = $"manage/{theaterId}";
        return
        [
            new("Profile and time zone", "Your address, phone and description, and the time zone showtimes are in.",
                TheaterTime.IsValidZone(theater.TimeZone) && !string.IsNullOrWhiteSpace(theater.AddressLine1), manage),
            new("Operating season", "Opening and closing dates. You're only billed for months in season.",
                theater.SeasonOpensOn is not null && theater.SeasonClosesOn is not null, manage),
            new("Screens and spots", "Name each screen and lay out its rows of parking spots (we started you with 8 rows of 15).",
                theater.Screens.Count > 0 && theater.Screens.All(s => s.SpotCount > 0), manage),
            new("Ticket prices", "Your ticket options (we started you with sample prices) and any add-ons.", prices, $"{manage}/pricing"),
            new("Films and showings", "Add films and schedule showings, including double features.", showings, $"{manage}/schedule"),
            new("Staff", "Invite employees and give them roles.", staff, $"{manage}/employees", Optional: true),
            new("Try a test sale", "Buy a ticket from your theater's page and check it in at the gate. Demo sales use test payments.",
                testSale, $"theaters/{theater.Slug}", Optional: true),
        ];
    }

    // --- Going live ---

    // Starts billing, so only the owner (or an admin) can ask. Admins are emailed to review it.
    public async Task RequestGoLiveAsync(ClaimsPrincipal user, int theaterId, bool acceptBilling, string baseUri)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.FirstOrDefaultAsync(t => t.Id == theaterId) ?? throw new NotFoundException("Theater not found.");
        if (!TheaterAccess.HasFullAccess(user, theater))
            throw new AccessDeniedException("Only the theater's owner can take it live.");
        if (!theater.IsDemo)
            throw new AppValidationException("This theater is already live.");
        if (theater.GoLiveRequestedAt is not null)
            throw new AppValidationException("You've already asked to go live. We'll be in touch soon.");
        if (!acceptBilling)
            throw new AppValidationException("Please agree to the Standard plan's billing to continue.");
        if (!TheaterTime.IsValidZone(theater.TimeZone))
            throw new AppValidationException("Set your theater's time zone first (Profile), so showtimes are right.");
        theater.GoLiveRequestedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
        metrics.GoLiveRequested();

        var admins = await AdminEmailsAsync(db);
        var link = $"{baseUri.TrimEnd('/')}/admin/theaters/{theater.Id}";
        foreach (var to in admins)
            await TrySendAsync(to, $"Go-live request: {theater.Name}",
                $"<p>{WebUtility.HtmlEncode(theater.Name)} ({WebUtility.HtmlEncode(Location(theater))}) has asked to go live.</p>" +
                $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">Review it</a>.</p>");
    }

    public async Task<List<Theater>> ListGoLiveRequestsAsync(ClaimsPrincipal user)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Theaters.AsNoTracking().Include(t => t.Owner).Include(t => t.Screens)
            .Where(t => t.Mode == TheaterMode.Demo && t.GoLiveRequestedAt != null)
            .OrderBy(t => t.GoLiveRequestedAt)
            .ToListAsync();
    }

    // Takes the theater live: it's listed publicly and sells real tickets. Its demo test tickets and gift cards are deleted.
    public async Task ActivateAsync(ClaimsPrincipal user, int theaterId, string baseUri)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.Include(t => t.Owner).FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        if (!theater.IsDemo)
            throw new AppValidationException("This theater is already live.");
        await using var tx = await db.Database.BeginTransactionAsync();
        var tests = await db.Tickets.Include(t => t.AddOns)
            .Where(t => t.IsTest && t.Showtime!.Screen!.TheaterId == theaterId).ToListAsync();
        db.TicketAddOns.RemoveRange(tests.SelectMany(t => t.AddOns));
        var testIds = tests.Select(t => t.Id).ToList();
        db.TicketMoves.RemoveRange(await db.TicketMoves.Where(m => testIds.Contains(m.TicketId)).ToListAsync());
        db.Tickets.RemoveRange(tests);
        db.CompEvents.RemoveRange(await db.CompEvents.Where(e => e.IsTest && e.TheaterId == theaterId).ToListAsync());
        db.GiftCards.RemoveRange(await db.GiftCards.Include(g => g.Transactions).Where(g => g.IsTest && g.TheaterId == theaterId).ToListAsync());
        theater.Mode = TheaterMode.Live;
        theater.LiveSince = time.GetUtcNow();
        theater.GoLiveRequestedAt = null;
        // Billing starts now at the plan's current price, this month included. Without a configured price the theater
        // is listed under Admin → Billing as live without a subscription, for an admin to start at a price they enter.
        if (plans.Value.PricePerScreenPerMonth is decimal price && !await db.Subscriptions.AnyAsync(s => s.TheaterId == theaterId))
            db.Subscriptions.Add(BillingService.NewSubscription(theater, price, time.GetUtcNow()));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        metrics.TheaterActivated("go_live");
        await billing.GenerateDraftsCoreAsync(theaterId);

        if (theater.Owner?.Email is string to)
            await TrySendAsync(to, $"{theater.Name} is live on Drive-In Online",
                $"<p>{WebUtility.HtmlEncode(theater.Name)} is now live: it's listed publicly and selling real tickets.</p>" +
                $"<p><a href=\"{WebUtility.HtmlEncode($"{baseUri.TrimEnd('/')}/theaters/{theater.Slug}")}\">See your theater's page</a>.</p>");
    }

    // Clears the request so the owner can fix things and ask again; the note is emailed to them.
    public async Task DeclineGoLiveAsync(ClaimsPrincipal user, int theaterId, string? note)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.Include(t => t.Owner).FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        if (theater.GoLiveRequestedAt is null)
            throw new AppValidationException("This theater hasn't asked to go live.");
        theater.GoLiveRequestedAt = null;
        await db.SaveChangesAsync();
        if (theater.Owner?.Email is string to)
            await TrySendAsync(to, $"About taking {theater.Name} live",
                "<p>We couldn't take your theater live just yet.</p>" +
                (string.IsNullOrWhiteSpace(note) ? "" : $"<p>{WebUtility.HtmlEncode(note.Trim())}</p>") +
                "<p>Reply to this email with any questions, and ask again from your theater's page when you're ready.</p>");
    }

    public PlanQuote Quote(Theater theater, int screens) =>
        PlanQuote.For(screens, plans.Value.PricePerScreenPerMonth, theater.SeasonOpensOn, theater.SeasonClosesOn);

    // --- Helpers ---

    // "Starlight Drive-In" -> "starlight-drive-in".
    public static string Slugify(string name)
    {
        var ascii = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                ascii.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        var slug = HyphenRuns().Replace(ascii.ToString(), "-").Trim('-');
        slug = slug.Length > 90 ? slug[..90].TrimEnd('-') : slug;
        return slug.Length == 0 ? "theater" : slug;
    }

    private static async Task<string> FreeSlugAsync(ApplicationDbContext db, string slug)
    {
        var candidate = slug;
        for (var n = 2; await db.Theaters.AnyAsync(t => t.Slug == candidate); n++)
            candidate = $"{slug}-{n}";
        return candidate;
    }

    internal static async Task<List<string>> AdminEmailsAsync(ApplicationDbContext db) =>
        await (from ur in db.UserRoles
               join r in db.Roles on ur.RoleId equals r.Id
               join u in db.Users on ur.UserId equals u.Id
               where r.Name == Roles.Admin && u.Email != null
               select u.Email!).ToListAsync();

    private async Task TrySendAsync(string to, string subject, string html)
    {
        try
        {
            await email.SendAsync(to, subject, html);
        }
        catch (Exception ex)
        {
            // The recipient isn't logged: email addresses don't belong in production logs.
            logger.LogError(ex, "Couldn't send email {Subject}", subject);
        }
    }

    private static string Location(Theater t) =>
        string.Join(", ", new[] { t.City, t.State }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } s ? s : "no location";

    private static string? Clean(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("-+")]
    private static partial Regex HyphenRuns();
}
