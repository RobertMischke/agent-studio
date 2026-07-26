

using Xunit;

namespace AgentStudio.Tests;

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

    [Fact]
    public void AgentGitViolation_StopsOnlyForVerifiedGitDamage()
    {
        var outcome = Outcome(AgentOutcomeKind.Done, sentinel: true) with
        {
            IssueKind = RunIssueKind.AgentGitViolation,
            Summary = "[agent-git-violation] Genuine git damage detected: worker push changed a protected remote branch."
        };

        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            outcome,
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.Equal(RunIssueKind.AgentGitViolation, action.IssueKind);
        Assert.Contains("Genuine git damage", action.MetaMessage);
        Assert.Contains("protected remote branch", action.MetaMessage);
    }

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
    /// after the analyzer's typed routing, i.e. NOT a failed run) must accept
    /// silently: the policy does not pile a "could not classify" dead-end onto
    /// an already-visible run. The concrete failed-run-with-real-text path
    /// (<see cref="RunIssueKind.OrchestratorInconclusive"/> /
    /// <see cref="RunIssueKind.InfraCrash"/>) is covered by the stop tests below.
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
    /// target, exit 2, ~0s) must never be a terminal inconclusive FAILURE. The
    /// category's copy promises "rebuilding from disk on the next attempt", so
    /// the policy MAKES that attempt: one automatic fresh-start retry before it
    /// escalates (AGT-1944; belege AGT-1945/1929/1930). This is the ASS-755 fix
    /// carried forward from "accept quietly" to "actually retry".
    /// </summary>
    [Fact]
    public void CliLaunchFailed_FirstDetection_FreshStartRetries_NotTerminalFailure()
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

        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, action.Kind);
        Assert.Equal(RunIssueKind.CliLaunchFailed, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.CliLaunchFailed, action.MessageKind);
        Assert.Equal(1, action.RetryAttempt);
        Assert.True(action.IsPreframedRetryPrompt);
        Assert.False(string.IsNullOrWhiteSpace(action.FollowupRetryPrompt));
        Assert.Equal(TimeSpan.Zero, action.RetryBackoff); // a dead session should retry promptly
        // Must not masquerade as an inconclusive / infra-crash verdict.
        Assert.NotEqual(OrchestratorMessageKind.OrchestratorInconclusive, action.MessageKind);
        Assert.NotEqual(OrchestratorMessageKind.InfraCrash, action.MessageKind);
    }

    /// <summary>
    /// After the one automatic fresh-start retry, a CLI launch/resume failure that
    /// still fails routes to human review with the cli-launch-failed category -
    /// no endless recovery loop (AGT-1944).
    /// </summary>
    [Fact]
    public void CliLaunchFailed_AfterFreshStartRetry_RoutesToHumanReview()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 0.0, agentChars: 10) with
            {
                IssueKind = RunIssueKind.CliLaunchFailed,
                Summary = "still cannot launch"
            },
            followupPrompt: "please continue",
            reissueAttempt: PostProcessingOutcomeTaxonomy.MaxCliLaunchRetries);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.Equal(RunIssueKind.CliLaunchFailed, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.CliLaunchFailed, action.MessageKind);
    }

    /// <summary>
    /// A transient host file lock / network glitch retries with backoff before
    /// escalating: the code was not the problem, and the fault clears on its own
    /// (AGT-1944 environmental retry-with-backoff).
    /// </summary>
    [Fact]
    public void EnvironmentalTransient_FirstDetection_RetriesWithBackoff()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 42.0, agentChars: 120) with
            {
                IssueKind = RunIssueKind.EnvironmentalTransient,
                Summary = "The run failed on a host file lock (MSB302x / file-in-use)."
            },
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, action.Kind);
        Assert.Equal(RunIssueKind.EnvironmentalTransient, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.EnvironmentalRetry, action.MessageKind);
        Assert.Equal(1, action.RetryAttempt);
        Assert.True(action.IsPreframedRetryPrompt);
        Assert.Equal(TimeSpan.FromSeconds(30), action.RetryBackoff);
    }

    /// <summary>
    /// Once the bounded retry budget is spent, a persistent transient fault stops
    /// and routes to human review flagged environmental (AGT-1944).
    /// </summary>
    [Fact]
    public void EnvironmentalTransient_BudgetSpent_RoutesToHumanReview()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 42.0, agentChars: 120) with
            {
                IssueKind = RunIssueKind.EnvironmentalTransient,
                Summary = "still locked"
            },
            followupPrompt: null,
            reissueAttempt: PostProcessingOutcomeTaxonomy.DefaultMaxEnvironmentalRetries);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.Equal(RunIssueKind.EnvironmentalTransient, action.IssueKind);
    }

    [Fact]
    public void EmptyFastExit_StopsAsVisibleFailedStart_NotNoOp()
    {
        var diagnosis = "The agent CLI exited almost immediately without producing an agent turn; treating this as a failed start, not as [[TASK_NOOP]]. status=completed; exitCode=0; duration=1.2s; outputLines=0.";
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 1.2, agentChars: 0) with
            {
                IssueKind = RunIssueKind.EmptyFastExit,
                Summary = diagnosis
            },
            followupPrompt: "please continue",
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.Equal(RunIssueKind.EmptyFastExit, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.EmptyFastExit, action.MessageKind);
        Assert.Equal(diagnosis, action.MetaMessage);
        Assert.False(action.IsHeuristicFallback);
    }

    /// <summary>
    /// Case (b): a failed run with a real agent turn that maps to no terminal
    /// verdict. Under drive-to-conclusion this is OrchestratorInconclusive and
    /// it STOPS: NotifyUserAndStop, carrying the typed issue so the escalation
    /// layer can route it to human review. It is never silently accepted - the
    /// old classifier-unknown path returned NotifyUserAndAccept with a marker
    /// that moved nothing and stranded the task in 3-progress.
    /// </summary>
    [Fact]
    public void OrchestratorInconclusive_Stops_HandsToUser()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 90.0, agentChars: 500) with
            {
                IssueKind = RunIssueKind.OrchestratorInconclusive,
                Summary = "Agent text did not match any known shape."
            },
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.NotEqual(OutcomeActionKind.NotifyUserAndAccept, action.Kind);
        Assert.Equal(RunIssueKind.OrchestratorInconclusive, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.OrchestratorInconclusive, action.MessageKind);
        Assert.False(action.IsHeuristicFallback);
        Assert.Contains("could not be mapped to a terminal verdict", action.MetaMessage);
    }

    /// <summary>
    /// Case (b'): a failed run whose CLI process died hard (exitCode &lt; 0)
    /// before reaching a terminal verdict. This is InfraCrash and it likewise
    /// STOPS with NotifyUserAndStop, but carries the InfraCrash marker so the
    /// escalation layer can distinguish infrastructure death from an
    /// inconclusive agent reply.
    /// </summary>
    [Fact]
    public void InfraCrash_Stops_HandsToUser()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 90.0, agentChars: 500) with
            {
                IssueKind = RunIssueKind.InfraCrash,
                Summary = "The agent CLI process died before producing a verdict."
            },
            followupPrompt: null,
            reissueAttempt: 0);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.NotEqual(OutcomeActionKind.NotifyUserAndAccept, action.Kind);
        Assert.Equal(RunIssueKind.InfraCrash, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.InfraCrash, action.MessageKind);
        Assert.False(action.IsHeuristicFallback);
        Assert.Contains("crashed", action.MetaMessage);
    }

    /// <summary>
    /// The load-bearing invariant of the drive-to-conclusion path: a failed,
    /// unclassified run NEVER returns NotifyUserAndAccept (the old stranding
    /// bug). Sweep the entire reissue budget - before, at the cap, and well
    /// past it - for both new kinds and assert the policy always STOPS and
    /// hands the task to the user, carrying the typed issue. A future refactor
    /// that "tidies" the branch back into an accept-with-marker would trip this.
    /// </summary>
    [Theory]
    [InlineData(RunIssueKind.OrchestratorInconclusive, 0)]
    [InlineData(RunIssueKind.OrchestratorInconclusive, 1)]
    [InlineData(RunIssueKind.OrchestratorInconclusive, 5)]
    [InlineData(RunIssueKind.InfraCrash, 0)]
    [InlineData(RunIssueKind.InfraCrash, 1)]
    [InlineData(RunIssueKind.InfraCrash, 5)]
    public void FailedUnclassified_AlwaysStops_AcrossEntireReissueBudget(
        RunIssueKind issueKind,
        int reissueAttempt)
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.UserContinue,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 90.0, agentChars: 500) with
            {
                IssueKind = issueKind,
                Summary = "Agent text did not match any known shape."
            },
            followupPrompt: null,
            reissueAttempt: reissueAttempt);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.Equal(issueKind, action.IssueKind);
        // The invariant, stated directly: never silently accept a failed run.
        Assert.NotEqual(OutcomeActionKind.NotifyUserAndAccept, action.Kind);
    }

    /// <summary>
    /// The two new failed-run kinds must not be confused with the structural
    /// neighbours that share their surface shape (Unknown kind, no sentinel,
    /// real agent text): a CLI launch/resume failure takes a fresh-start retry
    /// (ReissueWithStrongerFraming + CliLaunchFailed marker), while
    /// missing-terminal-sentinel spends one soft intervention. The two
    /// drive-to-conclusion kinds STOP. Feed the identical outcome with each
    /// issue kind and assert four separate routings.
    /// </summary>
    [Fact]
    public void FailedUnclassified_NotConfusedWith_CliLaunchFailed_Or_MissingTerminalSentinel()
    {
        AgentOutcome Shape(RunIssueKind issue) =>
            Outcome(AgentOutcomeKind.Unknown, sentinel: false, duration: 60.0, agentChars: 300)
                with { IssueKind = issue, Summary = "same surface text" };

        var inconclusive = RunOutcomePolicy.Decide(
            RunIntent.UserContinue, ContinuePlan(), Shape(RunIssueKind.OrchestratorInconclusive), null, 0);
        var infraCrash = RunOutcomePolicy.Decide(
            RunIntent.UserContinue, ContinuePlan(), Shape(RunIssueKind.InfraCrash), null, 0);
        var cliLaunchFailed = RunOutcomePolicy.Decide(
            RunIntent.UserContinue, ContinuePlan(), Shape(RunIssueKind.CliLaunchFailed), null, 0);
        var missingSentinel = RunOutcomePolicy.Decide(
            RunIntent.UserContinue, ContinuePlan(), Shape(RunIssueKind.MissingTerminalSentinel), null, 0);

        // CLI launch failure -> one automatic fresh-start retry; NOT a terminal stop.
        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, cliLaunchFailed.Kind);
        Assert.Equal(OrchestratorMessageKind.CliLaunchFailed, cliLaunchFailed.MessageKind);

        // missing-terminal-sentinel -> one soft intervention, tagged distinctly.
        Assert.Equal(OutcomeActionKind.ReissueWithStrongerFraming, missingSentinel.Kind);
        Assert.Equal(RunIssueKind.MissingTerminalSentinel, missingSentinel.IssueKind);

        // The two drive-to-conclusion kinds STOP, tagged as themselves.
        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, inconclusive.Kind);
        Assert.Equal(RunIssueKind.OrchestratorInconclusive, inconclusive.IssueKind);
        Assert.Equal(OrchestratorMessageKind.OrchestratorInconclusive, inconclusive.MessageKind);
        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, infraCrash.Kind);
        Assert.Equal(RunIssueKind.InfraCrash, infraCrash.IssueKind);
        Assert.Equal(OrchestratorMessageKind.InfraCrash, infraCrash.MessageKind);

        // All four issue kinds stay separate.
        var kinds = new[] { inconclusive.IssueKind, infraCrash.IssueKind, cliLaunchFailed.IssueKind, missingSentinel.IssueKind };
        Assert.Equal(4, kinds.Distinct().Count());
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

    /// <summary>
    /// A model-invalid failure (invalid_request / 400 "model not supported") is
    /// non-retryable - re-issuing spawns into the same 400 - so the policy stops
    /// and routes to human review with a clear model-invalid reason instead of
    /// the orchestrator-inconclusive catch-all (AGT-1941 codex signature).
    /// </summary>
    [Fact]
    public void ModelInvalid_StopsAndRoutesToHumanReview()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown) with { IssueKind = RunIssueKind.ModelInvalid },
            followupPrompt: "please continue",
            reissueAttempt: 0,
            codexEvidence: null);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.Equal(RunIssueKind.ModelInvalid, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.ModelInvalid, action.MessageKind);
        Assert.False(action.IsHeuristicFallback);
    }

    /// <summary>
    /// A quota-exhausted failure (session/usage/rate limit) is transient but
    /// re-issuing right now hits the same rejection, so the policy stops and
    /// routes to human review with an honest quota-exhausted reason instead of
    /// the orchestrator-inconclusive catch-all (AGT-1918/1919/1920 signature).
    /// </summary>
    [Fact]
    public void QuotaExhausted_StopsAndRoutesToHumanReview()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown) with { IssueKind = RunIssueKind.QuotaExhausted },
            followupPrompt: "please continue",
            reissueAttempt: 0,
            codexEvidence: null);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.Equal(RunIssueKind.QuotaExhausted, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.QuotaExhausted, action.MessageKind);
        Assert.False(action.IsHeuristicFallback);
    }

    /// <summary>
    /// AGT-2066 WÄCHTER / breaker. A failed OAuth-session refresh is
    /// NON-RETRYABLE and shared across every parallel run, so the policy STOPS
    /// immediately and routes to human review with a re-auth instruction - it
    /// never spends a retry, never re-issues. This is the guard that stops the
    /// 17-cards-in-minutes cascade the 2026-07-10 incident produced.
    /// </summary>
    [Fact]
    public void AuthRefreshFailed_StopsAndRoutesToHumanReview_WithoutRetry()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown) with { IssueKind = RunIssueKind.AuthRefreshFailed },
            followupPrompt: "please continue",
            reissueAttempt: 0,
            codexEvidence: null);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.Equal(RunIssueKind.AuthRefreshFailed, action.IssueKind);
        Assert.Equal(OrchestratorMessageKind.AuthRefreshFailed, action.MessageKind);
        Assert.Null(action.FollowupRetryPrompt);
        Assert.False(action.IsHeuristicFallback);
    }

    /// <summary>
    /// The breaker is non-retryable even once a retry budget has already been
    /// spent: re-issuing walks straight back into the same dead token, so the
    /// action stays a stop regardless of the attempt counter.
    /// </summary>
    [Fact]
    public void AuthRefreshFailed_StaysStop_EvenAfterPriorAttempts()
    {
        var action = RunOutcomePolicy.Decide(
            RunIntent.AutoPickup,
            ContinuePlan(),
            Outcome(AgentOutcomeKind.Unknown) with { IssueKind = RunIssueKind.AuthRefreshFailed },
            followupPrompt: "please continue",
            reissueAttempt: 3,
            codexEvidence: null);

        Assert.Equal(OutcomeActionKind.NotifyUserAndStop, action.Kind);
        Assert.Equal(RunIssueKind.AuthRefreshFailed, action.IssueKind);
    }
}
