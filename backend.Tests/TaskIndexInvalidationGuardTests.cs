using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskIndexInvalidationGuardTests : IDisposable
{
    private readonly string _watchPath = Path.Combine(
        Path.GetTempPath(),
        "task-index-invalidation-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly TaskIndexCache _cache;

    public TaskIndexInvalidationGuardTests()
    {
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "cache-guard-test",
                ["WatchPaths:0:Path"] = _watchPath,
                ["TaskIndexCache:SafetyTtlSeconds"] = "60",
            })
            .Build();
        var summary = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance,
            _config);
        _scanner = new TaskScannerService(
            _config,
            NullLogger<TaskScannerService>.Instance,
            summary);
        _cache = new TaskIndexCache(
            _scanner,
            NullLogger<TaskIndexCache>.Instance,
            _config);
        _scanner.SetIndexCache(_cache);
    }

    [Fact]
    public void ClearQuotaWait_InvalidatesOnlyWhenMarkerWasDeleted()
    {
        var folder = WriteJob(LifecyclePhases.ExecutionRunning);
        var task = Assert.Single(_scanner.ScanAllJobs());

        ProjectRunner.ClearQuotaWaitMarker(
            task,
            _scanner,
            NullLogger<ProjectRunner>.Instance);

        Assert.Equal(0, _cache.MutationInvalidations);

        QuotaWaitMarker.Write(folder, new QuotaWaitRecord
        {
            CliType = "codex",
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            ResetAt = DateTime.UtcNow.AddMinutes(5),
            ThresholdMinutes = 30,
            Reason = "test quota wait",
        });
        _cache.Invalidate(TaskIndexCache.InvalidationSource.External);
        task = Assert.Single(_scanner.ScanAllJobs());
        Assert.NotNull(task.QuotaWait);

        ProjectRunner.ClearQuotaWaitMarker(
            task,
            _scanner,
            NullLogger<ProjectRunner>.Instance);

        Assert.Equal(1, _cache.MutationInvalidations);
        Assert.Null(_scanner.FindJob(task.Id, _watchPath)?.QuotaWait);
    }

    [Fact]
    public void ClearRemoteDispatchRejection_InvalidatesOnlyWhenFieldWasRemoved()
    {
        var folder = WriteJob(LifecyclePhases.ExecutionRunning);
        var task = Assert.Single(_scanner.ScanAllJobs());
        var store = new RemoteDispatchRejectionStore(
            NullLogger<RemoteDispatchRejectionStore>.Instance,
            _scanner);

        store.Clear(task);

        Assert.Equal(0, _cache.MutationInvalidations);

        TaskJsonFile.UpdateFieldOrThrow(folder, RemoteDispatchRejectionStore.FieldName,
            new RemoteDispatchRejection
            {
                Code = "no-capacity",
                RunnerId = "runner-01",
                RunnerName = "Runner 01",
                Reason = "No capacity",
                RejectedAtUtc = DateTime.UtcNow,
            });
        _cache.Invalidate(TaskIndexCache.InvalidationSource.External);
        task = Assert.Single(_scanner.ScanAllJobs());
        Assert.NotNull(task.RemoteDispatchRejection);

        store.Clear(task);

        Assert.Equal(1, _cache.MutationInvalidations);
        Assert.Null(_scanner.FindJob(task.Id, _watchPath)?.RemoteDispatchRejection);
    }

    [Fact]
    public void SetJobPhase_InvalidatesAndRewritesOnlyWhenPhaseChanges()
    {
        var folder = WriteJob(LifecyclePhases.ExecutionRunning);
        var taskJsonPath = Path.Combine(folder, "task.json");
        var originalJson = File.ReadAllText(taskJsonPath);
        var mutations = BuildMutations();

        Assert.True(mutations.SetJobPhase(folder, LifecyclePhases.ExecutionRunning));

        Assert.Equal(0, _cache.MutationInvalidations);
        Assert.Equal(originalJson, File.ReadAllText(taskJsonPath));

        Assert.True(mutations.SetJobPhase(folder, LifecyclePhases.QuotaWaiting));

        Assert.Equal(1, _cache.MutationInvalidations);
        using var document = JsonDocument.Parse(File.ReadAllText(taskJsonPath));
        Assert.Equal(
            LifecyclePhases.QuotaWaiting,
            document.RootElement.GetProperty("phase").GetString());
        Assert.NotEqual(
            "2026-01-01T00:00:00.0000000Z",
            document.RootElement.GetProperty("phaseEnteredAt").GetString());
        Assert.Equal(
            LifecyclePhases.QuotaWaiting,
            _scanner.FindJob("job-1", _watchPath)?.Phase);
    }

    [Fact]
    public void Diagnostics_LogCountersAndConcreteInvalidationCaller()
    {
        var cacheLogger = new CapturingLogger<TaskIndexCache>();
        var cache = new TaskIndexCache(_scanner, cacheLogger, _config, () => []);

        InvalidateFromKnownCaller(cache);

        var invalidation = Assert.Single(cacheLogger.Entries);
        var invalidationProperties = Properties(invalidation);
        Assert.Equal(nameof(InvalidateFromKnownCaller), invalidationProperties["Caller"]);
        Assert.EndsWith(
            "TaskIndexInvalidationGuardTests.cs",
            Assert.IsType<string>(invalidationProperties["CallerFile"]),
            StringComparison.Ordinal);

        _ = cache.GetSnapshot();
        var diagnosticsLogger = new CapturingLogger<TaskIndexCacheDiagnosticsService>();
        var diagnostics = new TaskIndexCacheDiagnosticsService(cache, diagnosticsLogger);

        diagnostics.LogRollup();

        var rollup = Properties(Assert.Single(diagnosticsLogger.Entries));
        Assert.Equal(0L, rollup["Hits"]);
        Assert.Equal(1L, rollup["Misses"]);
        Assert.Equal(0L, rollup["StaleHits"]);
        Assert.Equal(0L, rollup["ExternalInvalidations"]);
        Assert.Equal(1L, rollup["MutationInvalidations"]);
    }

    private static void InvalidateFromKnownCaller(TaskIndexCache cache) => cache.Invalidate();

    private static Dictionary<string, object?> Properties(
        IReadOnlyList<KeyValuePair<string, object?>> entry)
        => entry.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private TaskMutationService BuildMutations() => new(
        _scanner,
        new ClientIdentityStore(_config, NullLogger<ClientIdentityStore>.Instance),
        new ProjectRegistry(_config, NullLogger<ProjectRegistry>.Instance),
        new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
        NullLogger<TaskMutationService>.Instance);

    private string WriteJob(string phase)
    {
        var folder = Path.Combine(_watchPath, TaskStates.Progress, "job-1");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(new
            {
                id = "job-1",
                key = "AGT-1",
                title = "Cache invalidation guard",
                state = TaskStates.Progress,
                order = 1,
                agent = "codex",
                ownerClientId = DefaultClientIdentity.Id,
                phase,
                phaseEnteredAt = "2026-01-01T00:00:00.0000000Z",
            }, new JsonSerializerOptions { WriteIndented = true }));
        return folder;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_watchPath, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of an isolated test directory.
        }
    }
}
