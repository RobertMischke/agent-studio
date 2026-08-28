using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using AgentStudio.Pipeline;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2688: the two integration writers (the runner publishing to
/// origin/develop and the backend merging into its own local develop) can drift
/// apart. A diverged local develop used to abort integration outright, which
/// left the delivery unmerged and the card in the undifferentiated "pending"
/// bucket - no recovery action, no alarm, and the fleet re-delivering against
/// the same wall.
///
/// These tests drive real temp repositories with a real bare origin so the
/// convergence and the follow-up push are exercised end to end, not mocked.
/// </summary>
public sealed class IntegrationBranchConvergenceTests : IDisposable
{
    private readonly string _tempDir;

    public IntegrationBranchConvergenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "integration-convergence-" + Guid.NewGuid().ToString("N"));
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
    public async Task DivergedDevelop_ConvergesAndTheIntegrationPushFastForwards()
    {
        var (repo, origin) = SeedDivergedPair("converge", conflicting: false);
        var git = BuildGitService(("Fixture", repo));

        // Precondition: genuinely diverged - neither tip contains the other.
        Assert.NotEqual(0, RunGit(repo, "merge-base --is-ancestor origin/develop develop").Code);
        Assert.NotEqual(0, RunGit(repo, "merge-base --is-ancestor develop origin/develop").Code);

        var sync = git.SynchronizeIntegrationBranch(repo, "develop");

        Assert.Equal(IntegrationBranchSyncOutcome.Converged, sync.Outcome);
        Assert.True(sync.Success, sync.Error);

        // Convergence is additive: the local branch now contains origin, and the
        // work that was only on origin is preserved rather than overwritten.
        Assert.Equal(0, RunGit(repo, "merge-base --is-ancestor origin/develop develop").Code);
        Assert.True(File.Exists(Path.Combine(repo, "from-origin.txt")));
        Assert.True(File.Exists(Path.Combine(repo, "from-local.txt")));

        // The whole point: the push that follows integration now fast-forwards
        // instead of being rejected non-fast-forward.
        var push = await git.PushIntegrationBranchAsync(repo, "develop", CancellationToken.None, null);

        Assert.True(push.Success, $"{push.Status}: {push.Error}");
        Assert.Equal("pushed", push.Status);
        Assert.Equal(
            RunGit(repo, "rev-parse develop").Out.Trim(),
            RunGit(origin, "rev-parse refs/heads/develop").Out.Trim());
    }

    [Fact]
    public void ConflictingDivergence_StaysDivergedAndLeavesTheTreeUntouched()
    {
        var (repo, _) = SeedDivergedPair("conflict", conflicting: true);
        var git = BuildGitService(("Fixture", repo));
        var localTipBefore = RunGit(repo, "rev-parse develop").Out.Trim();
        var remoteTipBefore = RunGit(repo, "rev-parse origin/develop").Out.Trim();

        var sync = git.SynchronizeIntegrationBranch(repo, "develop");

        // A conflicting convergence needs an operator. It must not be forced,
        // and it must not leave a half-merged tree behind.
        Assert.Equal(IntegrationBranchSyncOutcome.Diverged, sync.Outcome);
        Assert.False(sync.Success);
        Assert.Equal(localTipBefore, RunGit(repo, "rev-parse develop").Out.Trim());
        Assert.Equal(remoteTipBefore, RunGit(repo, "rev-parse origin/develop").Out.Trim());
        Assert.True(
            string.IsNullOrWhiteSpace(RunGit(repo, "status --porcelain").Out),
            "the aborted convergence must leave a clean working tree");
    }

    /// <summary>
    /// A genuinely unconvergeable lineage must reach the card as a distinct,
    /// alarming failure - not as "pending", which carries no recovery action and
    /// therefore stalls forever.
    /// </summary>
    [Fact]
    public void PublicationBlockedVerdict_ClassifiesAsADistinctBlockedFailure()
    {
        var failure = AcceptedIntegrationFailurePolicy.Classify(
            PipelineStepStatus.Failed,
            verdict: "publication-blocked",
            reason: "Integration branch 'develop' diverged from origin - heal or recreate it "
                + "via project settings before accepting deliveries.",
            verdictSummary: null);

        Assert.NotNull(failure);
        Assert.Equal(AcceptedIntegrationFailureCodes.IntegrationPublicationBlocked, failure!.Code);
        Assert.Equal("Integration publication blocked", failure.Label);
        Assert.Contains("diverged from origin", failure.Reason);

        // Not a rebase-recoverable conflict: requeueing the card would only
        // re-deliver into the same wall, which is exactly the overnight loop.
        Assert.False(failure.RebaseRecoveryAvailable);

        // It must be distinguishable from the generic bucket.
        Assert.NotEqual(AcceptedIntegrationFailureCodes.IntegrationError, failure.Code);
    }

    /// <summary>
    /// The persisted code round-trips, so a card projected from a stored
    /// pipeline record keeps the blocked classification instead of decaying into
    /// the generic "Integration failed" copy.
    /// </summary>
    [Fact]
    public void PublicationBlockedCode_RoundTripsFromThePersistedRecord()
    {
        var failure = AcceptedIntegrationFailurePolicy.Classify(
            PipelineStepStatus.Failed,
            verdict: null,
            reason: "Integration into develop cannot reach origin; the delivery was not merged.",
            verdictSummary: null,
            persistedCode: AcceptedIntegrationFailureCodes.IntegrationPublicationBlocked);

        Assert.NotNull(failure);
        Assert.Equal(AcceptedIntegrationFailureCodes.IntegrationPublicationBlocked, failure!.Code);
        Assert.False(failure.RebaseRecoveryAvailable);
    }

    /// <summary>
    /// Builds a repo whose local <c>develop</c> and <c>origin/develop</c> have
    /// diverged, mirroring the two-writer topology. When
    /// <paramref name="conflicting"/> both sides edit the same line of the same
    /// file; otherwise each side adds its own file.
    /// </summary>
    private (string Repo, string Origin) SeedDivergedPair(string name, bool conflicting)
    {
        var origin = Path.Combine(_tempDir, name + "-origin.git");
        Directory.CreateDirectory(origin);
        RunGit(origin, "init -q --bare -b main");

        var repo = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        ConfigureIdentity(repo);
        RunGit(repo, $"remote add origin \"{origin}\"");
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "base\n");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        RunGit(repo, "branch develop");
        RunGit(repo, "push -q origin main develop");

        // The other writer advances origin/develop through a scratch clone.
        var other = Path.Combine(_tempDir, name + "-other");
        RunGit(_tempDir, $"clone -q \"{origin}\" \"{other}\"");
        ConfigureIdentity(other);
        RunGit(other, "checkout -q develop");
        if (conflicting)
            File.WriteAllText(Path.Combine(other, "shared.txt"), "origin side\n");
        else
            File.WriteAllText(Path.Combine(other, "from-origin.txt"), "origin work\n");
        RunGit(other, "add -A");
        RunGit(other, "commit -q -m \"origin-side delivery\"");
        RunGit(other, "push -q origin develop");

        // This writer advances its own local develop without seeing the above.
        RunGit(repo, "checkout -q develop");
        if (conflicting)
            File.WriteAllText(Path.Combine(repo, "shared.txt"), "local side\n");
        else
            File.WriteAllText(Path.Combine(repo, "from-local.txt"), "local work\n");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m \"local-side delivery\"");
        RunGit(repo, "fetch -q origin develop");

        return (repo, origin);
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
