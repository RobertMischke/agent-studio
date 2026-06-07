using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the post-run policy that turns a run's outcome into the
/// orchestrator's next move. Tests follow the same matrix style as
/// <see cref="TaskRunnerPlanTests"/>: the inputs are intent + plan event
/// kind + outcome shape + retry attempt; the outputs are the
/// <see cref="OutcomeAction"/> kind, meta-message, and retry budget.
///
/// The bug class this guards: pre-policy, the orchestrator passively
/// accepted whatever the agent reported. A 4-second exit after a recovery
/// continue with a user follow-up was indistinguishable from a real
/// completion. Tests below pin the deterministic re-issue and the
/// heuristic-warning paths so a future refactor can't quietly drop them.
/// </summary>
public class RunOutcomePolicyTests
{
    private static RunPlan RecoveryPlan(string state = TaskStates.Progress) =>
        new(
            PromptTemplate: RuntimePromptService.RunnerRecoveryContinuation,
            PromptVariables: new Dictionary<string, string?> { ["user_followup"] = "do the work please" },
            PromptOverride: null,
            SessionToResume: null,
            ResumeFlag: false,
            EventKind: "recovery",
            EventReason: "no session recorded",
            EventInputSessionId: null,
            MoveJobToProgress: state != TaskStates.Progress,
            MarkSessionChainRecovery: true,
            WriteCutMarker: true,
            CutMarkerReason: "session lost",
            PersistSessionName: null,
            ClearStaleSessionName: false);

    private static RunPlan ContinuePlan() =>
        new(
            PromptTemplate: null,
            PromptVariables: new Dictionary<string, string?>(),
            PromptOverride: "follow-up",
            SessionToResume: "abc",
            ResumeFlag: true,
            EventKind: "continue",
            EventReason: null,
            EventInputSessionId: "abc",
            MoveJobToProgress: false,
            MarkSessionChainRecovery: false,
            WriteCutMarker: false,
            CutMarkerReason: null,
            PersistSessionName: null,
            ClearStaleSessionName: false);

    private static AgentOutcome Outcome(
        AgentOutcomeKind kind,
        bool sentinel = false,
        double duration = 30.0,
        int agentChars = 200) =>
        new(kind, "summary", sentinel, sentinel ? "DONE" : null, sentinel ? null : "heuristic", agentChars, agentChars / 5, duration);

