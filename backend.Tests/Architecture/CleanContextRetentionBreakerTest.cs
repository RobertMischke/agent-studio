using AgentStudio.CliHosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests.Architecture;

public sealed class CleanContextRetentionBreakerTest : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "clean-context-retention-breaker",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void SweepDeletesExpiredHomeWithinOneTickAndRetainsCurrentHome()
    {
        var userHome = Path.Combine(_root, "user");
        var storeRoot = Path.Combine(_root, "store");
        Directory.CreateDirectory(userHome);
        var baseline = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        using var expired = TaskCleanContextStore.Acquire(
            "codex", "expired", userHome, storeRoot, baseline, TimeSpan.FromDays(30));
        using var current = TaskCleanContextStore.Acquire(
            "codex", "current", userHome, storeRoot, baseline.AddDays(8), TimeSpan.FromDays(30));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CleanContext:Root"] = storeRoot,
                ["CleanContext:RetentionDays"] = "7",
            }).Build();
        var service = new CleanContextRetentionHostedService(
            configuration,
            NullLogger<CleanContextRetentionHostedService>.Instance);

        var result = service.RunOnce(baseline.AddDays(8));

        Assert.Equal(1, result.Deleted);
        Assert.False(Directory.Exists(expired.HomePath));
        Assert.True(Directory.Exists(current.HomePath));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* test cleanup is best-effort */ }
    }
}
