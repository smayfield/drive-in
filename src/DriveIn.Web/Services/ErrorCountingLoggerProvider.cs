namespace DriveIn.Web.Services;

// Counts every error and critical message logged (drivein.errors.logged, by logger category). Most failures in this
// Blazor Server app happen inside a circuit and never surface as a 5xx response, so request metrics alone would miss
// them; the log is where they all end up.
public sealed class ErrorCountingLoggerProvider(DriveInMetrics metrics) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ErrorCounter(metrics, categoryName);

    public void Dispose()
    {
    }

    private sealed class ErrorCounter(DriveInMetrics metrics, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is LogLevel.Error or LogLevel.Critical;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                metrics.ErrorLogged(category, logLevel);
        }
    }
}
