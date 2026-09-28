using System.Security.Claims;
using System.Security.Cryptography;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

// A showing as offered for sale. Local times are in the theater's time zone.
public sealed record ShowingForSale(
    Theater Theater, Screen Screen, ShowtimeView Showing, PriceSchedule Prices, List<AddOn> AddOns, string? NotOnSaleReason)
{
    public bool OnSale => NotOnSaleReason is null;
}

public enum SpotState
{
    Available,
    Held,  // someone else is paying for it; it comes back if their hold runs out
    Sold,
    Mine,  // held by the viewer
}

public sealed record HoldView(int TicketId, int Row, int Spot, string SpotLabel, DateTimeOffset HeldUntil);

// Spots that aren't listed are available.
public sealed record SpotAvailability(IReadOnlyDictionary<(int Row, int Spot), SpotState> Spots, HoldView? MyHold)
{
    public SpotState this[int row, int spot] => Spots.GetValueOrDefault((row, spot), SpotState.Available);
}

public sealed record PurchaseInput(int PriceOptionId, IReadOnlyList<int> AddOnIds, CardInput? Card);

public sealed record PurchaseResult(string Code, bool ReceiptSent);

// A sold ticket with its showing (local times in the theater's time zone).
public sealed record TicketView(Ticket Ticket, Theater Theater, ShowtimeView Showing, DateTime SoldLocal, DateTime? AdmittedLocal)
{
    public string Code => Ticket.Code!;
}

// A ticket looked up by its code, as the viewer may see it: its buyer, or gate staff who can admit it.
public sealed record TicketLookup(TicketView View, bool IsBuyer, bool CanAdmit, string? AdmitProblem);

