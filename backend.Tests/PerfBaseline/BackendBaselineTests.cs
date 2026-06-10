using System.Diagnostics;

// TaskEndpointHelpersAccessor lives in AgentStudio.Tests (this namespace)
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

/// <summary>
/// Captures the BACKEND baseline numbers the perf overhaul measures itself
/// against. Runs the polled hot paths (and the building blocks they call)
/// against synthetic boards of N = 10 / 50 / 200 / 500 jobs, computes
/// p50/p95/p99 across 30 iterations after warm-up, and writes a JSON report
/// to logs/perf/backend-&lt;scenario&gt;-latest.json that the HTML generator
/// in tools/perf-report consumes.
///
/// <para>
/// Scenario tag picks itself up from the PERF_SCENARIO env var; default is
/// <c>baseline</c>. Set it to <c>after-cycle-1</c> etc. between runs so the
/// generator can render the before/after comparison.
/// </para>
///
/// <para>
/// The test asserts nothing about the numbers - this is a measurement run,
/// not a regression gate. Regression gates live in JobsEndpointPerfTests,
/// ReorderJobsPerfTests, ProjectTokenUsageEndpointPerfTests; tightening
/// them after each cycle is a separate concern.
/// </para>
///
/// <para>
/// The test is gated by env var <c>RUN_PERF_BASELINE=1</c> so it does not
/// add ~30 seconds to the default <c>dotnet test</c> wall time. Invoke
/// directly with:
/// <code>RUN_PERF_BASELINE=1 dotnet test backend.Tests --filter "FullyQualifiedName~BackendBaselineTests"</code>
/// </para>
/// </summary>
public class BackendBaselineTests
{
    private readonly ITestOutputHelper _out;

    public BackendBaselineTests(ITestOutputHelper output)
    {
        _out = output;
    }

