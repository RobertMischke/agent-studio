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
/// HTTP-level coverage for the API-owned integration commit backfill. The
/// endpoint may stamp task metadata, but it must never create or rewrite Git
/// history.
/// </summary>
public sealed class AppendIntegrationCommitEndpointTests : IDisposable
{
    private const string ProjectName = "integration-commit-test";
    private const string TaskKey = "AGT-9999";

    private readonly string _workspace;
    private readonly string _watchPath;
    private readonly string _repository;

    public AppendIntegrationCommitEndpointTests()
    {
        _workspace = Path.Combine(
            Path.GetTempPath(),
            "agt-integration-commit-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        _repository = Path.Combine(_workspace, "repo");

        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        Directory.CreateDirectory(_repository);
        RunGit("init", "-q", "-b", "develop");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "test");
        RunGit("config", "commit.gpgsign", "false");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task AppendIntegrationCommit_AppendsDedupesMirrorsFinal_AndDoesNotCommit()
    {
        var workSha = CommitFile("work.txt", "task work", $"feat: {TaskKey}");
        var integrationSha = CommitFile(
            "integration.txt",
            "accepted",
            $"integrate: {TaskKey} (operator acceptance)");
        WriteTask(workSha);

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_watchPath);
        var headBefore = RunGitCapture("rev-parse", "HEAD").Trim();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/tasks/{TaskKey}/commits/integration?watchPath={watchPath}",
                new AppendIntegrationCommitRequest(integrationSha));
            var responseBody = await response.Content.ReadAsStringAsync();
            Assert.True(
                response.IsSuccessStatusCode,
                $"Expected integration append to succeed, got {(int)response.StatusCode}: {responseBody}");
        }

        Assert.Equal(headBefore, RunGitCapture("rev-parse", "HEAD").Trim());

        using var detailResponse = await client.GetAsync(
            $"/api/tasks/{TaskKey}?watchPath={watchPath}");
        detailResponse.EnsureSuccessStatusCode();
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        var info = detail.RootElement.GetProperty("info");
        var commits = info.GetProperty("commits").EnumerateArray().ToList();

        Assert.Equal(2, commits.Count);
        Assert.Equal(workSha, commits[0].GetProperty("sha").GetString());
        Assert.Equal(integrationSha, commits[1].GetProperty("sha").GetString());
        Assert.Equal(
            integrationSha,
            info.GetProperty("commit").GetProperty("sha").GetString());
        Assert.Equal(
            CommitAttributionKinds.Manual,
            commits[1].GetProperty("attribution").GetString());
        Assert.Equal(
            "integrate: AGT-9999 (operator acceptance)",
            commits[1].GetProperty("message").GetString());
    }

    [Fact]
    public async Task AppendIntegrationCommit_RejectsUnknownOrMismatchedSha_WithoutMutation()
    {
        var workSha = CommitFile("work.txt", "task work", $"feat: {TaskKey}");
        var otherSha = CommitFile("other.txt", "other", "integrate: AGT-1111");
        WriteTask(workSha);

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_watchPath);

        using var mismatched = await client.PostAsJsonAsync(
            $"/api/tasks/{TaskKey}/commits/integration?watchPath={watchPath}",
            new AppendIntegrationCommitRequest(otherSha));
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);

        using var unknown = await client.PostAsJsonAsync(
            $"/api/tasks/{TaskKey}/commits/integration?watchPath={watchPath}",
            new AppendIntegrationCommitRequest(new string('f', 40)));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        using var detailResponse = await client.GetAsync(
            $"/api/tasks/{TaskKey}?watchPath={watchPath}");
        detailResponse.EnsureSuccessStatusCode();
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        var info = detail.RootElement.GetProperty("info");
        var commits = info.GetProperty("commits").EnumerateArray().ToList();
        Assert.Single(commits);
        Assert.Equal(workSha, info.GetProperty("commit").GetProperty("sha").GetString());
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["WatchPaths:0:Name"] = ProjectName,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _repository,
                        ["WatchPaths:0:RepositoryPath"] = _repository,
                    });
                });
            });

    private string CommitFile(string relativePath, string content, string message)
    {
        File.WriteAllText(Path.Combine(_repository, relativePath), content);
        RunGit("add", "--", relativePath);
        RunGit("commit", "-q", "-m", message);
        return RunGitCapture("rev-parse", "HEAD").Trim();
    }

    private void WriteTask(string workSha)
    {
        var folder = Path.Combine(_watchPath, TaskStates.Archive, TaskKey);
        Directory.CreateDirectory(folder);
        var task = new
        {
            id = TaskKey,
            key = TaskKey,
            title = "Integration backfill fixture",
            state = TaskStates.Archive,
            order = 1,
            agent = "codex",
            cliType = "codex",
            createdAt = "2026-07-25T00:00:00Z",
            commit = new
            {
                sha = workSha,
                shortSha = workSha[..8],
                message = $"feat: {TaskKey}",
                filesChanged = 1,
                files = new[] { "work.txt" },
                at = "2026-07-25T00:01:00Z",
                attribution = CommitAttributionKinds.Automatic,
                confidence = 1.0,
            },
            commits = new[]
            {
                new
                {
                    sha = workSha,
                    shortSha = workSha[..8],
                    message = $"feat: {TaskKey}",
                    filesChanged = 1,
                    files = new[] { "work.txt" },
                    at = "2026-07-25T00:01:00Z",
                    attribution = CommitAttributionKinds.Automatic,
                    confidence = 1.0,
                },
            },
        };
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(task, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Backfill the integration SHA.");
    }

    private void RunGit(params string[] args)
    {
        var (stdout, stderr, exitCode) = RunProcess(args);
        Assert.True(
            exitCode == 0,
            $"git {string.Join(' ', args)} failed ({exitCode}): {stderr}\n{stdout}");
    }

    private string RunGitCapture(params string[] args)
    {
        var (stdout, stderr, exitCode) = RunProcess(args);
        Assert.True(
            exitCode == 0,
            $"git {string.Join(' ', args)} failed ({exitCode}): {stderr}\n{stdout}");
        return stdout;
    }

    private (string Stdout, string Stderr, int ExitCode) RunProcess(string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = _repository,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (stdout, stderr, process.ExitCode);
    }
}
