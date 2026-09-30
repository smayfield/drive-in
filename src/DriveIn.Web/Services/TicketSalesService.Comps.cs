using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

// A showing an employee can give free admission to: any that hasn't ended.
public sealed record CompShowing(ShowtimeView Showing, int SpotCount, int Taken)
{
    public int Open => Math.Max(0, SpotCount - Taken);
}

// A comp that is pending approval or has been issued, with who gave it.
public sealed record CompEntry(int TicketId, bool Pending, string SpotLabel, string GuestName, string? Reason, string OfferedBy,
    string ShowingTitle, DateTime ShowingStartsLocal, string ScreenName, DateTime CreatedLocal, bool Admitted);

public sealed record CompLogRow(CompEvent Event, DateTime AtLocal, DateTime ShowingStartsLocal);

public sealed record CompLog(List<CompLogRow> Rows, List<(string Name, int Count)> ByEmployee);

// Free admission ("comps"): an employee reserves a spot at a showing for a named guest at no charge. Each theater
// turns it on, and decides whether it needs approval and what limits apply (Theater.FreeAdmission*). Offering needs
// OfferFreeAdmission; approving, denying and withdrawing need ApproveFreeAdmission; the log needs ViewFreeAdmission.
// Everything is written to CompEvents.
public sealed partial class TicketSalesService
{
    public const int MaxCompLogRows = 500;

    // Showings that haven't ended, soonest first.
    public async Task<List<CompShowing>> ListCompShowingsAsync(ClaimsPrincipal user, int theaterId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await FindTheaterAsync(db, theaterId);
        await auth.RequireAsync(user, theater, TheaterPermissions.OfferFreeAdmission);
        var now = time.GetUtcNow();
        var showtimes = (await ScheduleService.WithFeatures(db.Showtimes.AsNoTracking()).Include(s => s.Screen)
                .Where(s => s.Screen!.TheaterId == theaterId && s.EndsAt > now)
                .ToListAsync())
            .OrderBy(s => s.StartsAt).ThenBy(s => s.Screen!.SortOrder).Take(100).ToList();
        var ids = showtimes.Select(s => s.Id).ToList();
        var taken = await db.Tickets
            .Where(t => ids.Contains(t.ShowtimeId) && (t.Status != TicketStatus.Held || t.HeldUntil > now))
            .GroupBy(t => t.ShowtimeId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count);
        return showtimes.Select(s => new CompShowing(ScheduleService.ToView(theater, s), s.Screen!.SpotCount,
            taken.GetValueOrDefault(s.Id))).ToList();
    }

    // The screen of a showing, for the seat map. Needs OfferFreeAdmission.
    public async Task<Screen> GetCompScreenAsync(ClaimsPrincipal user, int showtimeId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await TheaterOfShowtimeAsync(db, showtimeId);
        await auth.RequireAsync(user, theater, TheaterPermissions.OfferFreeAdmission);
        return await db.Showtimes.AsNoTracking().Where(s => s.Id == showtimeId).Select(s => s.Screen!).FirstAsync();
    }

