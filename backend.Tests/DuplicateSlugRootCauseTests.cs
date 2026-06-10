using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Root-cause coverage for the recurring duplicate-slug 409
/// (<c>A job folder named '&lt;slug&gt;' already exists in 7-archive</c>).
/// Confirmed cause: <c>ToSlug(title)</c> is deterministic and the old create
/// check only looked in the target lane, so re-spawning a same-title card
/// minted a second folder with the same slug in another lane; any later
/// cross-lane move then collided on the occupied name.
///
/// <list type="number">
/// <item>Layer 1 — create makes the slug globally unique across all lanes.</item>
/// <item>Acceptance — a create whose title collides with an archived shell
/// dodges the collision at birth, and the later archive move still succeeds.</item>
/// <item>Cleanup — the one-shot dedup sweep neutralises pre-existing stale
/// namesakes and is idempotent.</item>
/// </list>
/// </summary>
public class DuplicateSlugRootCauseTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public DuplicateSlugRootCauseTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "dup-slug-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void TwoCreatesWithSameTitle_YieldDistinctSlugs()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();

        var first = mutations.CreateJob(NewRequest("Same Title"));
        var second = mutations.CreateJob(NewRequest("Same Title"));

        Assert.Equal("same-title", first);
        Assert.Equal("same-title-2", second);
        Assert.NotEqual(first, second);

        var firstInfo = scanner.FindJob("same-title", _watchPath);
        var secondInfo = scanner.FindJob("same-title-2", _watchPath);
        Assert.NotNull(firstInfo);
        Assert.NotNull(secondInfo);
        Assert.Equal(TaskStates.Backlog, firstInfo.State);
        Assert.Equal(TaskStates.Backlog, secondInfo.State);
        Assert.Contains(Path.Combine("tasks", "000", "same-title"), firstInfo.FolderPath);
        Assert.Contains(Path.Combine("tasks", "000", "same-title-2"), secondInfo.FolderPath);
    }

    [Fact]
    public void CreateThenArchive_WhenSlugAlreadyParkedInArchive_DodgesCollisionAndArchives()
    {
        // The canonical incident shape: a 2 KB shell already sits in 7-archive
        // under the slug a new same-title card would slugify to.
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();
        SeedFolder(TaskStates.Archive, "dup-title", sizePadding: 0);

        // Layer 1: the new card avoids the parked slug at birth.
        var created = mutations.CreateJob(NewRequest("Dup Title", TaskStates.Ready));
        Assert.Equal("dup-title-2", created);

        // Archive-all of the real card no longer collides: it lands under its
        // own (already-unique) slug and the parked shell is left untouched.
        var outcome = machine.MoveJob("dup-title-2", TaskStates.Archive, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var archived = scanner.FindJob("dup-title-2", _watchPath);
        Assert.NotNull(archived);
        Assert.Equal(TaskStates.Archive, archived.State);
        Assert.Equal(outcome.NewFolderPath, archived.FolderPath);
        Assert.Contains(Path.Combine("tasks", "000", "dup-title"), archived.FolderPath);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Archive, "dup-title")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Archive, "dup-title-2")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "dup-title-2")));
    }

    [Fact]
    public void DedupeSlugFolders_KeepsRichestCopy_NeutralisesShell_AndIsIdempotent()
    {
        var (machine, scanner, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        // A real 6-completed task and a stale 7-archive shell share a slug.
        // The shell is deliberately smaller so the sweep keeps the real one.
        SeedFolder(TaskStates.Completed, "report", sizePadding: 4096);
        SeedFolder(TaskStates.Archive, "report", sizePadding: 0);

        var report = machine.DedupeSlugFolders();

        Assert.Equal(1, report.SlugsDeduped);
        Assert.Equal(1, report.FoldersNeutralised);
        var group = Assert.Single(report.Groups);
        Assert.Equal("report", group.Slug);
        Assert.Equal(TaskStates.Completed, group.KeptLane);

        // The richer copy stays live; the shell is renamed with a leading
        // underscore (scanner-ignored) in place, not deleted.
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Completed, "report")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Archive, "report")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Archive, "_report")));

        // Only the survivor is visible to the scanner now.
        var matches = scanner.ScanAllJobs().Where(j => j.Id == "report").ToList();
        Assert.Single(matches);
        Assert.Equal(TaskStates.Completed, matches[0].State);

        // Idempotent: a second run finds nothing left to do.
        var second = machine.DedupeSlugFolders();
        Assert.Equal(0, second.SlugsDeduped);
        Assert.Equal(0, second.FoldersNeutralised);
    }

    private CreateJobRequest NewRequest(string title, string? targetState = null) => new()
    {
        Title = title,
        WatchPath = _watchPath,
        Agent = "claude",
        TargetState = targetState
    };

    private void SeedFolder(string state, string slug, int sizePadding)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"}}");
        if (sizePadding > 0)
            File.WriteAllText(Path.Combine(dir, "status.md"), new string('x', sizePadding));
    }

    private (TaskStateMachine machine, TaskScannerService scanner, TaskMutationService mutations) Build()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var laneMutex = new LaneMutexRegistry(NullLogger<LaneMutexRegistry>.Instance);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance, laneMutex);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance,
            timeline: null,
            laneMutex: laneMutex);
        return (machine, scanner, mutations);
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
