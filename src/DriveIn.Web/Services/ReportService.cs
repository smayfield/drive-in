using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

// Cars counts every sold ticket, free ones included; Gross is what they sold for (gift card plus card). Attendance is
// only measured over showings that have ended, since a later showing's cars haven't had the chance to arrive.
public sealed record SalesTotals(int Showings, int Cars, int Free, int Admitted, int CarsAtEnded, int AdmittedAtEnded,
    decimal Gross, decimal GiftCardPaid, bool HasTestSales)
{
    public decimal CardPaid => Gross - GiftCardPaid;
    public double? AttendanceRate => CarsAtEnded == 0 ? null : (double)AdmittedAtEnded / CarsAtEnded;
}

// Capacity is the screen's current layout. NoShows is null until the showing has ended.
public sealed record ShowingReportRow(int ShowtimeId, DateTime StartsLocal, string Screen, string Films, int Capacity,
    int Cars, int Free, int Admitted, int? NoShows, decimal Gross, decimal GiftCardPaid)
{
    public double? Occupancy => Capacity == 0 ? null : (double)Cars / Capacity;
}

public sealed record DayReportRow(DateOnly Date, int Showings, int Cars, int Admitted, decimal Gross);

// A double feature's cars and gross count toward each of its films, so film rows don't add up to the totals.
public sealed record FilmReportRow(int FilmId, string Title, int Showings, int Cars, int Admitted, decimal Gross);

// A named line with how many times it was sold and what it came to: a channel, a ticket option or an add-on.
public sealed record ReportLine(string Name, int Count, decimal Amount);

public sealed record SalesReport(DateOnly From, DateOnly To, SalesTotals Totals, List<ShowingReportRow> Showings,
    List<DayReportRow> Days, List<FilmReportRow> Films, List<ReportLine> Channels, List<ReportLine> Options, List<ReportLine> AddOns);

public sealed record OutstandingGiftCardRow(int Id, string Last4, DateTime PurchasedLocal, string? PurchaserEmail,
    string? RecipientName, decimal InitialAmount, decimal Balance, DateTime? LastUsedLocal, bool IsTest);

// What was owed on the theater's gift cards when the range began and ended, and what moved in between:
// OwedAtStart + SoldAmount - Redeemed = OwedAtEnd. Outstanding is every card with a balance left right now.
public sealed record GiftCardReport(DateOnly From, DateOnly To, decimal OwedAtStart, int SoldCount, decimal SoldAmount,
    decimal Redeemed, decimal OwedAtEnd, List<OutstandingGiftCardRow> Outstanding)
{
    public decimal OutstandingTotal => Outstanding.Sum(c => c.Balance);
}

// Sales, attendance and gift card reports for a theater (ViewReports). Dates are the theater's local dates, both ends
// included. Ticket figures cover showings that start in the range, sold tickets only (not holds, payments in progress
// or free admission requests awaiting approval); gift card figures cover balance changes made in the range.
public sealed class ReportService(IDbContextFactory<ApplicationDbContext> dbFactory, IAuthorizationService auth, TimeProvider time)
{
    public const int MaxDays = 366;

    public async Task<SalesReport> GetSalesReportAsync(ClaimsPrincipal user, int theaterId, DateOnly from, DateOnly to)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await AuthorizeAsync(db, user, theaterId);
        var (fromUtc, toUtc) = Bounds(theater, from, to);
        var now = time.GetUtcNow();

        var showtimes = await ScheduleService.WithFeatures(db.Showtimes.AsNoTracking()).Include(s => s.Screen)
            .Where(s => s.Screen!.TheaterId == theaterId && s.StartsAt >= fromUtc && s.StartsAt < toUtc)
            .OrderBy(s => s.StartsAt).ThenBy(s => s.Screen!.SortOrder)
            .ToListAsync();
        var tickets = (await db.Tickets.AsNoTracking()
            .Where(t => t.Status == TicketStatus.Sold && t.Showtime!.Screen!.TheaterId == theaterId
                && t.Showtime.StartsAt >= fromUtc && t.Showtime.StartsAt < toUtc)
            .Select(t => new SoldTicket(t.ShowtimeId, t.Total, t.GiftCardAmount, t.OptionName, t.OptionPrice, t.SoldAtGate,
                t.IsComp, t.AdmittedAt != null, t.IsTest, t.AddOns.Select(a => new SoldAddOn(a.Name, a.Effect)).ToList()))
            .ToListAsync())
            .ToLookup(t => t.ShowtimeId);

