using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the real Tool-Use Read Analytics behind the former Agent Docs
/// mockup. Two layers:
/// <list type="bullet">
///   <item>The pure <see cref="AgentDocReadClassifier"/>: which CLI a tool row
///   belongs to, how a Codex shell read is recognized, and how a candidate path
///   resolves to the most specific inventory file.</item>
///   <item>The <see cref="AgentDocsReadAnalyticsService"/> fold end to end over
///   a temp workspace with real <c>tool-calls.jsonl</c> files, including the
///   acceptance case (one Claude-style Read event and one Codex-style shell
///   read).</item>
/// </list>
/// </summary>
public class AgentDocsReadAnalyticsTests : IDisposable
{
    private readonly string _root;
    private readonly string _repo;
    private readonly string _tasks;

    public AgentDocsReadAnalyticsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "atp-docs-reads-" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_root, "repo");
        _tasks = Path.Combine(_root, "tasks");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(Path.Combine(_repo, "frontend"));
        Directory.CreateDirectory(Path.Combine(_repo, ".github"));
        Directory.CreateDirectory(_tasks);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_tasks, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    // ----------------------------------------------------------------------
    // Classifier: CLI attribution + read recognition
    // ----------------------------------------------------------------------

    [Fact]
    public void Classify_ClaudeRead_AttributesToClaude_WithPath()
    {
        var c = AgentDocReadClassifier.Classify("Read", "C:/repo/AGENTS.md");
        Assert.NotNull(c);
        Assert.Equal("claude", c!.Cli);
        Assert.Equal(new[] { "C:/repo/AGENTS.md" }, c.Paths);
    }

    [Fact]
    public void Classify_GeminiReadFile_AttributesToGemini()
    {
        var c = AgentDocReadClassifier.Classify("ReadFile", "GEMINI.md");
        Assert.NotNull(c);
        Assert.Equal("gemini", c!.Cli);
    }

    [Fact]
    public void Classify_CodexCatCommand_IsAShellRead()
    {
        var c = AgentDocReadClassifier.Classify("command_call", "cat AGENTS.md");
        Assert.NotNull(c);
        Assert.Equal("codex", c!.Cli);
        Assert.Contains("AGENTS.md", c.Paths);
    }

    [Fact]
    public void Classify_CodexSed_TakesLastPositionalAsFile()
    {
        var c = AgentDocReadClassifier.Classify("command_call", "sed -n '1,40p' CLAUDE.md");
        Assert.NotNull(c);
        Assert.Equal("codex", c!.Cli);
        Assert.Equal(new[] { "CLAUDE.md" }, c.Paths);
    }

    [Fact]
    public void Classify_CodexCatWithPipe_ResolvesFileBeforePipe()
    {
        var c = AgentDocReadClassifier.Classify("command_call", "cat frontend/AGENTS.md | head -20");
        Assert.NotNull(c);
        Assert.Contains("frontend/AGENTS.md", c!.Paths);
    }

    [Fact]
    public void Classify_NonReadShellCommand_IsIgnored()
    {
        Assert.Null(AgentDocReadClassifier.Classify("command_call", "npm test"));
        Assert.Null(AgentDocReadClassifier.Classify("command_call", "ls -la"));
    }

    [Fact]
    public void Classify_WritesAndUnknownTools_AreIgnored()
    {
        Assert.Null(AgentDocReadClassifier.Classify("Edit", "AGENTS.md"));
        Assert.Null(AgentDocReadClassifier.Classify("Write", "AGENTS.md"));
        Assert.Null(AgentDocReadClassifier.Classify("TodoWrite", null));
        Assert.Null(AgentDocReadClassifier.Classify(null, null));
    }

    // ----------------------------------------------------------------------
    // Classifier: inventory matching
    // ----------------------------------------------------------------------

    [Fact]
    public void MatchInventory_AbsolutePath_MatchesBySuffix()
    {
        var inv = new[] { "AGENTS.md", "CLAUDE.md" };
        Assert.Equal("AGENTS.md", AgentDocReadClassifier.MatchInventory("C:/repo/AGENTS.md", inv));
    }

    [Fact]
    public void MatchInventory_PrefersMostSpecificScopedFile()
    {
        var inv = new[] { "AGENTS.md", "frontend/AGENTS.md" };
        // A path ending in frontend/AGENTS.md must not collapse onto root AGENTS.md.
        Assert.Equal("frontend/AGENTS.md",
            AgentDocReadClassifier.MatchInventory("C:/repo/frontend/AGENTS.md", inv));
        // A bare root read still maps to the root file only.
        Assert.Equal("AGENTS.md", AgentDocReadClassifier.MatchInventory("AGENTS.md", inv));
    }

    [Fact]
    public void MatchInventory_OutsideInventory_ReturnsNull()
    {
        var inv = new[] { "AGENTS.md" };
        Assert.Null(AgentDocReadClassifier.MatchInventory("src/Program.cs", inv));
        Assert.Null(AgentDocReadClassifier.MatchInventory("", inv));
    }

    // ----------------------------------------------------------------------
    // Service: end-to-end fold
    // ----------------------------------------------------------------------

    [Fact]
    public void GetAnalytics_CountsClaudeAndCodexReads_PerFileAndPerCli()
    {
        File.WriteAllText(Path.Combine(_repo, "AGENTS.md"), "# Agents\nSee docs/wiki/\n");
        File.WriteAllText(Path.Combine(_repo, "CLAUDE.md"), "See AGENTS.md\n");
        File.WriteAllText(Path.Combine(_repo, "frontend", "AGENTS.md"), "# Frontend\n");

        // Task A: Claude reads AGENTS.md twice; Codex reads AGENTS.md via cat.
        WriteToolCalls("job-a", new[]
        {
            Started("Read", "C:/anything/AGENTS.md"),
            Started("Read", "AGENTS.md"),
            Completed("Read"),
            Started("command_call", "cat AGENTS.md"),
            Started("Bash", "npm test"), // not a read - ignored
        });
        // Task B: Codex reads the scoped frontend/AGENTS.md; Claude reads CLAUDE.md.
        WriteToolCalls("job-b", new[]
        {
            Started("command_call", "sed -n '1,20p' frontend/AGENTS.md"),
            Started("Read", "CLAUDE.md"),
        });

        var svc = BuildService();
        var result = svc.GetAnalytics("Demo", windowDays: 7);

        Assert.NotNull(result);
        Assert.True(result!.HasData);
        Assert.Equal(5, result.TotalReads); // 2 Claude + 1 Codex on root, 1 Codex scoped, 1 Claude CLAUDE.md
        Assert.Equal(2, result.TaskCount);

        var rootAgents = result.Files.Single(f => f.RelPath == "AGENTS.md");
        Assert.Equal(3, rootAgents.Reads);
        Assert.Equal(2, rootAgents.ByCli.Single(c => c.Cli == "claude").Reads);
        Assert.Equal(1, rootAgents.ByCli.Single(c => c.Cli == "codex").Reads);

        var scoped = result.Files.Single(f => f.RelPath == "frontend/AGENTS.md");
        Assert.Equal(1, scoped.Reads);
        Assert.Equal("codex", scoped.ByCli.Single().Cli);

        // Per-CLI project totals.
        Assert.Equal(3, result.ByCli.Single(c => c.Cli == "claude").Reads);
        Assert.Equal(2, result.ByCli.Single(c => c.Cli == "codex").Reads);
    }

    [Fact]
    public void GetAnalytics_NeverReportsFilesOutsideInventory()
    {
        File.WriteAllText(Path.Combine(_repo, "AGENTS.md"), "# Agents\n");
        // A read of a real source file that is NOT an agent doc must not appear.
        WriteToolCalls("job-a", new[]
        {
            Started("Read", "src/Program.cs"),
            Started("Read", "README.md"),
        });

        var svc = BuildService();
        var result = svc.GetAnalytics("Demo", windowDays: 7);

        Assert.NotNull(result);
        Assert.All(result!.Files, f => Assert.True(f.RelPath == "AGENTS.md"));
        Assert.False(result.HasData);
        Assert.Equal(0, result.TotalReads);
    }

    [Fact]
    public void GetAnalytics_ProjectWithNoReadEvidence_ReportsHonestEmptyState()
    {
        File.WriteAllText(Path.Combine(_repo, "AGENTS.md"), "# Agents\n");
        // No task folders / no tool-calls at all.

        var svc = BuildService();
        var result = svc.GetAnalytics("Demo", windowDays: 7);

        Assert.NotNull(result);
        Assert.False(result!.HasData);
        Assert.Equal(0, result.TotalReads);
        Assert.Single(result.Files); // the inventory file is still listed, with zero reads
        Assert.Equal(0, result.Files[0].Reads);
    }

    [Fact]
    public void GetAnalytics_UnknownProject_ReturnsNull()
    {
        var svc = BuildService();
        Assert.Null(svc.GetAnalytics("no-such-project"));
    }

    [Fact]
    public void GetAnalytics_RecencyWindow_CountsOnlyRecentReads()
    {
        File.WriteAllText(Path.Combine(_repo, "AGENTS.md"), "# Agents\n");
        WriteToolCalls("job-a", new[]
        {
            StartedAt("Read", "AGENTS.md", "2000-01-01T00:00:00Z"), // ancient
            StartedAt("Read", "AGENTS.md", "2026-07-05T09:00:00Z"), // recent
        });

        var svc = BuildService();
        var now = DateTime.Parse("2026-07-05T10:00:00Z").ToUniversalTime();
        var result = svc.GetAnalytics("Demo", windowDays: 7, nowUtc: now);

        Assert.NotNull(result);
        Assert.Equal(2, result!.TotalReads);
        Assert.Equal(1, result.RecentReads);
        var file = result.Files.Single();
        Assert.Equal(2, file.Reads);
        Assert.Equal(1, file.RecentReads);
    }

    // ----------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------

    private AgentDocsReadAnalyticsService BuildService()
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
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var docs = new ProjectSteeringDocsService(scanner, NullLogger<ProjectSteeringDocsService>.Instance);
        return new AgentDocsReadAnalyticsService(scanner, docs, NullLogger<AgentDocsReadAnalyticsService>.Instance);
    }

    private void WriteToolCalls(string jobId, IEnumerable<string> lines)
    {
        var jobFolder = Path.Combine(_tasks, TaskStates.Progress, jobId);
        Directory.CreateDirectory(Path.Combine(jobFolder, "logs"));
        File.WriteAllText(Path.Combine(jobFolder, "task.json"),
            JsonSerializer.Serialize(new { id = jobId, title = jobId, state = TaskStates.Progress, order = 1, agent = "claude" }));
        File.WriteAllLines(Path.Combine(jobFolder, "logs", "tool-calls.jsonl"), lines, Encoding.UTF8);
    }

    private static string Started(string tool, string? argument) =>
        JsonSerializer.Serialize(new { ts = DateTime.UtcNow, kind = "started", tool, argument });

    private static string StartedAt(string tool, string? argument, string tsIso) =>
        JsonSerializer.Serialize(new { ts = DateTime.Parse(tsIso).ToUniversalTime(), kind = "started", tool, argument });

    private static string Completed(string tool) =>
        JsonSerializer.Serialize(new { ts = DateTime.UtcNow, kind = "completed", tool, isError = false });
}
