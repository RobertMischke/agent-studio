using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Decision-tree matrix for <see cref="RunPlanner.PlanRun"/> — the single
/// place that maps (intent × job state × session state × CLI compatibility) to
/// a <see cref="RunPlan"/>. The whole point of having one planner is that this
/// matrix locks every cell of the table; if a future change introduces a path
/// that "Continue" handles but "Start" doesn't (or vice versa), the gap shows
/// up here as a missing or surprising row instead of as a 4xx that ships to
/// the user.
///
/// The bug class this guards against: pre-refactor, "Start" and "Continue"
/// owned independent decision trees and the recovery fix landed only on one
/// side, so user follow-ups on a job without a captured session got a 400
/// "This job has no session yet — start it once before continuing" while the
/// Play button worked. Same inputs, different outputs depending on which
/// endpoint you reached. The planner now serialises both intents through one
/// function — these tests prove they cannot diverge silently.
/// </summary>
public class TaskRunnerPlanTests
{
    // Claude's compat predicate: only true 8-4-4-4-12 UUIDs are accepted.
    // Anything else (placeholder slug, foreign-CLI handle, garbage) is rejected.
    private static readonly System.Text.RegularExpressions.Regex UuidRegex =
        new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");
    private static bool ClaudeCompat(string? n) =>
        !string.IsNullOrWhiteSpace(n) && UuidRegex.IsMatch(n!);
    // Permissive base predicate (Copilot / Gemini / Codex default): non-empty.
    private static bool PermissiveCompat(string? n) => !string.IsNullOrWhiteSpace(n);

    private const string ValidUuid       = "a1b2c3d4-e5f6-4789-abcd-ef0123456789";
    private const string PlaceholderSlug = "taskboard-fix-bug-202604282114";
    private const string ForeignSlug     = "rollover_2025-04-01T12:00:00Z";

    private static RunPlan Plan(
        RunIntent intent,
        string state,
        string? sessionName,
        string cliType = CliTypes.Claude,
        System.Func<string?, bool>? compat = null,
        string? followup = null)
    {
        return RunPlanner.PlanRun(
            intent,
            state,
            sessionName,
            cliType,
            compat ?? ClaudeCompat,
            jobId: "fix-bug",
            promptPath: @"C:\jobs\fix-bug\prompt.md",
            jobFolder: @"C:\jobs\fix-bug",
            followupPrompt: followup);
    }

    // ===== Continue (the original symptom path: "no session yet") =====

