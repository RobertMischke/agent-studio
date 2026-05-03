using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

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
        var info = new JobInfo { Id = "j", State = JobStates.Progress };
        Assert.False(ProjectRunner.HasResumableSession(info));
    }

    [Fact]
    public void HasResumableSession_NameSet_True()
    {
        var info = new JobInfo { Id = "j", State = JobStates.Progress, SessionName = ValidUuid };
        Assert.True(ProjectRunner.HasResumableSession(info));
    }

    [Fact]
    public void HasResumableSession_OnlyChainHasEntry_True()
    {
        var info = new JobInfo
        {
            Id = "j",
            State = JobStates.Progress,
            SessionChain = new List<string> { ValidUuid }
        };
        Assert.True(ProjectRunner.HasResumableSession(info));
    }

    [Fact]
    public void HasResumableSession_ChainAllWhitespace_False()
    {
        var info = new JobInfo
        {
            Id = "j",
            State = JobStates.Progress,
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
            initialState: JobStates.Progress,
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
            initialState: JobStates.Progress,
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
}
