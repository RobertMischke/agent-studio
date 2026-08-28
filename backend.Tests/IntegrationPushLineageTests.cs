using System.Diagnostics;

using AgentStudio.Tasks;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2688: deliveries integrated into the local <c>develop</c> could never
/// reach <c>origin/develop</c>, so the board stalled while the runner
/// re-delivered forever. Two distinct defects produced that:
///
/// <list type="number">
/// <item>A diverged local integration branch had no fast-forward path to origin
///   and was refused rather than reconciled, so every push stayed rejected.</item>
/// <item>Runner-owned task commits were auto-pushed at <c>main</c>, which the
///   lineage guard correctly refuses in any repository with a develop line, so
///   the commit never became durable anywhere and the periodic backstop retried
///   the identical doomed push every sweep.</item>
/// </list>
///
/// These tests drive real git against throwaway repositories so the fast-forward
/// and the refusal are observed, not mocked.
/// </summary>
public sealed class IntegrationPushLineageTests : IDisposable
{
    private readonly string _tempDir;

    public IntegrationPushLineageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "integration-push-lineage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ---------------------------------------------------------------------
    // Pure policy matrix: where does a runner-owned commit become durable?
    // ---------------------------------------------------------------------

    [Fact]
    public void Durability_NonMainTarget_KeepsConfiguredTarget()
    {
        var decision = RunnerCommitDurabilityPolicy.Decide(
            "develop", developLineExists: true, candidateIsPublishedDevelopTip: false, taskRef: "task/1");

        Assert.Equal(RunnerCommitDurabilityMode.SharedLine, decision.Mode);
        Assert.Equal("develop", decision.TargetRef);
    }

    [Fact]
    public void Durability_SingleLineRepo_StillPushesMain()
    {
        var decision = RunnerCommitDurabilityPolicy.Decide(
            "main", developLineExists: false, candidateIsPublishedDevelopTip: false, taskRef: "task/1");

        Assert.Equal(RunnerCommitDurabilityMode.SharedLine, decision.Mode);
        Assert.Equal("main", decision.TargetRef);
    }

    [Fact]
    public void Durability_PublishedDevelopTip_IsTheLegitimatePromotion()
    {
        var decision = RunnerCommitDurabilityPolicy.Decide(
            "main", developLineExists: true, candidateIsPublishedDevelopTip: true, taskRef: "task/1");

        Assert.Equal(RunnerCommitDurabilityMode.SharedLine, decision.Mode);
        Assert.Equal("main", decision.TargetRef);
    }

    [Fact]
    public void Durability_RawCommitInDualLineRepo_GoesToTaskRefInsteadOfMain()
    {
        var decision = RunnerCommitDurabilityPolicy.Decide(
            "main", developLineExists: true, candidateIsPublishedDevelopTip: false, taskRef: "task/42");

        // The whole point: it must NOT keep aiming at main, because that is
        // refused every single time and the commit never becomes durable.
        Assert.Equal(RunnerCommitDurabilityMode.TaskRef, decision.Mode);
        Assert.Equal("task/42", decision.TargetRef);
    }

    [Fact]
    public void Durability_NoTaskRefAvailable_IsBlockedNotSilentlyRetried()
    {
        var decision = RunnerCommitDurabilityPolicy.Decide(
            "main", developLineExists: true, candidateIsPublishedDevelopTip: false, taskRef: "  ");

        Assert.Equal(RunnerCommitDurabilityMode.Blocked, decision.Mode);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    // ---------------------------------------------------------------------
    // Diverged develop: integrate and push fast-forward.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DivergedDevelop_IsReconciled_AndPushFastForwards()
    {
        var (repo, bare) = SeedDualLineRepoWithRemote("diverged-ff");

        // Origin advances through the other integration writer.
        var other = CloneOf(bare, "other-writer-ff");
        RunGit(other, "checkout -q develop");
        File.WriteAllText(Path.Combine(other, "from-origin.txt"), "origin side");
        Commit(other, "feat: origin side");
        Assert.Equal(0, RunGit(other, "push -q origin develop").Code);
        var originSide = RunGit(other, "rev-parse HEAD").Out.Trim();

        // The backend meanwhile merged a delivery into its own local develop.
        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "from-local.txt"), "local side");
        Commit(repo, "feat: local delivery merged locally");
        var localSide = RunGit(repo, "rev-parse HEAD").Out.Trim();

        // Precondition: genuinely diverged - neither tip contains the other.
        Assert.NotEqual(0, RunGit(repo, $"merge-base --is-ancestor {localSide} refs/remotes/origin/develop").Code);

        var git = BuildGitService(("Fixture", repo));
        var sync = git.SynchronizeIntegrationBranch(repo, "develop");

        Assert.True(sync.Success, sync.Error);
        Assert.Equal(IntegrationBranchSyncOutcome.Reconciled, sync.Outcome);

        // Reconciled means origin's tip is now an ancestor of local develop,
        // which is exactly what makes the following push a fast-forward.
        Assert.Equal(0, RunGit(repo, $"merge-base --is-ancestor {originSide} develop").Code);

        var push = await git.PushIntegrationBranchAsync(repo, "develop");

        Assert.True(push.Success, push.Error);
        Assert.Equal("pushed", push.Status);

