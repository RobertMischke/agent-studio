using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Pipeline;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Tests for the Phase-1 pipeline foundation: aspect post-steps now run
/// in parallel (not sequentially) and each step records into
/// <c>pipeline-execution.json</c> so the Overview pipeline view can
/// render per-step status, tokens, and duration without re-parsing
/// cli-output.log.
///
/// The parallel-overlap assertion is the load-bearing behavioural change
/// in this phase. A regression here means we are back to sum-of-aspects
/// wall-clock cost.
/// </summary>
public class PipelineExecutionParallelTests : IDisposable
{
    private readonly string _jobFolder;

    public PipelineExecutionParallelTests()
    {
        _jobFolder = Path.Combine(Path.GetTempPath(), "pipeline-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_jobFolder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task FourAspects_RunInParallel_WallClockIsCloseToOneAspectNotFour()
    {
        // Each stubbed aspect sleeps for a fixed delay. If the runner is
        // sequential, total wall time is ~4 * perAspect. Parallel runs
        // finish in ~1 * perAspect plus scheduling slack. We assert the
        // total finishes well under the sequential floor (2x perAspect)
        // so the test is robust against CI scheduler jitter.
        var perAspect = TimeSpan.FromMilliseconds(400);
        var runner = BuildRunner(async aspect =>
        {
            await Task.Delay(perAspect);
            return "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]";
        });

        var sw = Stopwatch.StartNew();
        var report = await runner.RunAsync(BuildInputs(),
            new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);
        sw.Stop();

        Assert.Equal(4, report.Verdicts.Count);
        Assert.Equal(AspectStatus.Pass, report.Overall);

        var sequentialFloor = TimeSpan.FromMilliseconds(perAspect.TotalMilliseconds * 2);
        Assert.True(sw.Elapsed < sequentialFloor,
            $"expected parallel run < {sequentialFloor.TotalMilliseconds}ms (sequential would be ~{perAspect.TotalMilliseconds * 4}ms), got {sw.Elapsed.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task FourAspects_AllStartWithinNarrowWindow_ProvesParallelLaunch()
    {
        // Capture wall-clock at the moment each stubbed CLI call begins.
        // A sequential runner would space starts by perAspect; the parallel
        // runner launches all four within milliseconds (bounded by the
        // semaphore cap of 4 = aspect count).
        var perAspect = TimeSpan.FromMilliseconds(300);
        var starts = new System.Collections.Concurrent.ConcurrentBag<DateTime>();
        var runner = BuildRunner(async aspect =>
        {
            starts.Add(DateTime.UtcNow);
            await Task.Delay(perAspect);
            return "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]";
        });

        await runner.RunAsync(BuildInputs(),
            new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        var ordered = starts.OrderBy(t => t).ToList();
        Assert.Equal(4, ordered.Count);
        var spread = ordered[^1] - ordered[0];
        Assert.True(spread < TimeSpan.FromMilliseconds(perAspect.TotalMilliseconds),
            $"expected all 4 aspect starts within < {perAspect.TotalMilliseconds}ms (proves parallel launch), got spread of {spread.TotalMilliseconds}ms");
    }

    [Fact]
    public async Task VerdictsReturnedInRequestedOrder_RegardlessOfCompletionOrder()
    {
        // Make later-requested aspects finish FIRST so a naive parallel
        // implementation would return them in the wrong order. The runner
        // must re-sort to match the input list so downstream consumers
        // (ReviewDecisionOrchestrator, AspectRunReport.From) see
        // deterministic ordering.
        var runner = BuildRunner(async aspect =>
        {
            var delay = aspect switch
            {
                "requirement-fit" => 300,
                "code-quality" => 200,
                "documentation-impact" => 100,
                "tests-and-evidence" => 50,
                _ => 0,
            };
            await Task.Delay(delay);
            return $"[[ASPECT_VERDICT: status=pass; summary={aspect} done]]\n[[TASK_DONE]]";
        });

        var requested = new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" };
        var report = await runner.RunAsync(BuildInputs(), requested,
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(requested.Length, report.Verdicts.Count);
        for (var i = 0; i < requested.Length; i++)
        {
            Assert.Equal(requested[i], report.Verdicts[i].Aspect);
        }
    }

    [Fact]
    public async Task PipelineExecutionLog_RecordsPerStepStatusTokensAndDuration()
    {
        // When a PipelineExecutionLog is wired AND the orchestrator's
        // Begin() has stamped the file, the runner appends one record per
        // aspect with the post-run status, model, and duration. We seed
        // the file via Begin() because the aspect runner does not write
        // it on its own (the orchestrator owns lifecycle).
        var pipelineLog = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        pipelineLog.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "test-job");

        var runner = BuildRunner(_ => Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]"),
            pipelineLog: pipelineLog);

        await runner.RunAsync(BuildInputs(),
            new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        pipelineLog.Complete(_jobFolder);

        var record = pipelineLog.Read(_jobFolder);
        Assert.NotNull(record);
        Assert.Equal(PipelineCatalogue.StandardPipelineId, record!.PipelineId);
        Assert.True(record.IsComplete);

        // Each aspect step is recorded with status=Passed and a verdict token.
        foreach (var stepId in PipelineCatalogue.AspectStepIds)
        {
            var step = record.Steps.FirstOrDefault(s =>
                string.Equals(s.StepId, stepId, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(step);
            Assert.Equal(PipelineStepStatus.Passed, step!.Status);
            Assert.Equal("pass", step.Verdict);
            Assert.Equal("claude-haiku-4-5", step.Model);
            Assert.NotNull(step.StartedAt);
            Assert.NotNull(step.CompletedAt);
        }

        // The git-commit-attribution slot stays Planned (no implementation
        // in Phase 1; the follow-up task fills it).
        var commitStep = record.Steps.First(s =>
            string.Equals(s.StepId, PipelineCatalogue.GitCommitAttributionStepId, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PipelineStepStatus.Planned, commitStep.Status);
    }

    [Fact]
    public void PipelineExecutionLog_FileIsValidJson_ParseableByExternalReader()
    {
        // The Overview pipeline-view will be served by a TS frontend, so
        // the JSON must be self-describing and round-trip cleanly through
        // a vanilla deserialiser (no custom converters).
        var pipelineLog = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        pipelineLog.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "test-job");
        pipelineLog.RecordStep(_jobFolder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.AspectStepIds[0],
            Kind = StepKind.Aspect,
            Model = "claude-haiku-4-5",
            Status = PipelineStepStatus.Passed,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow.AddMilliseconds(123),
            DurationMs = 123,
            InputTokens = 100,
            OutputTokens = 50,
            Verdict = "pass",
        });
        pipelineLog.Complete(_jobFolder);

        var path = Path.Combine(_jobFolder, PipelineExecutionLog.FileName);
        Assert.True(File.Exists(path));
        var json = File.ReadAllText(path);
        // The pretty-printed file must parse as JSON via the System.Text.Json
        // default options so an external reader (TS frontend) gets the same.
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("pipelineId", out var pipelineIdProp));
        Assert.Equal(PipelineCatalogue.StandardPipelineId, pipelineIdProp.GetString());
        Assert.True(doc.RootElement.TryGetProperty("steps", out var stepsProp));
        Assert.Equal(JsonValueKind.Array, stepsProp.ValueKind);
        Assert.True(stepsProp.GetArrayLength() > 0);
    }

    private AspectRunInputs BuildInputs() => new(
        Project: "demo",
        JobId: "test-job",
        JobTitle: "Test job",
        JobFolderPath: _jobFolder,
        TaskBody: "# Task\n\nDo the thing.",
        RecentLog: "[12:00:00] [stdout] running\n[12:00:01] [stdout] [[TASK_DONE]]",
        DiffSummary: "Files: src/foo.ts (+10, -2)",
        StatusSummary: "# Status\n\nDone.");

    private AspectRunnerService BuildRunner(
        Func<string, Task<string>> stub,
        PipelineExecutionLog? pipelineLog = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var runner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance,
            pipelineLog: pipelineLog);
        runner.CliRunner = (aspectId, _, _, _, _, _) => stub(aspectId);
        return runner;
    }
}