    // Reserves a spot for a guest. With approval required (and the giver not able to approve) this is a request that
    // holds the spot until it's approved or denied and returns null; otherwise the guest's ticket is issued straight away.
    public async Task<TicketView?> OfferFreeAdmissionAsync(ClaimsPrincipal user, int showtimeId, int row, int spot,
        string guestName, string? guestEmail, string? reason, string baseUri)
    {
        var userId = Guard.RequireUserId(user);
        guestName = (guestName ?? "").Trim();
        guestEmail = string.IsNullOrWhiteSpace(guestEmail) ? null : guestEmail.Trim();
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        Ticket ticket;
        bool pending;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var showtime = await db.Showtimes.Include(s => s.Screen!.Theater).FirstOrDefaultAsync(s => s.Id == showtimeId)
                ?? throw new NotFoundException("Showing not found.");
            var theater = showtime.Screen!.Theater!;
            await auth.RequireAsync(user, theater, TheaterPermissions.OfferFreeAdmission);
            if (!theater.IsActive)
                throw new AppValidationException("This theater is inactive.");
            if (!theater.FreeAdmissionEnabled)
                throw new AppValidationException("This theater doesn't offer free admission.");
            var now = time.GetUtcNow();
            if (showtime.EndsAt <= now)
                throw new AppValidationException("This showing has ended.");
            if (guestName.Length is 0 or > 100)
                throw new AppValidationException("Enter the guest's name (up to 100 characters).");
            if (reason is { Length: > 500 })
                throw new AppValidationException("The reason can be at most 500 characters.");
            if (theater.FreeAdmissionRequiresReason && reason is null)
                throw new AppValidationException("Enter a reason for the free admission.");
            if (guestEmail is not null && (guestEmail.Length > 256 || !new EmailAddressAttribute().IsValid(guestEmail)))
                throw new AppValidationException("That email address doesn't look right.");
            var screen = showtime.Screen;
            if (row < 1 || row > screen.RowSpots.Count || spot < 1 || spot > screen.RowSpots[row - 1])
                throw new NotFoundException("That spot isn't on this screen.");
            var label = SpotLabels.Spot(screen.LabelScheme, row, spot);

            var comps = db.Tickets.Where(t => t.ShowtimeId == showtimeId && t.IsComp);
            if (theater.FreeAdmissionMaxPerShowing is int maxShowing && await comps.CountAsync() >= maxShowing)
                throw new AppValidationException($"This showing already has its limit of {maxShowing} free admissions.");
            if (theater.FreeAdmissionMaxPerEmployeePerShowing is int maxEmployee
                && await comps.CountAsync(t => t.SoldById == userId) >= maxEmployee)
                throw new AppValidationException($"You've already given your limit of {maxEmployee} free admissions for this showing.");

            var existing = await db.Tickets.FirstOrDefaultAsync(t => t.ShowtimeId == showtimeId && t.Row == row && t.Spot == spot);
            if (existing is not null && !(existing.Status == TicketStatus.Held && existing.HeldUntil <= now))
                throw Taken(label);

            var canApprove = (await auth.AuthorizeAsync(user, theater,
                new TheaterPermissionRequirement(TheaterPermissions.ApproveFreeAdmission))).Succeeded;
            pending = theater.FreeAdmissionRequiresApproval && !canApprove;
            ticket = new Ticket
            {
                ShowtimeId = showtimeId, Row = row, Spot = spot, SpotLabel = label, CreatedAt = now,
                IsComp = true, GuestName = guestName, CompReason = reason, Email = guestEmail, SoldById = userId,
                IsTest = theater.IsDemo, Status = TicketStatus.Pending,
            };
            if (!pending)
                await IssueCompAsync(db, ticket, theater, now);

            await using var tx = await db.Database.BeginTransactionAsync();
            if (existing is not null)
            {
                db.Tickets.Remove(existing); // an expired hold
                await db.SaveChangesAsync();
            }
            db.Tickets.Add(ticket);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                throw Taken(label);
            }
            db.CompEvents.Add(await NewCompEventAsync(db, theater, showtime, ticket,
                pending ? CompAction.Requested : CompAction.Issued, userId, offeredBy: null, note: null));
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        events.Publish(showtimeId);

