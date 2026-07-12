using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectUrlProcessServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "project-url-process-" + Guid.NewGuid().ToString("N"));

    public ProjectUrlProcessServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void ResolveWorkingDirectory_PrefersExplicitCwd()
    {
        var explicitCwd = Directory.CreateDirectory(Path.Combine(_root, "explicit")).FullName;
        var repository = Directory.CreateDirectory(Path.Combine(_root, "repository")).FullName;

        var actual = ProjectUrlProcessService.ResolveWorkingDirectory(
            Project(repositoryPath: repository), Rule(cwd: explicitCwd));

        Assert.Equal(explicitCwd, actual);
    }

    [Fact]
    public void ResolveWorkingDirectory_FallsBackFromMissingRepositoryToRoot()
    {
        var root = Directory.CreateDirectory(Path.Combine(_root, "root")).FullName;

        var actual = ProjectUrlProcessService.ResolveWorkingDirectory(
            Project(repositoryPath: Path.Combine(_root, "missing"), rootPath: root), Rule());

        Assert.Equal(root, actual);
    }

    [Fact]
    public void ResolveWorkingDirectory_WithoutSourceRoot_ExplainsHowToFixIt()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ProjectUrlProcessService.ResolveWorkingDirectory(Project(), Rule()));

        Assert.Contains("Set a URL cwd or project repository/root path", error.Message);
    }

    [Fact]
    public void BuildStartInfo_DetachesTheCommandFromInteractiveInput()
    {
        var info = ProjectUrlProcessService.BuildStartInfo("echo ready", _root);

        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardInput);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
    }

    [Fact]
    public void Start_ReturnsCommandAndEffectiveWorkingDirectory()
    {
        var service = new ProjectUrlProcessService(NullLogger<ProjectUrlProcessService>.Instance);
        var url = new ProjectUrlRecord
        {
            Id = "url-1",
            Url = "http://localhost:4201",
            StartRule = Rule(command: OperatingSystem.IsWindows() ? "exit /b 0" : "exit 0"),
        };

        var result = service.Start(Project(repositoryPath: _root), url);

        Assert.True(result.Started);
        Assert.Equal("url-1", result.UrlId);
        Assert.Equal(url.StartRule.Command, result.Command);
        Assert.Equal(_root, result.Cwd);
        Assert.True(result.ProcessId > 0);
    }

    [Fact]
    public void PackageJsonDiscovery_PersistsRepositoryWorkingDirectory()
    {
        File.WriteAllText(Path.Combine(_root, "package.json"),
            "{\"scripts\":{\"website\":\"vite --port 4202\"}}");
        var detection = new ProjectUrlDetectionService(
            NullLogger<ProjectUrlDetectionService>.Instance);

        var suggestion = Assert.Single(detection.Detect(Project(repositoryPath: _root)));

        Assert.Equal(_root, suggestion.Cwd);
        Assert.Equal("npm run website", suggestion.Command);
    }

    private static ProjectRecord Project(string? repositoryPath = null, string? rootPath = null) => new()
    {
        Id = "PROJ-001",
        RepositoryPath = repositoryPath,
        RootPath = rootPath,
        StorageLocation = @"C:\task-store\must-not-be-used",
    };

    private static ProjectUrlStartRule Rule(string command = "npm start", string? cwd = null) => new()
    {
        Command = command,
        Cwd = cwd,
    };
}
