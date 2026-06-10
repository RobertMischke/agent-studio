using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskFileHistoryEndpointsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspaceRoot;
    private readonly string _workspaceProjectRoot;
    private readonly string _codeRoot;

    public TaskFileHistoryEndpointsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-file-history-" + Guid.NewGuid().ToString("N"));
        _workspaceRoot = Path.Combine(_tempDir, "workspace");
        _workspaceProjectRoot = Path.Combine(_workspaceRoot, "projects", "agent-taskboard");
        _codeRoot = Path.Combine(_tempDir, "code");

        Directory.CreateDirectory(_workspaceProjectRoot);
        Directory.CreateDirectory(_codeRoot);

        InitRepo(_workspaceRoot);
        InitRepo(_codeRoot);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task WorkspaceArtifactHistory_ReturnsTrailersAndVersionContent()
    {
        var job = WriteJob("ASS-853");
        var review = Path.Combine(job, "code-review.md");

        File.WriteAllText(review, "run 1 review\n", Encoding.UTF8);
        WriteGenerationIndex(job, runIndex: 1);
        RunGit(_workspaceRoot, "add", "-A");
        RunGit(_workspaceRoot, "commit", "-q", "-m", "chore(workspace): record run artifacts for ASS-853", "-m",
            "Run-Index: 1\nVerdict: reissue\nSteps: aspect-code-quality=warn");
        var firstSha = RunGitCapture(_workspaceRoot, "rev-parse", "HEAD").Trim();

        File.WriteAllText(review, "run 2 review\n", Encoding.UTF8);
        WriteGenerationIndex(job, runIndex: 2);
        RunGit(_workspaceRoot, "add", "-A");
        RunGit(_workspaceRoot, "commit", "-q", "-m", "chore(workspace): record run artifacts for ASS-853", "-m",
            "Run-Index: 2\nVerdict: accept\nSteps: aspect-code-quality=pass");
        var secondSha = RunGitCapture(_workspaceRoot, "rev-parse", "HEAD").Trim();

        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var watchPath = Uri.EscapeDataString(_workspaceProjectRoot);
        using var historyResponse = await client.GetAsync($"/api/tasks/ASS-853/files/code-review.md/history?watchPath={watchPath}");
        historyResponse.EnsureSuccessStatusCode();

        using var historyDoc = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
        var entries = historyDoc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(secondSha, entries[0].GetProperty("sha").GetString());
        Assert.Equal(2, entries[0].GetProperty("runIndex").GetInt32());
        Assert.Equal("accept", entries[0].GetProperty("verdict").GetString());
        Assert.Equal("workspace", entries[0].GetProperty("provenance").GetProperty("source").GetString());
        Assert.Equal("aspect-code-quality=pass", entries[0].GetProperty("provenance").GetProperty("steps").GetString());
        Assert.Equal(2, entries[0].GetProperty("provenance").GetProperty("generation").GetProperty("runIndex").GetInt32());
        Assert.Equal("aspect", entries[0].GetProperty("provenance").GetProperty("generation").GetProperty("kind").GetString());

        using var contentResponse = await client.GetAsync($"/api/tasks/ASS-853/files/code-review.md?watchPath={watchPath}&at={firstSha}");
        contentResponse.EnsureSuccessStatusCode();
        Assert.Equal("text/markdown", contentResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("run 1 review\n", await contentResponse.Content.ReadAsStringAsync());

        using var diffResponse = await client.GetAsync($"/api/tasks/ASS-853/files/code-review.md/diff?watchPath={watchPath}&from={firstSha}&to={secondSha}");
        diffResponse.EnsureSuccessStatusCode();
        var diff = await diffResponse.Content.ReadAsStringAsync();
        Assert.Contains("+run 2 review", diff);
    }

    [Fact]
    public async Task CodeFileHistory_UsesProjectRepositoryWhenScopedToCode()
    {
        WriteJob("ASS-900");
        WriteFile(_codeRoot, "src/app.cs", "class App { }\n");
        RunGit(_codeRoot, "add", "-A");
        RunGit(_codeRoot, "commit", "-q", "-m", "feat: add app");
        var firstSha = RunGitCapture(_codeRoot, "rev-parse", "HEAD").Trim();

        WriteFile(_codeRoot, "src/app.cs", "class App { void Run() { } }\n");
        RunGit(_codeRoot, "add", "-A");
        RunGit(_codeRoot, "commit", "-q", "-m", "feat: update app");
        var secondSha = RunGitCapture(_codeRoot, "rev-parse", "HEAD").Trim();

        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var watchPath = Uri.EscapeDataString(_workspaceProjectRoot);
        using var historyResponse = await client.GetAsync($"/api/tasks/ASS-900/files/src/app.cs/history?watchPath={watchPath}&scope=code");
        historyResponse.EnsureSuccessStatusCode();

        using var historyDoc = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
        var entries = historyDoc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(secondSha, entries[0].GetProperty("sha").GetString());
        Assert.Equal("code", entries[0].GetProperty("provenance").GetProperty("source").GetString());
        Assert.Equal("feat: update app", entries[0].GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, entries[0].GetProperty("runIndex").ValueKind);

        using var contentResponse = await client.GetAsync($"/api/tasks/ASS-900/files/src/app.cs?watchPath={watchPath}&scope=code&at={firstSha}");
        contentResponse.EnsureSuccessStatusCode();
        Assert.Equal("class App { }\n", await contentResponse.Content.ReadAsStringAsync());

        using var diffResponse = await client.GetAsync($"/api/tasks/ASS-900/files/src/app.cs/diff?watchPath={watchPath}&scope=code&from={firstSha}&to={secondSha}");
        diffResponse.EnsureSuccessStatusCode();
        Assert.Contains("+class App { void Run() { } }", await diffResponse.Content.ReadAsStringAsync());
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspaceRoot,
                        ["WatchPaths:0:Name"] = "agent-taskboard",
                        ["WatchPaths:0:Path"] = _workspaceProjectRoot,
                        ["WatchPaths:0:RootPath"] = _codeRoot,
                        ["WatchPaths:0:RepositoryPath"] = _codeRoot,
                    });
                });
            });
    }

    private string WriteJob(string id)
    {
        var job = Path.Combine(_workspaceProjectRoot, "tasks", "001", id);
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            title = id,
            state = TaskStates.HumanReview,
            order = 1,
            agent = "claude",
            createdAt = DateTime.UtcNow,
        }), Encoding.UTF8);
        File.WriteAllText(Path.Combine(job, "prompt.md"), "Do the thing.\n", Encoding.UTF8);
        return job;
    }

    private static void WriteGenerationIndex(string job, int runIndex)
    {
        var metadata = Path.Combine(job, ".metadata");
        Directory.CreateDirectory(metadata);
        var entries = new[]
        {
            new FileGenerationMeta
            {
                File = "code-review.md",
                Kind = "aspect",
                Model = "claude-test",
                Cli = "claude",
                TokensIn = 10,
                TokensOut = 5,
                RunIndex = runIndex,
                StepId = "aspect-code-quality",
            }
        };
        File.WriteAllText(
            Path.Combine(metadata, "files.json"),
            JsonSerializer.Serialize(entries, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            }),
            Encoding.UTF8);
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.UTF8);
    }

    private static void InitRepo(string root)
    {
        Directory.CreateDirectory(root);
        RunGit(root, "init", "-q", "-b", "main");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "test");
        RunGit(root, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(root, "README.md"), "seed\n", Encoding.UTF8);
        RunGit(root, "add", "README.md");
        RunGit(root, "commit", "-q", "-m", "seed");
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var result = RunGitResult(cwd, args);
        Assert.Equal(0, result.Code);
    }

    private static string RunGitCapture(string cwd, params string[] args)
    {
        var result = RunGitResult(cwd, args);
        Assert.Equal(0, result.Code);
        return result.Out;
    }

    private static (string Out, string Err, int Code) RunGitResult(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return (stdout, stderr, p.ExitCode);
    }
}
