using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class WorkbenchOrchestratorEndpointsTests : IDisposable
{
    private const string Project = "Workbench Project";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "workbench-orchestrator-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly string _watchPath;

    public WorkbenchOrchestratorEndpointsTests()
    {
        _repo = Path.Combine(_root, "repo");
        _watchPath = Path.Combine(_root, "tasks");
        Directory.CreateDirectory(_repo);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        var workbench = Path.Combine(_repo, "docs", "operations", "inspected");
        Directory.CreateDirectory(workbench);
        File.WriteAllText(Path.Combine(workbench, "index.html"), "<h1>Inspected</h1>");
        File.WriteAllText(Path.Combine(workbench, "brief.md"), "Compare the compact and detailed options.");
        File.WriteAllText(Path.Combine(workbench, "workbench.json"), """
          {
            "schemaVersion": 2,
            "id": "inspected",
            "title": "Inspected Workbench",
            "summary": "Choose the useful density.",
            "entrypoint": "index.html",
            "pageKind": "workbench",
            "lifecycleState": "in-progress",
            "phase": "testing",
            "editedBy": "Tests",
            "editedAt": "2026-07-26T10:00:00Z",
            "lifecycleHistory": [
              { "state": "in-progress", "editedBy": "Tests", "editedAt": "2026-07-26T10:00:00Z" }
            ],
            "sourceTaskKeys": ["WBP-1"]
          }
          """);
        RunGit("init");
        RunGit("config", "user.email", "tests@example.invalid");
        RunGit("config", "user.name", "Workbench Tests");
        RunGit("add", ".");
        RunGit("commit", "-m", "seed workbench");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task InspectAndAnchor_AreReadOnlyProjectScopedAndRejectStaleProvenance()
    {
        var descriptor = Path.Combine(
            _repo, "docs", "operations", "inspected", "workbench.json");
        var descriptorBefore = File.ReadAllText(descriptor);
        var head = RunGit("rev-parse", "HEAD").Trim();

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        using var inspect = await client.PostAsJsonAsync(
            $"/api/orchestrator/context/project:{Uri.EscapeDataString(Project)}/workbench",
            new
            {
                id = "inspected",
                expectedRevision = head,
                selection = new { key = "density", value = "compact", label = "Compact" },
            });
        inspect.EnsureSuccessStatusCode();
        using (var json = JsonDocument.Parse(await inspect.Content.ReadAsStringAsync()))
        {
            Assert.Equal($"project:{Project}", json.RootElement.GetProperty("contextKey").GetString());
            var attached = json.RootElement.GetProperty("workbench");
            Assert.Equal("inspected", attached.GetProperty("id").GetString());
            Assert.Equal(head, attached.GetProperty("revision").GetString());
            Assert.Equal("exact-revision", attached.GetProperty("provenanceState").GetString());
            Assert.Equal(
                "docs/operations/inspected/workbench.json",
                attached.GetProperty("descriptorPath").GetString());
            Assert.Contains(
                "presentationSelection: key=density; value=compact",
                json.RootElement.GetProperty("digest").GetString());
        }

        using var stale = await client.PostAsJsonAsync(
            $"/api/orchestrator/context/project:{Uri.EscapeDataString(Project)}/workbench",
            new { id = "inspected", expectedRevision = new string('0', 40) });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var anchor = await client.PostAsJsonAsync(
            $"/api/runner/project:{Uri.EscapeDataString(Project)}/orchestrator-chat/workbench-anchors",
            new
            {
                @event = "open",
                workbench = new { id = "inspected", expectedRevision = head },
            });
        anchor.EnsureSuccessStatusCode();

        using var transcript = await client.GetAsync(
            $"/api/runner/project:{Uri.EscapeDataString(Project)}/orchestrator-chat");
        transcript.EnsureSuccessStatusCode();
        using (var json = JsonDocument.Parse(await transcript.Content.ReadAsStringAsync()))
        {
            Assert.Equal($"project:{Project}", json.RootElement.GetProperty("contextKey").GetString());
            var turn = Assert.Single(json.RootElement.GetProperty("turns").EnumerateArray());
            Assert.Equal("anchor", turn.GetProperty("role").GetString());
            var persisted = turn.GetProperty("workbenchAnchor");
            Assert.Equal("open", persisted.GetProperty("event").GetString());
            Assert.Equal("inspected", persisted.GetProperty("workbenchId").GetString());
            Assert.Equal(head, persisted.GetProperty("revision").GetString());
        }

        Assert.Equal(descriptorBefore, File.ReadAllText(descriptor));
        Assert.Equal("", RunGit("status", "--porcelain").Trim());
        Assert.Empty(Directory.EnumerateFiles(_watchPath, "task.json", SearchOption.AllDirectories));
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["WatchPaths:0:Name"] = Project,
                    ["WatchPaths:0:Path"] = _watchPath,
                    ["WatchPaths:0:RootPath"] = _repo,
                    ["WatchPaths:0:RepositoryPath"] = _repo,
                }));
        });

    private string RunGit(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
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