    /// <summary>
    /// THE regression: user types in the chat for a job that never captured a
    /// session UUID. Pre-refactor: 400. Post-refactor: recovery plan that
    /// starts a fresh CLI run, instructs the agent to reconstruct context from
    /// disk, marks the chain break, writes a cut marker, appends the user
    /// follow-up to the recovery prompt.
    /// </summary>
    [Fact]
    public void Continue_NoSession_FallsBackToRecovery()
    {
        var p = Plan(RunIntent.UserContinue, JobStates.Progress, sessionName: null,
                     followup: "please continue with the chat box");

        Assert.Equal("recovery", p.EventKind);
        Assert.False(p.ResumeFlag);
        Assert.Null(p.SessionToResume);
        Assert.True(p.MarkSessionChainRecovery);
        Assert.True(p.WriteCutMarker);
        Assert.Equal("no session recorded", p.EventReason);
        Assert.Contains("please continue with the chat box", p.Prompt);
        Assert.Contains("session was lost", p.Prompt, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Continue against a real captured UUID — the happy path. Resume flag on,
    /// session passed through, no recovery side-effects, prompt is the raw
    /// follow-up so the agent receives it as the next conversation turn.
    /// </summary>
    [Fact]
    public void Continue_WithValidSession_Resumes()
    {
        var p = Plan(RunIntent.UserContinue, JobStates.Progress, sessionName: ValidUuid,
                     followup: "tighten the spacing");

        Assert.Equal("continue", p.EventKind);
        Assert.True(p.ResumeFlag);
        Assert.Equal(ValidUuid, p.SessionToResume);
        Assert.Equal(ValidUuid, p.EventInputSessionId);
        Assert.False(p.MarkSessionChainRecovery);
        Assert.False(p.WriteCutMarker);
        Assert.Equal("tighten the spacing", p.Prompt);
        Assert.Null(p.EventReason);
    }

    /// <summary>
    /// Legacy placeholder slug from before real session capture: the planner
    /// must treat it as "no session" and route through recovery. If we
    /// accidentally accepted it as a resume handle, Claude's `-r` would hang
    /// or reply "I don't see an interrupted task" — the exact regression the
    /// placeholder check exists to prevent.
    /// </summary>
    [Fact]
    public void Continue_WithPlaceholderSlug_RoutesToRecovery()
    {
        var p = Plan(RunIntent.UserContinue, JobStates.Progress, sessionName: PlaceholderSlug,
                     followup: "go on");

        Assert.Equal("recovery", p.EventKind);
        Assert.False(p.ResumeFlag);
        Assert.Equal("recorded id is a legacy placeholder slug", p.EventReason);
        Assert.True(p.MarkSessionChainRecovery);
    }

    /// <summary>
    /// Foreign-CLI handle (e.g. Copilot's slug under a Claude job): not UUID-
    /// shaped, so the Claude compat predicate rejects it. Planner routes to
    /// recovery with the cli-specific reason text so the session-events log
    /// explains why.
    /// </summary>
    [Fact]
    public void Continue_WithForeignCliHandle_RoutesToRecovery()
    {
        var p = Plan(RunIntent.UserContinue, JobStates.Progress, sessionName: ForeignSlug,
                     followup: "go on");

        Assert.Equal("recovery", p.EventKind);
        Assert.False(p.ResumeFlag);
        Assert.Contains("not a valid claude session", p.EventReason ?? "",
                        System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Continue from 4-review/5-completed (user re-opens a finished job with a
    /// follow-up): the plan must signal MoveJobToProgress so the runner state
    /// machine stays consistent — without that, the job would stay in review
    /// while a CLI is actively writing to it. This is the move-policy
    /// asymmetry vs Start (which only moves from Ready → Progress); both must
    /// be preserved.
    /// </summary>
    [Theory]
    [InlineData(JobStates.Ready)]
    [InlineData(JobStates.Review)]
    [InlineData(JobStates.Completed)]
    public void Continue_MovesJobBackToProgress(string state)
    {
        var p = Plan(RunIntent.UserContinue, state, sessionName: ValidUuid, followup: "go");
        Assert.True(p.MoveJobToProgress);
    }

    [Fact]
    public void Continue_AlreadyInProgress_DoesNotMove()
    {
        var p = Plan(RunIntent.UserContinue, JobStates.Progress, sessionName: ValidUuid, followup: "go");
        Assert.False(p.MoveJobToProgress);
    }

    // ===== Manual / Auto Start =====

    /// <summary>
    /// Brand-new job from 2-ready, no session: plain fresh start. The fresh-
    /// start prompt points at prompt.md and the job folder so the agent reads
    /// the task and runs it.
    /// </summary>
    [Fact]
    public void Start_FromReadyNoSession_FreshStart()
    {
        var p = Plan(RunIntent.ManualStart, JobStates.Ready, sessionName: null);

        Assert.Equal("start", p.EventKind);
        Assert.False(p.ResumeFlag);
        Assert.Null(p.SessionToResume);
        Assert.True(p.MoveJobToProgress);
        Assert.False(p.MarkSessionChainRecovery);
        Assert.False(p.WriteCutMarker);
        Assert.Contains("prompt.md", p.Prompt);
        Assert.Null(p.PersistSessionName);
    }

    /// <summary>
    /// Auto-pickup is identical to manual start — only the trigger reason
    /// differs, the plan must not. Locks in the symmetry; if a future change
    /// adds an Auto-only branch, this test catches it.
    /// </summary>
    [Fact]
    public void AutoPickup_AndManualStart_ProducePlansThatDifferOnlyByTrigger()
    {
        var manual = Plan(RunIntent.ManualStart, JobStates.Ready, sessionName: null);
        var auto   = Plan(RunIntent.AutoPickup,  JobStates.Ready, sessionName: null);
        Assert.Equal(manual, auto);
    }

    /// <summary>
    /// Job stuck in 3-progress with a captured UUID — previous run was
    /// interrupted. The planner detects "interrupted resume" via the
    /// ShouldUseResumePrompt rule and switches to the resume continuation
    /// prompt that re-anchors the agent to the job folder.
    /// </summary>
    [Fact]
    public void Start_FromProgressWithSession_UsesResumePrompt()
    {
        var p = Plan(RunIntent.ManualStart, JobStates.Progress, sessionName: ValidUuid);

        Assert.Equal("continue", p.EventKind);
        Assert.True(p.ResumeFlag);
        Assert.Equal(ValidUuid, p.SessionToResume);
        Assert.Contains("Resume the interrupted task", p.Prompt);
        Assert.False(p.MoveJobToProgress); // already there
    }

    /// <summary>
    /// Job in 3-progress but no UUID was ever captured (e.g. CLI crashed
    /// before the first stream-json frame). Sending the resume prompt here
    /// would just make the agent reply "I don't see an interrupted task" — so
    /// the plan must fall back to a fresh start with the regular prompt.
    /// </summary>
    [Fact]
    public void Start_FromProgressNoSession_FallsBackToFreshPrompt()
    {
        var p = Plan(RunIntent.ManualStart, JobStates.Progress, sessionName: null);

        Assert.Equal("start", p.EventKind);
        Assert.False(p.ResumeFlag);
        Assert.Contains("prompt.md", p.Prompt);
        Assert.DoesNotContain("Resume the interrupted task", p.Prompt);
    }

    /// <summary>
    /// Foreign-CLI session on a Claude start: drop the session, mark the chain
    /// break, send the resume prompt so the agent reconstructs from files the
    /// previous CLI wrote. Event kind is "recovery" because the chain visibly
    /// broke; without that the chip would never show the cut.
    /// </summary>
    [Fact]
    public void Start_DropsForeignSession_AndRecovers()
    {
        var p = Plan(RunIntent.ManualStart, JobStates.Progress, sessionName: ForeignSlug);

        Assert.Equal("recovery", p.EventKind);
        Assert.False(p.ResumeFlag);
        Assert.Null(p.SessionToResume);
        Assert.True(p.MarkSessionChainRecovery);
        Assert.False(p.ClearStaleSessionName); // recovery, not a quiet drop
        Assert.Contains("Resume the interrupted task", p.Prompt);
        Assert.Equal("previous session was for another CLI — files reconstructed", p.EventReason);
    }

    /// <summary>
    /// Legacy placeholder on a Claude start: dropped quietly (ClearStale, no
    /// chain-break mark, no recovery event) — placeholders never represented
    /// real sessions, so there is nothing to recover from and the chip should
    /// not show a cut.
    /// </summary>
    [Fact]
    public void Start_DropsLegacyPlaceholder_Quietly()
    {
        var p = Plan(RunIntent.ManualStart, JobStates.Progress, sessionName: PlaceholderSlug);

        Assert.Equal("start", p.EventKind);
        Assert.True(p.ClearStaleSessionName);
        Assert.False(p.MarkSessionChainRecovery);
        Assert.DoesNotContain("Resume the interrupted task", p.Prompt);
    }

    /// <summary>
    /// Copilot-specific: Copilot uses the persisted session name as the
    /// `--resume` handle, so the planner must pre-generate a slug on a fresh
    /// start and signal PersistSessionName so the runner writes it back to
    /// job.json. Other CLIs leave SessionName null until they capture a real
    /// UUID during streaming.
    /// </summary>
    [Fact]
    public void Start_Copilot_PreGeneratesSessionSlug()
    {
        var p = Plan(RunIntent.ManualStart, JobStates.Ready, sessionName: null,
                     cliType: CliTypes.Copilot, compat: PermissiveCompat);

        Assert.NotNull(p.PersistSessionName);
        Assert.NotNull(p.SessionToResume);
        Assert.Equal(p.PersistSessionName, p.SessionToResume);
        Assert.StartsWith("taskboard-fix-bug-", p.PersistSessionName);
    }

    [Fact]
    public void Start_Claude_DoesNotPreGenerateSessionSlug()
    {
        var p = Plan(RunIntent.ManualStart, JobStates.Ready, sessionName: null);
        Assert.Null(p.PersistSessionName);
        Assert.Null(p.SessionToResume);
    }

    // ===== Cross-intent invariants =====

    /// <summary>
    /// Whatever the intent, a valid captured session must always result in
    /// resume=true and the session being passed through. This is the property
    /// the user expects ("if my session is good, use it"); a planner change
    /// that breaks it on either path would be a regression.
    /// </summary>
    [Theory]
    [InlineData(RunIntent.ManualStart)]
    [InlineData(RunIntent.AutoPickup)]
    [InlineData(RunIntent.UserContinue)]
    public void AnyIntent_WithValidSession_ResumesIt(RunIntent intent)
    {
        var p = Plan(intent, JobStates.Progress, sessionName: ValidUuid, followup: "x");
        Assert.True(p.ResumeFlag);
        Assert.Equal(ValidUuid, p.SessionToResume);
    }

    /// <summary>
    /// THE invariant. No matter the intent, no matter the session state, the
    /// planner must always produce a runnable plan — never throw, never
    /// produce a "this is impossible" sentinel. The "no session yet — start it
    /// once" 400 was the violation of this property; this test enumerates the
    /// matrix that has to stay green.
    /// </summary>
    [Theory]
    [InlineData(RunIntent.ManualStart,  JobStates.Ready,    null)]
    [InlineData(RunIntent.ManualStart,  JobStates.Ready,    ValidUuid)]
    [InlineData(RunIntent.ManualStart,  JobStates.Ready,    PlaceholderSlug)]
    [InlineData(RunIntent.ManualStart,  JobStates.Ready,    ForeignSlug)]
    [InlineData(RunIntent.ManualStart,  JobStates.Progress, null)]
    [InlineData(RunIntent.ManualStart,  JobStates.Progress, ValidUuid)]
    [InlineData(RunIntent.ManualStart,  JobStates.Progress, PlaceholderSlug)]
    [InlineData(RunIntent.ManualStart,  JobStates.Progress, ForeignSlug)]
    [InlineData(RunIntent.AutoPickup,   JobStates.Ready,    null)]
    [InlineData(RunIntent.AutoPickup,   JobStates.Ready,    ValidUuid)]
    [InlineData(RunIntent.UserContinue, JobStates.Progress, null)]
    [InlineData(RunIntent.UserContinue, JobStates.Progress, ValidUuid)]
    [InlineData(RunIntent.UserContinue, JobStates.Progress, PlaceholderSlug)]
    [InlineData(RunIntent.UserContinue, JobStates.Progress, ForeignSlug)]
    [InlineData(RunIntent.UserContinue, JobStates.Review,   ValidUuid)]
    [InlineData(RunIntent.UserContinue, JobStates.Review,   null)]
    [InlineData(RunIntent.UserContinue, JobStates.Completed,ValidUuid)]
    [InlineData(RunIntent.UserContinue, JobStates.Completed,null)]
    public void Plan_AlwaysProducesRunnableOutput(RunIntent intent, string state, string? sessionName)
    {
        var p = Plan(intent, state, sessionName, followup: "go");

        Assert.NotNull(p);
        Assert.False(string.IsNullOrEmpty(p.Prompt), "Plan must always carry a prompt");
        Assert.Contains(p.EventKind, new[] { "start", "continue", "recovery" });
        // resume flag and session-to-resume must agree
        if (p.ResumeFlag) Assert.NotNull(p.SessionToResume);
        // a continuation event must reference the session it claims to resume
        if (p.EventKind == "continue") Assert.NotNull(p.EventInputSessionId);
        // a recovery event must explain itself
        if (p.EventKind == "recovery") Assert.False(string.IsNullOrEmpty(p.EventReason));
    }
}
