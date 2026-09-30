using System.Security.Claims;
using DriveIn.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

public sealed record BillingMonthRow(DateOnly Month, int Invoices, decimal Invoiced, decimal Collected);

public sealed record TheaterBillingRow(int? TheaterId, string TheaterName, int Invoices, decimal Invoiced, decimal Paid, decimal Outstanding);

// What's owed on issued invoices right now, by how far past due: not yet due (or due today), 1–30 days, and so on.
public sealed record AgingRow(int? TheaterId, string TheaterName, decimal Current, decimal Days1To30, decimal Days31To60,
    decimal Days61To90, decimal Over90)
{
    public decimal Total => Current + Days1To30 + Days31To60 + Days61To90 + Over90;
}

public sealed record BillingReport(
    DateOnly From, DateOnly To, DateOnly Today,
    int ActiveSubscriptions, int CanceledSubscriptions, int BilledScreens,
    decimal ProjectedThisMonth, decimal ProjectedNextMonth,
    List<BillingMonthRow> Months, List<TheaterBillingRow> Theaters,
    List<AgingRow> Aging, AgingRow AgingTotal,
    List<Invoice> Invoices, List<(Invoice Invoice, InvoicePayment Payment)> Payments);

// Admin reporting across every theater's billing, for a range of months (both ends included, by the month billed for
// invoices and by the date received for payments), plus a snapshot of subscriptions and receivables as of today.
public sealed class BillingReportService(IDbContextFactory<ApplicationDbContext> dbFactory, TimeProvider time)
{
    public const int MaxMonths = 60;

    public async Task<BillingReport> GetReportAsync(ClaimsPrincipal user, DateOnly from, DateOnly to)
    {
        Guard.RequireAdmin(user);
        from = BillingService.MonthOf(from);
        to = BillingService.MonthOf(to);
        if (to < from)
            throw new AppValidationException("The end month is before the start month.");
        if ((to.Year - from.Year) * 12 + to.Month - from.Month + 1 > MaxMonths)
            throw new AppValidationException($"Choose at most {MaxMonths} months.");
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var end = to.AddMonths(1); // exclusive

        await using var db = await dbFactory.CreateDbContextAsync();
        var subs = await db.Subscriptions.AsNoTracking().Include(s => s.Theater!).ThenInclude(t => t.Screens).ToListAsync();
        var active = subs.Where(s => s.IsActive).ToList();
        decimal Projected(int monthsAhead) => active.Sum(s =>
        {
            var month = BillingService.MonthOf(BillingService.LocalToday(s.Theater!, now)).AddMonths(monthsAhead);
            return BillingService.IsBillable(s, s.Theater!, month) ? s.PricePerScreenPerMonth * s.Theater!.Screens.Count : 0m;
        });

        var billed = await db.Invoices.AsNoTracking().Include(i => i.Payments)
            .Where(i => (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.Paid) && i.PeriodMonth >= from && i.PeriodMonth < end)
            .OrderBy(i => i.Number)
            .ToListAsync();
        List<(Invoice Invoice, InvoicePayment Payment)> payments = (await db.InvoicePayments.AsNoTracking().Include(p => p.Invoice)
            .Where(p => p.ReceivedOn >= from && p.ReceivedOn < end)
            .ToListAsync())
            .OrderBy(p => p.ReceivedOn).ThenBy(p => p.Id)
            .Select(p => (p.Invoice!, p)).ToList();
        var open = await db.Invoices.AsNoTracking().Include(i => i.Payments)
            .Where(i => i.Status == InvoiceStatus.Issued)
            .ToListAsync();

        var months = new List<BillingMonthRow>();
        for (var m = from; m < end; m = m.AddMonths(1))
        {
            var month = m;
            var invoiced = billed.Where(i => i.PeriodMonth == month).ToList();
            months.Add(new BillingMonthRow(month, invoiced.Count, invoiced.Sum(i => i.Total),
                payments.Where(p => BillingService.MonthOf(p.Payment.ReceivedOn) == month).Sum(p => p.Payment.Amount)));
        }

        var theaters = billed.GroupBy(i => (i.TheaterId, i.TheaterName))
            .Select(g => new TheaterBillingRow(g.Key.TheaterId, g.Key.TheaterName, g.Count(), g.Sum(i => i.Total),
                g.Sum(i => i.AmountPaid), g.Sum(i => i.Balance)))
            .OrderBy(r => r.TheaterName).ToList();

        var aging = open.GroupBy(i => (i.TheaterId, i.TheaterName))
            .Select(g => Age(g.Key.TheaterId, g.Key.TheaterName, g, today))
            .OrderBy(r => r.TheaterName).ToList();
        var agingTotal = new AgingRow(null, "Total", aging.Sum(a => a.Current), aging.Sum(a => a.Days1To30), aging.Sum(a => a.Days31To60),
            aging.Sum(a => a.Days61To90), aging.Sum(a => a.Over90));

        return new BillingReport(from, to, today,
            active.Count, subs.Count - active.Count, active.Sum(s => s.Theater!.Screens.Count),
            Projected(0), Projected(1),
            months, theaters, aging, agingTotal, billed, payments);
    }

    private static AgingRow Age(int? theaterId, string name, IEnumerable<Invoice> invoices, DateOnly today)
    {
        decimal current = 0, d30 = 0, d60 = 0, d90 = 0, over = 0;
        foreach (var invoice in invoices)
        {
            var daysLate = invoice.DueOn is DateOnly due ? today.DayNumber - due.DayNumber : 0;
            switch (daysLate)
            {
                case <= 0: current += invoice.Balance; break;
                case <= 30: d30 += invoice.Balance; break;
                case <= 60: d60 += invoice.Balance; break;
                case <= 90: d90 += invoice.Balance; break;
                default: over += invoice.Balance; break;
            }
        }
        return new AgingRow(theaterId, name, current, d30, d60, d90, over);
    }
}
