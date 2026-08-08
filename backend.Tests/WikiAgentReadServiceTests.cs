using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class WikiAgentReadLogParserTests
{
    [Theory]
    [InlineData("● Read docs/concepts/overview.md", "concepts/overview.md")]
    [InlineData("● Run sed -n '1,80p' docs/operations/setup/README.md", "operations/setup/README.md")]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"/repo/docs/quality/guide.md"}}]}}""", "quality/guide.md")]
    [InlineData("""{"type":"item.completed","item":{"type":"command_execution","command":"cat docs/a.md","exit_code":0}}""", "a.md")]
    public void ExtractDocsRelativePaths_RecognizesRenderedAndRawReadTools(string line, string expected)
    {
        Assert.Equal(new[] { expected }, WikiAgentReadLogParser.ExtractDocsRelativePaths(line));
    }

    [Fact]
    public void ExtractDocsRelativePaths_ReturnsEveryDistinctPageReadByOneTool()
    {
        var paths = WikiAgentReadLogParser.ExtractDocsRelativePaths(
            "● Run cat docs/a.md docs/folder/b.html docs/a.md");

        Assert.Equal(new[] { "a.md", "folder/b.html" }, paths);
    }

    [Theory]
    [InlineData("The next change is in docs/concepts/overview.md.")]
    [InlineData("""{"type":"item.completed","item":{"type":"agent_message","text":"I inspected docs/concepts/overview.md"}}""")]
    [InlineData("● Write docs/concepts/overview.md")]
    [InlineData("● Edit docs/concepts/overview.md")]
    [InlineData("● Run printf changed > docs/concepts/overview.md")]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write","input":{"file_path":"docs/concepts/overview.md"}}]}}""")]
    [InlineData("● Read docs/app/schemas/wiki-document-companion.schema.json")]
    [InlineData("● Read docs/concepts/overview.md.meta.json")]
    public void ExtractDocsRelativePaths_IgnoresMentionsWritesAndNonWikiContracts(string line)
    {
        Assert.Empty(WikiAgentReadLogParser.ExtractDocsRelativePaths(line));
    }
}

public sealed class WikiAgentReadStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wiki-agent-read-store-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Increment_WritesOutsideRepositoryAndCapsNewestHistory()
    {
        var repo = Path.Combine(_root, "repo");
        var wiki = Path.Combine(repo, "docs");
        Directory.CreateDirectory(wiki);
        var page = Path.Combine(wiki, "page.md");
        File.WriteAllText(page, "# Page\n");
        File.WriteAllText(page + ".meta.json",
            """{"schemaVersion":"wiki-document-companion/v1","grading":{"grade":"A"}}""");
        var companionBefore = File.ReadAllText(page + ".meta.json");
        var store = BuildStore();
        var start = new DateTime(2026, 7, 22, 10, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 22; i++)
            store.Increment("PROJ-001", wiki, "page.md", start.AddMinutes(i), $"AGT-{i}");

        Assert.Equal(companionBefore, File.ReadAllText(page + ".meta.json"));
        var statePath = store.StatePathFor("PROJ-001", "page.md");
        Assert.False(statePath.StartsWith(repo, StringComparison.OrdinalIgnoreCase));
        using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
        var root = doc.RootElement;
        Assert.Equal("wiki-agent-read-state/v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(22, root.GetProperty("total").GetInt32());
        Assert.Equal(20, root.GetProperty("recent").GetArrayLength());
        Assert.Equal("AGT-21", root.GetProperty("recent")[0].GetProperty("taskKey").GetString());
        Assert.Equal("AGT-2", root.GetProperty("recent")[19].GetProperty("taskKey").GetString());
        Assert.Equal(start.AddMinutes(21), root.GetProperty("lastReadAt").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public void Increment_CopyOnWriteMigratesLegacyBaselineWithoutEditingCompanion()
    {
        var wiki = Path.Combine(_root, "repo", "docs");
        Directory.CreateDirectory(wiki);
        File.WriteAllText(Path.Combine(wiki, "page.md"), "# Page\n");
        var companion = Path.Combine(wiki, "page.md.meta.json");
        File.WriteAllText(companion,
            """
            {
              "source": { "path": "docs/page.md" },
              "agentReads": {
                "total": 5,
                "lastReadAt": "2026-07-21T09:00:00Z",
                "recent": [
                  { "at": "2026-07-21T09:00:00Z", "taskKey": "AGT-OLD" }
                ]
              }
            }
            """);
        var companionBefore = File.ReadAllText(companion);
        var store = BuildStore();

        store.Increment(
            "PROJ-001",
            wiki,
            "page.md",
            new DateTime(2026, 7, 22, 10, 0, 0, DateTimeKind.Utc),
            "AGT-NEW");

        Assert.Equal(companionBefore, File.ReadAllText(companion));
        var reads = Assert.Single(store.ReadAll("PROJ-001").ByDocsRelativePath).Value;
        Assert.Equal(6, reads.Total);
        Assert.Equal(new[] { "AGT-NEW", "AGT-OLD" }, reads.Recent.Select(item => item.TaskKey));
    }

    [Fact]
    public void ApplyBackfill_IsIdempotentAndRetainsSameMillisecondReads()
    {
        var wiki = Path.Combine(_root, "repo", "docs");
        Directory.CreateDirectory(wiki);
        File.WriteAllText(Path.Combine(wiki, "page.md"), "# Page\n");
        var store = BuildStore();
        var at = new DateTime(2026, 7, 22, 10, 0, 0, 123, DateTimeKind.Utc);
        var recent = new[]
        {
            new WikiAgentReadRecent(at, "AGT-1"),
            new WikiAgentReadRecent(at, "AGT-1"),
        };

        store.ApplyBackfill("PROJ-001", wiki, "page.md", 2, recent);
        store.ApplyBackfill("PROJ-001", wiki, "page.md", 2, recent);

        var root = JsonNode.Parse(File.ReadAllText(store.StatePathFor("PROJ-001", "page.md")))!.AsObject();
        Assert.Equal(2, root["total"]!.GetValue<int>());
        Assert.Equal(2, root["recent"]!.AsArray().Count);
    }

    private WikiAgentReadStore BuildStore()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = Path.Combine(_root, "tasks") })
            .Build();
        return new WikiAgentReadStore(config);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }
}

public sealed class WikiAgentReadBackfillTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wiki-agent-read-backfill-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly string _tasks;

    public WikiAgentReadBackfillTests()
    {
        _repo = Path.Combine(_root, "repo");
        _tasks = Path.Combine(_root, "tasks");
        Directory.CreateDirectory(Path.Combine(_repo, "docs", "concepts"));
        Directory.CreateDirectory(_tasks);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_tasks, state));
        File.WriteAllText(Path.Combine(_repo, "docs", "concepts", "overview.md"), "# Overview\n");
    }

    [Fact]
    public void EnsureBackfilled_FoldsCompleteExistingLogAndMarkerMakesRepeatANoOp()
    {
        // Production's TaskIndexCache keeps archive outside ScanAllJobs().
        // Put the only historical log there so the backfill is forced to use
        // the archive-inclusive inventory rather than passing only in uncached
        // test mode.
        var taskDir = Path.Combine(_tasks, TaskStates.Archive, "wiki-reader");
        Directory.CreateDirectory(Path.Combine(taskDir, "logs"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), JsonSerializer.Serialize(new
        {
            id = "wiki-reader",
            key = "AGT-2242",
            title = "Wiki reader",
            state = TaskStates.Archive,
            order = 1,
            agent = "codex",
        }));

        var logPath = Path.Combine(taskDir, "logs", "cli-output.log");
        using (var writer = new StreamWriter(logPath))
        {
            writer.WriteLine("""[10:00:00.000] [stdout] {"type":"item.completed","item":{"type":"command_execution","command":"cat docs/concepts/overview.md","exit_code":0}}""");
            for (var i = 0; i <= CliOutputLogParser.MaxLinesCap; i++)
                writer.WriteLine($"[10:00:01.000] [stdout] noise {i}");
        }

        var service = BuildService(out var scanner);
        var archived = Assert.Single(scanner.ScanAllJobsWithArchive());
        Assert.Equal(taskDir, archived.FolderPath);
        Assert.True(File.Exists(TaskPaths.CliOutputLog(archived.FolderPath)));
        Assert.Empty(scanner.ScanAllJobs());
        Assert.Single(scanner.ScanAllJobsWithArchive());
        var first = service.EnsureBackfilled();
        var second = service.EnsureBackfilled();

        Assert.False(first.AlreadyCompleted);
        Assert.Equal(1, first.LogsScanned);
        Assert.Equal(1, first.ReadsApplied);
        Assert.True(File.Exists(first.MarkerPath));
        Assert.True(second.AlreadyCompleted);

        using var state = ReadOverviewState();
        var reads = state.RootElement;
        Assert.Equal(1, reads.GetProperty("total").GetInt32());
        Assert.Equal("AGT-2242", reads.GetProperty("recent")[0].GetProperty("taskKey").GetString());
    }

    [Fact]
    public void ProcessOutput_AppliesEachLiveReadEvenWhenTimestampsMatch()
    {
        WriteTask("live-reader", "AGT-2243");
        var service = BuildService(out _);
        var at = new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);

        var applied = service.ProcessOutput("AGT-2243", new[]
        {
            new CliOutputLine { Timestamp = at, Stream = "stdout", Text = "● Read docs/concepts/overview.md" },
            new CliOutputLine { Timestamp = at, Stream = "stdout", Text = "● Read docs/concepts/overview.md" },
        });

        Assert.Equal(2, applied);
        using var state = ReadOverviewState();
        Assert.Equal(2, state.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void ProcessOutput_LeavesTrackedWikiRepositoryClean()
    {
        var companion = Path.Combine(_repo, "docs", "concepts", "overview.md.meta.json");
        File.WriteAllText(companion,
            """
            {
              "source": { "path": "docs/concepts/overview.md" },
              "agentReads": {
                "total": 1,
                "lastReadAt": "2026-07-21T09:00:00Z",
                "recent": [
                  { "at": "2026-07-21T09:00:00Z", "taskKey": "AGT-OLD" }
                ]
              }
            }
            """);
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "test");
        RunGit("add", "docs");
        RunGit("commit", "-q", "-m", "test: seed wiki");
        WriteTask("clean-reader", "AGT-2245");
        var service = BuildService(out _);

        var applied = service.ProcessOutput("AGT-2245",
        [
            new CliOutputLine
            {
                Timestamp = new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc),
                Stream = "stdout",
                Text = "● Read docs/concepts/overview.md",
            },
        ]);

        Assert.Equal(1, applied);
        Assert.Equal(string.Empty, RunGit("status", "--porcelain"));
        using var state = ReadOverviewState();
        Assert.Equal(2, state.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public void BackfillThenLiveRead_PreservesTheHistoricalBaselineAndAddsContinuousEvidence()
    {
        WriteTask("reader", "AGT-2244");
        var taskDir = Path.Combine(_tasks, TaskStates.Progress, "reader");
        var logPath = Path.Combine(taskDir, "logs", "cli-output.log");
        File.WriteAllText(
            logPath,
            "[10:00:00.000] [stdout] ● Read docs/concepts/overview.md\n");
        File.SetLastWriteTimeUtc(logPath, new DateTime(2026, 7, 22, 10, 0, 0, DateTimeKind.Utc));
        var service = BuildService(out _);

        var baseline = service.EnsureBackfilled();
        var liveAt = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        var applied = service.ProcessOutput("AGT-2244",
        [
            new CliOutputLine
            {
                Timestamp = liveAt,
                Stream = "stdout",
                Text = "● Read docs/concepts/overview.md",
            },
        ]);

        Assert.Equal(1, baseline.ReadsApplied);
        Assert.Equal(1, applied);
        using var state = ReadOverviewState();
        var reads = state.RootElement;
        Assert.Equal(2, reads.GetProperty("total").GetInt32());
        Assert.Equal(liveAt, reads.GetProperty("lastReadAt").GetDateTime().ToUniversalTime());
        Assert.Equal(2, reads.GetProperty("recent").GetArrayLength());
        Assert.All(
            reads.GetProperty("recent").EnumerateArray(),
            item => Assert.Equal("AGT-2244", item.GetProperty("taskKey").GetString()));
    }

    private void WriteTask(string id, string key)
    {
        var taskDir = Path.Combine(_tasks, TaskStates.Progress, id);
        Directory.CreateDirectory(Path.Combine(taskDir, "logs"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            key,
            title = id,
            state = TaskStates.Progress,
            order = 1,
            agent = "codex",
        }));
    }

    private WikiAgentReadService BuildService(out TaskScannerService scanner)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "Demo",
                ["WatchPaths:0:Path"] = _tasks,
                ["WatchPaths:0:RepositoryPath"] = _repo,
                ["TaskRepository"] = _tasks,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var docs = new ProjectDocsService(scanner, registry, NullLogger<ProjectDocsService>.Instance);
        return new WikiAgentReadService(
            scanner,
            registry,
            new WikiAgentReadStore(config),
            docs,
            config,
            NullLogger<WikiAgentReadService>.Instance);
    }

    private JsonDocument ReadOverviewState()
    {
        var stateRoot = Path.Combine(_tasks, ".metadata", "wiki-agent-reads");
        var path = Assert.Single(Directory.EnumerateFiles(
            stateRoot,
            "overview.md.json",
            SearchOption.AllDirectories));
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private string RunGit(params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {stderr}");
        return stdout.Trim();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }
}
