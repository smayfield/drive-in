using Npgsql;

namespace DriveIn.Web.Services;

// Decides which running copy of the app does the background jobs (expiring holds, emailing notifications, drafting
// invoices, reading the business gauges), so they don't run twice while a deploy briefly runs two copies, or if the app
// is ever scaled out. Jobs ask before each run.
public interface IJobLeadership
{
    Task<bool> IsLeaderAsync(CancellationToken ct = default);
}

// For a single copy (tests, and jobs constructed by hand): always runs.
public sealed class AlwaysLeader : IJobLeadership
{
    public static readonly AlwaysLeader Instance = new();

    public Task<bool> IsLeaderAsync(CancellationToken ct = default) => Task.FromResult(true);
}

// The copy holding a PostgreSQL session advisory lock is the leader. The lock lives as long as its connection: a copy
// that stops (or crashes, or loses the database) releases it, and the next copy to ask takes over. The connection isn't
// pooled, since a pooled connection keeps its session, and the lock, after it's closed.
public sealed class PostgresJobLeadership : IJobLeadership, IAsyncDisposable
{
    // Any constant shared by every copy; "dij" (drive-in jobs) in ASCII.
    public const long LockKey = 0x64696A;

    private readonly string connectionString;
    private readonly ILogger<PostgresJobLeadership> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private NpgsqlConnection? connection;

    public PostgresJobLeadership(string connectionString, ILogger<PostgresJobLeadership> logger)
    {
        this.connectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            ApplicationName = "drive-in jobs leader",
        }.ConnectionString;
        this.logger = logger;
    }

    public async Task<bool> IsLeaderAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (connection is not null)
            {
                // Still leader as long as the session holding the lock is alive.
                try
                {
                    await using var ping = new NpgsqlCommand("SELECT 1", connection);
                    await ping.ExecuteScalarAsync(ct);
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Lost the background jobs lock's connection; no longer running background jobs");
                    await DropConnectionAsync();
                }
            }

            var candidate = new NpgsqlConnection(connectionString);
            try
            {
                await candidate.OpenAsync(ct);
                await using var take = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", candidate);
                take.Parameters.AddWithValue("key", LockKey);
                if (await take.ExecuteScalarAsync(ct) is true)
                {
                    connection = candidate;
                    candidate = null;
                    logger.LogInformation("Took the background jobs lock; this copy now runs the background jobs");
                    return true;
                }
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Couldn't check the background jobs lock");
                return false;
            }
            finally
            {
                if (candidate is not null)
                    await candidate.DisposeAsync();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DropConnectionAsync()
    {
        if (connection is not null)
        {
            await connection.DisposeAsync();
            connection = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Closing the session releases the lock at once, so the next copy takes over on its next check.
        await DropConnectionAsync();
        gate.Dispose();
    }
}
