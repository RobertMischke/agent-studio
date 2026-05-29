using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Registry;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Pins the operator-override side of commit attribution: persisting the
/// rule-engine result, then excluding and re-including a commit through
/// <see cref="JobMutationService"/> (the API-only path - no direct
/// job.json writes). Mirrors the binding rules in ADR
/// "Commit-Attribution-Regel": an exclude moves a commit from
/// <c>commits</c> to <c>excludedCommits</c> with a manual marker; a
/// subsequent include restores it as <c>manual-include-after-exclude</c>;
/// an include of an unseen SHA is a <c>manual-add</c>.
/// </summary>
public class CommitAttributionMutationTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public CommitAttributionMutationTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-commit-attr-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void SetCommitAttribution_PersistsChain_AndExcludedArray()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("alpha");

        var attributed = new List<JobCommitInfo>
        {
            Commit("aaaaaaa", "feat: real work", CommitAttributionKinds.Automatic, 0.9),
        };
        var excluded = new List<JobExcludedCommitInfo>
        {
            new() { Sha = Pad("bbbbbbb"), ShortSha = "bbbbbbb", Reason = CommitExclusionReasons.CrashRecoveryOfOtherTask, Subject = "rescue other" },
        };

        Assert.True(mutations.SetCommitAttributionOnFolder(jobDir, attributed, excluded));

        var info = scanner.FindJob("alpha", _watchPath)!;
        var c = Assert.Single(info.Commits);
        Assert.Equal("aaaaaaa", c.ShortSha);
        Assert.Equal(CommitAttributionKinds.Automatic, c.Attribution);
        Assert.Equal(0.9, c.Confidence);

        var ex = Assert.Single(info.ExcludedCommits);
        Assert.Equal("bbbbbbb", ex.ShortSha);
        Assert.Equal(CommitExclusionReasons.CrashRecoveryOfOtherTask, ex.Reason);
    }

    [Fact]
    public void ExcludeThenInclude_RoundTrips_WithManualMarkers()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("beta");
        mutations.SetCommitAttributionOnFolder(
            jobDir,
            [Commit("ccccccc", "feat: work", CommitAttributionKinds.Automatic, 0.9)],
            []);

        // Operator excludes the attributed commit.
        Assert.True(mutations.ExcludeCommit("beta", Pad("ccccccc"), _watchPath));
        var afterExclude = scanner.FindJob("beta", _watchPath)!;
        Assert.Empty(afterExclude.Commits);
        var ex = Assert.Single(afterExclude.ExcludedCommits);
        Assert.True(ex.Manual);
        Assert.Equal(CommitExclusionReasons.ManualExclude, ex.Reason);

        // Operator re-includes it -> manual-include-after-exclude.
        Assert.True(mutations.IncludeCommit("beta", Pad("ccccccc"), candidate: null, _watchPath));
        var afterInclude = scanner.FindJob("beta", _watchPath)!;
        Assert.Empty(afterInclude.ExcludedCommits);
        var c = Assert.Single(afterInclude.Commits);
        Assert.Equal(CommitAttributionKinds.ManualIncludeAfterExclude, c.Attribution);
        Assert.Null(c.Confidence);
    }

    [Fact]
    public void IncludeUnseenCommit_IsManualAdd()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("gamma");
        mutations.SetCommitAttributionOnFolder(jobDir, [], []);

        var candidate = Commit("ddddddd", "feat: missed by rule", CommitAttributionKinds.Automatic, 0.9);
        Assert.True(mutations.IncludeCommit("gamma", Pad("ddddddd"), candidate, _watchPath));

        var info = scanner.FindJob("gamma", _watchPath)!;
        var c = Assert.Single(info.Commits);
        Assert.Equal(CommitAttributionKinds.ManualAdd, c.Attribution);
        Assert.Equal("ddddddd", c.ShortSha);
    }

    private static JobCommitInfo Commit(string shortSha, string message, string attribution, double? confidence) => new()
    {
        Sha = Pad(shortSha),
        ShortSha = shortSha,
        Message = message,
        FilesChanged = 1,
        Files = [],
        At = new DateTime(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc),
        Attribution = attribution,
        Confidence = confidence,
    };

    private string SeedJobFolder(string id)
    {
        var jobDir = Path.Combine(_watchPath, "4-auto-review", id);
        Directory.CreateDirectory(jobDir);
        File.WriteAllText(Path.Combine(jobDir, "job.json"),
            $$"""
              {
                "id": "{{id}}",
                "title": "Fixture",
                "state": "4-auto-review",
                "agent": "claude",
                "createdAt": "2026-05-20T08:00:00Z"
              }
              """);
        File.WriteAllText(Path.Combine(jobDir, "prompt.md"), "fixture");
        return jobDir;
    }

    private static string Pad(string s) => s.Length >= 40 ? s : s + new string('0', 40 - s.Length);

    private (JobScannerService scanner, JobMutationService mutations) Build()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var mutations = new JobMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new JobChangeNotifier(NullLogger<JobChangeNotifier>.Instance),
            NullLogger<JobMutationService>.Instance);
        return (scanner, mutations);
    }

    private IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
}
