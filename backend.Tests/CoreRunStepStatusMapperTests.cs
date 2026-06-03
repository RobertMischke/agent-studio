using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the run-status -> CORE pipeline step status mapping.
///
/// The bug this guards against: <c>ProjectRunner.RecordCoreRunFinish</c> used
/// to mark the CORE "Agent execution" step Passed only when
/// <c>status == "completed" AND exitCode is null or 0</c>. But a
/// sentinel-detected or silent-completion run - the deterministic happy path
/// for Claude / Codex - is killed to release the lingering process, and
/// <c>Process.Kill</c> hands back <c>exitCode = -1</c> on Windows. The
/// <see cref="RunStatusClassifier"/> already settles that into
/// <see cref="RunStatuses.Completed"/>, but the extra exit-code gate flipped
/// the CORE step back to Failed, so it never showed as completed in the
/// Overview pipeline table even though the run had ended cleanly.
/// </summary>
public class CoreRunStepStatusMapperTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(137)]
    public void CompletedStatus_AnyExitCode_IsPassed(int? exitCode)
    {
        // The headline regression: a sentinel-killed completion reports
        // status="completed" with exitCode=-1 on Windows. The CORE step must
        // be Passed regardless of the kill-induced exit code.
        var execution = new CliExecution { Status = RunStatuses.Completed, ExitCode = exitCode };

        Assert.Equal(PipelineStepStatus.Passed, CoreRunStepStatusMapper.From(execution));
    }

    [Theory]
    [InlineData(RunStatuses.Failed)]
    [InlineData(RunStatuses.Stopped)]
    [InlineData("cancelled")]
    [InlineData("running")]
    [InlineData("")]
    public void NonCompletedStatus_IsFailed(string status)
    {
        // Anything the classifier did not call "completed" is not a successful
        // CORE run, so the step is Failed - even with a zero exit code.
        var execution = new CliExecution { Status = status, ExitCode = 0 };

        Assert.Equal(PipelineStepStatus.Failed, CoreRunStepStatusMapper.From(execution));
    }

    [Fact]
    public void StatusComparison_IsCaseInsensitive()
    {
        Assert.Equal(PipelineStepStatus.Passed, CoreRunStepStatusMapper.From("COMPLETED"));
    }

    [Fact]
    public void NullStatus_IsFailed()
    {
        Assert.Equal(PipelineStepStatus.Failed, CoreRunStepStatusMapper.From((string?)null));
    }

    // --- Resolve: the call-site invariant ---------------------------------
    //
    // The original bug lived at the CALL SITE (ProjectRunner.RecordCoreRunFinish
    // gated the step on "completed AND exitCode is null or 0"), not in the pure
    // status mapper. A test that only exercises From() can stay green while a
    // refactor re-adds an exit-code gate at the call site. Resolve binds status
    // AND failure reason into the single function the call site is forced to
    // use, so these tests lock the whole decision the row actually renders.

    [Fact]
    public void Resolve_SentinelKilledCompletion_IsPassedWithNoReason()
    {
        // The headline regression: status="completed", exitCode=-1 (Process.Kill
        // on Windows). Must be Passed, and a completed run carries no reason.
        var execution = new CliExecution { Status = RunStatuses.Completed, ExitCode = -1 };

        var (status, reason, _) = CoreRunStepStatusMapper.Resolve(execution);

        Assert.Equal(PipelineStepStatus.Passed, status);
        Assert.Null(reason);
    }

    [Fact]
    public void Resolve_CleanCompletion_IsPassedWithNoReason()
    {
        var execution = new CliExecution { Status = RunStatuses.Completed, ExitCode = 0 };

        var (status, reason, _) = CoreRunStepStatusMapper.Resolve(execution);

        Assert.Equal(PipelineStepStatus.Passed, status);
        Assert.Null(reason);
    }

    [Fact]
    public void Resolve_FailedRun_IsFailedWithExitCodeReason()
    {
        var execution = new CliExecution { Status = RunStatuses.Failed, ExitCode = 1 };

        var (status, reason, _) = CoreRunStepStatusMapper.Resolve(execution);

        Assert.Equal(PipelineStepStatus.Failed, status);
        Assert.Equal("agent run failed (exit 1)", reason);
    }

    [Fact]
    public void Resolve_FailedRunWithoutExitCode_OmitsExitFragment()
    {
        var execution = new CliExecution { Status = RunStatuses.Failed, ExitCode = null };

        var (status, reason, _) = CoreRunStepStatusMapper.Resolve(execution);

        Assert.Equal(PipelineStepStatus.Failed, status);
        Assert.Equal("agent run failed", reason);
    }

    [Fact]
    public void Resolve_BlankStatus_DescribesUnknown()
    {
        var execution = new CliExecution { Status = "", ExitCode = 2 };

        var (status, reason, _) = CoreRunStepStatusMapper.Resolve(execution);

        Assert.Equal(PipelineStepStatus.Failed, status);
        Assert.Equal("agent run unknown (exit 2)", reason);
    }

    // --- ReconcileVerdict: icon and badge can never tell two stories -------
    //
    // Bug ASS-2: the CORE row showed a red "Failed" icon (from Status) AND a
    // green "SUCCESS" badge (from Verdict) at once because the two fields were
    // computed independently and never reconciled. Resolve now drops a
    // success-class verdict whenever the deterministic status is not Passed, so
    // a single persisted record can no longer contradict itself.

    [Fact]
    public void Resolve_FailedRunClaimingSuccess_DropsContradictoryVerdict()
    {
        // The exact ASS-2 record: status not "completed" but RunOutcome="success".
        // The Failed icon is authoritative; the success badge must be suppressed.
        var execution = new CliExecution
        {
            Status = RunStatuses.Failed,
            ExitCode = 1,
            RunOutcome = TerminalRunOutcomeKinds.Success,
        };

        var (status, _, verdict) = CoreRunStepStatusMapper.Resolve(execution);

        Assert.Equal(PipelineStepStatus.Failed, status);
        Assert.Null(verdict);
    }

    [Fact]
    public void Resolve_StoppedRunClaimingNoOp_DropsContradictoryVerdict()
    {
        var execution = new CliExecution
        {
            Status = RunStatuses.Stopped,
            ExitCode = -1,
            RunOutcome = TerminalRunOutcomeKinds.NoOp,
        };

        var (status, _, verdict) = CoreRunStepStatusMapper.Resolve(execution);

        Assert.Equal(PipelineStepStatus.Failed, status);
        Assert.Null(verdict);
    }

    [Fact]
    public void Resolve_CompletedRunWithSuccess_KeepsVerdict()
    {
        // A consistent record: Passed icon + success badge tell one story.
        var execution = new CliExecution
        {
            Status = RunStatuses.Completed,
            ExitCode = 0,
            RunOutcome = TerminalRunOutcomeKinds.Success,
        };

        var (status, _, verdict) = CoreRunStepStatusMapper.Resolve(execution);

        Assert.Equal(PipelineStepStatus.Passed, status);
        Assert.Equal(TerminalRunOutcomeKinds.Success, verdict);
    }

    [Fact]
    public void Resolve_SentinelKilledCompletionWithSuccess_KeepsVerdict()
    {
        // The load-bearing rule: a kill-induced exitCode=-1 deterministic
        // completion is Passed and keeps its success verdict - reconciliation
        // keys off the classified status, never the exit code, so it never
        // re-fails this happy path.
        var execution = new CliExecution
        {
            Status = RunStatuses.Completed,
            ExitCode = -1,
            RunOutcome = TerminalRunOutcomeKinds.Success,
        };

        var (status, _, verdict) = CoreRunStepStatusMapper.Resolve(execution);

        Assert.Equal(PipelineStepStatus.Passed, status);
        Assert.Equal(TerminalRunOutcomeKinds.Success, verdict);
    }

    [Theory]
    [InlineData(TerminalRunOutcomeKinds.Failed)]
    [InlineData(TerminalRunOutcomeKinds.Interrupted)]
    [InlineData(TerminalRunOutcomeKinds.Blocked)]
    [InlineData(TerminalRunOutcomeKinds.CommittedPartial)]
    public void Resolve_FailedRunWithFailureClassVerdict_KeepsVerdict(string runOutcome)
    {
        // A failed step paired with a failure-class verdict is internally
        // consistent - nothing to reconcile, so the verdict passes through and
        // the row can still show e.g. a "Partial" or "Blocked" badge.
        var execution = new CliExecution
        {
            Status = RunStatuses.Failed,
            ExitCode = 1,
            RunOutcome = runOutcome,
        };

        var (status, _, verdict) = CoreRunStepStatusMapper.Resolve(execution);

        Assert.Equal(PipelineStepStatus.Failed, status);
        Assert.Equal(runOutcome, verdict);
    }

    [Fact]
    public void ReconcileVerdict_IsCaseInsensitiveOnSuccessClaim()
    {
        // RunOutcome casing must not let a contradictory badge slip through.
        Assert.Null(CoreRunStepStatusMapper.ReconcileVerdict(PipelineStepStatus.Failed, "SUCCESS"));
        Assert.Null(CoreRunStepStatusMapper.ReconcileVerdict(PipelineStepStatus.Failed, "NoOp"));
    }
}
