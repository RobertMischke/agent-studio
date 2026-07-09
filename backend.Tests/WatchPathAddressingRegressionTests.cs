using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Regression coverage for AGT-1940 — "watchPath addressing teilkaputt".
/// Three operator-visible defects all traced to the same root cause: watch
/// paths were matched with raw string equality (ordinal case-sensitive in
/// <c>CreateJob</c>, <c>OrdinalIgnoreCase</c> in <c>FindJob</c>) instead of a
/// path-aware, OS-correct compare.
///
/// <list type="number">
/// <item><b>POST /api/tasks → 409.</b> A create that addressed the project
/// with a differently-spelled-but-identical path (trailing separator,
/// forward slashes) matched no watch entry and returned null → the endpoint
/// surfaced "Job already exists or invalid input".</item>
/// <item><b>PUT …/state &amp; DELETE → 404.</b> Both funnel through
/// <see cref="TaskScannerService.FindJob"/>; the same spelling drift meant an
/// existing card could not be located → 404 on move / delete.</item>
/// <item><b>GET filter → wrong project on Linux.</b> OrdinalIgnoreCase is
/// case-blind, but Linux filesystems are case-sensitive, so two projects
/// whose paths differed only in case collapsed to one and the filter returned
/// the wrong project's tasks.</item>
/// </list>
///
/// The fix (<see cref="AgentStudio.Shared.WatchPathComparison"/>) is unit-pinned
/// in <see cref="WatchPathComparisonTests"/>; this file asserts the fix at the
/// service boundary the endpoints actually call.
/// </summary>
public class WatchPathAddressingRegressionTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _demoPath;
    private const string Project = "demo";

    public WatchPathAddressingRegressionTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "wp-addr-" + Guid.NewGuid().ToString("N"));
        _demoPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_demoPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    // A different spelling of the same directory: swap to forward slashes
    // (a no-op on Linux, meaningful on Windows) and add a trailing separator.
    private static string VariantSpelling(string path)
        => path.Replace('\\', '/') + "/";

    [Fact]
    public void CreateJob_WithDifferentlySpelledWatchPath_Succeeds_NoSpurious409()
    {
        var (_, scanner, mutations, _) = Build(_demoPath);

        var variant = VariantSpelling(_demoPath);
        Assert.NotEqual(_demoPath, variant); // the two spellings are not byte-equal

        var id = mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Watch Path Bug",
            WatchPath = variant,
            Agent = "claude"
        });

        // Old behavior: FirstOrDefault(w => w.Path == variant) == null → null → 409.
        Assert.NotNull(id);

        // The card landed under the canonical project directory and is
        // addressable by the canonical watch path.
        var info = scanner.FindJob(id!, _demoPath);
        Assert.NotNull(info);
        Assert.Equal(_demoPath, info!.WatchPath);
    }

    [Fact]
    public void MoveAndDelete_WithDifferentlySpelledWatchPath_ResolveExistingCard_No404()
    {
        var (machine, scanner, mutations, _) = Build(_demoPath);
        machine.EnsureStateFoldersAndMigrate();

        // Create with the canonical spelling the way the board does.
        var id = mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Move Me",
            WatchPath = _demoPath,
            Agent = "claude"
        });
        Assert.NotNull(id);

        var variant = VariantSpelling(_demoPath);

        // PUT …/state path: MoveJob → FindJob(id, variant) must resolve.
        var move = machine.MoveJob(id!, TaskStates.Ready, variant);
        Assert.Equal(MoveJobStatus.Success, move.Status);
        Assert.Equal(TaskStates.Ready, scanner.FindJob(id!, _demoPath)!.State);

        // DELETE path: DeleteJob → FindJob(id, variant) must resolve.
        Assert.True(machine.DeleteJob(id!, variant));
        Assert.Null(scanner.FindJob(id!, _demoPath));
    }

    [Fact]
    public void CreateJob_AcceptsProjectIdInPlaceOfWatchPath()
    {
        // D1: address the target project by its stable PROJ-NNN id. The id is
        // resolved to the project's storage location, so create lands in the
        // right project without the caller hard-coding a filesystem path.
        var (_, scanner, mutations, registry) = Build(_demoPath);
        var project = registry.EnsureProjectForStorage(_demoPath, Project, "ws-test");
        Assert.StartsWith("PROJ-", project.Id);

        var id = mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Created By Project Id",
            WatchPath = project.Id,
            Agent = "claude"
        });

        Assert.NotNull(id);
        var info = scanner.FindJob(id!, _demoPath);
        Assert.NotNull(info);
        Assert.Equal(_demoPath, info!.WatchPath);
    }

    [Fact]
    public void CreateJob_UnknownProjectId_DoesNotSilentlyDefault()
    {
        // An unresolvable PROJ id must not fall through to the first project.
        var (_, _, mutations, _) = Build(_demoPath);
        var id = mutations.CreateJob(new CreateTaskRequest
        {
            Title = "Ghost Project",
            WatchPath = "PROJ-999",
            Agent = "claude"
        });
        Assert.Null(id); // → endpoint returns 409, not a card in the wrong project
    }

    [Fact]
    public void FindJob_DistinctProjects_ResolvesRequestedProject()
    {
        // Two distinct projects that both own a card with the same id. FindJob
        // filtered by watch path must return the card in the requested project.
        var otherPath = Path.Combine(_workspace, "projects", "other");
        Directory.CreateDirectory(otherPath);
        var (_, scanner, _, _) = Build(_demoPath, otherPath);

        SeedBacklogCard(_demoPath, "shared-id", "Demo copy");
        SeedBacklogCard(otherPath, "shared-id", "Other copy");

        Assert.Equal(_demoPath, scanner.FindJob("shared-id", _demoPath)!.WatchPath);
        Assert.Equal(otherPath, scanner.FindJob("shared-id", otherPath)!.WatchPath);
    }

    [SkippableFact]
    public void FindJob_CaseVariantProjects_KeepsProjectsDistinct_OnCaseSensitiveFilesystem()
    {
        Skip.If(OperatingSystem.IsWindows(),
            "Windows filesystems are case-insensitive: 'Demo' and 'demo' are the same directory.");

        // The exact Linux failure: two projects whose paths differ only in case.
        // OrdinalIgnoreCase treated them as one, so ?watchPath= returned the
        // wrong project's tasks. The OS-aware compare keeps them distinct.
        var upperPath = Path.Combine(_workspace, "projects", "Casey");
        var lowerPath = Path.Combine(_workspace, "projects", "casey");
        Directory.CreateDirectory(upperPath);
        Directory.CreateDirectory(lowerPath);
        var (_, scanner, _, _) = Build(upperPath, lowerPath);

        SeedBacklogCard(upperPath, "shared-id", "Upper");
        SeedBacklogCard(lowerPath, "shared-id", "Lower");

        var fromUpper = scanner.FindJob("shared-id", upperPath);
        var fromLower = scanner.FindJob("shared-id", lowerPath);

        Assert.Equal("Upper", fromUpper!.Title);
        Assert.Equal(upperPath, fromUpper.WatchPath);
        Assert.Equal("Lower", fromLower!.Title);
        Assert.Equal(lowerPath, fromLower.WatchPath);
    }

    private static void SeedBacklogCard(string watchPath, string id, string title)
    {
        var dir = Path.Combine(watchPath, TaskStates.Backlog, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{id}\",\"title\":\"{title}\",\"state\":\"{TaskStates.Backlog}\",\"order\":1,\"agent\":\"claude\"}}");
    }

    private (TaskStateMachine machine, TaskScannerService scanner, TaskMutationService mutations, ProjectRegistry registry) Build(params string[] watchPaths)
    {
        var config = BuildConfig(watchPaths);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var laneMutex = new LaneMutexRegistry(NullLogger<LaneMutexRegistry>.Instance);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance, laneMutex);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance,
            timeline: null,
            laneMutex: laneMutex);
        return (machine, scanner, mutations, registry);
    }

    private IConfiguration BuildConfig(string[] watchPaths)
    {
        var settings = new Dictionary<string, string?> { ["TaskRepository"] = _workspace };
        for (var i = 0; i < watchPaths.Length; i++)
        {
            settings[$"WatchPaths:{i}:Name"] = Path.GetFileName(watchPaths[i]);
            settings[$"WatchPaths:{i}:Path"] = watchPaths[i];
            settings[$"WatchPaths:{i}:RootPath"] = watchPaths[i];
        }
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
}
