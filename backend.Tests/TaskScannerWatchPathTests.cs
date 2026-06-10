using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Regression: a misconfigured watch path that resolves to a non-existent
/// folder (the Runbook <c>.orchestrator/jobs</c> case observed 2026-06-02)
/// made <see cref="TaskScannerService.ScanAllJobsRaw"/> log
/// "Watch path does not exist" on every scan. ScanAllJobsRaw runs on every
/// cache refresh, which a busy job's FileSystemWatcher churn triggers many
/// times per second, so the warning spammed the api log endlessly and buried
/// the real crash cause in the last seconds before a silent host death. The
/// warning must be throttled to once per missing path.
/// </summary>
public class TaskScannerWatchPathTests
{
    [Fact]
    public void ScanAllJobsRaw_MissingWatchPath_WarnsOnlyOnceAcrossManyScans()
    {
        var missing = Path.Combine(Path.GetTempPath(), "atp-missing-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(missing));

        var capture = new CapturingLogger();
        var scanner = BuildScanner(missing, capture);

        for (var i = 0; i < 25; i++)
        {
            var jobs = scanner.ScanAllJobsRaw();
            Assert.Empty(jobs); // missing path contributes nothing
        }

        var warnings = capture.Warnings.Count(w => w.Contains("Watch path does not exist"));
        Assert.Equal(1, warnings);
    }

    private static TaskScannerService BuildScanner(string watchPath, ILogger<TaskScannerService> logger)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "missing",
                ["WatchPaths:0:Path"] = watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, logger, summary);
    }

    private sealed class CapturingLogger : ILogger<TaskScannerService>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullDisposable.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