        var showings = new List<ShowingReportRow>();
        var films = new Dictionary<int, FilmReportRow>();
        foreach (var s in showtimes)
        {
            var sold = tickets[s.Id].ToList();
            var cars = sold.Count;
            var admitted = sold.Count(t => t.Admitted);
            var gross = sold.Sum(t => t.Total);
            var ended = s.EndsAt <= now;
            var features = s.Features.OrderBy(f => f.Position).Select(f => f.Film!).ToList();
            showings.Add(new ShowingReportRow(s.Id, TheaterTime.ToLocal(theater, s.StartsAt), s.Screen!.Name,
                string.Join(" + ", features.Select(f => f.Title)), s.Screen.SpotCount, cars, sold.Count(t => t.IsComp), admitted,
                ended ? cars - admitted : null, gross, sold.Sum(t => t.GiftCardAmount)));
            foreach (var film in features.DistinctBy(f => f.Id))
            {
                var row = films.GetValueOrDefault(film.Id) ?? new FilmReportRow(film.Id, film.Title, 0, 0, 0, 0m);
                films[film.Id] = row with
                {
                    Showings = row.Showings + 1, Cars = row.Cars + cars, Admitted = row.Admitted + admitted, Gross = row.Gross + gross,
                };
            }
        }

        var all = tickets.SelectMany(g => g).ToList();
        var endedIds = showtimes.Where(s => s.EndsAt <= now).Select(s => s.Id).ToHashSet();
        var atEnded = all.Where(t => endedIds.Contains(t.ShowtimeId)).ToList();
        var totals = new SalesTotals(showtimes.Count, all.Count, all.Count(t => t.IsComp), all.Count(t => t.Admitted),
            atEnded.Count, atEnded.Count(t => t.Admitted), all.Sum(t => t.Total), all.Sum(t => t.GiftCardAmount), all.Any(t => t.IsTest));

        var days = showings.GroupBy(r => DateOnly.FromDateTime(r.StartsLocal))
            .Select(g => new DayReportRow(g.Key, g.Count(), g.Sum(r => r.Cars), g.Sum(r => r.Admitted), g.Sum(r => r.Gross)))
            .OrderBy(d => d.Date)
            .ToList();

        List<ReportLine> channels =
        [
            Line("Online", all.Where(t => !t.SoldAtGate && !t.IsComp)),
            Line("At the gate", all.Where(t => t.SoldAtGate && !t.IsComp)),
            Line("Free admission", all.Where(t => t.IsComp)),
        ];
        var options = all.Where(t => !t.IsComp)
            .GroupBy(t => string.IsNullOrWhiteSpace(t.OptionName) ? "(no option)" : t.OptionName)
            .Select(g => new ReportLine(g.Key, g.Count(), g.Sum(t => t.OptionPrice)))
            .OrderByDescending(l => l.Count).ThenBy(l => l.Name)
            .ToList();
        var addOns = all.SelectMany(t => t.AddOns)
            .GroupBy(a => a.Name)
            .Select(g => new ReportLine(g.Key, g.Count(), g.Sum(a => a.Effect)))
            .OrderByDescending(l => l.Count).ThenBy(l => l.Name)
            .ToList();

