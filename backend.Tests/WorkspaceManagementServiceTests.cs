using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Configuration;
using OrchestratorApi.Services.Jobs;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the create / delete contract for
/// <see cref="WorkspaceManagementService"/>: the happy path round-trips
/// through <c>appsettings.Local.json</c>, validation refuses bad input,
/// uniqueness is enforced case-insensitively, and a non-empty workspace
/// cannot be removed by mistake. Each test sets up its own scratch
/// content root + TaskRepository so the live <c>appsettings.Local.json</c>
/// of the running checkout is never touched.
/// </summary>
public class WorkspaceManagementServiceTests : IDisposable
{
    private readonly string _contentRoot;
    private readonly string _taskRepository;

    public WorkspaceManagementServiceTests()
    {
        var prefix = "wsmgmt-" + Guid.NewGuid().ToString("N")[..8];
        _contentRoot = Path.Combine(Path.GetTempPath(), prefix + "-cr");
        _taskRepository = Path.Combine(Path.GetTempPath(), prefix + "-tr");
        Directory.CreateDirectory(_contentRoot);
        Directory.CreateDirectory(_taskRepository);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _contentRoot, _taskRepository })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private (WorkspaceManagementService svc, IConfigurationRoot config) Build(
        IEnumerable<(string Name, string Path)>? seed = null)
    {
        var localPath = Path.Combine(_contentRoot, "appsettings.Local.json");
        if (seed is not null)
        {
            var seedArray = seed.Select(s => new Dictionary<string, object?>
            {
                ["Name"] = s.Name,
                ["Path"] = s.Path,
            }).ToArray();
            var root = new Dictionary<string, object?>
            {
                ["TaskRepository"] = _taskRepository,
                ["WatchPaths"] = seedArray,
            };
            File.WriteAllText(localPath, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            File.WriteAllText(localPath, JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["TaskRepository"] = _taskRepository,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        var config = new ConfigurationBuilder()
            .AddJsonFile(localPath, optional: false, reloadOnChange: false)
            .Build();

        var env = new TestHostEnvironment(_contentRoot);
        var scanner = new TaskScannerService(
            config,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var svc = new WorkspaceManagementService(
            config, env, scanner,
            NullLogger<WorkspaceManagementService>.Instance);
        return (svc, config);
    }

    [Fact]
    public void Create_HappyPath_WritesEntryAndCreatesFolder()
    {
        var (svc, config) = Build();

        var result = svc.Create("My New Workspace");

        Assert.Equal(WorkspaceManagementOutcome.Created, result.Outcome);
        Assert.NotNull(result.Entry);
        Assert.Equal("My New Workspace", result.Entry!.Name);
        var expectedPath = Path.GetFullPath(Path.Combine(_taskRepository, "projects", "my-new-workspace"));
        Assert.Equal(expectedPath, result.Entry.Path);
        Assert.True(Directory.Exists(expectedPath));

        // Persisted to appsettings.Local.json
        var written = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_contentRoot, "appsettings.Local.json"))).RootElement;
        var watchPaths = written.GetProperty("WatchPaths");
        Assert.Equal(1, watchPaths.GetArrayLength());
        Assert.Equal("My New Workspace", watchPaths[0].GetProperty("Name").GetString());
        Assert.Equal(expectedPath, watchPaths[0].GetProperty("Path").GetString());

        // Reload is observable via the live config root.
        Assert.Equal("My New Workspace",
            config.GetSection("WatchPaths").GetChildren().First().GetSection("Name").Value);
    }

    [Fact]
    public void Create_PreservesUnrelatedKeysAndExistingWatchPaths()
    {
        var existing = JsonSerializer.Serialize(new
        {
            TaskRepository = _taskRepository,
            Environment = new { IsDev = true },
            WatchPaths = new[]
            {
                new { Name = "Existing", Path = Path.Combine(_taskRepository, "projects", "existing") }
            }
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(_contentRoot, "appsettings.Local.json"), existing);

        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(_contentRoot, "appsettings.Local.json"), optional: false, reloadOnChange: false)
            .Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var svc = new WorkspaceManagementService(config, new TestHostEnvironment(_contentRoot), scanner,
            NullLogger<WorkspaceManagementService>.Instance);

        var result = svc.Create("Another");
        Assert.Equal(WorkspaceManagementOutcome.Created, result.Outcome);

        var written = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_contentRoot, "appsettings.Local.json"))).RootElement;
        Assert.True(written.GetProperty("Environment").GetProperty("IsDev").GetBoolean());
        Assert.Equal(2, written.GetProperty("WatchPaths").GetArrayLength());
        Assert.Equal("Existing", written.GetProperty("WatchPaths")[0].GetProperty("Name").GetString());
        Assert.Equal("Another", written.GetProperty("WatchPaths")[1].GetProperty("Name").GetString());
    }

    [Fact]
    public void Create_RejectsEmptyName()
    {
        var (svc, _) = Build();
        Assert.Equal(WorkspaceManagementOutcome.BadRequest, svc.Create("").Outcome);
        Assert.Equal(WorkspaceManagementOutcome.BadRequest, svc.Create("   ").Outcome);
        Assert.Equal(WorkspaceManagementOutcome.BadRequest, svc.Create(null).Outcome);
    }

    [Fact]
    public void Create_RejectsTooLongName()
    {
        var (svc, _) = Build();
        var tooLong = new string('a', WorkspaceManagementService.MaxNameLength + 1);
        var result = svc.Create(tooLong);
        Assert.Equal(WorkspaceManagementOutcome.BadRequest, result.Outcome);
        Assert.Contains("64", result.Error!);
    }

    [Fact]
    public void Create_RejectsNameThatSlugsToEmpty()
    {
        var (svc, _) = Build();
        var result = svc.Create("###");
        Assert.Equal(WorkspaceManagementOutcome.BadRequest, result.Outcome);
        Assert.Contains("letter or digit", result.Error!);
    }

    [Fact]
    public void Create_ConflictOnDuplicateName_CaseInsensitive()
    {
        var (svc, _) = Build();
        Assert.Equal(WorkspaceManagementOutcome.Created, svc.Create("Alpha").Outcome);
        Assert.Equal(WorkspaceManagementOutcome.Conflict, svc.Create("Alpha").Outcome);
        Assert.Equal(WorkspaceManagementOutcome.Conflict, svc.Create("alpha").Outcome);
        Assert.Equal(WorkspaceManagementOutcome.Conflict, svc.Create("ALPHA").Outcome);
    }

    [Fact]
    public void Create_ConflictOnSlugCollision()
    {
        var (svc, _) = Build();
        Assert.Equal(WorkspaceManagementOutcome.Created, svc.Create("Hello World").Outcome);
        // Different display name but same slug.
        var collision = svc.Create("hello-world");
        Assert.Equal(WorkspaceManagementOutcome.Conflict, collision.Outcome);
        Assert.Contains("already maps to the folder", collision.Error!);
    }

    [Fact]
    public void Create_RejectedWhenTaskRepositoryMissing()
    {
        // Build without seeding TaskRepository.
        File.WriteAllText(Path.Combine(_contentRoot, "appsettings.Local.json"),
            JsonSerializer.Serialize(new Dictionary<string, object?>()));
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(_contentRoot, "appsettings.Local.json"), optional: false, reloadOnChange: false)
            .Build();
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var svc = new WorkspaceManagementService(config, new TestHostEnvironment(_contentRoot), scanner,
            NullLogger<WorkspaceManagementService>.Instance);

        var result = svc.Create("Whatever");
        Assert.Equal(WorkspaceManagementOutcome.BadRequest, result.Outcome);
        Assert.Contains("TaskRepository", result.Error!);
    }

    [Fact]
    public void Delete_HappyPath_RemovesEntryAndLeavesFolderOnDisk()
    {
        var (svc, _) = Build();
        var created = svc.Create("Disposable");
        Assert.Equal(WorkspaceManagementOutcome.Created, created.Outcome);
        var path = created.Entry!.Path;

        var deleted = svc.Delete("Disposable");
        Assert.Equal(WorkspaceManagementOutcome.Ok, deleted.Outcome);
        Assert.Equal("Disposable", deleted.Entry!.Name);
        // Folder stays on disk (re-create-with-same-name reversibility).
        Assert.True(Directory.Exists(path));

        var written = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_contentRoot, "appsettings.Local.json"))).RootElement;
        if (written.TryGetProperty("WatchPaths", out var watchPaths))
        {
            Assert.Equal(0, watchPaths.GetArrayLength());
        }
    }

    [Fact]
    public void Delete_NotFound_ReturnsNotFound()
    {
        var (svc, _) = Build();
        var result = svc.Delete("missing");
        Assert.Equal(WorkspaceManagementOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public void Delete_RejectsEmptyName()
    {
        var (svc, _) = Build();
        Assert.Equal(WorkspaceManagementOutcome.BadRequest, svc.Delete("").Outcome);
        Assert.Equal(WorkspaceManagementOutcome.BadRequest, svc.Delete(null).Outcome);
    }

    [Fact]
    public void Delete_BlocksWhenWorkspaceContainsJobs()
    {
        var (svc, _) = Build();
        var created = svc.Create("Busy");
        Assert.Equal(WorkspaceManagementOutcome.Created, created.Outcome);

        // Drop a fake job folder under one of the lanes to simulate
        // non-empty workspace (no TaskMutationService needed for this
        // boundary check; the scanner-style walk only requires lane
        // dir + slug dir + job.json).
        var fakeJobDir = Path.Combine(created.Entry!.Path, "2-ready", "fake-job");
        Directory.CreateDirectory(fakeJobDir);
        File.WriteAllText(Path.Combine(fakeJobDir, "job.json"), "{\"id\":\"fake-job\"}");

        var result = svc.Delete("Busy");
        Assert.Equal(WorkspaceManagementOutcome.Conflict, result.Outcome);
        Assert.Equal(1, result.TaskCount);
        Assert.Contains("still contains 1 job", result.Error!);
    }

    [Fact]
    public void Slugify_HandlesCommonCases()
    {
        Assert.Equal("my-workspace", WorkspaceManagementService.Slugify("My Workspace"));
        Assert.Equal("my-workspace", WorkspaceManagementService.Slugify("  My---Workspace  "));
        Assert.Equal("hello-world-1", WorkspaceManagementService.Slugify("Hello World 1!"));
        Assert.Equal("a-b-c", WorkspaceManagementService.Slugify("a/b/c"));
        Assert.Equal("", WorkspaceManagementService.Slugify("###"));
        Assert.Equal("camelcase", WorkspaceManagementService.Slugify("CamelCase"));
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRoot)
        {
            ContentRootPath = contentRoot;
            ContentRootFileProvider = new PhysicalFileProvider(contentRoot);
        }
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "OrchestratorApi.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
