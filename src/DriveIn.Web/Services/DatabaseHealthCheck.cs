using DriveIn.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DriveIn.Web.Services;

// Ready means the app can reach its database (served at /readyz).
public sealed class DatabaseHealthCheck(IDbContextFactory<ApplicationDbContext> dbFactory) : IHealthCheck
{
    public const string Tag = "ready";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Can't reach the database.");
    }
}