        return pending ? null : await SendCompReceiptAsync(ticket.Id, baseUri);
    }

    // Approves a pending request: the guest's ticket is issued.
    public async Task<TicketView> ApproveFreeAdmissionAsync(ClaimsPrincipal user, int ticketId, string baseUri)
    {
        var userId = Guard.RequireUserId(user);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var ticket = await db.Tickets.Include(t => t.Showtime!.Screen!.Theater)
                .FirstOrDefaultAsync(t => t.Id == ticketId && t.IsComp && t.Status == TicketStatus.Pending)
                ?? throw new NotFoundException("That request isn't waiting for approval any more.");
            var showtime = ticket.Showtime!;
            var theater = showtime.Screen!.Theater!;
            await auth.RequireAsync(user, theater, TheaterPermissions.ApproveFreeAdmission);
            var now = time.GetUtcNow();
            if (showtime.EndsAt <= now)
                throw new AppValidationException("This showing has ended. Deny the request to clear it.");
            var offeredBy = await NameOfAsync(db, ticket.SoldById);
            await IssueCompAsync(db, ticket, theater, now);
            db.CompEvents.Add(await NewCompEventAsync(db, theater, showtime, ticket, CompAction.Approved, userId, offeredBy, note: null));
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new AppValidationException("This request was just handled by someone else.");
            }
            events.Publish(ticket.ShowtimeId);
        }
        return await SendCompReceiptAsync(ticketId, baseUri);
    }

    // Denies a pending request: the spot goes back on sale.
    public async Task DenyFreeAdmissionAsync(ClaimsPrincipal user, int ticketId, string? note)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ticket = await db.Tickets.Include(t => t.Showtime!.Screen!.Theater)
            .FirstOrDefaultAsync(t => t.Id == ticketId && t.IsComp && t.Status == TicketStatus.Pending)
            ?? throw new NotFoundException("That request isn't waiting for approval any more.");
        var theater = ticket.Showtime!.Screen!.Theater!;
        await auth.RequireAsync(user, theater, TheaterPermissions.ApproveFreeAdmission);
        await RemoveCompAsync(db, ticket, theater, CompAction.Denied, userId, note);
    }

    // Withdraws a free ticket that hasn't been used, or a pending request. The giver may withdraw their own pending
    // request; anything else needs ApproveFreeAdmission.
    public async Task CancelFreeAdmissionAsync(ClaimsPrincipal user, int ticketId, string? note)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ticket = await db.Tickets.Include(t => t.Showtime!.Screen!.Theater)
            .FirstOrDefaultAsync(t => t.Id == ticketId && t.IsComp && (t.Status == TicketStatus.Pending || t.Status == TicketStatus.Sold))
            ?? throw new NotFoundException("Free admission not found.");
        var theater = ticket.Showtime!.Screen!.Theater!;
        var own = ticket.Status == TicketStatus.Pending && ticket.SoldById == userId;
        await auth.RequireAsync(user, theater, own ? TheaterPermissions.OfferFreeAdmission : TheaterPermissions.ApproveFreeAdmission);
        if (ticket.AdmittedAt is not null)
            throw new AppValidationException("This guest has already been let in, so it can't be withdrawn.");
        await RemoveCompAsync(db, ticket, theater, CompAction.Cancelled, userId, note);
    }

    // Requests waiting for approval, oldest first.
    public async Task<List<CompEntry>> ListPendingFreeAdmissionAsync(ClaimsPrincipal user, int theaterId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await FindTheaterAsync(db, theaterId);
        await auth.RequireAsync(user, theater, TheaterPermissions.ApproveFreeAdmission);
        return await ListCompsAsync(db, theater, t => t.Status == TicketStatus.Pending);
    }

    // The requests and free tickets the user gave for showings that haven't ended.
    public async Task<List<CompEntry>> ListMyFreeAdmissionAsync(ClaimsPrincipal user, int theaterId)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await FindTheaterAsync(db, theaterId);
        await auth.RequireAsync(user, theater, TheaterPermissions.OfferFreeAdmission);
        var now = time.GetUtcNow();
        return await ListCompsAsync(db, theater, t => t.SoldById == userId && t.Showtime!.EndsAt > now);
    }

    // The log, newest first (up to MaxCompLogRows), and how many each employee has given.
    public async Task<CompLog> GetFreeAdmissionLogAsync(ClaimsPrincipal user, int theaterId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await FindTheaterAsync(db, theaterId);
        await auth.RequireAsync(user, theater, TheaterPermissions.ViewFreeAdmission);
        var rows = (await db.CompEvents.AsNoTracking().Where(e => e.TheaterId == theaterId)
                .OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Take(MaxCompLogRows).ToListAsync())
            .Select(e => new CompLogRow(e, TheaterTime.ToLocal(theater, e.At), TheaterTime.ToLocal(theater, e.ShowingStartsAt)))
            .ToList();
        // Requested / Issued events are written by the giver themselves (later events name them in OfferedByName).
        var byEmployee = (await db.CompEvents.AsNoTracking()
                .Where(e => e.TheaterId == theaterId && (e.Action == CompAction.Requested || e.Action == CompAction.Issued))
                .GroupBy(e => e.ActorName).Select(g => new { Name = g.Key, Count = g.Count() }).ToListAsync())
            .OrderByDescending(g => g.Count).ThenBy(g => g.Name).Select(g => (g.Name, g.Count)).ToList();
        return new CompLog(rows, byEmployee);
    }

    // --- Helpers ---

    private static async Task IssueCompAsync(ApplicationDbContext db, Ticket ticket, Theater theater, DateTimeOffset now)
    {
        ticket.Status = TicketStatus.Sold;
        ticket.HeldUntil = null;
        ticket.SoldAt = now;
        ticket.OptionName = "Free admission";
        ticket.OptionPrice = 0;
        ticket.Total = 0;
        ticket.Code = NewCode();
        ticket.ShortCode = await NewShortCodeAsync(db, theater.Id, now);
        ticket.Stamp = Guid.NewGuid();
    }

    private async Task RemoveCompAsync(ApplicationDbContext db, Ticket ticket, Theater theater, CompAction action, string userId, string? note)
    {
        var offeredBy = await NameOfAsync(db, ticket.SoldById);
        db.CompEvents.Add(await NewCompEventAsync(db, theater, ticket.Showtime!, ticket, action, userId, offeredBy, note));
        db.Tickets.Remove(ticket);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AppValidationException("This was just changed by someone else. Reload and check.");
        }
        events.Publish(ticket.ShowtimeId);
    }

    private async Task<CompEvent> NewCompEventAsync(ApplicationDbContext db, Theater theater, Showtime showtime, Ticket ticket,
        CompAction action, string actorId, string? offeredBy, string? note)
    {
        var withFeatures = await ScheduleService.WithFeatures(db.Showtimes.AsNoTracking()).Include(s => s.Screen)
            .FirstAsync(s => s.Id == showtime.Id);
        return new CompEvent
        {
            TheaterId = theater.Id, TicketId = ticket.Id == 0 ? null : ticket.Id, Action = action, At = time.GetUtcNow(),
            ActorId = actorId, ActorName = Truncate(await NameOfAsync(db, actorId) ?? "(unknown)", 256),
            OfferedByName = action is CompAction.Requested or CompAction.Issued ? null : offeredBy,
            ShowingTitle = Truncate(ScheduleService.ToView(theater, withFeatures).Title, 200), ShowingStartsAt = showtime.StartsAt,
            SpotLabel = ticket.SpotLabel, GuestName = ticket.GuestName ?? "", Reason = ticket.CompReason,
            Note = string.IsNullOrWhiteSpace(note) ? null : Truncate(note.Trim(), 500), IsTest = theater.IsDemo,
        };
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static async Task<string?> NameOfAsync(ApplicationDbContext db, string? userId) =>
        userId is null ? null
            : await db.Users.AsNoTracking().Where(u => u.Id == userId)
                .Select(u => string.IsNullOrEmpty(u.DisplayName) ? u.Email : u.DisplayName).FirstOrDefaultAsync();

    private static async Task<List<CompEntry>> ListCompsAsync(ApplicationDbContext db, Theater theater, Expression<Func<Ticket, bool>> filter)
    {
        var tickets = await TicketsWithShowings(db).Include(t => t.SoldBy)
            .Where(t => t.IsComp && t.Showtime!.Screen!.TheaterId == theater.Id
                && (t.Status == TicketStatus.Pending || t.Status == TicketStatus.Sold))
            .Where(filter).ToListAsync();
        return tickets.OrderBy(t => t.CreatedAt).Select(t =>
        {
            var view = ScheduleService.ToView(theater, t.Showtime!);
            var giver = t.SoldBy is null ? "(deleted account)" : t.SoldBy.DisplayName is { Length: > 0 } n ? n : t.SoldBy.Email ?? "";
            return new CompEntry(t.Id, t.Status == TicketStatus.Pending, t.SpotLabel, t.GuestName ?? "", t.CompReason, giver,
                view.Title, view.StartsLocal, view.ScreenName, TheaterTime.ToLocal(theater, t.CreatedAt), t.AdmittedAt is not null);
        }).ToList();
    }

    // Emails the guest their ticket when the giver entered an address; the ticket stands if the email fails.
    private async Task<TicketView> SendCompReceiptAsync(int ticketId, string baseUri)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var view = await LoadViewAsync(db, ticketId);
        if (!string.IsNullOrEmpty(view.Ticket.Email))
            await TrySendReceiptAsync(view, baseUri);
        return view;
    }
}
