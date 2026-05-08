using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Tags;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Acceptance tests for the backlog-lane + task-type + tag-registry slice.
/// Covers the load-bearing rules from the task spec:
///   - new jobs without a `targetState` land in `0-backlog`;
///   - existing `targetState=2-ready` shortcut still lands in Ready;
///   - `taskType` round-trips through create -> scan;
///   - the workspace tag registry seeds three default tags on first read;
///   - per-job tag mutation replaces-all and survives a registry deletion
///     (the soft-delete leaves the job's tag id in place; readers fall back
///     to a ghost chip).
/// </summary>
public class BacklogLaneAndTagsTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public BacklogLaneAndTagsTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-backlog-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void NewJob_WithoutTargetState_LandsInBacklog()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();

        var jobId = mutations.CreateJob(new CreateJobRequest
        {
            Id = "alpha",
            Title = "Alpha task",
            WatchPath = _watchPath,
            Agent = "claude"
        });

        Assert.Equal("alpha", jobId);
        var info = scanner.FindJob("alpha", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(JobStates.Backlog, info!.State);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Backlog, "alpha")));
        Assert.Equal(TaskTypes.Chore, info.TaskType);
    }

    [Fact]
    public void NewJob_WithExplicitTargetReady_StillLandsInReady()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();

        var jobId = mutations.CreateJob(new CreateJobRequest
        {
            Id = "beta",
            Title = "Beta task",
            WatchPath = _watchPath,
            Agent = "claude",
            TargetState = JobStates.Ready,
            TaskType = "bug",
            Tags = new List<string> { "architecture", "performance" }
        });

        Assert.Equal("beta", jobId);
        var info = scanner.FindJob("beta", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(JobStates.Ready, info!.State);
        Assert.Equal(TaskTypes.Bug, info.TaskType);
        Assert.Equal(new[] { "architecture", "performance" }, info.Tags.ToArray());
    }

    [Fact]
    public void BacklogState_IsListedFirstInJobStatesAll()
    {
        // Sort key invariant: 0-backlog must come before 1-preparation in
        // the canonical lane order so disk listings, kanban iteration, and
        // boot-time folder creation produce backlog at the leftmost
        // position.
        Assert.Equal(JobStates.Backlog, JobStates.All[0]);
        Assert.Equal(JobStates.Preparation, JobStates.All[1]);
    }

    [Fact]
    public void EnsureStateFoldersAndMigrate_CreatesBacklogFolder()
    {
        var (machine, _, _) = Build();
        machine.EnsureStateFoldersAndMigrate();
        Assert.True(Directory.Exists(Path.Combine(_watchPath, JobStates.Backlog)));
    }

    [Fact]
    public void TagRegistry_FirstRead_SeedsSevenDefaults()
    {
        var (_, _, _) = Build();
        var tags = NewTagRegistry();
        var entries = tags.GetAll();
        Assert.Equal(7, entries.Count);
        foreach (var id in new[] { "ui-ux", "performance", "quality", "architecture", "security", "docs", "observability" })
            Assert.Contains(entries, t => t.Id == id);
        // Every seed entry must carry a non-empty description so the UI can
        // surface the "wofür" line on hover and in the registry manager.
        Assert.All(entries, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        // The seed must be persisted so a second instance reads the same set
        // without re-seeding (idempotency on boot).
        Assert.True(File.Exists(Path.Combine(_workspace, "tags.json")));
    }

    [Fact]
    public void TagRegistry_SecondBoot_IsIdempotent_AndMergesNewSeedsOnly()
    {
        // First boot: seed file written with the full default set.
        var first = NewTagRegistry();
        var firstEntries = first.GetAll();
        Assert.Equal(7, firstEntries.Count);

        // Second boot: re-reading should produce exactly the same rows; no
        // duplicates appended on subsequent loads.
        var second = NewTagRegistry();
        var secondEntries = second.GetAll();
        Assert.Equal(7, secondEntries.Count);
        Assert.Equal(
            firstEntries.Select(t => t.Id).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            secondEntries.Select(t => t.Id).OrderBy(s => s, StringComparer.Ordinal).ToArray());

        // Custom user labels survive: simulate an older registry by writing
        // only a subset to disk, with a custom label/colour, and confirm the
        // missing seeds are merged in while the user's row is left untouched.
        var path = Path.Combine(_workspace, "tags.json");
        File.WriteAllText(path, """
            [
              { "id": "architecture", "label": "My Custom Arch", "color": "#abcdef", "description": "user note" }
            ]
            """);

        var third = NewTagRegistry();
        var thirdEntries = third.GetAll();
        Assert.Equal(7, thirdEntries.Count);
        var arch = thirdEntries.Single(t => t.Id == "architecture");
        Assert.Equal("My Custom Arch", arch.Label);
        Assert.Equal("#abcdef", arch.Color);
        Assert.Equal("user note", arch.Description);
        // The new seeds (ui-ux, security, docs, observability) were appended.
        foreach (var id in new[] { "ui-ux", "security", "docs", "observability" })
            Assert.Contains(thirdEntries, t => t.Id == id);
    }

    [Fact]
    public void TagRegistry_DeleteEntry_LeavesPerJobTagIdsIntact()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();
        var tags = NewTagRegistry();

        // Seed and create a job referencing the tag.
        tags.GetAll();
        mutations.CreateJob(new CreateJobRequest
        {
            Id = "gamma",
            Title = "Gamma",
            WatchPath = _watchPath,
            Agent = "claude",
            Tags = new List<string> { "architecture" }
        });

        // Soft delete: registry loses the entry, but the job still carries the id.
        Assert.True(tags.Delete("architecture"));
        Assert.False(tags.Exists("architecture"));

        var info = scanner.FindJob("gamma", _watchPath);
        Assert.NotNull(info);
        Assert.Equal(new[] { "architecture" }, info!.Tags.ToArray());
    }

    [Fact]
    public void SetJobTags_ReplacesAll_AndNormalizes()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();

        mutations.CreateJob(new CreateJobRequest
        {
            Id = "delta",
            Title = "Delta",
            WatchPath = _watchPath,
            Agent = "claude",
            Tags = new List<string> { "architecture" }
        });

        // Replace-all: the new list is the new full set, with normalization.
        // " Performance ", duplicate "performance", and "BAD WORDS!" all get
        // sanitized; empty results are dropped.
        Assert.True(mutations.SetJobTags(
            "delta",
            new[] { " Performance ", "performance", "BAD WORDS!", "" },
            _watchPath));

        var info = scanner.FindJob("delta", _watchPath);
        Assert.NotNull(info);
        // " Performance " → "performance"; duplicate dropped; "BAD WORDS!" → "bad-words".
        Assert.Equal(new[] { "performance", "bad-words" }, info!.Tags.ToArray());

        // Empty list clears tags.
        Assert.True(mutations.SetJobTags("delta", Array.Empty<string>(), _watchPath));
        info = scanner.FindJob("delta", _watchPath);
        Assert.NotNull(info);
        Assert.Empty(info!.Tags);
    }

    [Fact]
    public void TaskType_NormalizesUnknown_ToChore()
    {
        Assert.Equal(TaskTypes.Chore, TaskTypes.Normalize(null));
        Assert.Equal(TaskTypes.Chore, TaskTypes.Normalize(""));
        Assert.Equal(TaskTypes.Chore, TaskTypes.Normalize("garbage"));
        Assert.Equal(TaskTypes.Bug, TaskTypes.Normalize("Bug"));
        Assert.Equal(TaskTypes.Feature, TaskTypes.Normalize("feature"));
        // Migration: legacy "user-story" on disk silently maps to "feature".
        Assert.Equal(TaskTypes.Feature, TaskTypes.Normalize("user-story"));
        Assert.Equal(TaskTypes.Feature, TaskTypes.Normalize("User-Story"));
    }

    private (JobStateMachine machine, JobScannerService scanner, JobMutationService mutations) Build()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        var machine = new JobStateMachine(scanner, NullLogger<JobStateMachine>.Instance);
        var mutations = new JobMutationService(scanner, NullLogger<JobMutationService>.Instance);
        return (machine, scanner, mutations);
    }

    private TagRegistryService NewTagRegistry() =>
        new(NullLogger<TagRegistryService>.Instance, BuildConfig());

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