        // Both writers' work is now visible on origin/develop.
        var publishedTip = RunGit(_tempDir, $"--git-dir=\"{bare}\" rev-parse refs/heads/develop").Out.Trim();
        Assert.Equal(0, RunGit(repo, $"merge-base --is-ancestor {localSide} {publishedTip}").Code);
        Assert.Equal(0, RunGit(repo, $"merge-base --is-ancestor {originSide} {publishedTip}").Code);
    }

    [Fact]
    public void ConflictingDivergence_ReportsBlocked_AndLeavesNoMergeInProgress()
    {
        var (repo, bare) = SeedDualLineRepoWithRemote("diverged-conflict");

        // Both writers change the SAME file differently: unmergeable.
        var other = CloneOf(bare, "other-writer-conflict");
        RunGit(other, "checkout -q develop");
        File.WriteAllText(Path.Combine(other, "contested.txt"), "origin version");
        Commit(other, "feat: origin contested");
        Assert.Equal(0, RunGit(other, "push -q origin develop").Code);

        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "contested.txt"), "local version");
        Commit(repo, "feat: local contested");
        var localTipBefore = RunGit(repo, "rev-parse develop").Out.Trim();

        var git = BuildGitService(("Fixture", repo));
        var sync = git.SynchronizeIntegrationBranch(repo, "develop");

        // Reported as a genuine blocker, not swallowed and not left "pending".
        Assert.False(sync.Success);
        Assert.Equal(IntegrationBranchSyncOutcome.Diverged, sync.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(sync.Error));

        // Neither tip was rewritten and no half-finished merge is wedged in the
        // checkout, so the next sweep re-reports the same blocker instead of
        // corrupting the branch or spinning on a dirty tree.
        Assert.Equal(localTipBefore, RunGit(repo, "rev-parse develop").Out.Trim());
        Assert.False(File.Exists(Path.Combine(repo, ".git", "MERGE_HEAD")));
        Assert.True(string.IsNullOrWhiteSpace(RunGit(repo, "status --porcelain").Out));
    }

    // ---------------------------------------------------------------------
    // The auto-push redirect, end to end at the git layer.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task RawTaskCommit_IsPublishedOnTaskRef_WhileMainStaysRefused()
    {
        var (repo, bare) = SeedDualLineRepoWithRemote("raw-commit-redirect");

        RunGit(repo, "checkout -q develop");
        File.WriteAllText(Path.Combine(repo, "delivery.txt"), "raw delivery");
        Commit(repo, "feat: raw delivery");
        var rawCommit = RunGit(repo, "rev-parse HEAD").Out.Trim();

        var git = BuildGitService(("Fixture", repo));

        // The probe that drives the policy sees a develop line and a candidate
        // that is not the published tip.
        var probe = git.ProbeDevelopLine(repo, rawCommit);
        Assert.True(probe.Ok, probe.Error);
        Assert.True(probe.DevelopLineExists);
        Assert.False(probe.CandidateIsPublishedDevelopTip);

        var decision = RunnerCommitDurabilityPolicy.Decide(
            "main", probe.DevelopLineExists, probe.CandidateIsPublishedDevelopTip, "task/2688");
        Assert.Equal(RunnerCommitDurabilityMode.TaskRef, decision.Mode);

        // Aiming at main is refused - this is the 'lineage-blocked' the backend
        // logged hundreds of times overnight.
        var atMain = await git.PushShaAsync(rawCommit, repo, default, "main");
        Assert.False(atMain.Success);
        Assert.Equal("lineage-blocked", atMain.Status);

        // Aiming at the policy-chosen task ref makes the commit durable.
        var atTaskRef = await git.PushShaAsync(rawCommit, repo, default, decision.TargetRef);
        Assert.True(atTaskRef.Success, atTaskRef.Error);
        Assert.Equal(
            rawCommit,
            RunGit(_tempDir, $"--git-dir=\"{bare}\" rev-parse refs/heads/{decision.TargetRef}").Out.Trim());

        // And it converges: a second sweep is a no-op instead of another
        // warning, which is what stops the 15-minute backstop from looping.
        var again = await git.PushShaAsync(rawCommit, repo, default, decision.TargetRef);
        Assert.True(again.Success, again.Error);
        Assert.Equal("already-remote", again.Status);
    }

    // ---------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------

    private (string Repo, string Bare) SeedDualLineRepoWithRemote(string name)
    {
        var repo = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        Commit(repo, "seed");

        var bare = Path.Combine(_tempDir, name + "-remote.git");
        Assert.Equal(0, RunGit(_tempDir, $"init -q --bare \"{bare}\"").Code);
        Assert.Equal(0, RunGit(repo, $"remote add origin \"{bare}\"").Code);
        Assert.Equal(0, RunGit(repo, "push -q -u origin main").Code);
        Assert.Equal(0, RunGit(repo, "branch develop").Code);
        Assert.Equal(0, RunGit(repo, "push -q -u origin develop").Code);
        return (repo, bare);
    }

    private string CloneOf(string bare, string name)
    {
        var path = Path.Combine(_tempDir, name);
        Assert.Equal(0, RunGit(_tempDir, $"clone -q \"{bare}\" \"{path}\"").Code);
        RunGit(path, "config user.email other@example.com");
        RunGit(path, "config user.name other");
        return path;
    }

    private static void Commit(string cwd, string message)
    {
        RunGit(cwd, "add -A");
        RunGit(cwd, $"commit -q -m \"{message}\"");
    }

    private GitService BuildGitService(params (string Name, string RepoPath)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < entries.Length; i++)
        {
            dict[$"WatchPaths:{i}:Name"] = entries[i].Name;
            dict[$"WatchPaths:{i}:RootPath"] = entries[i].RepoPath;
            dict[$"WatchPaths:{i}:RepositoryPath"] = entries[i].RepoPath;
            dict[$"WatchPaths:{i}:Path"] = Path.Combine(entries[i].RepoPath, ".orchestrator", "jobs");
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private static (string Out, string Err, int Code) RunGit(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return (so, se, p.ExitCode);
    }
}
