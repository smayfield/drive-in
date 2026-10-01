using System.Diagnostics.Metrics;
using DriveIn.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

// Current totals (users, theaters, upcoming showings, money owed) as gauges on the "DriveIn" meter. A snapshot is read
// from the database every Interval and the gauges report the latest one, so a metrics collection never queries.
// Registered as a singleton (for the meter) and as the hosted service that refreshes it.
public sealed class BusinessGauges : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    public sealed record Snapshot(
        int Customers, int Employees, int DemoTheaters, int LiveTheaters, int LiveScreens, int ShowingsNext7Days,
        int GoLiveRequests, int FreeAdmissionRequests, decimal InvoicesOutstanding);

    private readonly IServiceScopeFactory scopes;
    private readonly TimeProvider time;
    private readonly DriveInMetrics metrics;
    private readonly ILogger<BusinessGauges> logger;
    private volatile Snapshot? latest;

    public BusinessGauges(IServiceScopeFactory scopes, TimeProvider time, IMeterFactory meterFactory, DriveInMetrics metrics,
        ILogger<BusinessGauges> logger)
    {
        this.scopes = scopes;
        this.time = time;
        this.metrics = metrics;
        this.logger = logger;

        var meter = meterFactory.Create(DriveInMetrics.MeterName);
        meter.CreateObservableGauge("drivein.users", () => Measure(s =>
        [
            new(s.Customers, new KeyValuePair<string, object?>("kind", "customer")),
            new(s.Employees, new KeyValuePair<string, object?>("kind", "employee")),
        ]), "{user}", "Accounts (kind: customer = personal accounts, including owners and admins; employee).");
        meter.CreateObservableGauge("drivein.theaters", () => Measure(s =>
        [
            new(s.DemoTheaters, new KeyValuePair<string, object?>("mode", "demo")),
            new(s.LiveTheaters, new KeyValuePair<string, object?>("mode", "live")),
        ]), "{theater}", "Active theaters by mode.");
        meter.CreateObservableGauge("drivein.screens.live", () => Measure(s => [new(s.LiveScreens)]), "{screen}",
            "Screens at active live theaters (what billing charges for).");
        meter.CreateObservableGauge("drivein.showings.upcoming", () => Measure(s => [new(s.ShowingsNext7Days)]), "{showing}",
            "Showings in the next 7 days at active live theaters.");
        meter.CreateObservableGauge("drivein.theaters.go_live_pending", () => Measure(s => [new(s.GoLiveRequests)]), "{request}",
            "Go-live requests waiting for an admin.");
        meter.CreateObservableGauge("drivein.free_admission.pending", () => Measure(s => [new(s.FreeAdmissionRequests)]),
            "{request}", "Free admission requests waiting for approval.");
        meter.CreateObservableGauge("drivein.invoices.outstanding", () => Measure(s => [new((double)s.InvoicesOutstanding)]),
            "{USD}", "Unpaid balance of issued invoices, in dollars.");
    }

    public Snapshot? Latest => latest;

    // Nothing until the first snapshot, rather than zeros that would look like real data.
    private IEnumerable<Measurement<double>> Measure(Func<Snapshot, Measurement<double>[]> read) =>
        latest is Snapshot s ? read(s) : [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Couldn't read the business gauges");
                metrics.JobFailed("business_gauges");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<Snapshot> RefreshAsync(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        var weekAhead = now.AddDays(7);

        var employees = await db.Users.CountAsync(u => u.EmployeeTheaterId != null, ct);
        var customers = await db.Users.CountAsync(ct) - employees;
        var byMode = await db.Theaters.Where(t => t.IsActive).GroupBy(t => t.Mode)
            .Select(g => new { Mode = g.Key, Count = g.Count() }).ToListAsync(ct);
        var liveScreens = await db.Screens.CountAsync(s => s.Theater!.IsActive && s.Theater.Mode == TheaterMode.Live, ct);
        var showings = await db.Showtimes.CountAsync(s => s.StartsAt >= now && s.StartsAt < weekAhead
            && s.Screen!.Theater!.IsActive && s.Screen.Theater.Mode == TheaterMode.Live, ct);
        // As Admin → Theaters lists them (OnboardingService.ListGoLiveRequestsAsync).
        var goLive = await db.Theaters.CountAsync(t => t.Mode == TheaterMode.Demo && t.GoLiveRequestedAt != null, ct);
        var freeAdmission = await db.Tickets.CountAsync(t => t.IsComp && t.Status == TicketStatus.Pending, ct);
        var issuedTotal = await db.Invoices.Where(i => i.Status == InvoiceStatus.Issued).SumAsync(i => (decimal?)i.Total, ct) ?? 0m;
        var issuedPaid = await db.InvoicePayments.Where(p => p.Invoice!.Status == InvoiceStatus.Issued).SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;

        var snapshot = new Snapshot(customers, employees,
            byMode.Where(m => m.Mode == TheaterMode.Demo).Sum(m => m.Count),
            byMode.Where(m => m.Mode == TheaterMode.Live).Sum(m => m.Count),
            liveScreens, showings, goLive, freeAdmission, issuedTotal - issuedPaid);
        latest = snapshot;
        return snapshot;
    }
}
