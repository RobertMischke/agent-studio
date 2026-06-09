using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Correctness tests for the Cycle-1 TaskIndexCache. The perf claim of the
/// cache is "polled hot paths see O(1) reads instead of O(N) disk walks";
/// the correctness claim is "an explicit Invalidate() makes the next read
/// see what was just written, and the safety TTL eventually picks up
/// changes the watcher missed". These tests pin both.
/// </summary>
public class TaskIndexCacheTests : IDisposable
{
    private readonly string _watchPath;
    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly TaskIndexCache _cache;

    public TaskIndexCacheTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-cache-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "cache-test",
                ["WatchPaths:0:Path"] = _watchPath,
                ["TaskIndexCache:SafetyTtlSeconds"] = "1", // tight TTL for the safety test
            })
            .Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config);
        _scanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance, summary);
        _cache = new TaskIndexCache(_scanner, NullLogger<TaskIndexCache>.Instance, _config);
        _scanner.SetIndexCache(_cache);
    }

    [Fact]
    public void FirstRead_LoadsFromDisk()
    {
        WriteJob("2-ready", "job-1", "First");
        var jobs = _scanner.ScanAllJobs();
        Assert.Single(jobs);
        Assert.Equal("job-1", jobs[0].Id);
        Assert.Equal(1, _cache.Misses);
        Assert.Equal(0, _cache.Hits);
    }

    [Fact]
    public void SecondRead_HitsCache_WithoutDiskAccess()
    {
        WriteJob("2-ready", "job-1", "First");
        _ = _scanner.ScanAllJobs(); // miss → loads
        _ = _scanner.ScanAllJobs(); // hit
        _ = _scanner.ScanAllJobs(); // hit
        Assert.Equal(1, _cache.Misses);
        Assert.Equal(2, _cache.Hits);
    }

    [Fact]
    public void Invalidate_CausesNextReadToSeeFreshDiskState()
    {
        WriteJob("2-ready", "job-1", "First");
        var firstScan = _scanner.ScanAllJobs();
        Assert.Single(firstScan);

        // Add a job directly on disk - cache doesn't know yet.
        WriteJob("2-ready", "job-2", "Second");
        var stillCached = _scanner.ScanAllJobs();
        Assert.Single(stillCached); // cache returned the stale view, by design

        // Mutation service would call this after a write.
        _cache.Invalidate();

        var fresh = _scanner.ScanAllJobs();
        Assert.Equal(2, fresh.Count);
        Assert.Contains(fresh, j => j.Id == "job-1");
        Assert.Contains(fresh, j => j.Id == "job-2");
        Assert.Equal(1, _cache.MutationInvalidations);
    }

    [Fact]
    public void SafetyTtl_PicksUpExternalChanges_EvenWithoutInvalidation()
    {
        WriteJob("2-ready", "job-1", "First");
        var firstScan = _scanner.ScanAllJobs();
        Assert.Single(firstScan);

        WriteJob("2-ready", "job-2", "Second");
        // No invalidation (simulating a missed FileSystemWatcher event).
        // Wait past the configured 1 s safety TTL.
        Thread.Sleep(1200);

        var fresh = _scanner.ScanAllJobs();
        Assert.Equal(2, fresh.Count);
    }

    [Fact]
    public void ScanAllJobsRaw_AlwaysHitsDisk_BypassingCache()
    {
        WriteJob("2-ready", "job-1", "First");
        _ = _scanner.ScanAllJobs(); // primes cache

        // Add directly on disk.
        WriteJob("2-ready", "job-2", "Second");

        // Raw read sees the new job even though cache is stale.
        var raw = _scanner.ScanAllJobsRaw();
        Assert.Equal(2, raw.Count);

        // Cached read still returns the stale view (Invalidate wasn't called).
        var cached = _scanner.ScanAllJobs();
        Assert.Single(cached);
    }

    [Fact]
    public void CachedSnapshot_ExcludesArchive_ButRawAndLazyFindStillResolveIt()
    {
        WriteJob(TaskStates.Ready, "job-1", "First");
        WriteJob(TaskStates.Archive, "archived-1", "Archived");

        var raw = _scanner.ScanAllJobsRaw();
        Assert.Equal(2, raw.Count);
        Assert.Contains(raw, j => j.State == TaskStates.Archive);

        var cached = _scanner.ScanAllJobs();
        Assert.Single(cached);
        Assert.DoesNotContain(cached, j => j.State == TaskStates.Archive);

        var archived = _scanner.FindJob("archived-1", _watchPath);
        Assert.NotNull(archived);
        Assert.Equal(TaskStates.Archive, archived!.State);
        Assert.Equal("Archived", archived.Title);
    }

    [Fact]
    public void WithoutCache_ScanAllJobs_AlwaysHitsDisk()
    {
        // Build a scanner without a cache (mimics test fixtures that don't
        // wire one). Each call must read from disk.
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config);
        var bareScanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance, summary);

        WriteJob("2-ready", "job-1", "First");
        Assert.Single(bareScanner.ScanAllJobs());

        WriteJob("2-ready", "job-2", "Second");
        // No cache → next call sees the new job immediately.
        Assert.Equal(2, bareScanner.ScanAllJobs().Count);
    }

    private void WriteJob(string state, string slug, string title)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            JsonSerializer.Serialize(new { id = slug, title, state, order = 1, agent = "claude" }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { }
    }
}
