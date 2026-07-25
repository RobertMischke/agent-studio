using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Focused HTTP coverage for the operator-owned replace-all commit
/// attribution mutation.
/// </summary>
public sealed class ReplaceTaskCommitsEndpointTests : IDisposable
{
    private const string ProjectName = "Commit Attribution Test";
    private const string JobId = "commit-card";

    private readonly string _root;
    private readonly string _watchPath;
    private readonly string _repositoryPath;
    private readonly string _firstSha;
    private readonly string _secondSha;
    private readonly string _unreachableSha;

    public ReplaceTaskCommitsEndpointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "replace-task-commits-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "task-store");
        _repositoryPath = Path.Combine(_root, "repository");
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        Directory.CreateDirectory(_repositoryPath);

        Git("init", "-b", "develop");
        Git("config", "user.name", "Commit API Test");
        Git("config", "user.email", "commit-api@example.test");

        File.WriteAllText(Path.Combine(_repositoryPath, "first.txt"), "first\n");
        Git("add", "first.txt");
        Git("commit", "-m", "feat: first reachable commit");
        _firstSha = Git("rev-parse", "HEAD").Trim();

        File.WriteAllText(Path.Combine(_repositoryPath, "second.txt"), "second\n");
        Git("add", "second.txt");
        Git("commit", "-m", "fix: second reachable commit");
        _secondSha = Git("rev-parse", "HEAD").Trim();

        Git("switch", "-c", "discarded");
        File.WriteAllText(Path.Combine(_repositoryPath, "discarded.txt"), "discarded\n");
        Git("add", "discarded.txt");
        Git("commit", "-m", "chore: unreachable commit");
        _unreachableSha = Git("rev-parse", "HEAD").Trim();
        Git("switch", "develop");
        Git("branch", "-D", "discarded");

        var taskFolder = Path.Combine(_watchPath, TaskStates.Completed, JobId);
        Directory.CreateDirectory(taskFolder);
        File.WriteAllText(Path.Combine(taskFolder, "task.json"), JsonSerializer.Serialize(new
        {
            id = JobId,
            key = "CAT-1",
            title = "Commit card",
            state = TaskStates.Completed,
            order = 1,
            agent = "codex",
            commit = new { sha = "legacy", shortSha = "legacy", message = "old", at = "2026-01-01T00:00:00Z" },
            commits = new[] { new { sha = "legacy", shortSha = "legacy", message = "old", at = "2026-01-01T00:00:00Z" } },
            excludedCommits = new[] { "obsolete-override" },
        }));
        File.WriteAllText(Path.Combine(taskFolder, "prompt.md"), "Replace commits.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Replace_UsesCanonicalProjectResolution_PreservesOrderAndLegacyProjection_AndAudits()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        var projectId = await GetProjectId(client);
        using var response = await client.PutAsJsonAsync(
            $"/api/tasks/{JobId}/commits?project={Uri.EscapeDataString(projectId)}&watchPath={Uri.EscapeDataString("/stale/not-canonical")}",
            new ReplaceTaskCommitsRequest { Commits = [_firstSha, _secondSha] });

        response.EnsureSuccessStatusCode();

        var taskFolder = Path.Combine(_watchPath, TaskStates.Completed, JobId);
        using var task = JsonDocument.Parse(File.ReadAllText(Path.Combine(taskFolder, "task.json")));
        var commits = task.RootElement.GetProperty("commits");
        Assert.Equal(2, commits.GetArrayLength());
        Assert.Equal(_firstSha, Property(commits[0], "sha").GetString());
        Assert.Equal(_secondSha, Property(commits[1], "sha").GetString());
        Assert.All(commits.EnumerateArray(), commit =>
            Assert.Equal(CommitAttributionKinds.Operator, Property(commit, "attribution").GetString()));

        var legacy = task.RootElement.GetProperty("commit");
        Assert.Equal(_secondSha, Property(legacy, "sha").GetString());
        Assert.Equal("fix: second reachable commit", Property(legacy, "message").GetString());
        Assert.False(task.RootElement.TryGetProperty("excludedCommits", out _));

        var timeline = new TimelineLog(Microsoft.Extensions.Logging.Abstractions.NullLogger<TimelineLog>.Instance)
            .ReadAll(taskFolder);
        var audit = Assert.Single(timeline, evt => evt.Kind == TimelineEventKinds.CommitAttributionReplaced);
        Assert.Equal("human:local-default", audit.Actor);
        Assert.Equal("2", audit.Details!["newCount"]);
        Assert.Equal($"{_firstSha},{_secondSha}", audit.Details["shas"]);
    }

    [Fact]
    public async Task Replace_WithoutClientId_IsUnauthorizedAndDoesNotWrite()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync(
            $"/api/tasks/{JobId}/commits?watchPath={Uri.EscapeDataString(_watchPath)}",
            new ReplaceTaskCommitsRequest { Commits = [_firstSha] });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("legacy", ReadSinglePersistedSha());
    }

    [Fact]
    public async Task Replace_RejectsWatchPathOutsideConfiguredProjects()
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        using var response = await client.PutAsJsonAsync(
            $"/api/tasks/{JobId}/commits?watchPath={Uri.EscapeDataString(Path.Combine(_root, "not-configured"))}",
            new ReplaceTaskCommitsRequest { Commits = [_firstSha] });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("legacy", ReadSinglePersistedSha());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unreachable")]
    public async Task Replace_RejectsUnknownOrUnreachableSha_Atomically(string invalidKind)
    {
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var invalidSha = invalidKind == "unknown" ? new string('f', 40) : _unreachableSha;

        using var response = await client.PutAsJsonAsync(
            $"/api/tasks/{JobId}/commits?watchPath={Uri.EscapeDataString(_watchPath)}",
            new ReplaceTaskCommitsRequest { Commits = [_firstSha, invalidSha] });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = Assert.Single(body.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal(invalidKind + "-sha", error.GetProperty("code").GetString());
        Assert.Equal("legacy", ReadSinglePersistedSha());
        Assert.False(File.Exists(TaskPaths.TimelineLog(Path.Combine(_watchPath, TaskStates.Completed, JobId))));
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["WatchPaths:0:Name"] = ProjectName,
                    ["WatchPaths:0:Path"] = _watchPath,
                    ["WatchPaths:0:RootPath"] = _repositoryPath,
                    ["WatchPaths:0:RepositoryPath"] = _repositoryPath,
                }));
        });

    private async Task<string> GetProjectId(HttpClient client)
    {
        using var response = await client.GetAsync("/api/projects");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.EnumerateArray()
            .Single(project => string.Equals(
                project.GetProperty("storageLocation").GetString(),
                _watchPath,
                StringComparison.Ordinal))
            .GetProperty("id")
            .GetString()!;
    }

    private string ReadSinglePersistedSha()
    {
        var taskPath = Path.Combine(_watchPath, TaskStates.Completed, JobId, "task.json");
        using var task = JsonDocument.Parse(File.ReadAllText(taskPath));
        return Assert.Single(task.RootElement.GetProperty("commits").EnumerateArray())
            .GetProperty("sha")
            .GetString()!;
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.EnumerateObject()
            .Single(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            .Value;

    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output;
    }
}
