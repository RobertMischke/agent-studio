

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the two cascade-containment rules added after the 2026-05-03
/// post-mortem (see <c>docs/research/auto-pickup-cascade-analysis-2026-05.md</c>):
///
/// <list type="number">
///   <item>Auto-pickup prefers a 3-progress job with a captured session id
///   over a 2-ready job. The pure helper that classifies "resumable" is
///   <see cref="ProjectRunner.HasResumableSession"/>.</item>
///   <item>The plan produced for AutoPickup against a 3-progress job that
///   carries a real UUID is a resume plan - so the runner only has to call
///   <see cref="RunPlanner.PlanRun"/> and the resume framing is automatic.</item>
/// </list>
/// </summary>
public class AutoPickupCascadeTests
{
    private static readonly System.Text.RegularExpressions.Regex UuidRegex =
        new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");
    private static bool ClaudeCompat(string? n) =>
        !string.IsNullOrWhiteSpace(n) && UuidRegex.IsMatch(n!);

    private const string ValidUuid = "a1b2c3d4-e5f6-4789-abcd-ef0123456789";

    // ===== HasResumableSession =====

    [Fact]
    public void HasResumableSession_NameAndChainEmpty_False()
    {
        var info = new TaskInfo { Id = "j", State = TaskStates.Progress };
        Assert.False(ProjectRunner.HasResumableSession(info));
    }

    [Fact]
    public void HasResumableSession_NameSet_True()
    {
        var info = new TaskInfo { Id = "j", State = TaskStates.Progress, SessionName = ValidUuid };
        Assert.True(ProjectRunner.HasResumableSession(info));
    }

    [Fact]
    public void HasResumableSession_OnlyChainHasEntry_True()
    {
        var info = new TaskInfo
        {
            Id = "j",
            State = TaskStates.Progress,
            SessionChain = new List<string> { ValidUuid }
        };
        Assert.True(ProjectRunner.HasResumableSession(info));
    }

    [Fact]
    public void HasResumableSession_ChainAllWhitespace_False()
    {
        var info = new TaskInfo
        {
            Id = "j",
            State = TaskStates.Progress,
            SessionChain = new List<string> { "", "   " }
        };
        Assert.False(ProjectRunner.HasResumableSession(info));
    }

    // ===== AutoPickup-against-Progress planner contract =====

    /// <summary>
    /// When the auto-pickup tick prefers a resumable progress job and feeds
    /// it back through <see cref="RunPlanner.PlanRun"/> with intent
    /// <see cref="RunIntent.AutoPickup"/>, the produced plan must be a
    /// <c>--resume</c> plan with the captured UUID. Without this, the
    /// progress-first feature would silently degrade to a fresh start and
    /// burn the agent's context.
    /// </summary>
    [Fact]
    public void AutoPickup_ProgressJobWithSession_ProducesResumePlan()
    {
        var plan = RunPlanner.PlanRun(
            RunIntent.AutoPickup,
            initialState: TaskStates.Progress,
            sessionName: ValidUuid,
            cliType: CliTypes.Claude,
            isCompatibleSessionName: ClaudeCompat,
            jobId: "fix-bug",
            promptPath: @"C:\jobs\fix-bug\prompt.md",
            jobFolder: @"C:\jobs\fix-bug",
            followupPrompt: null);

        Assert.True(plan.ResumeFlag);
        Assert.Equal(ValidUuid, plan.SessionToResume);
        Assert.Equal("continue", plan.EventKind);
        // 3-progress jobs stay in progress; only Ready / Review / Completed move.
        Assert.False(plan.MoveJobToProgress);
    }

    /// <summary>
    /// AutoPickup against a 3-progress job that lost its session id (no
    /// captured UUID anywhere) is the case the cascade-halt is supposed to
    /// catch in production. The planner here treats it as a fresh start,
    /// which is fine - the halt logic in <see cref="ProjectRunner"/> is
    /// what stops the cascade after several such attempts.
    /// </summary>
    [Fact]
    public void AutoPickup_ProgressJobWithoutSession_ProducesFreshStartPlan()
    {
        var plan = RunPlanner.PlanRun(
            RunIntent.AutoPickup,
            initialState: TaskStates.Progress,
            sessionName: null,
            cliType: CliTypes.Claude,
            isCompatibleSessionName: ClaudeCompat,
            jobId: "fix-bug",
            promptPath: @"C:\jobs\fix-bug\prompt.md",
            jobFolder: @"C:\jobs\fix-bug",
            followupPrompt: null);

        Assert.False(plan.ResumeFlag);
        Assert.Null(plan.SessionToResume);
        Assert.Equal("start", plan.EventKind);
    }

