using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

// A showing the gate can sell: today's, or one still running.
public sealed record GateShowing(ShowtimeView Showing, int SpotCount, int Taken)
{
    public int Open => Math.Max(0, SpotCount - Taken);
}

// Sales and check-in at the gate. Selling requires SellAtGate; checking tickets requires AdmitGuests.
public sealed partial class TicketSalesService
{
    // --- Selling at the gate ---

    // Today's showings (in the theater's time zone) that haven't ended, plus any still running from last night.
    public async Task<List<GateShowing>> ListGateShowingsAsync(ClaimsPrincipal user, int theaterId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await FindTheaterAsync(db, theaterId);
        await auth.RequireAsync(user, theater, TheaterPermissions.SellAtGate);
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(TheaterTime.ToLocal(theater, now));
        var showtimes = (await ScheduleService.WithFeatures(db.Showtimes.AsNoTracking()).Include(s => s.Screen)
                .Where(s => s.Screen!.TheaterId == theaterId && s.EndsAt > now && s.StartsAt < now.AddDays(2))
                .ToListAsync())
            .Where(s => s.StartsAt <= now || DateOnly.FromDateTime(TheaterTime.ToLocal(theater, s.StartsAt)) == today)
            .OrderBy(s => s.StartsAt).ThenBy(s => s.Screen!.SortOrder)
            .ToList();
        var ids = showtimes.Select(s => s.Id).ToList();
        var taken = await db.Tickets
            .Where(t => ids.Contains(t.ShowtimeId) && (t.Status != TicketStatus.Held || t.HeldUntil > now))
            .GroupBy(t => t.ShowtimeId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count);
        return showtimes.Select(s => new GateShowing(ScheduleService.ToView(theater, s), s.Screen!.SpotCount,
            taken.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<ShowingForSale> GetGateShowingAsync(ClaimsPrincipal user, int showtimeId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var sale = await LoadShowingForSaleAsync(db, showtimeId, atGate: true);
        await auth.RequireAsync(user, sale.Theater, TheaterPermissions.SellAtGate);
        return sale;
    }

    // Holds a spot for the car at the gate while the attendant takes payment. Like an online hold, the first to
    // hold a spot gets it, and the attendant holds one spot at a time.
    public async Task<HoldView> HoldAtGateAsync(ClaimsPrincipal user, int showtimeId, int row, int spot)
    {
        var userId = Guard.RequireUserId(user);
        await using (var db = await dbFactory.CreateDbContextAsync())
            await auth.RequireAsync(user, await TheaterOfShowtimeAsync(db, showtimeId), TheaterPermissions.SellAtGate);
        return await HoldAsync(userId, showtimeId, row, spot, atGate: true);
    }

    // Takes a card-present payment for the held spot, sells it and checks the car in. With a gift card code (the
    // theater's own), the card pays first and the terminal is charged only for the rest.
    public async Task<TicketView> SellAtGateAsync(ClaimsPrincipal user, int ticketId, int priceOptionId, IReadOnlyList<int> addOnIds,
        string? giftCardCode = null)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var showtimeId = await db.Tickets.Where(t => t.Id == ticketId && t.UserId == userId).Select(t => (int?)t.ShowtimeId)
            .FirstOrDefaultAsync() ?? throw new AppValidationException("The hold on this spot ran out. Choose a spot again.");
        await auth.RequireAsync(user, await TheaterOfShowtimeAsync(db, showtimeId), TheaterPermissions.SellAtGate);
        var ticket = await SellHeldAsync(db, userId, ticketId, priceOptionId, addOnIds, typedCard: null, giftCardCode, atGate: true);
        return await LoadViewAsync(db, ticket.Id);
    }

    // --- Checking in ---

    // Finds tickets for this theater from what the attendant scanned or typed: the 4-character gate code (case and
    // spaces don't matter), the ticket code, or the QR code's link. Gate codes are only unique among upcoming
    // tickets, so they match tickets for showings from yesterday on, soonest first; there's normally one.
    public async Task<List<TicketLookup>> FindAtGateAsync(ClaimsPrincipal user, int theaterId, string input)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await FindTheaterAsync(db, theaterId);
        await auth.RequireAsync(user, theater, TheaterPermissions.AdmitGuests);
        var now = time.GetUtcNow();

        List<Ticket> tickets;
        if (ShortCodes.Normalize(input) is string shortCode)
        {
            var since = now.AddDays(-1);
            tickets = await TicketsWithShowings(db)
                .Where(t => t.Status == TicketStatus.Sold && t.ShortCode == shortCode
                    && t.Showtime!.Screen!.TheaterId == theaterId && t.Showtime.EndsAt > since)
                .ToListAsync();
        }
        else
        {
            var code = TicketLinks.CodeFrom(input);
            tickets = code.Length == 0 ? [] : await TicketsWithShowings(db)
                .Where(t => t.Status == TicketStatus.Sold && t.Code == code)
                .ToListAsync();
            if (tickets.FirstOrDefault(t => t.Showtime!.Screen!.TheaterId != theaterId) is Ticket elsewhere)
                throw new AppValidationException($"Not valid here: this ticket is for {elsewhere.Showtime!.Screen!.Theater!.Name}.");
        }
        if (tickets.Count == 0)
            throw new AppValidationException("No ticket matches that code. Check it and try again.");
        return tickets
            .Select(t => new TicketLookup(ToView(t), t.UserId == userId, CanAdmit: true, AdmitProblem(t)))
            .OrderBy(l => l.AdmitProblem is not null).ThenBy(l => l.View.Ticket.Showtime!.StartsAt)
            .ToList();
    }

    // --- Helpers ---

    private static async Task<Theater> FindTheaterAsync(ApplicationDbContext db, int theaterId) =>
        await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");

    private static async Task<Theater> TheaterOfShowtimeAsync(ApplicationDbContext db, int showtimeId) =>
        await db.Showtimes.AsNoTracking().Where(s => s.Id == showtimeId).Select(s => s.Screen!.Theater).FirstOrDefaultAsync()
            ?? throw new NotFoundException("Showing not found.");
}
