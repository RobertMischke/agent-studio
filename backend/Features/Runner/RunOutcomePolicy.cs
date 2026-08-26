
namespace AgentStudio.Runner;

/// <summary>
/// What the orchestrator should do after a CLI run finishes. Pure data,
/// applied by <see cref="ProjectRunner"/>.
/// </summary>
public enum OutcomeActionKind
{
    /// <summary>Honor the run's exit status. Move the job per <see cref="RunCompletionPolicy"/>.</summary>
    Accept,
    /// <summary>Accept the run but post a meta message into the chat for the user.</summary>
    NotifyUserAndAccept,
    /// <summary>Re-issue the same intent with a stronger framing because the agent did not honor the user request.</summary>
    ReissueWithStrongerFraming,
    /// <summary>Halt the job and post a meta message explaining why a re-issue did not run.</summary>
    NotifyUserAndStop,
    /// <summary>Hold the task for an account-level provider reset without escalation.</summary>
    WaitForProvider,
}

/// <summary>
/// Pure description of the orchestrator's decision after a run. Side effects
/// (writing into the chat log, kicking off another run) are applied by the
/// runner, never inside the policy.
/// </summary>
public sealed record OutcomeAction(
    OutcomeActionKind Kind,
    string MetaMessage,
    bool IsHeuristicFallback,
    string? FollowupRetryPrompt = null,
    int RetryAttempt = 0)
{
    public RunIssueKind IssueKind { get; init; } = RunIssueKind.None;
    public OrchestratorMessageKind MessageKind { get; init; } = OrchestratorMessageKind.Decision;
    public bool IsPreframedRetryPrompt { get; init; }

    /// <summary>
    /// How long the runner should wait before running a
    /// <see cref="OutcomeActionKind.ReissueWithStrongerFraming"/> retry.
    /// <see cref="TimeSpan.Zero"/> (the default) means retry immediately. Set for
    /// environmental retries so a transient host file lock / network glitch gets
    /// real wall-clock time to clear before the re-run (AGT-1944 environmental
    /// retry-with-backoff). Ignored for non-reissue actions.
    /// </summary>
    public TimeSpan RetryBackoff { get; init; } = TimeSpan.Zero;
}

/// <summary>
/// Pure decision library that maps (intent, run plan, outcome, follow-up,
/// retry count) to an <see cref="OutcomeAction"/>.
///
/// <para>
/// <b>Design center.</b> The product was previously prompt-driven: we asked
/// the agent to "treat this as a continuation" and accepted whatever came
/// back, even when the run was a 4-second no-op that ignored the user's
/// follow-up. This policy makes the orchestrator a deterministic arbiter:
/// when the agent's report contradicts the structural evidence (no edits,
/// near-zero duration) AND the user supplied a follow-up that has not been
/// honored, the orchestrator re-issues the work itself with stronger
/// framing rather than letting the inconsistency stand. When no hard
/// sentinel matched and the verdict came from the heuristic, the user is
/// always told (the meta channel makes the heuristic visible).
/// </para>
/// </summary>
public static class RunOutcomePolicy
{
    /// <summary>Maximum number of automatic re-issue attempts before the orchestrator gives up and asks the user.</summary>
    public const int MaxAutoReissueAttempts = 1;
    /// <summary>Maximum number of soft orchestration interventions for one detected issue class.</summary>
    public const int MaxSoftInterventionAttempts = 1;

    /// <summary>
    /// The load-bearing first rule on every reissue / continuation / completion
    /// follow-up prompt: steer the open diff, do not restart. The operator
    /// symptom this fixes (ASS-734) was the orchestrator re-running a task from
    /// scratch on every reissue, duplicating already-committed work. Kept as one
    /// shared constant so the reissue, missing-sentinel, Codex-continuation and
    /// completion-gate builders cannot drift apart on this instruction.
    /// </summary>
    public const string DiffOnlySteeringRule =
        "STEER THE DIFF, DO NOT RESTART: Do ONLY the open remaining work. "
        + "Build on the commits already made for this task - do NOT redo the work from scratch, "
        + "and do NOT re-apply changes that are already committed. Resume where the previous run "
        + "left off and close out only the items still open.";