    // ===== Threshold sanity =====

    /// <summary>
    /// Three was chosen so a single transient dead-UUID + immediate retry
    /// (e.g. on cold cache) does not flap the runner; three in a row is
    /// structural and warrants user attention. Pinning the value here
    /// prevents an unintentional change to e.g. 1 making the runner halt
    /// on the very first hiccup.
    /// </summary>
    [Fact]
    public void AutoFailureHaltThreshold_Is3()
    {
        Assert.Equal(3, ProjectRunner.AutoFailureHaltThreshold);
    }

    /// <summary>
    /// Pinned for the same reason as <see cref="ProjectRunner.AutoFailureHaltThreshold"/>:
    /// the capture-fail circuit-breaker is the second line of defence after
    /// the recovery-marker write. If a structural bug feeds the same dead
    /// UUID back into the planner, this caps the loop at 3 spawns instead
    /// of the 31 the 2026-05-03 production trace recorded for arhciv.
    /// </summary>
    [Fact]
    public void CaptureFailHaltThreshold_Is3()
    {
        Assert.Equal(3, ProjectRunner.CaptureFailHaltThreshold);
    }

    // ===== ShouldMarkSessionChainRecovery =====
    //
    // The 2026-05-03 arhciv-besser-darzustellen loop: 31 consecutive
    // continues, all resumed against UUID dacb0f58-..., none captured a
    // session id back, and the recovery marker was NEVER appended to the
    // chain. Root cause: the runner read _activePlan directly instead of
    // a field snapshot, and a concurrent path (re-issue branch /
    // RunOrchestratorDecisionAsync / next tick re-entry) sometimes cleared
    // the field before this read. Pulling the decision into a pure helper
    // and feeding it the snapshot is the structural fix; these tests pin
    // the helper's truth table.

    [Fact]
    public void ShouldMarkSessionChainRecovery_NullPlan_False()
    {
        Assert.False(ProjectRunner.ShouldMarkSessionChainRecovery(null));
    }

    [Fact]
    public void ShouldMarkSessionChainRecovery_PlanWithoutResumeFlag_False()
    {
        var plan = MakePlan(resume: false, sessionToResume: null);
        Assert.False(ProjectRunner.ShouldMarkSessionChainRecovery(plan));
    }

    [Fact]
    public void ShouldMarkSessionChainRecovery_PlanWithResumeFlagButEmptySession_False()
    {
        var plan = MakePlan(resume: true, sessionToResume: "");
        Assert.False(ProjectRunner.ShouldMarkSessionChainRecovery(plan));
    }

    [Fact]
    public void ShouldMarkSessionChainRecovery_PlanResumesRealUuid_True()
    {
        var plan = MakePlan(resume: true, sessionToResume: ValidUuid);
        Assert.True(ProjectRunner.ShouldMarkSessionChainRecovery(plan));
    }

    /// <summary>
    /// The exact bug shape from the arhciv post-mortem: every continue's
    /// plan was a resume against the same captured UUID. Each capture-fail
    /// MUST request a recovery marker; without it the chain stays
    /// <c>[dacb0f58]</c> forever and the next pickup resumes the same
    /// dead id.
    /// </summary>
    [Fact]
    public void ShouldMarkSessionChainRecovery_ArhcivShape_AlwaysTrue()
    {
        const string capturedUuid = "dacb0f58-8508-43f4-99ba-93b0f7b6775c";
        var plan = MakePlan(resume: true, sessionToResume: capturedUuid);
        Assert.True(ProjectRunner.ShouldMarkSessionChainRecovery(plan));
    }

    private static RunPlan MakePlan(bool resume, string? sessionToResume) =>
        new RunPlan(
            PromptTemplate: null,
            PromptVariables: new Dictionary<string, string?>(),
            PromptOverride: "test",
            SessionToResume: sessionToResume,
            ResumeFlag: resume,
            EventKind: resume ? "continue" : "start",
            EventReason: null,
            EventInputSessionId: resume ? sessionToResume : null,
            MoveJobToProgress: false,
            MarkSessionChainRecovery: false,
            WriteCutMarker: false,
            CutMarkerReason: null,
            PersistSessionName: null,
            ClearStaleSessionName: false);
}
