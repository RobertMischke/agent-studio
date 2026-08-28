using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Real-git coverage for the topology that stalled the board: the shared
/// checkout's local integration branch had an unpublished delivery merge while
/// origin advanced from another writer, so the two lines diverged. A
/// <c>--ff-only</c> synchronization could never heal that, every later delivery
/// merge failed, and the publish was refused on every retry.
/// </summary>
public sealed class IntegrationBranchDivergenceTests : IDisposable
{
    private readonly string _tempDir;

    public IntegrationBranchDivergenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "integration-divergence-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task DivergedDevelop_IntegratesAndPublishesAsFastForward()
    {
        var (origin, repo) = SeedDualLineRepo("converge");
        var git = BuildGitService(("Fixture", repo));

        // The other writer advances origin/develop.
        var otherWriterSha = PushFromSecondClone(origin, "other-writer.txt", "published elsewhere");

        // This checkout carries an integration merge whose publish never landed.
        var strandedSha = MergeDeliveryLocally(repo, "task/stranded", "stranded.txt", "delivered but unpublished");

        // Precondition: genuinely diverged, and the publish is refused.
        RunGit(repo, "fetch -q origin develop");
        Assert.NotEqual(0, RunGit(repo, "merge-base --is-ancestor develop origin/develop").Code);
        Assert.NotEqual(0, RunGit(repo, "merge-base --is-ancestor origin/develop develop").Code);
        var refusedBefore = await git.PushIntegrationBranchAsync(repo, "develop");
        Assert.False(refusedBefore.Success);

        // Convergence: origin is merged back into the local branch. Neither the
        // published commit nor the stranded local merge is discarded.
        var synchronized = git.SynchronizeIntegrationBranch(repo, "develop");
        Assert.Equal(IntegrationBranchSyncOutcome.Reconciled, synchronized.Outcome);
        Assert.True(synchronized.Success);
        Assert.Equal(0, RunGit(repo, $"merge-base --is-ancestor {otherWriterSha} develop").Code);
        Assert.Equal(0, RunGit(repo, $"merge-base --is-ancestor {strandedSha} develop").Code);

        // A new delivery now integrates instead of failing on the divergence.
        var deliverySha = CommitOnBranch(repo, "task/next", "next.txt", "the next delivery");
        var merged = git.MergeBranchIntoIntegration(repo, "task/next", "develop");
        Assert.True(
            merged.Outcome is MergeIntoIntegrationOutcome.Merged
                or MergeIntoIntegrationOutcome.MergedAfterRebase,
            $"{merged.Outcome}: {merged.Error}");

        // And the publish is a fast-forward.
        var pushed = await git.PushIntegrationBranchAsync(repo, "develop");
        Assert.True(pushed.Success, pushed.Error);
        Assert.Equal("pushed", pushed.Status);

        var localTip = RunGit(repo, "rev-parse develop").Out.Trim();
        var publishedTip = RunGit(origin, "rev-parse refs/heads/develop").Out.Trim();
        Assert.Equal(localTip, publishedTip);
        foreach (var sha in new[] { otherWriterSha, strandedSha, deliverySha })
            Assert.Equal(0, RunGit(repo, $"merge-base --is-ancestor {sha} origin/develop").Code);
    }

    [Fact]
    public void ConflictingDivergence_StaysDivergedInsteadOfSilentlyRewritingEitherLine()
    {
        var (origin, repo) = SeedDualLineRepo("conflict");
        var git = BuildGitService(("Fixture", repo));

        // Both writers change the same line differently: convergence conflicts.
        PushFromSecondClone(origin, "contested.txt", "origin wins");
        var strandedSha = MergeDeliveryLocally(repo, "task/contested", "contested.txt", "local wins");
        var publishedTipBefore = RunGit(origin, "rev-parse refs/heads/develop").Out.Trim();

        var synchronized = git.SynchronizeIntegrationBranch(repo, "develop");

        Assert.Equal(IntegrationBranchSyncOutcome.Diverged, synchronized.Outcome);
        Assert.False(synchronized.Success);
        Assert.Contains("conflict", synchronized.Error, StringComparison.OrdinalIgnoreCase);

        // Nothing was rewritten, nothing was force-published, and no merge is
        // left half-applied in the shared checkout.
        Assert.Equal(0, RunGit(repo, $"merge-base --is-ancestor {strandedSha} develop").Code);
        Assert.Equal(publishedTipBefore, RunGit(origin, "rev-parse refs/heads/develop").Out.Trim());
        Assert.False(File.Exists(Path.Combine(repo, ".git", "MERGE_HEAD")));
        Assert.True(string.IsNullOrWhiteSpace(RunGit(repo, "status --porcelain").Out));
    }

