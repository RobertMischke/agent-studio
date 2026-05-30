using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Registry;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Tests for the task -&gt; commit binding rules. Tasks regularly produce
/// more than one commit across iterations (continue-mode follow-up,
/// crash-recovery commit + repair, operator-driven steers); each new
/// commit lands on the task's <c>commits</c> chain via
/// <see cref="TaskMutationService.AppendJobCommitOnFolder"/>.
///
/// <para>
/// The rules these tests pin:
/// </para>
/// <list type="bullet">
/// <item>Appending a commit to a job with no prior commits seeds the
///   chain with one entry and updates the legacy singular <c>commit</c>
///   field.</item>
/// <item>Appending a second commit grows the chain to two entries in
///   chronological order; singular <c>commit</c> moves to the newest.</item>
/// <item>A commit that shares its SHA with an existing entry replaces
///   that entry in place rather than bloating the chain - re-stamping
///   the same SHA refreshes metadata (file count, message) without
///   re-ordering.</item>
/// <item>A legacy <c>job.json</c> that carries only the singular
///   <c>commit</c> field gets migrated to the array on the first
///   append, with the legacy entry preserved as the chain head.</item>
/// </list>
/// </summary>
public class TaskCommitBindingTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public TaskCommitBindingTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-task-commit-bind-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void AppendCommit_OnEmptyChain_SeedsChain_AndUpdatesLegacySingular()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("alpha", "3-progress", legacyCommit: null);

        var first = MakeCommit("aaaaaaa", "feat: first", filesChanged: 2, atIso: "2026-05-09T10:00:00Z");
        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, first));

        var info = scanner.FindJob("alpha", _watchPath);
        Assert.NotNull(info);
        Assert.Single(info!.Commits);
        Assert.Equal("aaaaaaa", info.Commits[0].ShortSha);
        Assert.NotNull(info.Commit);
        Assert.Equal("aaaaaaa", info.Commit!.ShortSha);
    }

    [Fact]
    public void AppendCommit_TwiceWithDifferentShas_GrowsChain_LegacyTracksNewest()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("beta", "3-progress", legacyCommit: null);

        var first  = MakeCommit("aaaaaaa", "feat: first",  filesChanged: 1, atIso: "2026-05-09T10:00:00Z");
        var second = MakeCommit("bbbbbbb", "fix: follow-up", filesChanged: 3, atIso: "2026-05-09T10:30:00Z");

        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, first));
        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, second));

        var info = scanner.FindJob("beta", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(2, info!.Commits.Count);
        Assert.Equal("aaaaaaa", info.Commits[0].ShortSha);
        Assert.Equal("bbbbbbb", info.Commits[1].ShortSha);
        Assert.NotNull(info.Commit);
        Assert.Equal("bbbbbbb", info.Commit!.ShortSha);
    }

    [Fact]
    public void AppendCommit_WithExistingSha_ReplacesInPlace_DoesNotDuplicate()
    {
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("gamma", "3-progress", legacyCommit: null);

        var first  = MakeCommit("aaaaaaa", "feat: first",  filesChanged: 1, atIso: "2026-05-09T10:00:00Z");
        var second = MakeCommit("bbbbbbb", "fix: follow",  filesChanged: 1, atIso: "2026-05-09T10:10:00Z");
        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, first));
        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, second));

        // Re-stamp the second commit with refreshed metadata. Some flows
        // re-derive the file list from `git show --name-status` after the
        // initial stamp; the chain must show the latest numbers without
        // adding a duplicate row.
        var refreshed = MakeCommit("bbbbbbb", "fix: follow (refreshed)", filesChanged: 5, atIso: "2026-05-09T10:11:00Z");
        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, refreshed));

        var info = scanner.FindJob("gamma", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(2, info!.Commits.Count);
        Assert.Equal(5, info.Commits[1].FilesChanged);
        Assert.Equal("fix: follow (refreshed)", info.Commits[1].Message);
    }

    [Fact]
    public void AppendCommit_OverLegacySingularOnly_MigratesToChain_PreservingLegacyAsHead()
    {
        // A job with the pre-migration shape (only the singular `commit`
        // object on disk, no `commits` array). The first append after the
        // migration must preserve the legacy entry as the chain head and
        // grow from there. Anything else would drop history the moment a
        // continuation lands.
        var legacy = MakeCommit("9999999", "chore: pre-migration", filesChanged: 4, atIso: "2026-05-09T09:00:00Z");
        var (scanner, mutations) = Build();
        var jobDir = SeedJobFolder("delta", "3-progress", legacyCommit: legacy);

        var followup = MakeCommit("ddddddd", "feat: post-migration", filesChanged: 2, atIso: "2026-05-09T10:00:00Z");
        Assert.True(mutations.AppendJobCommitOnFolder(jobDir, followup));

        var info = scanner.FindJob("delta", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(2, info!.Commits.Count);
        Assert.Equal("9999999", info.Commits[0].ShortSha);
        Assert.Equal("ddddddd", info.Commits[1].ShortSha);
        Assert.NotNull(info.Commit);
        Assert.Equal("ddddddd", info.Commit!.ShortSha);
    }

    [Fact]
    public void AppendJobCommit_FailsCleanly_OnMissingFolder()
    {
        var (_, mutations) = Build();
        var nonexistent = Path.Combine(_workspace, "nope");
        var commit = MakeCommit("aaaaaaa", "feat: x", filesChanged: 1, atIso: "2026-05-09T10:00:00Z");
        Assert.False(mutations.AppendJobCommitOnFolder(nonexistent, commit));
    }

    private string SeedJobFolder(string id, string lane, JobCommitInfo? legacyCommit)
    {
        var laneDir = Path.Combine(_watchPath, lane);
        Directory.CreateDirectory(laneDir);
        var jobDir = Path.Combine(laneDir, id);
        Directory.CreateDirectory(jobDir);
        var jobJson = legacyCommit == null
            ? $$"""
                {
                  "id": "{{id}}",
                  "title": "Fixture",
                  "state": "{{lane}}",
                  "agent": "claude",
                  "createdAt": "2026-05-09T08:00:00Z"
                }
                """
            : $$"""
                {
                  "id": "{{id}}",
                  "title": "Fixture",
                  "state": "{{lane}}",
                  "agent": "claude",
                  "createdAt": "2026-05-09T08:00:00Z",
                  "commit": {
                    "sha": "{{Pad(legacyCommit.Sha)}}",
                    "shortSha": "{{legacyCommit.ShortSha}}",
                    "message": "{{legacyCommit.Message}}",
                    "filesChanged": {{legacyCommit.FilesChanged}},
                    "files": [],
                    "at": "{{legacyCommit.At:o}}"
                  }
                }
                """;
        File.WriteAllText(Path.Combine(jobDir, "job.json"), jobJson);
        File.WriteAllText(Path.Combine(jobDir, "prompt.md"), "fixture");
        return jobDir;
    }

    private static JobCommitInfo MakeCommit(string shortSha, string message, int filesChanged, string atIso)
    {
        var fullSha = Pad(shortSha);
        return new JobCommitInfo
        {
            Sha = fullSha,
            ShortSha = shortSha,
            Message = message,
            FilesChanged = filesChanged,
            Files = new List<string>(),
            At = DateTime.Parse(atIso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
        };
    }

    private static string Pad(string s) => s.Length >= 40 ? s : s + new string('0', 40 - s.Length);

    private (TaskScannerService scanner, TaskMutationService mutations) Build()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
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