        return new SalesReport(from, to, totals, showings, days,
            films.Values.OrderByDescending(f => f.Gross).ThenByDescending(f => f.Cars).ThenBy(f => f.Title).ToList(),
            channels, options, addOns);
    }

    public async Task<GiftCardReport> GetGiftCardReportAsync(ClaimsPrincipal user, int theaterId, DateOnly from, DateOnly to)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await AuthorizeAsync(db, user, theaterId);
        var (fromUtc, toUtc) = Bounds(theater, from, to);

        var transactions = db.GiftCardTransactions.AsNoTracking().Where(t => t.GiftCard!.TheaterId == theaterId);
        var owedAtStart = await transactions.Where(t => t.At < fromUtc).SumAsync(t => (decimal?)t.Amount) ?? 0m;
        var owedAtEnd = await transactions.Where(t => t.At < toUtc).SumAsync(t => (decimal?)t.Amount) ?? 0m;
        var inRange = transactions.Where(t => t.At >= fromUtc && t.At < toUtc);
        var purchases = inRange.Where(t => t.Kind == GiftCardTransactionKind.Purchase);
        var soldCount = await purchases.CountAsync();
        var soldAmount = await purchases.SumAsync(t => (decimal?)t.Amount) ?? 0m;
        // Redemptions are negative and money put back after a declined card positive, so this is the net spent.
        var redeemed = -(await inRange.Where(t => t.Kind != GiftCardTransactionKind.Purchase).SumAsync(t => (decimal?)t.Amount) ?? 0m);

        var cards = await db.GiftCards.AsNoTracking().Where(g => g.TheaterId == theaterId && g.Balance > 0)
            .OrderBy(g => g.PurchasedAt).ThenBy(g => g.Id)
            .ToListAsync();
        var ids = cards.Select(g => g.Id).ToList();
        var lastUsed = await transactions.Where(t => ids.Contains(t.GiftCardId) && t.Kind == GiftCardTransactionKind.Redeem)
            .GroupBy(t => t.GiftCardId)
            .Select(g => new { g.Key, At = g.Max(t => t.At) })
            .ToDictionaryAsync(x => x.Key, x => x.At);
        var outstanding = cards.Select(g => new OutstandingGiftCardRow(g.Id, g.Last4, TheaterTime.ToLocal(theater, g.PurchasedAt),
                g.PurchaserEmail, g.RecipientName, g.InitialAmount, g.Balance,
                lastUsed.TryGetValue(g.Id, out var at) ? TheaterTime.ToLocal(theater, at) : null, g.IsTest))
            .ToList();

        return new GiftCardReport(from, to, owedAtStart, soldCount, soldAmount, redeemed, owedAtEnd, outstanding);
    }

    private async Task<Theater> AuthorizeAsync(ApplicationDbContext db, ClaimsPrincipal user, int theaterId)
    {
        var theater = await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(user, theater, TheaterPermissions.ViewReports);
        return theater;
    }

    // From the start of `from` to the start of the day after `to`, in the theater's time zone.
    private static (DateTimeOffset From, DateTimeOffset To) Bounds(Theater theater, DateOnly from, DateOnly to)
    {
        if (to < from)
            throw new AppValidationException("The end date is before the start date.");
        if (to.DayNumber - from.DayNumber >= MaxDays)
            throw new AppValidationException($"Choose at most {MaxDays} days.");
        return (StartOfDay(theater, from), StartOfDay(theater, to.AddDays(1)));
    }

    // Midnight, or the first time that exists when the clocks skip it.
    private static DateTimeOffset StartOfDay(Theater theater, DateOnly date)
    {
        var zone = TheaterTime.ZoneOf(theater);
        var local = date.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(15);
        return TheaterTime.ToUtc(theater, date, TimeOnly.FromDateTime(local));
    }

    private static ReportLine Line(string name, IEnumerable<SoldTicket> tickets)
    {
        var list = tickets.ToList();
        return new ReportLine(name, list.Count, list.Sum(t => t.Total));
    }

    private sealed record SoldAddOn(string Name, decimal Effect);

    private sealed record SoldTicket(int ShowtimeId, decimal Total, decimal GiftCardAmount, string? OptionName, decimal OptionPrice,
        bool SoldAtGate, bool IsComp, bool Admitted, bool IsTest, List<SoldAddOn> AddOns);
}
