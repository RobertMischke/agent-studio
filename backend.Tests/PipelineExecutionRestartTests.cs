using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Pipeline;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Tests for restart visibility in <c>pipeline-execution.json</c>. A
/// re-run / re-issue must surface as a fresh record with an incremented
/// <see cref="PipelineExecutionRecord.Attempt"/>, and the previous run's
/// steps must survive in <see cref="PipelineExecutionRecord.PreviousAttempts"/>
/// so an operator can still tell old step runs apart from the current ones.
///
/// This is the acceptance criterion for "Pipeline-Neustart sichtbar machen":
/// a re-run is clearly a new run, and old vs. new step runs are
/// distinguishable.
/// </summary>
public class PipelineExecutionRestartTests : IDisposable
{
    private readonly string _jobFolder;
    private readonly PipelineExecutionLog _log;

    public PipelineExecutionRestartTests()
    {
        _jobFolder = Path.Combine(Path.GetTempPath(), "pipeline-restart-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobFolder);
        _log = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_jobFolder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void FirstRun_IsAttemptOne_WithNoPreviousAttempts()
    {
        var record = _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");

        Assert.Equal(1, record.Attempt);
        Assert.Empty(record.PreviousAttempts);
    }

    [Fact]
    public void CompletedRun_ThenEnsureRun_StartsAttemptTwo_ArchivingPriorSteps()
    {
        // First run: mark a step done, complete it.
        _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");
        var doneAt = DateTime.UtcNow;
        _log.RecordStep(_jobFolder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.CoreAgentRunStepId,
            Kind = StepKind.Core,
            Model = "claude-opus-4-8",
            Status = PipelineStepStatus.Passed,
            StartedAt = doneAt.AddSeconds(-5),
            CompletedAt = doneAt,
            DurationMs = 5000,
        });
        _log.Complete(_jobFolder);

        // Re-issue: EnsureRun sees a complete record and begins a fresh run.
        var second = _log.EnsureRun(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");

        Assert.Equal(2, second.Attempt);
        Assert.Single(second.PreviousAttempts);

        // The archived prior run keeps its step outcome so old vs. new is clear.
        var archived = second.PreviousAttempts[0];
        Assert.Equal(1, archived.Attempt);
        Assert.True(archived.IsComplete);
        var archivedCore = archived.Steps.First(s =>
            string.Equals(s.StepId, PipelineCatalogue.CoreAgentRunStepId, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PipelineStepStatus.Passed, archivedCore.Status);
        Assert.Equal(5000, archivedCore.DurationMs);

        // The fresh run's own steps are reset to Pending/Planned.
        var freshCore = second.Steps.First(s =>
            string.Equals(s.StepId, PipelineCatalogue.CoreAgentRunStepId, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PipelineStepStatus.Pending, freshCore.Status);
        Assert.Null(second.CompletedAt);
    }

    [Fact]
    public void Begin_OnExistingSameJobRun_ArchivesAndIncrementsAttempt()
    {
        _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");
        var second = _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");

        Assert.Equal(2, second.Attempt);
        Assert.Single(second.PreviousAttempts);
        Assert.Equal(1, second.PreviousAttempts[0].Attempt);
    }

    [Fact]
    public void EnsureRun_WhileInFlight_ReturnsSameRecord_NoAttemptBump()
    {
        var first = _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");
        // No Complete() -> the run is still in flight.
        var same = _log.EnsureRun(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");

        Assert.Equal(1, same.Attempt);
        Assert.Empty(same.PreviousAttempts);
        Assert.Equal(first.StartedAt, same.StartedAt);
    }

    [Fact]
    public void MultipleRestarts_KeepNewestFirstOrder_AndFlattenHistory()
    {
        // Run three times, completing each, so the third run carries two
        // archived attempts ordered most-recent-first.
        for (var i = 0; i < 3; i++)
        {
            _log.EnsureRun(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");
            _log.Complete(_jobFolder);
        }
        var fourth = _log.EnsureRun(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");

        Assert.Equal(4, fourth.Attempt);
        Assert.Equal(3, fourth.PreviousAttempts.Count);

        // Newest-first: the immediately prior run (attempt 3) leads the list.
        Assert.Equal(3, fourth.PreviousAttempts[0].Attempt);
        Assert.Equal(2, fourth.PreviousAttempts[1].Attempt);
        Assert.Equal(1, fourth.PreviousAttempts[2].Attempt);

        // Flattened: archived entries carry no nested history of their own.
        Assert.All(fourth.PreviousAttempts, a => Assert.Empty(a.PreviousAttempts));
    }

    [Fact]
    public void ArchivedAttempts_AreBounded()
    {
        // Drive many more restarts than the archive cap and confirm the
        // PreviousAttempts list stays bounded while the attempt counter keeps
        // climbing.
        const int runs = 15;
        for (var i = 0; i < runs; i++)
        {
            _log.EnsureRun(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");
            _log.Complete(_jobFolder);
        }
        var latest = _log.EnsureRun(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");

        Assert.Equal(runs + 1, latest.Attempt);
        Assert.True(latest.PreviousAttempts.Count <= 10,
            $"expected archived attempts bounded to 10, got {latest.PreviousAttempts.Count}");
        // The most recent archived run is the one just before this attempt.
        Assert.Equal(runs, latest.PreviousAttempts[0].Attempt);
    }

    [Fact]
    public void LeftoverRecordFromDifferentJob_IsNotTreatedAsRestart()
    {
        // A stale file from another job is not a restart of this one: the new
        // record begins at attempt 1 with no inherited history.
        _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "other-job");
        _log.Complete(_jobFolder);

        var fresh = _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");

        Assert.Equal(1, fresh.Attempt);
        Assert.Empty(fresh.PreviousAttempts);
    }

    [Fact]
    public void RestartHistory_SurvivesRoundTripThroughDisk()
    {
        _log.Begin(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");
        _log.Complete(_jobFolder);
        _log.EnsureRun(_jobFolder, PipelineCatalogue.Standard, project: "demo", jobId: "job-1");

        // Read back fresh from disk (no in-memory shortcut).
        var reread = _log.Read(_jobFolder);
        Assert.NotNull(reread);
        Assert.Equal(2, reread!.Attempt);
        Assert.Single(reread.PreviousAttempts);
        Assert.Equal(1, reread.PreviousAttempts[0].Attempt);
    }
}
