using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Regression for the second wave of "Sortieren ist buggy" reports: even
/// after the frontend reorder went optimistic, clicking on a card right
/// after a drag stalled noticeably. Root cause sat in
/// <see cref="JobStateMachine.ReorderJobs"/>: it called
/// <c>JobScannerService.FindJob</c> once per item, and FindJob does a full
/// disk-walking <c>ScanAllJobs</c> on every call. For an N-card lane on
/// an M-job board that is O(N x M) folder reads. Subsequent
/// <c>/api/jobs/{id}</c> requests then queue behind the loop, so the
/// detail panel feels like it hangs after a drop.
///
/// Contract: ReorderJobs must complete in O(N+M) - a single scan, then a
/// dictionary lookup per item. We measure on a realistic board (50-card
/// lane in a 200-job workspace) and assert under one second. Pre-fix the
/// same call took multiple seconds; the fix lands it well below 100 ms.
/// </summary>
public class ReorderJobsPerfTests : IDisposable
{
    private readonly string _watchPath;

    public ReorderJobsPerfTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-reorder-perf-" + Guid.NewGuid().ToString("N"));
        foreach (var state in JobStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ReorderJobs_50CardLane_On200JobBoard_FinishesUnderOneSecond()
    {
        // Arrange — 150 archived jobs (typical accumulated tail) plus a
        // 50-card Ready lane that the user is actively shuffling.
        const int archiveCount = 150;
        const int laneCount = 50;
        for (var i = 0; i < archiveCount; i++)
            WriteJob(JobStates.Archive, $"archive-{i:D4}");
        var laneIds = new List<string>(laneCount);
        for (var i = 0; i < laneCount; i++)
        {
            var slug = $"ready-{i:D4}";
            WriteJob(JobStates.Ready, slug);
            laneIds.Add(slug);
        }

        var (scanner, machine) = BuildMachine();

        // Warm one scan so we measure the reorder hot path, not first-touch.
        var jobs = scanner.ScanAllJobs();
        Assert.Equal(archiveCount + laneCount, jobs.Count);

        // Reorder request: rotate the lane (move first to last).
        var rotated = laneIds.Skip(1).Append(laneIds[0]).ToList();
        var payload = rotated.Select(id => new JobOrderItem { JobId = id, WatchPath = _watchPath }).ToList();

        var sw = Stopwatch.StartNew();
        var success = machine.ReorderJobs(payload);
        sw.Stop();

        Assert.True(success);
        Assert.True(
            sw.ElapsedMilliseconds < 1000,
            $"ReorderJobs over a {laneCount}-card lane on a {archiveCount + laneCount}-job board took " +
            $"{sw.ElapsedMilliseconds} ms; this path must be O(N+M) (one scan + dict lookup per item), " +
            "not O(N x M). Look for per-item JobScannerService.FindJob calls in JobStateMachine.ReorderJobs.");

        // Verify persistence: every item received the new order index.
        var afterScan = scanner.ScanAllJobs().ToDictionary(j => j.Id);
        for (var i = 0; i < rotated.Count; i++)
        {
            Assert.Equal(i + 1, afterScan[rotated[i]].Order);
        }
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\"}}");
    }

    private (JobScannerService, JobStateMachine) BuildMachine()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "perf-test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var machine = new JobStateMachine(scanner, NullLogger<JobStateMachine>.Instance);
        return (scanner, machine);
    }
}
