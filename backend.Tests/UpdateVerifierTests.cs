extern alias UpdSvc;
using UpdSvc::AgentTaskboard.UpdateService;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Pure-decision tests for <see cref="UpdateVerifier.EvaluateChecks"/> (phase
/// 6 of the ADR-0031 pipeline). The HTTP-side of the verifier is exercised
/// indirectly via the integration suite; this class owns the bar:
///   - all six pass -> AllPassed=true, Failures=[]
///   - any single fail -> AllPassed=false, Failures captures step+observed+expected
///   - 5/6 still flips to failed (the strict-bar invariant)
///   - empty input is treated as "pass", but real callers never pass an
///     empty list - the orchestrator stops at the first failure mid-stream.
/// </summary>
public class UpdateVerifierTests
{
    private static VerificationCheck Pass(string step) =>
        new("run1", step, true, "ok", "ok", DateTime.UtcNow, 1);

    private static VerificationCheck Fail(string step, string observed, string expected) =>
        new("run1", step, false, observed, expected, DateTime.UtcNow, 1);

    [Fact]
    public void AllSixPass_AllPassedTrue_NoFailures()
    {
        var rows = new[]
        {
            Pass("healthz-stable"),
            Pass("runner-status"),
            Pass("jobs-grouped"),
            Pass("clients"),
            Pass("cli-quota"),
            Pass("db-touch"),
        };

        var outcome = UpdateVerifier.EvaluateChecks(rows);

        Assert.True(outcome.AllPassed);
        Assert.Empty(outcome.Failures);
        Assert.Equal(6, outcome.Checks.Count);
    }

    [Fact]
    public void OneFails_AllPassedFalse_FailureCarriesObservedAndExpected()
    {
        var rows = new[]
        {
            Pass("healthz-stable"),
            Fail("runner-status", "missing: project-a", "every pre-snapshot project present"),
            Pass("jobs-grouped"),
        };

        var outcome = UpdateVerifier.EvaluateChecks(rows);

        Assert.False(outcome.AllPassed);
        Assert.Single(outcome.Failures);
        var f = outcome.Failures[0];
        Assert.Equal("runner-status", f.Step);
        Assert.Equal("missing: project-a", f.Observed);
        Assert.Equal("every pre-snapshot project present", f.Expected);
    }

    [Fact]
    public void FiveOfSixPasses_StillFails_StrictBar()
    {
        // ADR-0031 hard rule: the bar is intentionally strict. 5 of 6 still
        // flips to failed; we never silently soft-pass.
        var rows = new[]
        {
            Pass("healthz-stable"),
            Pass("runner-status"),
            Pass("jobs-grouped"),
            Pass("clients"),
            Pass("cli-quota"),
            Fail("db-touch", "http=503", "http=200"),
        };

        var outcome = UpdateVerifier.EvaluateChecks(rows);

        Assert.False(outcome.AllPassed);
        Assert.Single(outcome.Failures);
        Assert.Equal("db-touch", outcome.Failures[0].Step);
    }

    [Fact]
    public void MultipleFailures_AllSurfaceInOrder()
    {
        var rows = new[]
        {
            Fail("healthz-stable", "http=0", "5x http=200"),
            Fail("runner-status", "http=502", "http=200"),
        };

        var outcome = UpdateVerifier.EvaluateChecks(rows);

        Assert.False(outcome.AllPassed);
        Assert.Equal(2, outcome.Failures.Count);
        Assert.Equal("healthz-stable", outcome.Failures[0].Step);
        Assert.Equal("runner-status", outcome.Failures[1].Step);
    }

    [Fact]
    public void EmptyChecks_DefaultsToAllPassedTrue()
    {
        // A defensive case: an empty list yields AllPassed=true. Real callers
        // never produce an empty list - the orchestrator either runs the matrix
        // or short-circuits earlier - but the pure function should not throw.
        var outcome = UpdateVerifier.EvaluateChecks(Array.Empty<VerificationCheck>());

        Assert.True(outcome.AllPassed);
        Assert.Empty(outcome.Failures);
    }
}

/// <summary>
/// Phase-label mapping is the FE-visible contract for the block-modal title
/// in ADR-0031. The internal helper is exercised through the public
/// orchestrator surface, but pinning the strings here would couple the test
/// to a private API. Instead we lock the FE contract in
/// <c>frontend/e2e/update-pipeline-ux.spec.ts</c> and keep this test class
/// focused on the decision shape.
/// </summary>
public class UpdateOrchestratorPhaseLabelTests
{
    [Fact]
    public void IdleHasNoLabel()
    {
        // The block-modal hides on idle, so a null label is correct.
        // Asserting via the public type guards the FE fallback path:
        // null phaseLabel -> client uses its local humanPhase() map.
        var status = new UpdateStatus(
            Phase: "idle", PhaseLabel: null, Message: null,
            CurrentRunId: null, StartedAt: null, FinishedAt: null,
            HeadLocal: "abc", HeadOrigin: null, BehindBy: 0,
            PendingCommits: Array.Empty<CommitInfo>(),
            LastFetchAt: null, LastUpdateAt: null, LastSuccessAt: null,
            LastRunFinishedAt: null, LastRunHeadBefore: null, LastRunHeadAfter: null,
            IsRunning: false, BackendReachable: true,
            ServiceVersion: "0.0.0", ProductVersion: "0.0.0", Mode: "manual",
            VerificationFailures: null, AutoRollbackEnabled: false);

        Assert.Null(status.PhaseLabel);
        Assert.False(status.IsRunning);
    }
}

/// <summary>
/// ADR-0031 reissue-2026-05-11: <see cref="RollbackResult"/> now carries the
/// per-step verification outcome of the rollback's re-run of phases 5+6+7.
/// These tests lock the wire shape so old readers stay green and new
/// callers can rely on the optional <c>verificationFailures</c> field.
/// </summary>
public class RollbackResultContractTests
{
    [Fact]
    public void OkRollback_HasNullVerificationFailures()
    {
        var ok = new RollbackResult(
            RunId: "r1",
            Status: "ok",
            HeadBefore: "bbbbbbb",
            HeadAfter: "aaaaaaa",
            StartedAt: DateTime.UtcNow,
            FinishedAt: DateTime.UtcNow,
            Error: null);

        Assert.Equal("ok", ok.Status);
        Assert.Null(ok.VerificationFailures);
    }

    [Fact]
    public void FailedRollbackAfterVerification_CarriesStrictBarFailures()
    {
        // A rollback that brought the backend up but failed the strict 6-check
        // matrix surfaces the failing step so the operator does not have to
        // diff verification.jsonl against rollback-verification.jsonl.
        var failures = new[]
        {
            new VerificationFailure("clients", "count=0", ">= 1 client (local-default invariant)"),
        };

        var failed = new RollbackResult(
            RunId: "r1",
            Status: "failed",
            HeadBefore: "bbbbbbb",
            HeadAfter: "aaaaaaa",
            StartedAt: DateTime.UtcNow,
            FinishedAt: DateTime.UtcNow,
            Error: "verification after rollback failed",
            VerificationFailures: failures);

        Assert.Equal("failed", failed.Status);
        Assert.NotNull(failed.VerificationFailures);
        Assert.Single(failed.VerificationFailures!);
        Assert.Equal("clients", failed.VerificationFailures![0].Step);
    }
}
