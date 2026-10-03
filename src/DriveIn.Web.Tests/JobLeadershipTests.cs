using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveIn.Web.Tests;

// Background jobs only run in the copy of the app that holds the jobs lock (see PostgresJobLeadership), so a deploy that
// briefly runs two copies doesn't, say, email every notification twice.
public class JobLeadershipTests
{
    private sealed class SwitchableLeadership : IJobLeadership
    {
        public bool Leader { get; set; }
        public int Asked { get; private set; }

        public Task<bool> IsLeaderAsync(CancellationToken ct = default)
        {
            Asked++;
            return Task.FromResult(Leader);
        }
    }

    // Advances the clock a tick at a time until the job has asked for the lock again and done is true. (The job starts on
    // a background thread, so its timer may not exist yet when the first tick goes by.)
    private static async Task TickAsync(TestApp app, SwitchableLeadership leadership, TimeSpan interval, Func<Task<bool>> done)
    {
        var asked = leadership.Asked;
        for (var i = 0; i < 100 && (leadership.Asked == asked || !await done()); i++)
        {
            app.Time.Advance(interval);
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Expired_holds_are_only_released_by_the_copy_that_holds_the_jobs_lock()
    {
        await using var s = await TicketSalesTests.SetUpAsync();
        var buyer = await TicketSalesTests.BuyerAsync(s.App);
        await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        s.App.Time.Advance(TimeSpan.FromMinutes(Ticket.HoldMinutes) + TimeSpan.FromSeconds(1));
        var leadership = new SwitchableLeadership();
        using var job = new HoldExpiryService(s.App.Get<IServiceScopeFactory>(), s.App.Time, s.App.Get<DriveInMetrics>(),
            leadership, NullLogger<HoldExpiryService>.Instance);
        await job.StartAsync(CancellationToken.None);
        async Task<bool> Released()
        {
            await using var db = s.App.Db();
            return !db.Tickets.Any();
        }

        await TickAsync(s.App, leadership, HoldExpiryService.Interval, () => Task.FromResult(true));
        Assert.True(leadership.Asked > 0);
        Assert.False(await Released());

        leadership.Leader = true;
        await TickAsync(s.App, leadership, HoldExpiryService.Interval, Released);
        Assert.True(await Released());
        await job.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Only_the_leader_reports_the_business_gauges()
    {
        await using var app = new TestApp();
        var leadership = new SwitchableLeadership();
        using var gauges = new BusinessGauges(app.Get<IServiceScopeFactory>(), app.Time,
            app.Get<System.Diagnostics.Metrics.IMeterFactory>(), app.Get<DriveInMetrics>(), leadership,
            NullLogger<BusinessGauges>.Instance);

        await gauges.StartAsync(CancellationToken.None);
        await TickAsync(app, leadership, BusinessGauges.Interval, () => Task.FromResult(true));
        Assert.Null(gauges.Latest);

        leadership.Leader = true;
        await TickAsync(app, leadership, BusinessGauges.Interval, () => Task.FromResult(gauges.Latest is not null));
        Assert.NotNull(gauges.Latest);

        leadership.Leader = false;
        await TickAsync(app, leadership, BusinessGauges.Interval, () => Task.FromResult(gauges.Latest is null));
        Assert.Null(gauges.Latest);
        await gauges.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Ready_means_the_database_can_be_reached()
    {
        await using var app = new TestApp();
        var check = new DatabaseHealthCheck(app.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<ApplicationDbContext>>());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
