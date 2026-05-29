using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Registry;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Backwards-compat tests for the <c>job.json</c> commit shape. The
/// task lifecycle moved from a singular <c>commit</c> object to an
/// ordered <c>commits</c> array (task: task-detail-worktree-isolation-and-multi-commit-support);
/// existing on-disk jobs that predate the change must still parse and
/// render correctly. These tests pin the
/// <see cref="JobScannerService.ParseJobJson"/> reader so the legacy
/// path can never be silently dropped.
/// </summary>
public class JobJsonSchemaTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public JobJsonSchemaTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-jobjson-schema-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Legacy_SingularCommit_Parses_AsSingleEntryChain()
    {
        var (scanner, _) = Build();
        SeedRawJob("legacy", "5-human-review", $$"""
            {
              "id": "legacy",
              "title": "Legacy single-commit",
              "state": "5-human-review",
              "agent": "claude",
              "createdAt": "2026-05-09T10:00:00Z",
              "commit": {
                "sha": "0123456789abcdef0123456789abcdef01234567",
                "shortSha": "0123456",
                "message": "feat: legacy",
                "filesChanged": 2,
                "files": ["a.txt", "b.txt"],
                "at": "2026-05-09T10:01:00Z"
              }
            }
            """);

        var info = scanner.FindJob("legacy", _watchPath);
        Assert.NotNull(info);
        Assert.NotNull(info!.Commit);
        Assert.Equal("0123456", info.Commit!.ShortSha);
        // Legacy single-commit shape is surfaced as commits = [commit] so
        // every consumer can read the same field regardless of disk shape.
        Assert.Single(info.Commits);
        Assert.Equal(info.Commit.Sha, info.Commits[0].Sha);
    }

    [Fact]
    public void New_CommitsArray_Parses_InOrder_AndExposesNewestAsSingularLegacy()
    {
        var (scanner, _) = Build();
        SeedRawJob("multi", "5-human-review", $$"""
            {
              "id": "multi",
              "title": "Multi-commit",
              "state": "5-human-review",
              "agent": "claude",
              "createdAt": "2026-05-09T10:00:00Z",
              "commits": [
                { "sha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "shortSha": "aaaaaaa",
                  "message": "feat: first",  "filesChanged": 1, "files": ["x"], "at": "2026-05-09T10:01:00Z" },
                { "sha": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "shortSha": "bbbbbbb",
                  "message": "fix: second", "filesChanged": 2, "files": ["y","z"], "at": "2026-05-09T10:05:00Z" },
                { "sha": "cccccccccccccccccccccccccccccccccccccccc", "shortSha": "ccccccc",
                  "message": "chore: third", "filesChanged": 3, "files": ["a","b","c"], "at": "2026-05-09T10:10:00Z" }
              ]
            }
            """);

        var info = scanner.FindJob("multi", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(3, info!.Commits.Count);
        Assert.Equal("aaaaaaa", info.Commits[0].ShortSha);
        Assert.Equal("bbbbbbb", info.Commits[1].ShortSha);
        Assert.Equal("ccccccc", info.Commits[2].ShortSha);
        // Singular Commit field reflects the newest entry so consumers
        // that have not migrated still see "the latest commit", not a
        // stale first one.
        Assert.NotNull(info.Commit);
        Assert.Equal("ccccccc", info.Commit!.ShortSha);
    }

    [Fact]
    public void New_CommitsArrayPlusLegacyCommit_PrefersTheArray()
    {
        // A migrated job will carry both shapes for one or two writes
        // (singular `commit` is kept in sync with the chain's tail). The
        // reader must prefer the array as the source of truth so the
        // chain never gets truncated.
        var (scanner, _) = Build();
        SeedRawJob("both", "6-completed", $$"""
            {
              "id": "both",
              "title": "Migrated",
              "state": "6-completed",
              "agent": "claude",
              "createdAt": "2026-05-09T10:00:00Z",
              "commits": [
                { "sha": "1111111111111111111111111111111111111111", "shortSha": "1111111",
                  "message": "feat: first", "filesChanged": 1, "files": ["x"], "at": "2026-05-09T10:01:00Z" },
                { "sha": "2222222222222222222222222222222222222222", "shortSha": "2222222",
                  "message": "fix: tail",   "filesChanged": 1, "files": ["y"], "at": "2026-05-09T10:05:00Z" }
              ],
              "commit": {
                "sha": "2222222222222222222222222222222222222222",
                "shortSha": "2222222",
                "message": "fix: tail",
                "filesChanged": 1,
                "files": ["y"],
                "at": "2026-05-09T10:05:00Z"
              }
            }
            """);

        var info = scanner.FindJob("both", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(2, info!.Commits.Count);
        Assert.Equal("1111111", info.Commits[0].ShortSha);
        Assert.Equal("2222222", info.Commits[1].ShortSha);
    }

    [Fact]
    public void NoCommitFields_ProducesEmptyChain()
    {
        var (scanner, _) = Build();
        SeedRawJob("nocommit", "2-ready", $$"""
            {
              "id": "nocommit",
              "title": "No commit yet",
              "state": "2-ready",
              "agent": "claude",
              "createdAt": "2026-05-09T10:00:00Z"
            }
            """);

        var info = scanner.FindJob("nocommit", _watchPath);
        Assert.NotNull(info);
        Assert.Null(info!.Commit);
        Assert.Empty(info.Commits);
    }

    private void SeedRawJob(string id, string lane, string jobJson)
    {
        var laneDir = Path.Combine(_watchPath, lane);
        Directory.CreateDirectory(laneDir);
        var jobDir = Path.Combine(laneDir, id);
        Directory.CreateDirectory(jobDir);
        File.WriteAllText(Path.Combine(jobDir, "job.json"), jobJson);
        File.WriteAllText(Path.Combine(jobDir, "prompt.md"), "fixture");
    }

    private (JobScannerService scanner, JobMutationService mutations) Build()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var mutations = new JobMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new JobChangeNotifier(NullLogger<JobChangeNotifier>.Instance), NullLogger<JobMutationService>.Instance);
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