    /// <summary>
    /// Graceful Recovery (ADR-0006): even when the prior session is
    /// unrecoverable, Recovery + follow-up + no-output gets ONE re-issue
    /// with a sharper, recovery-aware framing. The user explicitly asked
    /// for "selbst wenn die Session nicht fortgesetzt werden kann, soll
    /// es weitergehen", so dropping the follow-up is not acceptable.
    /// </summary>
    [Fact]
    public void Recovery_NoOpWithFollowup_GracefullyReissuesOnce()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            RecoveryPlan(),
            Outcome(AgentOutcomeKind.NoOp, duration: 4.6, agentChars: 0),
            followupPrompt: "Please process the task again",
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, action.Kind);
        Assert.Equal(1, action.RetryAttempt);
        Assert.Equal("Please process the task again", action.FollowupRetryPrompt);
        Assert.Contains("Recovery from session loss", action.MetaMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One auto-re-issue, then stop. The retry budget is shared across
    /// Resume-continue and Recovery so a stuck loop cannot burn quota.
    /// </summary>
    [Fact]
    public void Recovery_NoOpAfterReissue_GivesUp()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            RecoveryPlan(),
            Outcome(AgentOutcomeKind.NoOp, duration: 4.6, agentChars: 0),
            followupPrompt: "do it",
            reissueAttempt: RunOutcomePolicy.MaxAutoReissueAttempts);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
    }

    /// <summary>
    /// The recovery-aware re-issue prompt must explicitly tell the agent
    /// the session is unrecoverable and that the user request is the only
    /// context. Without this, the agent's "I'll wait for your next request"
    /// reply (the symptom that triggered the redesign) repeats.
    /// </summary>
    [Fact]
    public void RecoveryAwarePrompt_TellsAgentHistoryIsGone()
    {
        var prompt = RunOutcomePolicy.BuildReissueFollowupPrompt("look at the screenshot and improve the layout", recoveryContext: true);
        Assert.Contains("unrecoverable", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("standing by", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("look at the screenshot and improve the layout", prompt);
        Assert.Contains("[[TASK_BLOCKED", prompt);
    }

    /// <summary>
    /// ASS-734: every steering prompt must lead with the "steer the diff, do not
    /// restart" rule so a reissue/continuation builds on the existing commits
    /// instead of redoing the task from scratch. Pin it across all four builders
    /// so a future edit cannot quietly drop the rule from one of them.
    /// </summary>
    [Fact]
    public void EverySteeringPrompt_LeadsWithDiffOnlyRule()
    {
        var reissue = RunOutcomePolicy.BuildReissueFollowupPrompt("do the thing");
        var missing = RunOutcomePolicy.BuildMissingSentinelInterventionPrompt("prior summary");
        var codex = RunOutcomePolicy.BuildCodexContinuationPrompt(
            Outcome(AgentOutcomeKind.Done, sentinel: false));

        Assert.StartsWith(RunOutcomePolicy.DiffOnlySteeringRule, reissue);
        Assert.StartsWith(RunOutcomePolicy.DiffOnlySteeringRule, missing);
        Assert.StartsWith(RunOutcomePolicy.DiffOnlySteeringRule, codex);
        foreach (var prompt in new[] { reissue, missing, codex })
        {
            Assert.Contains("Build on the commits already made", prompt);
            Assert.Contains("do NOT redo the work from scratch", prompt);
        }
    }

    /// <summary>
    /// When the orchestrator knows the commits already made for the task, the
    /// builders list them so the agent does not re-apply committed work.
    /// </summary>
    [Fact]
    public void SteeringPrompt_ListsPriorCommits_WhenSupplied()
    {
        var commits = new[] { "a1b2c3 feat: add lane move", "d4e5f6 test: cover backfill" };

        var reissue = RunOutcomePolicy.BuildReissueFollowupPrompt("do it", priorCommits: commits);
        var missing = RunOutcomePolicy.BuildMissingSentinelInterventionPrompt("sum", priorCommits: commits);
        var codex = RunOutcomePolicy.BuildCodexContinuationPrompt(
            Outcome(AgentOutcomeKind.Done, sentinel: false),
            priorCommits: commits);

        foreach (var prompt in new[] { reissue, missing, codex })
        {
            Assert.Contains("Commits already made for this task", prompt);
            Assert.Contains("a1b2c3 feat: add lane move", prompt);
            Assert.Contains("d4e5f6 test: cover backfill", prompt);
        }

        // No commit block leaks when none are supplied.
        var noCommits = RunOutcomePolicy.BuildReissueFollowupPrompt("do it");
        Assert.DoesNotContain("Commits already made for this task", noCommits);
    }

    [Theory]
    [InlineData(RunIssueKind.MissingTerminalSentinel)]
    [InlineData(RunIssueKind.ClassifierUnknown)]
    public void SoftIntervention_DecidePrompt_ListsPriorCommits(RunIssueKind issueKind)
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown) with { IssueKind = issueKind },
            followupPrompt: null,
            reissueAttempt: 0,
            priorCommits: new[] { "abc1234 feat: keep existing work" });

        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, action.Kind);
        Assert.Contains("Commits already made for this task", action.FollowupRetryPrompt);
        Assert.Contains("abc1234 feat: keep existing work", action.FollowupRetryPrompt);
    }

    /// <summary>
    /// Resume-continue with follow-up + fast no-output IS the case where
    /// auto-reissue still fires: the session was alive, the agent had real
    /// context, and it still ignored the follow-up. One retry with sharper
    /// framing, then stop.
    /// </summary>
    [Fact]
    public void ResumeContinue_NoOpWithFollowup_Reissues()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.NoOp, duration: 4.6, agentChars: 0),
            followupPrompt: "Please tighten the spacing",
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, action.Kind);
        Assert.Equal(1, action.RetryAttempt);
        Assert.Equal("Please tighten the spacing", action.FollowupRetryPrompt);
    }

    /// <summary>
    /// Same shape as above but we already re-issued once: orchestrator stops
    /// and asks the user to step in.
    /// </summary>
    [Fact]
    public void ResumeContinue_NoOpAfterReissue_GivesUp()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.NoOp, duration: 4.6, agentChars: 0),
            followupPrompt: "do it",
            reissueAttempt: RunOutcomePolicy.MaxAutoReissueAttempts);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
    }

    /// <summary>
    /// Sentinel-backed Done with a real run duration is accepted silently.
    /// This is the "happy path" the meta channel must stay quiet on.
    /// </summary>
    [Fact]
    public void RealRun_SentinelDone_AcceptedSilently()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Done, sentinel: true, duration: 90.0, agentChars: 1500),
            followupPrompt: "tighten the code",
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.Accept, action.Kind);
        Assert.False(action.IsHeuristicFallback);
        Assert.Equal(string.Empty, action.MetaMessage);
    }

    /// <summary>
    /// No sentinel, heuristic concluded "done" - we accept but the meta
    /// channel must surface the fact that the deterministic contract did
    /// not match. This is the "show the user when we are guessing" promise.
    /// </summary>
    [Fact]
    public void RealRun_HeuristicDone_AcceptedWithWarning()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.ManualStart,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Done, sentinel: false, duration: 60.0, agentChars: 1500),
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.NotifyUserAndAccept, action.Kind);
        Assert.True(action.IsHeuristicFallback);
        Assert.Contains("heuristic", action.MetaMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Continue without follow-up + agent says NoOp: nothing for the policy
    /// to do (no follow-up to re-issue). Accept with a heuristic warning.
    /// </summary>
    [Fact]
    public void Continue_NoFollowup_NoOp_AcceptsWithWarning()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.NoOp, duration: 4.6, agentChars: 0),
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.NotEqual(OutcomeActionKind.ReissueWithStrongerFraming, action.Kind);
    }

    /// <summary>
    /// The agent's question is already visible in the chat; an extra
    /// "[heuristic] verdict: needsinput" line is just noise. Policy should
    /// stay silent on NeedsInput so the user can respond directly to the
    /// agent or use the quick-reply chips.
    /// </summary>
    [Fact]
    public void RealRun_HeuristicNeedsInput_AcceptedSilently()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.NeedsInput, sentinel: false, duration: 30.0, agentChars: 200),
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.Accept, action.Kind);
        Assert.Equal(string.Empty, action.MetaMessage);
    }

    /// <summary>
    /// Codex silent-completion: the runtime detector tripped, the analyzer
    /// surfaced <see cref="RunIssueKind.SilentCompletion"/>. Policy must
    /// route through <see cref="OutcomeActionKind.NotifyUserAndAccept"/> so
    /// the regular post-completion path runs (auto-review move + aspect
    /// calls) while the chat surface clearly distinguishes the case from a
    /// real sentinel-backed Done.
    /// </summary>
    [Fact]
    public void SilentCompletion_NotifiesUserAndAccepts_RoutesThroughAutoReview()
    {
        var outcome = Outcome(AgentOutcomeKind.Done, sentinel: false, duration: 320.0, agentChars: 1500)
            with { IssueKind = RunIssueKind.SilentCompletion, Summary = "Codex stopped after final tool call without a closing sentinel (silence=92s)." };
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            outcome,
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.NotifyUserAndAccept, action.Kind);
        Assert.Equal(RunIssueKind.SilentCompletion, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.SilentCompletion, action.MessageKind);
        Assert.False(action.IsHeuristicFallback);
        Assert.Contains("sentinel", action.MetaMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Progress mid-run shape: the agent kept moving, no need for a meta
    /// message. The activity log itself shows the run is alive.
    /// </summary>
    [Fact]
    public void RealRun_HeuristicProgress_AcceptedSilently()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.ManualStart,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Progress, sentinel: false, duration: 45.0, agentChars: 800),
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.Accept, action.Kind);
    }

    /// <summary>
    /// A bare Unknown outcome with text but no typed issue kind (the residual
    /// after the analyzer's typed routing) must accept silently. classifier-
    /// unknown is never a terminal, user-visible FAILURE, so the policy does
    /// not pile a "could not classify" dead-end onto an already-visible run.
    /// The concrete <see cref="RunIssueKind.ClassifierUnknown"/> path (failed
    /// run with real text) is covered by the re-issue tests below.
    /// </summary>
    [Fact]
    public void BareUnknownWithText_AcceptedSilently()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 30.0, agentChars: 250),
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.Accept, action.Kind);
        Assert.Equal(string.Empty, action.MetaMessage);
    }

    /// <summary>
    /// Case (a): a CLI launch / resume failure (codex rejected the resume
    /// target, exit 2, ~0s) is routed to Recovery, never a terminal
    /// classifier-unknown FAILURE. The policy accepts the run quietly with a
    /// typed CliLaunchFailed marker so the runner's existing recovery machinery
    /// rebuilds from disk on the next pickup. This is the core ASS-755 fix.
    /// </summary>
    [Fact]
    public void CliLaunchFailed_RoutesToRecovery_NotTerminalFailure()
    {
        var diagnosis = "The agent CLI rejected the resume target; rebuilding from disk on the next attempt.";
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 0.0, agentChars: 10) with
            {
                IssueKind = RunIssueKind.CliLaunchFailed,
                Summary = diagnosis
            },
            followupPrompt: "please continue",
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.NotifyUserAndAccept, action.Kind);
        Assert.NotEqual(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.Equal(RunIssueKind.CliLaunchFailed, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.CliLaunchFailed, action.MessageKind);
        Assert.False(action.IsHeuristicFallback);
        Assert.Equal(diagnosis, action.MetaMessage);
        // Must not masquerade as the old terminal classifier-unknown verdict.
        Assert.NotEqual(OrchestratorMessageKind.ClassifierUnknown, action.MessageKind);
    }

    /// <summary>
    /// Case (b): a failed run with a real (but unclassifiable) agent turn.
    /// classifier-unknown is never terminal: the orchestrator re-issues once
    /// with a structured close-out prompt, exactly like
    /// missing-terminal-sentinel, rather than ending on "could not classify".
    /// </summary>
    [Fact]
    public void ClassifierUnknown_FirstOccurrence_ReissuesOnce()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 90.0, agentChars: 500) with
            {
                IssueKind = RunIssueKind.ClassifierUnknown,
                Summary = "Agent text did not match any known shape."
            },
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, action.Kind);
        Assert.Equal(1, action.RetryAttempt);
        Assert.True(action.IsPreframedRetryPrompt);
        Assert.Equal(RunIssueKind.ClassifierUnknown, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.SoftIntervention, action.MessageKind);
        Assert.Contains("[[TASK_DONE]]", action.FollowupRetryPrompt);
    }

    /// <summary>
    /// Case (b) after the one intervention is spent: accept with a visible
    /// classifier-unknown marker so the lane moves forward for review. Still
    /// an Accept (not a NotifyUserAndStop) - never a terminal FAILURE.
    /// </summary>
    [Fact]
    public void ClassifierUnknown_AfterIntervention_AcceptsWithVisibleMarker_NotTerminal()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 90.0, agentChars: 500) with
            {
                IssueKind = RunIssueKind.ClassifierUnknown,
                Summary = "Agent text did not match any known shape."
            },
            followupPrompt: null,
            reissueAttempt: RunOutcomePolicy.MaxSoftInterventionAttempts);

        Assert.Equal(OutcomeActionKind.NotifyUserAndAccept, action.Kind);
        Assert.NotEqual(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.Equal(RunIssueKind.ClassifierUnknown, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.ClassifierUnknown, action.MessageKind);
    }

    /// <summary>
    /// The load-bearing invariant of this whole path: classifier-unknown is
    /// NEVER a terminal, user-facing FAILURE. Sweep the entire reissue budget -
    /// before it, exactly at the cap, and well past it - and assert the policy
    /// never returns <see cref="OutcomeActionKind.NotifyUserAndStop"/>. Within
    /// budget it spends one soft intervention; once the budget is gone it
    /// accepts with a visible classifier-unknown marker so the lane keeps
    /// moving. A future refactor that "tidies" the branch into a stop/escalate
    /// would trip this.
    /// </summary>
    [Theory]
    [InlineData(0, OutcomeActionKind.ReissueWithStrongerFraming, OrchestratorMessageKind.SoftIntervention)]
    [InlineData(1, OutcomeActionKind.NotifyUserAndAccept, OrchestratorMessageKind.ClassifierUnknown)]
    [InlineData(2, OutcomeActionKind.NotifyUserAndAccept, OrchestratorMessageKind.ClassifierUnknown)]
    [InlineData(5, OutcomeActionKind.NotifyUserAndAccept, OrchestratorMessageKind.ClassifierUnknown)]
    public void ClassifierUnknown_NeverStops_AcrossEntireReissueBudget(
        int reissueAttempt,
        OutcomeActionKind expectedKind,
        OrchestratorMessageKind expectedMessageKind)
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 90.0, agentChars: 500) with
            {
                IssueKind = RunIssueKind.ClassifierUnknown,
                Summary = "Agent text did not match any known shape."
            },
            followupPrompt: null,
            reissueAttempt: reissueAttempt);

        Assert.Equal(expectedKind, action.Kind);
        Assert.Equal(expectedMessageKind, action.MessageKind);
        Assert.Equal(RunIssueKind.ClassifierUnknown, action.IssueKind);
        // The invariant, stated directly: never a terminal stop / human-review FAILURE.
        Assert.NotEqual(OutcomeActionKind.NotifyUserAndStop, action.Kind);
    }

    /// <summary>
    /// classifier-unknown must not be confused with the two structural
    /// neighbours that share its surface shape (Unknown kind, no sentinel, real
    /// agent text): a CLI launch/resume failure routes straight to Recovery
    /// (NotifyUserAndAccept + CliLaunchFailed marker, NO reissue), while
    /// classifier-unknown and missing-terminal-sentinel each spend one soft
    /// intervention but stay tagged distinctly. Feed the identical outcome with
    /// each issue kind and assert three separate routings - none terminal.
    /// </summary>
    [Fact]
    public void ClassifierUnknown_NotConfusedWith_CliLaunchFailed_Or_MissingTerminalSentinel()
    {
        AgentOutcome Shape(RunIssueKind issue) =>
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 60.0, agentChars: 300)
                with { IssueKind = issue, Summary = "same surface text" };

        var classifierUnknown = RunOutcomePolicy.Decide(
            RunIntent.UserContinue, ContinuePlan(), Shape(RunIssueKind.ClassifierUnknown), null, 0);
        var cliLaunchFailed = RunOutcomePolicy.Decide(
            RunIntent.UserContinue, ContinuePlan(), Shape(RunIssueKind.CliLaunchFailed), null, 0);
        var missingSentinel = RunOutcomePolicy.Decide(
            RunIntent.UserContinue, ContinuePlan(), Shape(RunIssueKind.MissingTerminalSentinel), null, 0);

        // CLI launch failure -> Recovery accept; NOT a soft reissue, NOT classifier-unknown.
        Assert.Equal(OutcomeActionKind.NotifyUserAndAccept, cliLaunchFailed.Kind);
        Assert.Equal(OrchestratorMessageKind.CliLaunchFailed, cliLaunchFailed.MessageKind);

        // classifier-unknown -> one soft intervention, tagged as itself.
        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, classifierUnknown.Kind);
        Assert.Equal(RunIssueKind.ClassifierUnknown, classifierUnknown.IssueKind);

        // missing-terminal-sentinel -> also a soft intervention, but tagged distinctly.
        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, missingSentinel.Kind);
        Assert.Equal(RunIssueKind.MissingTerminalSentinel, missingSentinel.IssueKind);

        // The three issue kinds stay separate, and none collapses to a terminal stop.
        Assert.NotEqual(classifierUnknown.IssueKind, cliLaunchFailed.IssueKind);
        Assert.NotEqual(classifierUnknown.IssueKind, missingSentinel.IssueKind);
        foreach (var a in new[] { classifierUnknown, cliLaunchFailed, missingSentinel })
            Assert.NotEqual(OutcomeActionKind.NotifyUserAndStop, a.Kind);
    }

    /// <summary>
    /// The classifier-unknown soft intervention must ask the agent for a
    /// structured close-out: both terminal sentinels offered, and led by the
    /// shared diff-only steering rule so the reissue builds on existing commits
    /// instead of restarting from scratch. Pins the prompt contract for this
    /// branch so it cannot silently drift from the missing-sentinel framing.
    /// </summary>
    [Fact]
    public void ClassifierUnknown_ReissuePrompt_AsksForStructuredCloseOut()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 90.0, agentChars: 500) with
            {
                IssueKind = RunIssueKind.ClassifierUnknown,
                Summary = "Agent text did not match any known shape."
            },
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.True(action.IsPreframedRetryPrompt);
        Assert.StartsWith(RunOutcomePolicy.DiffOnlySteeringRule, action.FollowupRetryPrompt);
        Assert.Contains("[[TASK_DONE]]", action.FollowupRetryPrompt);
        Assert.Contains("[[TASK_BLOCKED", action.FollowupRetryPrompt);
    }

    /// <summary>
    /// Run produced no agent text (e.g. claude rejected the resume target
    /// and exited with error_during_execution). The user already sees the
    /// system-error block in the chat and a [capture-fail] decision
    /// message right next to it - the frontend's protocol-pane banner also
    /// surfaces the failed-run state explicitly. Adding "[heuristic] Could
    /// not classify the agent's reply." on top is just noise. Policy
    /// stays silent so the meta channel does not pile redundant warnings
    /// on a single failed turn.
    /// </summary>
    [Fact]
    public void FailedRun_UnknownWithoutText_AcceptedSilently()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 2.0, agentChars: 0),
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.Accept, action.Kind);
        Assert.False(action.IsHeuristicFallback);
        Assert.Equal(string.Empty, action.MetaMessage);
    }

    [Fact]
    public void EnvironmentBlocker_RoutesStraightToHumanReview_NoSoftIntervention()
    {
        // EnvironmentBlocker is unrecoverable by the agent: the OS / sandbox
        // refused the work. Policy must NotifyUserAndStop on the first
        // occurrence and tag the message kind so the chat log + the
        // ProjectRunner human-review router pick it up.
        var diagnosis = "Codex Windows sandbox refused to execute commands (cli=codex): set sandbox_mode = \"danger-full-access\".";
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 3.2, agentChars: 0) with
            {
                IssueKind = RunIssueKind.EnvironmentBlocker,
                Summary = diagnosis
            },
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.Equal(RunIssueKind.EnvironmentBlocker, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.EnvironmentBlocker, action.MessageKind);
        Assert.Equal(diagnosis, action.MetaMessage);
        Assert.Null(action.FollowupRetryPrompt);
    }

    [Fact]
    public void PermissionBlocked_FirstOccurrence_ReissuesOneSoftIntervention()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 65.0, agentChars: 500) with
            {
                IssueKind = RunIssueKind.PermissionBlocked,
                Summary = "Tool permission failure prevented the agent from inspecting the workspace."
            },
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, action.Kind);
        Assert.Equal(1, action.RetryAttempt);
        Assert.True(action.IsPreframedRetryPrompt);
        Assert.Equal(RunIssueKind.PermissionBlocked, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.SoftIntervention, action.MessageKind);
        Assert.Contains("available permissions", action.FollowupRetryPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PermissionBlocked_AfterOneIntervention_StopsWithConcreteCategory()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 65.0, agentChars: 500) with
            {
                IssueKind = RunIssueKind.PermissionBlocked,
                Summary = "Tool permission failure prevented the agent from inspecting the workspace."
            },
            followupPrompt: null,
            reissueAttempt: RunOutcomePolicy.MaxSoftInterventionAttempts);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.False(action.IsHeuristicFallback);
        Assert.Equal(RunIssueKind.PermissionBlocked, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.PermissionBlocked, action.MessageKind);
        Assert.Contains("permission", action.MetaMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingTerminalSentinel_FirstOccurrence_AsksForSentinelOnce()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Done, sentinel: false, duration: 35.0, agentChars: 900) with
            {
                IssueKind = RunIssueKind.MissingTerminalSentinel,
                Summary = "Agent text suggests the task is done (heuristic)."
            },
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, action.Kind);
        Assert.True(action.IsPreframedRetryPrompt);
        Assert.Equal(RunIssueKind.MissingTerminalSentinel, action.IssueKind);
        Assert.Contains("[[TASK_DONE]]", action.FollowupRetryPrompt);
    }

    [Fact]
    public void WatchdogTimeout_IsConcreteGiveupNotHeuristicFallback()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 60.0, agentChars: 0) with
            {
                IssueKind = RunIssueKind.WatchdogTimeout,
                Summary = "Run was killed by the watchdog after silence."
            },
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.False(action.IsHeuristicFallback);
        Assert.Equal(OrchestratorMessageKind.WatchdogTimeout, action.MessageKind);
    }

    private static CodexCompletionEvidence.Inputs CodexEvidence(
        bool hasCommits = true,
        string? resultToken = "Success",
        int openFindings = 0,
        bool timedOut = false,
        int continuationsUsed = 0)
        => new(
            IsCodex: true,
            HasCommits: hasCommits,
            StatusResultToken: resultToken,
            OpenFindingsCount: openFindings,
            TimedOutMidTask: timedOut,
            ContinuationAttemptsUsed: continuationsUsed);

    /// <summary>
    /// The Codex silent-finish main shape (MissingTerminalSentinel) with real
    /// commits + a clean self-reported status is accepted directly - no reissue
    /// churn - when evidence is supplied. This is the core accept-rate fix.
    /// </summary>
    [Fact]
    public void Codex_MissingSentinel_CleanEvidence_AcceptsWithoutReissue()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Done) with { IssueKind = RunIssueKind.MissingTerminalSentinel },
            followupPrompt: null,
            reissueAttempt: 0,
            codexEvidence: CodexEvidence(hasCommits: true, resultToken: "Success", openFindings: 0));

        Assert.Equal(OutcomeActionKind.NotifyUserAndAccept, action.Kind);
        Assert.Equal(RunIssueKind.SilentCompletion, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.SilentCompletion, action.MessageKind);
    }

    /// <summary>
    /// A Codex silent finish with open items is driven to completion via a
    /// bounded continuation (codex exec resume) instead of being reissued/
    /// escalated. The retry attempt advances so the loop is bounded.
    /// </summary>
    [Fact]
    public void Codex_MissingSentinel_OpenItems_ContinuesBounded()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Done) with { IssueKind = RunIssueKind.MissingTerminalSentinel },
            followupPrompt: null,
            reissueAttempt: 0,
            codexEvidence: CodexEvidence(openFindings: 2));

        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, action.Kind);
        Assert.Equal(1, action.RetryAttempt);
        Assert.True(action.IsPreframedRetryPrompt);
        Assert.Contains("[[TASK_DONE]]", action.FollowupRetryPrompt);
    }

    /// <summary>
    /// Once the continuation budget is exhausted the evidence path goes
    /// Inconclusive and the run falls through to the existing
    /// MissingTerminalSentinel routing (accept with a visible marker after the
    /// soft-intervention budget is spent), so the loop converges.
    /// </summary>
    [Fact]
    public void Codex_MissingSentinel_OpenItems_BudgetExhausted_FallsThrough()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Done) with { IssueKind = RunIssueKind.MissingTerminalSentinel },
            followupPrompt: null,
            reissueAttempt: RunOutcomePolicy.MaxSoftInterventionAttempts,
            codexEvidence: CodexEvidence(
                openFindings: 2,
                continuationsUsed: CodexCompletionEvidence.DefaultContinuationBudget));

        Assert.Equal(OutcomeActionKind.NotifyUserAndAccept, action.Kind);
        Assert.Equal(RunIssueKind.MissingTerminalSentinel, action.IssueKind);
    }

    /// <summary>
    /// Without evidence (e.g. a Claude run) the MissingTerminalSentinel path is
    /// unchanged: one soft reissue asking for a structured close-out.
    /// </summary>
    [Fact]
    public void NonCodex_MissingSentinel_KeepsExistingSoftReissue()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Done) with { IssueKind = RunIssueKind.MissingTerminalSentinel },
            followupPrompt: null,
            reissueAttempt: 0,
            codexEvidence: null);

        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, action.Kind);
        Assert.Equal(RunIssueKind.MissingTerminalSentinel, action.IssueKind);
    }

    /// <summary>
    /// A Codex SilentCompletion with open items also runs the bounded
    /// continuation rather than the plain accept it would otherwise get.
    /// </summary>
    [Fact]
    public void Codex_SilentCompletion_OpenItems_Continues()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Done) with { IssueKind = RunIssueKind.SilentCompletion },
            followupPrompt: null,
            reissueAttempt: 0,
            codexEvidence: CodexEvidence(openFindings: 1));

        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, action.Kind);
        Assert.Contains("[[TASK_DONE]]", action.FollowupRetryPrompt);
    }

    /// <summary>
    /// A context-overflow run is NON-RETRYABLE: re-issuing resends the same or
    /// a larger context and overflows identically. This is the exact failure
    /// that looped forever ("Prompt too long" re-issued without limit), so the
    /// policy must stop and route to human review on first detection - never
    /// re-issue.
    /// </summary>
    [Fact]
    public void ContextOverflow_StopsAndRoutesToHumanReview()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown) with { IssueKind = RunIssueKind.ContextOverflow },
            followupPrompt: "please continue",
            reissueAttempt: 0,
            codexEvidence: null);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.Equal(RunIssueKind.ContextOverflow, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.ContextOverflow, action.MessageKind);
        Assert.False(action.IsHeuristicFallback);
    }
}