    [Fact]
    public void PushBlockedStatuses_AreTerminalSoTheBackstopStopsRedrivingThem()
    {
        Assert.True(MergeIntoDevelopRunner.IsPushBlocked("remote-rejected"));
        Assert.True(MergeIntoDevelopRunner.IsPushBlocked("lineage-blocked"));
        Assert.True(MergeIntoDevelopRunner.IsPushBlocked("lineage-check-failed"));

        // A transient network fault must stay retryable.
        Assert.False(MergeIntoDevelopRunner.IsPushBlocked("failed"));
        Assert.False(MergeIntoDevelopRunner.IsPushBlocked("pushed"));
        Assert.False(MergeIntoDevelopRunner.IsPushBlocked(null));
    }

    /// <summary>Bare origin with main and develop, plus a clone standing in for the shared backend checkout.</summary>
    private (string Origin, string Repo) SeedDualLineRepo(string name)
    {
        var origin = Path.Combine(_tempDir, name + "-origin.git");
        var seed = Path.Combine(_tempDir, name + "-seed");
        var repo = Path.Combine(_tempDir, name + "-repo");

        RunGit(_tempDir, $"init --bare -q --initial-branch=main \"{origin}\"");
        Directory.CreateDirectory(seed);
        RunGit(seed, "init -q -b main");
        ConfigureIdentity(seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), "seed\n");
        RunGit(seed, "add -A");
        RunGit(seed, "commit -q -m seed");
        RunGit(seed, $"remote add origin \"{origin}\"");
        RunGit(seed, "push -q origin main");
        RunGit(seed, "checkout -q -b develop");
        RunGit(seed, "push -q origin develop");

        RunGit(_tempDir, $"clone -q \"{origin}\" \"{repo}\"");
        ConfigureIdentity(repo);
        RunGit(repo, "checkout -q develop");
        return (origin, repo);
    }

    /// <summary>Another writer publishes to origin/develop, so origin moves ahead.</summary>
    private string PushFromSecondClone(string origin, string file, string content)
    {
        var clone = Path.Combine(_tempDir, "writer-" + Guid.NewGuid().ToString("N")[..8]);
        RunGit(_tempDir, $"clone -q \"{origin}\" \"{clone}\"");
        ConfigureIdentity(clone);
        RunGit(clone, "checkout -q develop");
        File.WriteAllText(Path.Combine(clone, file), content + "\n");
        RunGit(clone, "add -A");
        RunGit(clone, $"commit -q -m \"feat: {content}\"");
        RunGit(clone, "push -q origin develop");
        return RunGit(clone, "rev-parse HEAD").Out.Trim();
    }

    /// <summary>A delivery merged into the local integration branch whose publish never landed.</summary>
    private static string MergeDeliveryLocally(string repo, string branch, string file, string content)
    {
        var sha = CommitOnBranch(repo, branch, file, content);
        RunGit(repo, "checkout -q develop");
        RunGit(repo, $"merge --no-ff --no-edit -m \"merge {branch}\" {branch}");
        return sha;
    }

    private static string CommitOnBranch(string repo, string branch, string file, string content)
    {
        RunGit(repo, "checkout -q develop");
        RunGit(repo, $"checkout -q -b {branch}");
        File.WriteAllText(Path.Combine(repo, file), content + "\n");
        RunGit(repo, "add -A");
        RunGit(repo, $"commit -q -m \"feat: {content}\"");
        var sha = RunGit(repo, "rev-parse HEAD").Out.Trim();
        RunGit(repo, "checkout -q develop");
        return sha;
    }

    private static void ConfigureIdentity(string repo)
    {
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
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
            // The fixture's origin is a bare repo; hosts with
            // safe.bareRepository=explicit otherwise refuse to operate on it.
            Arguments = "-c safe.bareRepository=all " + args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return (so, se, p.ExitCode);
    }
}
