using System.IO;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// ASS-1732 "always-worktree" invariant as pure logic (<see cref="WorktreeRunPolicy"/>):
/// every coding run is isolated in a task worktree - fresh pickup, reissue, resume,
/// or crash-recovery alike - while read-only (planning / research) and epic-planning
/// runs may run in the shared main checkout. The guard refuses a coding run that
/// resolved to the main checkout, and the resume gate forbids reusing a CLI session
/// born in a different working directory.
/// </summary>
public sealed class WorktreeRunPolicyTests
{
    // --- RequiresWorktree ---------------------------------------------------

    [Theory]
    [InlineData(TaskModes.Coding)]
    [InlineData(null)]          // unknown/empty -> coerced to coding
    [InlineData("something-else")]
    public void RequiresWorktree_CodingModes_True(string? mode)
        => Assert.True(WorktreeRunPolicy.RequiresWorktree(mode, isEpicPlanningRun: false));

    [Theory]
    [InlineData(TaskModes.Planning)]
    [InlineData(TaskModes.Research)]
    public void RequiresWorktree_ReadOnlyModes_False(string mode)
        => Assert.False(WorktreeRunPolicy.RequiresWorktree(mode, isEpicPlanningRun: false));

    [Fact]
    public void RequiresWorktree_EpicPlanningRun_False_EvenForCodingMode()
        => Assert.False(WorktreeRunPolicy.RequiresWorktree(TaskModes.Coding, isEpicPlanningRun: true));

    // --- IsMainCheckoutViolation -------------------------------------------

    [Fact]
    public void IsMainCheckoutViolation_CodingRunAtMainCheckout_True()
    {
        var main = Path.Combine(Path.GetTempPath(), "repo-main");
        Assert.True(WorktreeRunPolicy.IsMainCheckoutViolation(
            requiresWorktree: true, runWorkingDir: main, mainCheckoutRoot: main));
    }

    [Fact]
    public void IsMainCheckoutViolation_CodingRunInWorktree_False()
    {
        var main = Path.Combine(Path.GetTempPath(), "repo-main");
        var worktree = Path.Combine(Path.GetTempPath(), "wts", "task-1");
        Assert.False(WorktreeRunPolicy.IsMainCheckoutViolation(
            requiresWorktree: true, runWorkingDir: worktree, mainCheckoutRoot: main));
    }

    [Fact]
    public void IsMainCheckoutViolation_ReadOnlyRunAtMainCheckout_False()
    {
        // Read-only runs (requiresWorktree == false) legitimately run in-place.
        var main = Path.Combine(Path.GetTempPath(), "repo-main");
        Assert.False(WorktreeRunPolicy.IsMainCheckoutViolation(
            requiresWorktree: false, runWorkingDir: main, mainCheckoutRoot: main));
    }

    [Theory]
    [InlineData(null, "C:/repo")]
    [InlineData("C:/repo", null)]
    [InlineData("   ", "C:/repo")]
    [InlineData("C:/repo", "")]
    public void IsMainCheckoutViolation_MissingPaths_False(string? runDir, string? mainRoot)
        => Assert.False(WorktreeRunPolicy.IsMainCheckoutViolation(true, runDir, mainRoot));

    [Fact]
    public void IsMainCheckoutViolation_TrailingSeparatorDifference_StillTrips()
    {
        // The same directory written with vs. without a trailing separator must
        // still count as the main checkout (defense in depth against path drift).
        var main = Path.Combine(Path.GetTempPath(), "repo-main");
        Assert.True(WorktreeRunPolicy.IsMainCheckoutViolation(
            requiresWorktree: true,
            runWorkingDir: main + Path.DirectorySeparatorChar,
            mainCheckoutRoot: main));
    }

    // --- CanResumeSession ---------------------------------------------------

    [Fact]
    public void CanResumeSession_NonWorktreeRun_True()
        => Assert.True(WorktreeRunPolicy.CanResumeSession(isWorktreeRun: false, worktreeReused: false));

    [Fact]
    public void CanResumeSession_WorktreeRun_Reused_True()
        => Assert.True(WorktreeRunPolicy.CanResumeSession(isWorktreeRun: true, worktreeReused: true));

    [Fact]
    public void CanResumeSession_WorktreeRun_FreshCut_False()
    {
        // A freshly-cut worktree means any prior session lived elsewhere (the old
        // main checkout) and would hang on --resume; force a fresh session.
        Assert.False(WorktreeRunPolicy.CanResumeSession(isWorktreeRun: true, worktreeReused: false));
    }

    // --- PathsEqual ---------------------------------------------------------

    [Fact]
    public void PathsEqual_SamePath_True()
    {
        var p = Path.Combine(Path.GetTempPath(), "x", "y");
        Assert.True(WorktreeRunPolicy.PathsEqual(p, p));
    }

    [Fact]
    public void PathsEqual_TrailingSeparator_NormalizedEqual()
    {
        var p = Path.Combine(Path.GetTempPath(), "x", "y");
        Assert.True(WorktreeRunPolicy.PathsEqual(p, p + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void PathsEqual_DifferentPaths_False()
    {
        var a = Path.Combine(Path.GetTempPath(), "x", "y");
        var b = Path.Combine(Path.GetTempPath(), "x", "z");
        Assert.False(WorktreeRunPolicy.PathsEqual(a, b));
    }

    [Fact]
    public void PathsEqual_CaseInsensitiveOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = Path.Combine(Path.GetTempPath(), "Repo", "Main");
        Assert.True(WorktreeRunPolicy.PathsEqual(p.ToUpperInvariant(), p.ToLowerInvariant()));
    }
}