    /// <summary>
    /// Render a short, agent-readable block listing the commits already made for
    /// this task so a reissue/continuation builds on them instead of redoing the
    /// work. Returns the empty string when no commits are supplied so callers can
    /// concatenate unconditionally.
    /// </summary>
    public static string RenderPriorCommitsBlock(IReadOnlyList<string>? priorCommits)
    {
        if (priorCommits == null || priorCommits.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();
        sb.Append("\n\nCommits already made for this task (build on these, do not repeat them):\n");
        foreach (var commit in priorCommits.Take(20))
        {
            var line = (commit ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            if (line.Length == 0) continue;
            sb.Append("- ").Append(line).Append('\n');
        }
        return sb.ToString();
    }

    public static IReadOnlyList<string> PriorCommitLines(TaskInfo? info)
    {
        if (info == null) return Array.Empty<string>();

        var commits = info.Commits.Count > 0
            ? info.Commits
            : info.Commit == null ? [] : [info.Commit];

        return commits
            .Where(c => !string.IsNullOrWhiteSpace(c.Sha) || !string.IsNullOrWhiteSpace(c.ShortSha))
            .Select(c =>
            {
                var sha = !string.IsNullOrWhiteSpace(c.ShortSha)
                    ? c.ShortSha.Trim()
                    : c.Sha.Length > 7 ? c.Sha[..7] : c.Sha.Trim();
                var subject = (c.Message ?? string.Empty).Split('\n')[0].Trim();
                return string.IsNullOrWhiteSpace(subject) ? sha : $"{sha} {subject}";
            })
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(20)
            .ToList();
    }

    /// <summary>
    /// Decide what to do with a finished run.
    /// </summary>
    /// <param name="intent">Why the run was started.</param>
    /// <param name="plan">The plan the runner executed.</param>
    /// <param name="outcome">The analyzed agent outcome from <see cref="AgentOutcomeAnalyzer"/>.</param>
    /// <param name="followupPrompt">The follow-up the user typed (only meaningful for <see cref="RunIntent.UserContinue"/>).</param>
    /// <param name="reissueAttempt">How many times this same intent has already been re-issued by the orchestrator. Used to cap retries.</param>
    public static OutcomeAction Decide(
        RunIntent intent,
        RunPlan plan,
        AgentOutcome outcome,
        string? followupPrompt,
        int reissueAttempt,
        CodexCompletionEvidence.Inputs? codexEvidence = null,
        IReadOnlyList<string>? priorCommits = null)
    {
        var heuristic = !outcome.MatchedSentinel;
        var hasFollowup = !string.IsNullOrWhiteSpace(followupPrompt);
        var isRecovery = string.Equals(plan.EventKind, "recovery", StringComparison.OrdinalIgnoreCase);
        var isResumeContinue = intent == RunIntent.UserContinue
            && string.Equals(plan.EventKind, "continue", StringComparison.OrdinalIgnoreCase)
            && plan.ResumeFlag;

        if (outcome.IssueKind == RunIssueKind.SilentCompletion)
        {
            // Evidence-based completion for Codex (see CodexCompletionEvidence):
            // a silent finish with open items / a mid-task timeout is driven to
            // a clean finish via a bounded continuation loop before we accept.
            // A clean finish (commits + success status, nothing open) still
            // accepts, just with an evidence-grounded note.
            var codexAction = TryCodexEvidenceAction(codexEvidence, outcome, reissueAttempt, RunIssueKind.SilentCompletion, priorCommits);
            if (codexAction != null) return codexAction;

            // Codex stopped after a successful tool call and never produced
            // a closing sentinel. The runtime detector already killed the
            // lingering process with RunStopReason.SilentCompletion (which
            // the classifier maps to status=Completed), so the run flows
            // through the standard accept path: lane moves to
            // 4-auto-review, summary generation runs, aspect calls run.
            // We surface a typed meta note + IssueKind so the orchestrator
            // chat clearly distinguishes "agent finished and signed off"
            // from "agent likely finished but never said so".
            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndAccept,
                MetaMessage: outcome.Summary
                    ?? "Codex stopped after its final tool call without a closing sentinel. Treating the run as complete but flagging it for review.",
                IsHeuristicFallback: false)
            {
                IssueKind = RunIssueKind.SilentCompletion,
                MessageKind = OrchestratorMessageKind.SilentCompletion
            };
        }

        if (outcome.IssueKind == RunIssueKind.AgentGitViolation)
        {
            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndStop,
                MetaMessage: outcome.Summary
                    ?? "Genuine git damage was verified: the worker changed a protected remote branch or rewrote history that predates this run. Routing to human review.",
                IsHeuristicFallback: false)
            {
                IssueKind = RunIssueKind.AgentGitViolation,
                MessageKind = OrchestratorMessageKind.AgentGitViolation
            };
        }

        if (outcome.IssueKind == RunIssueKind.ContextOverflow)
        {
            // Context overflow (prompt too long / context length exceeded) is
            // NON-RETRYABLE: re-issuing resends the same or a larger context
            // and overflows identically. This is the precise failure that was
            // looping forever (a "Prompt too long" failure re-issued without
            // limit). Route straight to human review on first detection - no
            // soft intervention, no retry budget to spend - so a human can
            // re-scope or split the task. Mirrors EnvironmentBlocker.
            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndStop,
                MetaMessage: outcome.Summary
                    ?? "The run exceeded the model's context window (prompt too long). This cannot be fixed by re-issuing; the task needs to be re-scoped or split. Routing to human review.",
                IsHeuristicFallback: false)
            {
                IssueKind = RunIssueKind.ContextOverflow,
                MessageKind = OrchestratorMessageKind.ContextOverflow
            };
        }