// Online ticket sales. Buyers are any signed-in user: they pick a showing and a spot on its screen, hold the spot for
// Ticket.HoldMinutes while they pay, and get an emailed receipt whose QR code is checked at the gate. Admitting
// guests requires AdmitGuests at the theater. Every change to a showing's spots is published on SpotEvents so
// open seat maps update live.
public sealed class TicketSalesService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IAuthorizationService auth,
    IPaymentProcessor payments,
    IAppEmailSender email,
    SpotEvents events,
    TimeProvider time,
    ILogger<TicketSalesService> logger)
{
    // Gates open this long before the first film; a ticket admits until the showing ends.
    public const int AdmitOpensHoursBefore = 3;

    // --- Browsing ---

    // The showings of an active theater that haven't started yet, soonest first.
    public async Task<(Theater Theater, List<ShowtimeView> Showings)> ListOnSaleAsync(ClaimsPrincipal user, string slug)
    {
        Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug && t.IsActive)
            ?? throw new NotFoundException("Theater not found.");
        var now = time.GetUtcNow();
        var showtimes = await ScheduleService.WithFeatures(db.Showtimes.AsNoTracking()).Include(s => s.Screen)
            .Where(s => s.Screen!.TheaterId == theater.Id && s.StartsAt > now)
            .ToListAsync();
        return (theater, showtimes
            .OrderBy(s => s.StartsAt).ThenBy(s => s.Screen!.SortOrder)
            .Select(s => ScheduleService.ToView(theater, s))
            .ToList());
    }

    public async Task<ShowingForSale> GetShowingAsync(ClaimsPrincipal user, int showtimeId)
    {
        Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var showtime = await ScheduleService.WithFeatures(db.Showtimes.AsNoTracking()).Include(s => s.Screen!.Theater)
            .FirstOrDefaultAsync(s => s.Id == showtimeId);
        if (showtime is null || !showtime.Screen!.Theater!.IsActive)
            throw new NotFoundException("Showing not found.");
        var theater = showtime.Screen.Theater;
        var prices = await LoadPricesAsync(db, showtime);
        var addOns = await db.AddOns.AsNoTracking().Where(a => a.TheaterId == theater.Id && a.IsActive)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id).ToListAsync();
        return new ShowingForSale(theater, showtime.Screen, ScheduleService.ToView(theater, showtime), prices, addOns,
            NotOnSaleReason(showtime, prices));
    }

    public async Task<SpotAvailability> GetAvailabilityAsync(ClaimsPrincipal user, int showtimeId)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = time.GetUtcNow();
        var tickets = await db.Tickets.AsNoTracking()
            .Where(t => t.ShowtimeId == showtimeId && (t.Status != TicketStatus.Held || t.HeldUntil > now))
            .ToListAsync();
        var spots = new Dictionary<(int, int), SpotState>();
        HoldView? mine = null;
        foreach (var t in tickets)
        {
            var state = t.Status == TicketStatus.Sold ? SpotState.Sold : t.UserId == userId ? SpotState.Mine : SpotState.Held;
            spots[(t.Row, t.Spot)] = state;
            if (t.Status == TicketStatus.Held && t.UserId == userId)
                mine = new HoldView(t.Id, t.Row, t.Spot, t.SpotLabel, t.HeldUntil!.Value);
        }
        return new SpotAvailability(spots, mine);
    }

    // --- Buying ---

    // Holds a spot for the user while they pay; the first to hold a spot gets it. Holding another spot lets go of
    // any spot the user already holds, so one person can't tie up several at once.
    public async Task<HoldView> HoldAsync(ClaimsPrincipal user, int showtimeId, int row, int spot)
    {
        var userId = Guard.RequireUserId(user);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryHoldAsync(userId, showtimeId, row, spot);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                // An expired hold we were clearing was removed first (by the sweeper or another buyer): look again.
            }
        }
    }

    private async Task<HoldView> TryHoldAsync(string userId, int showtimeId, int row, int spot)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var showtime = await db.Showtimes.AsNoTracking().Include(s => s.Screen!.Theater)
            .FirstOrDefaultAsync(s => s.Id == showtimeId);
        if (showtime is null || !showtime.Screen!.Theater!.IsActive)
            throw new NotFoundException("Showing not found.");
        if (NotOnSaleReason(showtime, await LoadPricesAsync(db, showtime)) is string reason)
            throw new AppValidationException(reason);
        var screen = showtime.Screen;
        if (row < 1 || row > screen.RowSpots.Count || spot < 1 || spot > screen.RowSpots[row - 1])
            throw new NotFoundException("That spot isn't on this screen.");
        var label = SpotLabels.Spot(screen.LabelScheme, row, spot);
        var now = time.GetUtcNow();

        var existing = await db.Tickets.FirstOrDefaultAsync(t => t.ShowtimeId == showtimeId && t.Row == row && t.Spot == spot);
        if (existing is { Status: TicketStatus.Held } && existing.UserId == userId && existing.HeldUntil > now)
            return ToHold(existing); // already theirs; holding it again doesn't extend the hold
        if (existing is not null && !(existing.Status == TicketStatus.Held && existing.HeldUntil <= now))
            throw Taken(label);

        var released = await db.Tickets.Where(t => t.UserId == userId && t.Status == TicketStatus.Held).ToListAsync();
        var ticket = new Ticket
        {
            ShowtimeId = showtimeId, Row = row, Spot = spot, SpotLabel = label, UserId = userId,
            Status = TicketStatus.Held, HeldUntil = now.AddMinutes(Ticket.HoldMinutes), CreatedAt = now,
        };
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            // Deletes first, in their own save, so an expired hold on this spot is gone before the new one goes in.
            db.Tickets.RemoveRange(released);
            if (existing is not null && !released.Contains(existing))
                db.Tickets.Remove(existing);
            await db.SaveChangesAsync(); // DbUpdateConcurrencyException: retried by HoldAsync
            db.Tickets.Add(ticket);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                throw Taken(label); // someone else got there first
            }
            await tx.CommitAsync();
        }

        foreach (var id in released.Select(t => t.ShowtimeId).Append(showtimeId).Distinct())
            events.Publish(id);
        return ToHold(ticket);
    }

    // Lets go of the user's hold so the spot goes back on sale.
    public async Task ReleaseHoldAsync(ClaimsPrincipal user, int ticketId)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId && t.UserId == userId && t.Status == TicketStatus.Held);
        if (ticket is null)
            return;
        db.Tickets.Remove(ticket);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return; // already expired and released
        }
        events.Publish(ticket.ShowtimeId);
    }

    // Pays for a held spot. The hold must still be the user's and not have run out. On approval the spot is sold to
    // them and a receipt with the ticket's QR code is emailed. Sales are final.
    public async Task<PurchaseResult> PurchaseAsync(ClaimsPrincipal user, int ticketId, PurchaseInput input, string baseUri)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ticket = await db.Tickets.Include(t => t.Showtime!.Screen!.Theater)
            .FirstOrDefaultAsync(t => t.Id == ticketId && t.UserId == userId);
        var now = time.GetUtcNow();
        if (ticket is null || ticket.Status != TicketStatus.Held || ticket.HeldUntil <= now)
            throw new AppValidationException("Your hold on this spot ran out. Choose a spot again.");
        var showtime = ticket.Showtime!;
        var theater = showtime.Screen!.Theater!;
        var prices = await LoadPricesAsync(db, showtime);
        if (NotOnSaleReason(showtime, prices) is string reason)
            throw new AppValidationException(reason);

        var option = prices.Options.FirstOrDefault(o => o.Id == input.PriceOptionId)
            ?? throw new AppValidationException("Choose a ticket type.");
        var addOnIds = input.AddOnIds.Distinct().ToList();
        var addOns = await db.AddOns.AsNoTracking()
            .Where(a => addOnIds.Contains(a.Id) && a.TheaterId == theater.Id && a.IsActive)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id).ToListAsync();
        if (addOns.Count != addOnIds.Count)
            throw new AppValidationException("One of the add-ons you chose is no longer offered. Check your choices and try again.");
        var quote = TicketQuote.For(option, addOns);
        var card = quote.Total > 0 ? Cards.Validate(input.Card, now) : null;
        var buyerEmail = (await db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync())?.Trim();
        if (string.IsNullOrEmpty(buyerEmail))
            throw new AppValidationException("Your account needs an email address to receive tickets.");

        // Paying: the hold can no longer expire out from under the charge.
        ticket.Status = TicketStatus.Paying;
        ticket.Stamp = Guid.NewGuid();
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AppValidationException("Your hold on this spot ran out. Choose a spot again.");
        }

        PaymentResult result;
        try
        {
            result = card is null
                ? new PaymentResult(true, null)
                : await payments.ChargeAsync(new PaymentRequest(quote.Total,
                    $"{theater.Name}: {ScheduleService.ToView(theater, await WithFeaturesAsync(db, showtime)).Title}, spot {ticket.SpotLabel}", card));
        }
        catch
        {
            await BackToHeldAsync(db, ticket);
            throw;
        }
        if (!result.Approved)
        {
            await BackToHeldAsync(db, ticket);
            throw new AppValidationException($"Your payment wasn't approved: {result.DeclineReason ?? "declined"}. Check your card details or try another card.");
        }

        ticket.Status = TicketStatus.Sold;
        ticket.HeldUntil = null;
        ticket.SoldAt = now;
        ticket.Email = buyerEmail;
        ticket.OptionName = option.Name;
        ticket.OptionPrice = option.Price;
        ticket.AddOns = quote.Lines.Select((l, i) => new TicketAddOn
        {
            Position = i + 1, Name = l.AddOn.Name, Kind = l.AddOn.Kind, Amount = l.AddOn.Amount, Effect = l.Effect,
        }).ToList();
        ticket.Total = quote.Total;
        ticket.CardBrand = card is null ? null : Cards.Brand(card.Number);
        ticket.CardLast4 = card?.Number[^4..];
        ticket.PaymentReference = result.Reference;
        ticket.Code = NewCode();
        ticket.Stamp = Guid.NewGuid();
        await db.SaveChangesAsync();
        events.Publish(ticket.ShowtimeId);

        var sent = await TrySendReceiptAsync(await LoadViewAsync(db, ticket.Id), baseUri);
        return new PurchaseResult(ticket.Code, sent);
    }

    // --- Tickets ---

    public async Task<List<TicketView>> ListMyTicketsAsync(ClaimsPrincipal user)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var tickets = await TicketsWithShowings(db)
            .Where(t => t.UserId == userId && t.Status == TicketStatus.Sold)
            .ToListAsync();
        return tickets.Select(ToView).OrderBy(v => v.Ticket.Showtime!.StartsAt).ToList();
    }

    // The buyer sees their ticket; staff who can admit guests at its theater see it with the admit option.
    public async Task<TicketLookup> GetByCodeAsync(ClaimsPrincipal user, string code)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ticket = await TicketsWithShowings(db).FirstOrDefaultAsync(t => t.Code == code && t.Status == TicketStatus.Sold)
            ?? throw new NotFoundException("Ticket not found.");
        var isBuyer = ticket.UserId == userId;
        var canAdmit = (await auth.AuthorizeAsync(user, ticket.Showtime!.Screen!.Theater!,
            new TheaterPermissionRequirement(TheaterPermissions.AdmitGuests))).Succeeded;
        if (!isBuyer && !canAdmit)
            throw new AccessDeniedException();
        return new TicketLookup(ToView(ticket), isBuyer, canAdmit, AdmitProblem(ticket));
    }

    // Lets the car in: the ticket must be for a showing whose gates are open today, and not used already.
    public async Task AdmitAsync(ClaimsPrincipal user, string code)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ticket = await db.Tickets.Include(t => t.Showtime!.Screen!.Theater)
            .FirstOrDefaultAsync(t => t.Code == code && t.Status == TicketStatus.Sold)
            ?? throw new NotFoundException("Ticket not found.");
        await auth.RequireAsync(user, ticket.Showtime!.Screen!.Theater!, TheaterPermissions.AdmitGuests);
        if (AdmitProblem(ticket) is string problem)
            throw new AppValidationException(problem);
        ticket.AdmittedAt = time.GetUtcNow();
        ticket.Stamp = Guid.NewGuid();
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AppValidationException("This ticket was just used.");
        }
    }

    public async Task<bool> ResendReceiptAsync(ClaimsPrincipal user, string code, string baseUri)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ticket = await TicketsWithShowings(db)
            .FirstOrDefaultAsync(t => t.Code == code && t.Status == TicketStatus.Sold && t.UserId == userId)
            ?? throw new NotFoundException("Ticket not found.");
        return await TrySendReceiptAsync(ToView(ticket), baseUri);
    }

    // --- Housekeeping ---

    // Deletes holds that ran out and tells open seat maps. Run periodically by HoldExpiryService; also safe to run
    // alongside buyers, since everything else treats an expired hold as available.
    public async Task<int> ReleaseExpiredHoldsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        var expired = await db.Tickets.Where(t => t.Status == TicketStatus.Held && t.HeldUntil <= now).ToListAsync(ct);
        if (expired.Count == 0)
            return 0;
        db.Tickets.RemoveRange(expired);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return 0; // a buyer took one of these spots meanwhile; the next pass gets the rest
        }
        foreach (var id in expired.Select(t => t.ShowtimeId).Distinct())
            events.Publish(id);
        return expired.Count;
    }

    // --- Helpers ---

    // Showtime.Screen.Theater must be loaded.
    private string? NotOnSaleReason(Showtime showtime, PriceSchedule prices)
    {
        if (!showtime.Screen!.Theater!.IsActive)
            return "This theater isn't selling tickets online.";
        if (showtime.StartsAt <= time.GetUtcNow())
            return "This showing has started, so tickets are no longer sold online.";
        if (!payments.IsAvailable)
            return "Online ticket sales aren't available yet.";
        if (prices.Options.Count == 0)
            return "Tickets for this showing aren't on sale yet.";
        return null;
    }

    private string? AdmitProblem(Ticket ticket)
    {
        var showtime = ticket.Showtime!;
        var theater = showtime.Screen!.Theater!;
        var now = time.GetUtcNow();
        if (ticket.AdmittedAt is DateTimeOffset at)
            return $"Already used: admitted {TheaterTime.ToLocal(theater, at):ddd, MMM d h:mm tt}.";
        if (now >= showtime.EndsAt)
            return $"This ticket was for {TheaterTime.ToLocal(theater, showtime.StartsAt):ddd, MMM d} and can't be used on a later date.";
        if (now < showtime.StartsAt.AddHours(-AdmitOpensHoursBefore))
            return $"This ticket is for {TheaterTime.ToLocal(theater, showtime.StartsAt):ddd, MMM d h:mm tt}. " +
                   $"Gates open {AdmitOpensHoursBefore} hours before the showing.";
        return null;
    }

    private static async Task<PriceSchedule> LoadPricesAsync(ApplicationDbContext db, Showtime showtime)
    {
        var schedules = db.PriceSchedules.AsNoTracking().Include(s => s.Options).Where(s => s.TheaterId == showtime.Screen!.TheaterId);
        var schedule = await (showtime.PriceScheduleId is int id ? schedules.Where(s => s.Id == id) : schedules.Where(s => s.IsDefault))
            .FirstOrDefaultAsync() ?? new PriceSchedule();
        schedule.Options.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
        return schedule;
    }

    private static async Task<Showtime> WithFeaturesAsync(ApplicationDbContext db, Showtime showtime)
    {
        await db.Entry(showtime).Collection(s => s.Features).Query().Include(f => f.Film).LoadAsync();
        return showtime;
    }

    private static IQueryable<Ticket> TicketsWithShowings(ApplicationDbContext db) =>
        db.Tickets.AsNoTracking()
            .Include(t => t.AddOns)
            .Include(t => t.Showtime!.Screen!.Theater)
            .Include(t => t.Showtime!.Features).ThenInclude(f => f.Film);

    private static async Task<TicketView> LoadViewAsync(ApplicationDbContext db, int ticketId) =>
        ToView(await TicketsWithShowings(db).SingleAsync(t => t.Id == ticketId));

    private static TicketView ToView(Ticket ticket)
    {
        var theater = ticket.Showtime!.Screen!.Theater!;
        ticket.AddOns.Sort((a, b) => a.Position.CompareTo(b.Position));
        return new TicketView(ticket, theater, ScheduleService.ToView(theater, ticket.Showtime),
            TheaterTime.ToLocal(theater, ticket.SoldAt!.Value),
            ticket.AdmittedAt is DateTimeOffset at ? TheaterTime.ToLocal(theater, at) : null);
    }

    private static HoldView ToHold(Ticket t) => new(t.Id, t.Row, t.Spot, t.SpotLabel, t.HeldUntil!.Value);

    private static async Task BackToHeldAsync(ApplicationDbContext db, Ticket ticket)
    {
        ticket.Status = TicketStatus.Held;
        ticket.Stamp = Guid.NewGuid();
        await db.SaveChangesAsync();
    }

    private async Task<bool> TrySendReceiptAsync(TicketView view, string baseUri)
    {
        try
        {
            var receipt = TicketReceipt.Build(view, baseUri);
            await email.SendAsync(view.Ticket.Email!, receipt.Subject, receipt.Html, receipt.Images);
            return true;
        }
        catch (Exception ex)
        {
            // The sale stands; the ticket is under My tickets and the receipt can be sent again from there.
            logger.LogError(ex, "Couldn't email the receipt for ticket {TicketId}", view.Ticket.Id);
            return false;
        }
    }

    private static AppValidationException Taken(string label) =>
        new($"Sorry, someone else just took spot {label}. Please choose another spot.");

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

    // 128 random bits, URL-safe.
    private static string NewCode() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
}