    [Fact]
    public void Run_BackendBaseline_AcrossNValues()
    {
        if (Environment.GetEnvironmentVariable("RUN_PERF_BASELINE") != "1")
        {
            _out.WriteLine("Skipped: set RUN_PERF_BASELINE=1 to run.");
            return;
        }

        var scenario = Environment.GetEnvironmentVariable("PERF_SCENARIO") ?? "baseline";
        // Cycle 1+: PERF_USE_CACHE=1 wires the TaskIndexCache so the test
        // measures cached hot paths. Default false keeps the baseline runs
        // (Cycle 0) honest — they reflect ScanAllJobs going through disk.
        var withCache = Environment.GetEnvironmentVariable("PERF_USE_CACHE") == "1";
        var nValues = new[] { 10, 50, 200, 500 };
        var iterations = 30;
        var warmup = 3;
        var metrics = new List<PerfMetric>();

        foreach (var n in nValues)
        {
            using var fx = new PerfBaselineFixture(n, scenarioTag: scenario, withCache: withCache);

            // 1) ScanAllJobs - the foundation of every polled endpoint today.
            metrics.Add(Measure($"ScanAllJobs", fx, n, iterations, warmup,
                () => { _ = fx.Scanner.ScanAllJobs(); }));

            // 2) FindJob (calls ScanAllJobs internally per current impl).
            //    Use a job in the middle of the archive lane so the hash
            //    doesn't favor early/late positions.
            var midSlug = $"job-{n / 2:D5}";
            metrics.Add(Measure($"FindJob", fx, n, iterations, warmup,
                () => { _ = fx.Scanner.FindJob(midSlug, fx.WatchPath); }));

            // 3) GetJobDetail (calls FindJob -> ScanAllJobs + reads task.json).
            metrics.Add(Measure($"GetJobDetail", fx, n, iterations, warmup,
                () => { _ = fx.Scanner.GetJobDetail(midSlug, fx.WatchPath); }));

            // 4) ProjectRunner.GetStatus equivalent: ScanAllJobs + lane filter.
            //    Today this is ProjectRunner.GetQueuedJobIds() which does
            //    ScanAllJobs().Where(...).OrderBy(...).Select(...).ToList().
            metrics.Add(Measure($"GetQueuedJobIds (status hot path)", fx, n, iterations, warmup,
                () =>
                {
                    var queued = fx.Scanner.ScanAllJobs()
                        .Where(j => j.ProjectName == fx.ProjectName && j.State == TaskStates.Ready)
                        .OrderBy(j => j.Order)
                        .Select(j => j.Id)
                        .ToList();
                    _ = queued;
                }));

            // 5) /api/tasks simulation: ScanAllJobs + WithRuntime per job.
            metrics.Add(Measure($"/api/tasks (full pipeline)", fx, n, iterations, warmup,
                () =>
                {
                    var raw = fx.Scanner.ScanAllJobs();
                    var enriched = raw
                        .Where(j => !j.Fixture)
                        .Select(j => TaskEndpointHelpersAccessor.WithRuntime(j, fx.Router, fx.Runners))
                        .ToList();
                    _ = enriched;
                }));

            // 6) /api/tasks/grouped simulation: same + grouping projection.
            metrics.Add(Measure($"/api/tasks/grouped (full pipeline)", fx, n, iterations, warmup,
                () =>
                {
                    var raw = fx.Scanner.ScanAllJobs();
                    var enriched = raw
                        .Where(j => !j.Fixture)
                        .Select(j => TaskEndpointHelpersAccessor.WithRuntime(j, fx.Router, fx.Runners))
                        .ToList();
                    var grouped = new
                    {
                        Backlog = enriched.Where(j => j.State == TaskStates.Backlog).OrderBy(j => j.Order).ToList(),
                        Preparation = enriched.Where(j => j.State == TaskStates.Preparation).OrderBy(j => j.Order).ToList(),
                        Ready = enriched.Where(j => j.State == TaskStates.Ready).OrderBy(j => j.Order).ToList(),
                        Progress = enriched.Where(j => j.State == TaskStates.Progress).OrderBy(j => j.Order).ToList(),
                        AutoReview = enriched.Where(j => j.State == TaskStates.AutoReview).OrderBy(j => j.Order).ToList(),
                        HumanReview = enriched.Where(j => j.State == TaskStates.HumanReview).OrderBy(j => j.Order).ToList(),
                        Completed = enriched.Where(j => j.State == TaskStates.Completed).OrderBy(j => j.Order).ToList(),
                        Archive = enriched.Where(j => j.State == TaskStates.Archive).OrderBy(j => j.Order).ToList(),
                    };
                    _ = grouped;
                }));

            // 7) /api/runner/status simulation: TaskRunnerService.GetStatus().
            //    Note: TaskRunnerService runs as a BackgroundService, but
            //    GetStatus() iterates the in-memory _runners dictionary which
            //    is populated by ExecuteAsync. For this baseline we measure
            //    the pure hot-path cost: an empty _runners dict + zero work.
            //    This is the lower bound; with N runners the cost grows.
            metrics.Add(Measure($"TaskRunnerService.GetStatus (no runners)", fx, n, iterations, warmup,
                () => { _ = fx.Runners.GetStatus(); }));
        }

        var path = PerfReportSink.Write(scenario, metrics);
        _out.WriteLine($"Wrote backend perf report: {path}");
        foreach (var m in metrics)
        {
            _out.WriteLine($"  {m.Name,-50} N={m.TaskCount,4}  p50={m.Stats.P50Ms,7:F2}ms  p95={m.Stats.P95Ms,7:F2}ms  p99={m.Stats.P99Ms,7:F2}ms  max={m.Stats.MaxMs,7:F2}ms");
        }
    }

    private static PerfMetric Measure(
        string name, PerfBaselineFixture fx, int jobCount, int iterations, int warmup,
        Action body)
    {
        // Warmup so JIT, file system cache, and any first-touch lazy state
        // don't bias the first measurements.
        for (var i = 0; i < warmup; i++) body();

        var samples = new List<double>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            body();
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        return new PerfMetric(name, fx.ProjectName, jobCount, PerfStats.From(samples));
    }
}