        if (outcome.IssueKind == RunIssueKind.ModelInvalid)
        {
            // The configured model is invalid/unsupported for this account or
            // CLI (invalid_request / HTTP 400 "model not supported"). NON-
            // RETRYABLE: re-issuing spawns straight back into the same 400.
            // Route to human review with a clear model-invalid reason so a
            // human changes the model, instead of the misleading
            // orchestrator-inconclusive catch-all. Mirrors ContextOverflow.
            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndStop,
                MetaMessage: outcome.Summary
                    ?? "The configured model is invalid/unsupported for this account or CLI. This cannot be fixed by re-issuing; change the model on the task. Routing to human review.",
                IsHeuristicFallback: false)
            {
                IssueKind = RunIssueKind.ModelInvalid,
                MessageKind = OrchestratorMessageKind.ModelInvalid
            };
        }

        if (outcome.IssueKind == RunIssueKind.QuotaExhausted)
        {
            // Account limits are provider capability state, not task failure.
            // The runner persists quotaWait and schedules a reset-time probe;
            // this action exists as the policy-level guard against any caller
            // accidentally routing the same signal to human review.
            return new OutcomeAction(
                Kind: OutcomeActionKind.WaitForProvider,
                MetaMessage: outcome.Summary
                    ?? "The account's usage/session/rate-limit budget is exhausted. Waiting for the provider reset; the task will resume automatically.",
                IsHeuristicFallback: false)
            {
                IssueKind = RunIssueKind.QuotaExhausted,
                MessageKind = OrchestratorMessageKind.QuotaExhausted
            };
        }

        if (outcome.IssueKind == RunIssueKind.AuthRefreshFailed)
        {
            // AGT-2066 WÄCHTER / breaker. The CLI could not launch because its
            // OAuth session expired and the token refresh failed. Because every
            // parallel run shares the one operator credential, this fails EVERY
            // launch identically until the credential is re-authenticated - the
            // 2026-07-10 incident burned 17 cards this way. NON-RETRYABLE: a
            // re-issue spawns straight back into the same dead token, so (unlike
            // CliLaunchFailed, which rebuilds from disk and retries once) we
            // spend NO retry budget. Stop immediately and route to human review
            // with a concrete re-auth instruction so the operator fixes the
            // credential centrally instead of watching the lane drain. Mirrors
            // the non-retryable ContextOverflow / ModelInvalid stops.
            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndStop,
                MetaMessage: outcome.Summary
                    ?? "The agent CLI could not refresh its OAuth session. Every launch shares the one credential and will fail identically until it is re-authenticated centrally. Stopping instead of burning further launches; re-auth the CLI, then re-queue. Routing to human review.",
                IsHeuristicFallback: false)
            {
                IssueKind = RunIssueKind.AuthRefreshFailed,
                MessageKind = OrchestratorMessageKind.AuthRefreshFailed
            };
        }

        if (outcome.IssueKind == RunIssueKind.EnvironmentBlocker)
        {
            // Environment blockers are unrecoverable by the agent. The
            // base CLI service has already killed the process, so there
            // is no retry to spend - we route straight to human review
            // with the typed diagnosis. Auto-review aspects would be
            // pointless: there is no change set to evaluate.
            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndStop,
                MetaMessage: outcome.Summary
                    ?? "An OS or sandbox blocker prevented the agent from making progress. Human attention required.",
                IsHeuristicFallback: false)
            {
                IssueKind = RunIssueKind.EnvironmentBlocker,
                MessageKind = OrchestratorMessageKind.EnvironmentBlocker
            };
        }

        if (outcome.IssueKind == RunIssueKind.EnvironmentalTransient)
        {
            // A transient host file lock (MSB302x copy-lock family) or network
            // glitch. Unlike EnvironmentBlocker the condition clears on its own,
            // so RETRY with exponential backoff before escalating: the code was
            // not the problem, and re-running after the lock releases / the
            // network recovers usually succeeds. Only once the bounded retry
            // budget is spent do we route to human review, flagged environmental
            // so a reviewer does not read an infra blip as a failed change
            // (AGT-1944 environmental retry-with-backoff).
            var retry = PostProcessingOutcomeTaxonomy.DecideEnvironmentalRetry(
                RunIssueKind.EnvironmentalTransient, reissueAttempt);
            if (retry.Action == EnvironmentalRetryAction.RetryWithBackoff)
            {
                return new OutcomeAction(
                    Kind: OutcomeActionKind.ReissueWithStrongerFraming,
                    MetaMessage: $"The run failed on a transient environmental fault (host file lock / network), not on the change. Retrying after a {FormatBackoff(retry.Backoff)} backoff (attempt {retry.Attempt}).",
                    IsHeuristicFallback: false,
                    FollowupRetryPrompt: BuildEnvironmentalRetryPrompt(outcome, priorCommits),
                    RetryAttempt: retry.Attempt)
                {
                    IssueKind = RunIssueKind.EnvironmentalTransient,
                    MessageKind = OrchestratorMessageKind.EnvironmentalRetry,
                    IsPreframedRetryPrompt = true,
                    RetryBackoff = retry.Backoff
                };
            }

            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndStop,
                MetaMessage: outcome.Summary
                    ?? "A transient environmental fault (host file lock / network) persisted after automatic retries. Routing to human review flagged environmental.",
                IsHeuristicFallback: false)
            {
                IssueKind = RunIssueKind.EnvironmentalTransient,
                MessageKind = OrchestratorMessageKind.EnvironmentBlocker
            };
        }

        if (outcome.IssueKind == RunIssueKind.CliLaunchFailed)
        {
            // The agent CLI failed to launch or its --resume target was rejected
            // before any agent turn happened (exit != 0, ~0s, only a CLI error
            // fragment). This is a recoverable host/CLI condition (a dead session
            // after a backend restart, a rejected resume id), NOT an agent
            // decision. The category's own copy promises "rebuilding from disk on
            // the next attempt" - so MAKE that next attempt instead of parking to
            // 5e: one automatic fresh-start retry (the runner has already cleared
            // the dead session id / marked the chain for Recovery, so the re-run
            // rebuilds from disk), and escalate to human review only if the fresh
            // start also fails. Fixes the belege where a resume loss after a
            // restart cost a whole cycle by escalating on first detection
            // (AGT-1945/1929/1930).
            var retry = PostProcessingOutcomeTaxonomy.DecideEnvironmentalRetry(
                RunIssueKind.CliLaunchFailed, reissueAttempt);
            if (retry.Action == EnvironmentalRetryAction.RetryWithBackoff)
            {
                return new OutcomeAction(
                    Kind: OutcomeActionKind.ReissueWithStrongerFraming,
                    MetaMessage: outcome.Summary
                        ?? "The agent CLI could not launch or resume the prior session. Rebuilding from disk and retrying once before escalating.",
                    IsHeuristicFallback: false,
                    FollowupRetryPrompt: BuildCliLaunchRetryPrompt(priorCommits),
                    RetryAttempt: retry.Attempt)
                {
                    IssueKind = RunIssueKind.CliLaunchFailed,
                    MessageKind = OrchestratorMessageKind.CliLaunchFailed,
                    IsPreframedRetryPrompt = true
                    // No backoff: a fresh start after a dead session should run
                    // promptly; the launch failure is not a rate/resource fault.
                };
            }

            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndStop,
                MetaMessage: "The agent CLI could not launch or resume even after an automatic fresh-start retry. Routing to human review.",
                IsHeuristicFallback: false)
            {
                IssueKind = RunIssueKind.CliLaunchFailed,
                MessageKind = OrchestratorMessageKind.CliLaunchFailed
            };
        }

        if (outcome.IssueKind == RunIssueKind.EmptyFastExit)
        {
            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndStop,
                MetaMessage: outcome.Summary
                    ?? "The agent CLI exited almost immediately without producing an agent turn. This is a failed start, not an agent no-op.",
                IsHeuristicFallback: false)
            {
                IssueKind = RunIssueKind.EmptyFastExit,
                MessageKind = OrchestratorMessageKind.EmptyFastExit
            };
        }

        if (outcome.IssueKind == RunIssueKind.PermissionBlocked)
        {
            if (reissueAttempt < MaxSoftInterventionAttempts)
            {
                return new OutcomeAction(
                    Kind: OutcomeActionKind.ReissueWithStrongerFraming,
                    MetaMessage: "The agent hit a tool permission boundary. The orchestrator is giving it one soft intervention to find a solution within the available permissions.",
                    IsHeuristicFallback: false,
                    FollowupRetryPrompt: BuildPermissionInterventionPrompt(),
                    RetryAttempt: reissueAttempt + 1)
                {
                    IssueKind = RunIssueKind.PermissionBlocked,
                    MessageKind = OrchestratorMessageKind.SoftIntervention,
                    IsPreframedRetryPrompt = true
                };
            }

            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndStop,
                MetaMessage: "Permission issue persisted after one orchestrator intervention. The task needs human attention before it can continue.",
                IsHeuristicFallback: false)
            {
                IssueKind = RunIssueKind.PermissionBlocked,
                MessageKind = OrchestratorMessageKind.PermissionBlocked
            };
        }

        if (outcome.IssueKind == RunIssueKind.WatchdogTimeout)
        {
            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndStop,
                MetaMessage: "The watchdog killed this run after a silence timeout. This is a runner/process outcome, not an agent decision.",
                IsHeuristicFallback: false)
            {
                IssueKind = RunIssueKind.WatchdogTimeout,
                MessageKind = OrchestratorMessageKind.WatchdogTimeout
            };
        }

        if (outcome.IssueKind == RunIssueKind.MissingTerminalSentinel)
        {
            // Codex completion is process-exit based: exec
            // --experimental-json stream close + exit code is the completion
            // gate. A missing sentinel remains a review hint, but must not
            // trigger the legacy "ask once for a structured close-out" loop.
            var codexAction = TryCodexEvidenceAction(codexEvidence, outcome, reissueAttempt, RunIssueKind.MissingTerminalSentinel, priorCommits);
            if (codexAction != null) return codexAction;
            if (codexEvidence is { } evidence && evidence.IsCodex)
            {
                return new OutcomeAction(
                    Kind: OutcomeActionKind.NotifyUserAndAccept,
                    MetaMessage: "Codex exited after the experimental-json stream closed without a terminal sentinel. Treating process exit as completion and surfacing the missing sentinel as a review marker.",
                    IsHeuristicFallback: false)
                {
                    IssueKind = RunIssueKind.MissingTerminalSentinel,
                    MessageKind = OrchestratorMessageKind.MissingTerminalSentinel
                };
            }

            if (reissueAttempt < MaxSoftInterventionAttempts)
            {
                return new OutcomeAction(
                    Kind: OutcomeActionKind.ReissueWithStrongerFraming,
                    MetaMessage: "The agent replied without a terminal sentinel. The orchestrator is asking once for a structured close-out so the task can move through the pipeline cleanly.",
                    IsHeuristicFallback: false,
                    FollowupRetryPrompt: BuildMissingSentinelInterventionPrompt(outcome, priorCommits),
                    RetryAttempt: reissueAttempt + 1)
                {
                    IssueKind = RunIssueKind.MissingTerminalSentinel,
                    MessageKind = OrchestratorMessageKind.SoftIntervention,
                    IsPreframedRetryPrompt = true
                };
            }

            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndAccept,
                MetaMessage: "The agent still did not emit a terminal sentinel after one orchestrator intervention. Continuing with a visible missing-terminal-sentinel marker.",
                IsHeuristicFallback: false)
            {
                IssueKind = RunIssueKind.MissingTerminalSentinel,
                MessageKind = OrchestratorMessageKind.MissingTerminalSentinel
            };
        }

        if (outcome.IssueKind is RunIssueKind.InfraCrash or RunIssueKind.OrchestratorInconclusive)
        {
            // A failed run the deterministic contract could not close out
            // cleanly: either the CLI process died hard (InfraCrash) or it
            // produced real text that maps to no terminal verdict
            // (OrchestratorInconclusive). Neither is ever silently accepted -
            // the old classifier-unknown path returned NotifyUserAndAccept with
            // a marker that moved nothing and stranded the task in 3-progress.
            // The drive-to-conclusion invariant is that no failed/unclassified
            // run returns NotifyUserAndAccept: it stops and hands the task to
            // the user, carrying the typed issue so the escalation layer can
            // route it to human review.
            var isInfraCrash = outcome.IssueKind == RunIssueKind.InfraCrash;
            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndStop,
                MetaMessage: isInfraCrash
                    ? "The agent CLI crashed before it could reach a terminal verdict. Stopping and handing the task to the user for review."
                    : "The run failed and its reply could not be mapped to a terminal verdict. Stopping and handing the task to the user for review.",
                IsHeuristicFallback: false)
            {
                IssueKind = outcome.IssueKind,
                MessageKind = isInfraCrash
                    ? OrchestratorMessageKind.InfraCrash
                    : OrchestratorMessageKind.OrchestratorInconclusive
            };
        }

        // No-effort completion: agent exited fast with little or no text.
        // Graceful Recovery (ADR-0006 supersedes ADR-0003): even when the
        // prior session is unrecoverable, we re-issue ONCE with a sharper
        // framing rather than letting the user's follow-up fall on the
        // floor. The retry prompt makes the follow-up the only thing that
        // matters and explicitly tells the agent that conversation history
        // is gone, so "I'll wait for your next request" is not an
        // acceptable answer. The retry budget is the same as the
        // Resume-Continue path - one shot, then NotifyUserAndStop.
        var lookedLikeNoEffortDone =
            outcome.Kind == AgentOutcomeKind.Done
            && outcome.DurationSeconds < AgentOutcomeAnalyzer.NoOpDurationThresholdSeconds
            && outcome.AgentTextChars < 50;

        var noEffortShape = outcome.Kind == AgentOutcomeKind.NoOp || lookedLikeNoEffortDone;
        var triggersReissue =
            (isResumeContinue || isRecovery)
            && intent == RunIntent.UserContinue
            && hasFollowup
            && noEffortShape;

        if (triggersReissue && reissueAttempt < MaxAutoReissueAttempts)
        {
            var msg = isRecovery
                ? "Recovery from session loss did not produce useful output. Re-issuing your follow-up with a sharper framing so the agent acts on it even without prior conversation context."
                : "The agent exited without acting on your follow-up. Re-issuing your request with a sharper framing.";
            return new OutcomeAction(
                Kind: OutcomeActionKind.ReissueWithStrongerFraming,
                MetaMessage: msg,
                IsHeuristicFallback: heuristic,
                FollowupRetryPrompt: followupPrompt,
                RetryAttempt: reissueAttempt + 1);
        }
        if (triggersReissue && reissueAttempt >= MaxAutoReissueAttempts)
        {
            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndStop,
                MetaMessage: "I retried your follow-up once and the agent still exited without acting on it. Please rephrase, check the agent CLI, or look at the Trace view.",
                IsHeuristicFallback: heuristic);
        }

        // Heuristic verdicts surface only when the user gets actionable
        // signal from the meta message. NeedsInput is the obvious skip:
        // the agent's question is already visible in the chat and the
        // frontend's quick-reply chips react to it directly; an extra
        // "[heuristic] verdict: needsinput" line is just noise.
        // Progress is similarly self-evident from the run still moving.
        // We keep the warning for Done (the agent might be claiming done
        // without doing the work) and Unknown (we genuinely cannot tell).
        if (heuristic && outcome.Kind == AgentOutcomeKind.Done)
        {
            return new OutcomeAction(
                Kind: OutcomeActionKind.NotifyUserAndAccept,
                MetaMessage: "[heuristic] Agent reports done (no structured signal).",
                IsHeuristicFallback: true)
            {
                IssueKind = RunIssueKind.HeuristicDone,
                MessageKind = OrchestratorMessageKind.HeuristicDone
            };
        }

        if (outcome.Kind == AgentOutcomeKind.Unknown)
        {
            // Any Unknown that needs the user's attention has already been
            // routed by a typed issue kind above (CliLaunchFailed for a CLI
            // launch/resume failure, InfraCrash / OrchestratorInconclusive for
            // a failed run with real text, MissingTerminalSentinel for a
            // successful run with inconclusive text). What reaches here is the
            // residual: no agent text to classify (NoAgentOutput) or a
            // long-but-silent run. The cause is already visible in the chat
            // (system-error block, [capture-fail] decision, protocol-pane
            // banner), so piling a terminal "could not classify" FAILURE on top
            // is just noise.
            return new OutcomeAction(
                Kind: OutcomeActionKind.Accept,
                MetaMessage: string.Empty,
                IsHeuristicFallback: false);
        }

        return new OutcomeAction(
            Kind: OutcomeActionKind.Accept,
            MetaMessage: string.Empty,
            IsHeuristicFallback: false);
    }

    /// <summary>
    /// Build the prompt used when the orchestrator re-issues a follow-up that
    /// the agent failed to honor. Kept here (next to the policy that decides
    /// to re-issue) so any change to the policy and its prompt move together.
    ///
    /// <para>
    /// <paramref name="recoveryContext"/> is true when the previous run was a
    /// Recovery (no resumable session). The framing then tells the agent
    /// explicitly that conversation history is unavailable and that the
    /// user request, plus any task evidence on disk (prompt.md, status.md,
    /// logs), is the entire context. This is the load-bearing prompt for
    /// graceful recovery: if it fails, the orchestrator gives up.
    /// </para>
    /// </summary>
    public static string BuildReissueFollowupPrompt(
        string originalFollowup,
        bool recoveryContext = false,
        IReadOnlyList<string>? priorCommits = null)
    {
        var head = recoveryContext
            ? "The previous CLI session for this task is unrecoverable. There is no conversation history to read; the only context you have is the job folder on disk (prompt.md, status.md, logs/cli-output.log) and the user request below. Do the work the user asked for, end-to-end. "
              + "Reading 'I'll wait for your request' or 'standing by' as a valid answer is not acceptable - the user already gave you the request. "
            : "The previous run exited without acting on the user request below. "
              + "Treat the user request as the only thing that matters now. ";
        return DiffOnlySteeringRule + "\n\n"
             + head
             + "Do not reply 'task done' unless you have actually performed the work the user asked for. "
             + "If you genuinely cannot perform the request, emit [[TASK_BLOCKED:missing-dependency-xyz]], replacing the example reason with the actual short reason, and explain why."
             + RenderPriorCommitsBlock(priorCommits)
             + "\n\nUser request:\n"
             + (originalFollowup ?? string.Empty).Trim();
    }

    /// <summary>
    /// Apply the Codex evidence-based completion verdict, when evidence is
    /// supplied. Returns the resulting <see cref="OutcomeAction"/> for an
    /// AcceptAsDone or Continue verdict, or <c>null</c> for Inconclusive /
    /// no-evidence so the caller falls through to the existing routing for the
    /// given <paramref name="issueKind"/>. Shared by the SilentCompletion and
    /// MissingTerminalSentinel branches so both Codex silent-finish shapes get
    /// identical evidence treatment.
    /// </summary>
    private static OutcomeAction? TryCodexEvidenceAction(
        CodexCompletionEvidence.Inputs? codexEvidence,
        AgentOutcome outcome,
        int reissueAttempt,
        RunIssueKind issueKind,
        IReadOnlyList<string>? priorCommits)
    {
        if (codexEvidence is not { } evidence) return null;

        var verdict = CodexCompletionEvidence.Decide(evidence);
        switch (verdict.Action)
        {
            case CodexCompletionEvidence.CompletionAction.AcceptAsDone:
                return new OutcomeAction(
                    Kind: OutcomeActionKind.NotifyUserAndAccept,
                    MetaMessage: $"Codex finished without a terminal sentinel, but the evidence shows the work is done. {verdict.Reason}",
                    IsHeuristicFallback: false)
                {
                    IssueKind = RunIssueKind.SilentCompletion,
                    MessageKind = OrchestratorMessageKind.SilentCompletion
                };

            case CodexCompletionEvidence.CompletionAction.Continue:
                return new OutcomeAction(
                    Kind: OutcomeActionKind.ReissueWithStrongerFraming,
                    MetaMessage: $"Codex finished without a terminal sentinel and left open work. Running a bounded continuation to drive it to completion. {verdict.Reason}",
                    IsHeuristicFallback: false,
                    FollowupRetryPrompt: BuildCodexContinuationPrompt(outcome, priorCommits),
                    RetryAttempt: reissueAttempt + 1)
                {
                    IssueKind = issueKind,
                    MessageKind = OrchestratorMessageKind.SoftIntervention,
                    IsPreframedRetryPrompt = true
                };

            default:
                return null;
        }
    }

    /// <summary>
    /// Continuation prompt for the bounded Codex completion loop. Used when a
    /// silent finish left open items or timed out mid-task: resume the same
    /// session, finish the remaining work, then sign off. Kept next to the
    /// policy that decides to continue so they move together.
    /// </summary>
    public static string BuildCodexContinuationPrompt(AgentOutcome outcome, IReadOnlyList<string>? priorCommits = null)
        => DiffOnlySteeringRule + "\n\n"
         + "Your previous turn ended without the required terminal sentinel and the task still has open work. "
         + "Finish the remaining items now - do not re-investigate from scratch, build on what you already did. "
         + "When the work is genuinely complete, end with exactly one terminal sentinel on its own line: "
         + "[[TASK_DONE]] (or [[TASK_BLOCKED:missing-dependency-xyz]] if you truly cannot finish). Replace the example reason with the actual short reason. "
         + $"The orchestrator's summary of your previous turn was: {outcome.Summary ?? "unclassified"}."
         + RenderPriorCommitsBlock(priorCommits);

    /// <summary>
    /// Retry prompt for a transient environmental fault (host file lock /
    /// network glitch). Tells the agent the fault was the environment - not its
    /// changes - and to resume the open work rather than redo committed work.
    /// Kept next to the policy that decides to retry so they move together.
    /// </summary>
    public static string BuildEnvironmentalRetryPrompt(AgentOutcome outcome, IReadOnlyList<string>? priorCommits = null)
        => DiffOnlySteeringRule + "\n\n"
         + "The previous attempt failed on a TRANSIENT environmental fault (a host file lock or a network glitch), "
         + "not on your changes. The condition has cleared. Resume the open work and finish it. "
         + "When the work is genuinely complete, end with exactly one terminal sentinel on its own line: "
         + "[[TASK_DONE]] (or [[TASK_BLOCKED:missing-dependency-xyz]] if you truly cannot finish). Replace the example reason with the actual short reason. "
         + $"The previous run's diagnosis was: {outcome.Summary ?? "transient environmental fault"}."
         + RenderPriorCommitsBlock(priorCommits);

    /// <summary>
    /// Fresh-start prompt for a CLI launch / resume failure. There is no
    /// conversation history to resume, so the agent must rebuild its context from
    /// the job folder on disk and carry out the task from there. Used for the one
    /// automatic fresh-start retry before a launch failure escalates.
    /// </summary>
    public static string BuildCliLaunchRetryPrompt(IReadOnlyList<string>? priorCommits = null)
        => DiffOnlySteeringRule + "\n\n"
         + "The previous attempt could not launch or resume its CLI session, so there is no conversation history. "
         + "Rebuild your context from the job folder on disk (prompt.md, status.md, logs/) and carry out the task it "
         + "describes, end to end. Do not treat the missing history as a reason to stop. "
         + "When you finish, end with exactly one terminal sentinel on its own line: "
         + "[[TASK_DONE]], [[TASK_BLOCKED:missing-dependency-xyz]], [[TASK_NEEDS_INPUT:choose-primary-column]], or [[TASK_NOOP]]. Replace the example reason with the actual short reason."
         + RenderPriorCommitsBlock(priorCommits);

    /// <summary>Human-readable backoff, e.g. "30 s" or "2 min", for a meta message.</summary>
    private static string FormatBackoff(TimeSpan backoff)
        => backoff.TotalSeconds >= 60
            ? $"{backoff.TotalMinutes:0.#} min"
            : $"{backoff.TotalSeconds:0} s";

    public static string BuildPermissionInterventionPrompt()
        => "The previous attempt hit one or more tool permission errors. "
         + "Do not ask for additional permissions again. Find a solution using only the available permissions and the context already accessible in this run. "
         + "If a command or path is unavailable, try a narrower read, use existing repository files, inspect the task folder evidence, or reason from the visible output. "
         + "When you finish, end with exactly one terminal sentinel on its own line: [[TASK_DONE]], [[TASK_BLOCKED:missing-dependency-xyz]], [[TASK_NEEDS_INPUT:choose-primary-column]], or [[TASK_NOOP]]. Replace the example reason with the actual short reason.";

    public static string BuildMissingSentinelInterventionPrompt(AgentOutcome outcome, IReadOnlyList<string>? priorCommits = null)
        => BuildMissingSentinelInterventionPrompt(outcome.Summary, priorCommits);

    /// <summary>
    /// Overload used by the review-decision orchestrator's no-completion-signal
    /// loop, which has a short situational summary rather than a full
    /// <see cref="AgentOutcome"/>. Same framing so the agent always gets the
    /// identical "close out with exactly one terminal sentinel" instruction.
    /// </summary>
    public static string BuildMissingSentinelInterventionPrompt(string? previousSummary, IReadOnlyList<string>? priorCommits = null)
        => DiffOnlySteeringRule + "\n\n"
         + "Your previous reply did not include the terminal sentinel required by this taskboard. "
         + "Continue the task if work remains; otherwise close it out now. "
         + "End with exactly one terminal sentinel on its own line: [[TASK_DONE]], [[TASK_BLOCKED:missing-dependency-xyz]], [[TASK_NEEDS_INPUT:choose-primary-column]], or [[TASK_NOOP]]. Replace the example reason with the actual short reason. "
         + $"The orchestrator's current summary of your previous reply was: {previousSummary ?? "unclassified"}."
         + RenderPriorCommitsBlock(priorCommits);
}