// What a ticket costs: the option's price, plus fees, minus discounts (percent discounts are off the option's price),
// never below zero.
public sealed record TicketQuote(PriceOption Option, List<(AddOn AddOn, decimal Effect)> Lines, decimal Total)
{
    public static TicketQuote For(PriceOption option, IEnumerable<AddOn> addOns)
    {
        var lines = addOns.Select(a => (a, a.Kind switch
        {
            AddOnKind.Fee => a.Amount,
            AddOnKind.Discount => -a.Amount,
            AddOnKind.PercentDiscount => -Math.Round(option.Price * a.Amount / 100m, 2, MidpointRounding.AwayFromZero),
            _ => 0m,
        })).ToList();
        return new TicketQuote(option, lines, Math.Max(0m, option.Price + lines.Sum(l => l.Item2)));
    }
}

// Open seat maps subscribe to hear when a showing's spots change (held, released, sold). In-process: the app runs as
// a single server; running several would need a shared bus (e.g. Postgres LISTEN/NOTIFY) instead.
public sealed class SpotEvents
{
    public event Action<int>? Changed;

    public void Publish(int showtimeId)
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<int>)handler)(showtimeId);
            }
            catch
            {
                // One broken subscriber (e.g. a closing circuit) mustn't stop the others hearing about it.
            }
        }
    }
}

// Releases expired holds every few seconds so everyone's seat map shows the spot free again promptly.
public sealed class HoldExpiryService(IServiceScopeFactory scopes, TimeProvider time, ILogger<HoldExpiryService> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TicketSalesService>().ReleaseExpiredHoldsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Couldn't release expired ticket holds");
            }
        }
    }
}
