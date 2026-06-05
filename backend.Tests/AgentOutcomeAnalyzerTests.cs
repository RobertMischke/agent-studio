using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the deterministic-signal contract between agent and orchestrator.
/// The signals analyzed here are the load-bearing piece of the bigger
/// orchestration philosophy (deterministic parsing over prompt trust):
/// when a hard sentinel fires, the orchestrator treats it as authoritative;
/// when no sentinel fires, the analyzer must signal that the verdict is a
/// heuristic so <see cref="RunOutcomePolicy"/> can warn the user.
/// </summary>
public class AgentOutcomeAnalyzerTests
{
    private static List<CliOutputLine> Lines(params string[] texts)
    {
        var ts = new DateTime(2026, 5, 2, 10, 0, 0, DateTimeKind.Utc);
        return texts.Select((t, i) => new CliOutputLine
        {
            Timestamp = ts.AddSeconds(i),
            Stream = "stdout",
            Text = t
        }).ToList();
    }

    [Fact]
    public void Sentinel_Done_IsAuthoritativeAndOverridesHeuristic()
    {
        var lines = Lines("I tried but I cannot find the file.", "[[TASK_DONE]]");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 22.5);
        Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
        Assert.True(outcome.MatchedSentinel);
        Assert.Equal("DONE", outcome.SentinelKeyword);
    }

    [Fact]
    public void Sentinel_Blocked_CapturesReason()
    {
        var lines = Lines("Some text.", "[[TASK_BLOCKED:missing credentials]]");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 12.0);
        Assert.Equal(AgentOutcomeKind.Blocked, outcome.Kind);
        Assert.True(outcome.MatchedSentinel);
        Assert.Equal("missing credentials", outcome.Reason);
    }

    [Fact]
    public void Sentinel_NeedsInput_Recognised()
    {
        var lines = Lines("[[TASK_NEEDS_INPUT:please pick A or B]]");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 5.0);
        Assert.Equal(AgentOutcomeKind.NeedsInput, outcome.Kind);
        Assert.True(outcome.MatchedSentinel);
    }

    [Fact]
    public void Sentinel_Noop_Recognised()
    {
        var lines = Lines("Nothing to do.", "[[TASK_NOOP]]");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 4.0);
        Assert.Equal(AgentOutcomeKind.NoOp, outcome.Kind);
        Assert.True(outcome.MatchedSentinel);
    }

    [Fact]
    public void NoOutput_ShortDuration_ClassifiesNoOp()
    {
        // The exact failure shape the user reported: backend ran for 4.6s and
        // produced nothing. This must be a clear NoOp so policy can re-issue.
        var lines = new List<CliOutputLine>();
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 4.6);
        Assert.Equal(AgentOutcomeKind.NoOp, outcome.Kind);
        Assert.False(outcome.MatchedSentinel);
        Assert.Equal(0, outcome.AgentTextChars);
    }

    [Fact]
    public void NoOutput_LongDuration_ClassifiesUnknown()
    {
        // 60 s with no output isn't strictly a no-op (it could be a CLI that
        // suppressed everything); treat as unknown so the policy surfaces a
        // heuristic warning instead of silently re-issuing.
        var outcome = AgentOutcomeAnalyzer.Analyze(new List<CliOutputLine>(), "completed", 60.0);
        Assert.Equal(AgentOutcomeKind.Unknown, outcome.Kind);
        Assert.False(outcome.MatchedSentinel);
    }

    [Fact]
    public void Heuristic_DoneTextWithoutSentinel_FlagsFallback()
    {
        var lines = Lines("Implemented the feature and committed the change.");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 30.0);
        Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
        Assert.False(outcome.MatchedSentinel);
        Assert.Contains("heuristic", outcome.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Heuristic_TerminalQuestion_ClassifiesNeedsInput()
    {
        var lines = Lines("Should I switch the tab to Markdown mode first?");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 12.0);
        Assert.Equal(AgentOutcomeKind.NeedsInput, outcome.Kind);
        Assert.False(outcome.MatchedSentinel);
    }

    [Fact]
    public void SystemAndUserStreams_AreIgnored()
    {
        var ts = DateTime.UtcNow;
        var lines = new List<CliOutputLine>
        {
            new() { Timestamp = ts, Stream = "user", Text = "please continue" },
            new() { Timestamp = ts, Stream = "system", Text = "[taskboard] Started claude CLI" },
            new() { Timestamp = ts, Stream = "orchestrator", Text = "[reissue] previous run no-op'd" }
        };
        // No agent text and a short duration => NoOp.
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 4.6);
        Assert.Equal(AgentOutcomeKind.NoOp, outcome.Kind);
        Assert.Equal(0, outcome.AgentTextChars);
    }

    [Fact]
    public void Failed_Status_DoesNotClassifyNoOp()
    {
        var outcome = AgentOutcomeAnalyzer.Analyze(new List<CliOutputLine>(), "failed", 4.6);
        Assert.NotEqual(AgentOutcomeKind.NoOp, outcome.Kind);
    }

    [Fact]
    public void PermissionDenied_Output_IsClassifiedAsPermissionBlocked()
    {
        var lines = Lines(
            "x List workspace projects (shell)",
            "  | Get-ChildItem C:\\Projects\\agent-taskboard-workspace\\projects -Directory",
            "  └ Permission denied and could not request permission from user");

        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 64.0);

        Assert.Equal(RunIssueKind.PermissionBlocked, outcome.IssueKind);
        Assert.False(outcome.MatchedSentinel);
        Assert.Contains("permission", outcome.Summary ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SilentCompletionMarker_Output_IsClassifiedAsSilentCompletion()
    {
        // Mirror of the [environment-blocker] path: the analyzer trusts
        // the synthetic [codex-silent-completion] marker the runner wrote
        // when CodexSilentCompletionDetector tripped, because the gating
        // happened one layer up and the needle cannot appear elsewhere
        // without the runtime detector having fired.
        var ts = DateTime.UtcNow;
        var lines = new List<CliOutputLine>
        {
            new() { Timestamp = ts, Stream = "stdout", Text = "I edited some files." },
            new() { Timestamp = ts.AddSeconds(2), Stream = "system",
                Text = "[codex-silent-completion] Codex stopped after final tool call without a closing sentinel (silence=92s)" }
        };
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 320.0);
        Assert.Equal(RunIssueKind.SilentCompletion, outcome.IssueKind);
        Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
        Assert.False(outcome.MatchedSentinel);
        Assert.Contains("sentinel", outcome.Summary ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WatchdogKilled_Output_IsClassifiedAsWatchdogTimeout()
    {
        var lines = new List<CliOutputLine>
        {
            new() { Timestamp = DateTime.UtcNow, Stream = "orchestrator", Text = "[giveup] [watchdog] Killed after 60s of silence. Process tree terminated; the run will finalize as failed." }
        };

        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "failed", 60.0);

        Assert.Equal(RunIssueKind.WatchdogTimeout, outcome.IssueKind);
        Assert.Equal(AgentOutcomeKind.Unknown, outcome.Kind);
        Assert.False(outcome.MatchedSentinel);
    }

    [Fact]
    public void WatchdogAutoCancelled_OperatorFriendlyForm_IsClassifiedAsWatchdogTimeout()
    {
        // The Hung state now writes the operator-friendly form
        // `[watchdog-timeout] "title" (cli): auto-cancelled after Ns ...`.
        // The classifier must still mark the run as WatchdogTimeout so the
        // outcome policy treats it the same as the legacy `[watchdog] Killed`
        // shape; otherwise the new wording would silently demote watchdog
        // kills to MissingTerminalSentinel.
        var lines = new List<CliOutputLine>
        {
            new()
            {
                Timestamp = DateTime.UtcNow,
                Stream = "orchestrator",
                Text = "[watchdog-timeout] \"fix-git-diff-container-display\" (claude): auto-cancelled after 180s of silence. The run will finalize as failed."
            }
        };

        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "failed", 180.0);

        Assert.Equal(RunIssueKind.WatchdogTimeout, outcome.IssueKind);
        Assert.False(outcome.MatchedSentinel);
    }

    [Fact]
    public void CompletedTextWithoutSentinel_IsClassifiedAsMissingTerminalSentinel()
    {
        var lines = Lines("I checked the implementation and it looks ready for review.");

        var outcome = AgentOutcomeAnalyzer.Analyze(lines, "completed", 22.0);

        Assert.Equal(RunIssueKind.MissingTerminalSentinel, outcome.IssueKind);
        Assert.Equal(AgentOutcomeKind.Done, outcome.Kind);
        Assert.False(outcome.MatchedSentinel);
    }

    // ---- Case (a): CLI / launch / resume failure --------------------------
    // A resume run that fails the instant it starts (exit != 0, ~0s) and
    // leaves only a CLI error fragment behind must NOT become a terminal
    // classifier-unknown FAILURE. It is a host/CLI failure, routed to the
    // typed CliLaunchFailed issue so the policy rebuilds from disk. This is
    // the exact ASS-755 shape: status=failed, duration=0.0s, only a
    // truncated CLI fragment as "agent" text.

    [Fact]
    public void FailedResume_ZeroDuration_OnlyCliFragment_IsCliLaunchFailed()
    {
        // The truncated codex fragment the user saw ("...orchestrator)").
        var lines = Lines("hestrator)");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "failed", durationSeconds: 0.0);
        Assert.Equal(RunIssueKind.CliLaunchFailed, outcome.IssueKind);
        Assert.NotEqual(RunIssueKind.ClassifierUnknown, outcome.IssueKind);
        Assert.False(outcome.MatchedSentinel);
    }

    [Fact]
    public void FailedResume_RejectedResumeTargetNeedle_IsCliLaunchFailed_RegardlessOfDuration()
    {
        // The capture-fail decision line can leak into the consolidated buffer.
        // The needle is definitive even if the duration is not near-zero.
        var lines = new List<CliOutputLine>
        {
            new() { Timestamp = DateTime.UtcNow, Stream = "stdout", Text = "starting up" },
            new() { Timestamp = DateTime.UtcNow.AddSeconds(1), Stream = "system",
                Text = "[capture-fail] codex rejected the resume target (abc-123); next follow-up will rebuild from disk via Recovery." }
        };
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "failed", durationSeconds: 12.0);
        Assert.Equal(RunIssueKind.CliLaunchFailed, outcome.IssueKind);
    }

    [Fact]
    public void FailedResume_NoConversationFound_ClaudeNeedle_IsCliLaunchFailed()
    {
        var lines = Lines("No conversation found with session ID: 11111111-2222-3333-4444-555555555555");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "failed", durationSeconds: 1.0);
        Assert.Equal(RunIssueKind.CliLaunchFailed, outcome.IssueKind);
    }

    // ---- Case (b): failed run WITH a real agent turn ----------------------
    // A run that produced a genuine, substantial agent turn before failing is
    // NOT a launch failure - it is an unclassifiable agent reply. It stays
    // ClassifierUnknown so the policy re-issues with context (never a
    // terminal FAILURE), but the analyzer must not swallow it into the
    // launch-failure bucket.

    [Fact]
    public void FailedRun_WithRealAgentText_StaysClassifierUnknown_NotCliLaunchFailed()
    {
        var prose = new string('x', 400) + " I made several edits and ran a long investigation across the module.";
        var lines = Lines(prose);
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "failed", durationSeconds: 120.0);
        Assert.Equal(RunIssueKind.ClassifierUnknown, outcome.IssueKind);
        Assert.NotEqual(RunIssueKind.CliLaunchFailed, outcome.IssueKind);
    }

    // ---- Case (c): successful run, no sentinel, inconclusive text ---------
    // A clean exit whose text the heuristic cannot map to any shape stays
    // MissingTerminalSentinel so the orchestrator drives it to a structured
    // close-out, never a terminal classifier-unknown FAILURE.

    [Fact]
    public void SuccessfulRun_InconclusiveText_IsMissingTerminalSentinel()
    {
        var lines = Lines("The weather over the harbour was unusually calm this morning.");
        var outcome = AgentOutcomeAnalyzer.Analyze(lines, status: "completed", durationSeconds: 18.0);
        Assert.Equal(AgentOutcomeKind.Unknown, outcome.Kind);
        Assert.Equal(RunIssueKind.MissingTerminalSentinel, outcome.IssueKind);
        Assert.NotEqual(RunIssueKind.ClassifierUnknown, outcome.IssueKind);
    }
}
