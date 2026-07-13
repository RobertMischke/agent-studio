using System.Text.RegularExpressions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class WorkstreamCollectorPostStepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workstream-collector-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ContextDefaultsToCodexMiniHigh()
    {
        var context = new WorkstreamCollectorContext();
        Assert.Equal(CliTypes.Codex, context.Cli);
        Assert.Equal(ModelIds.Gpt54Mini, context.Model);
        Assert.Equal("high", context.ThinkingLevel);
    }

    [Fact]
    public void Apply_MergesSignalByIdentity_AndIncrementsFrequency()
    {
        var docs = PrepareDocs();
        var proposal = Proposal(Item("20-development-signals", "runner-crash", "Runner crash", "Seen again", frequency: "1"));

        var first = WorkstreamCollectorPostStepRunner.Apply(docs, Context(), proposal, new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc));
        var second = WorkstreamCollectorPostStepRunner.Apply(docs, Context("AGT-1988"), proposal, new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal((1, 0), first);
        Assert.Equal((1, 0), second);
        var path = AreaPage(docs, "20-development-signals", "runner-crash.md");
        Assert.Contains("frequency: 2", File.ReadAllText(path));
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.md"));
    }

    [Fact]
    public void Apply_PersistsSignalStatusAndHumanActionFrontmatter()
    {
        var docs = PrepareDocs();
        var item = Item("20-development-signals", "runner-crash", "Runner crash", "Seen again") with
        {
            Status = "active",
            HumanAction = "Inspect the latest failed run."
        };

        WorkstreamCollectorPostStepRunner.Apply(docs, Context(), Proposal(item), DateTime.UtcNow);

        var text = File.ReadAllText(AreaPage(docs, "20-development-signals", "runner-crash.md"));
        Assert.Contains("status: active", text);
        Assert.Contains("human-action: \"Inspect the latest failed run.\"", text);
    }

    [Fact]
    public void Apply_SameTaskRerun_DoesNotDoubleCountSignalOrLog()
    {
        var docs = PrepareDocs();
        var proposal = Proposal(
            Item("20-development-signals", "runner-crash", "Runner crash", "Seen", frequency: "1"),
            Item("50-workstream-log", "outcome", "Completed", "Done"));

        WorkstreamCollectorPostStepRunner.Apply(docs, Context(), proposal, DateTime.UtcNow);
        WorkstreamCollectorPostStepRunner.Apply(docs, Context(), proposal, DateTime.UtcNow.AddMinutes(1));

        Assert.Contains("frequency: 1", File.ReadAllText(AreaPage(docs, "20-development-signals", "runner-crash.md")));
        var log = File.ReadAllText(AreaPage(docs, "50-workstream-log", "workstream-log.md"));
        Assert.Single(Regex.Matches(log, @"(?m)^Source: AGT-1987$").Cast<Match>());
    }

    [Fact]
    public void Apply_SystemKnowledgeUpdatesInPlace_WithRequiredProvenance()
    {
        var docs = PrepareDocs();
        var first = Proposal(Item("30-system-knowledge", "pipeline/contracts", "Pipeline contract", "Version one"));
        var second = Proposal(Item("30-system-knowledge", "pipeline/contracts", "Pipeline contract", "Version two"));

        WorkstreamCollectorPostStepRunner.Apply(docs, Context(), first, DateTime.UtcNow);
        WorkstreamCollectorPostStepRunner.Apply(docs, Context(), second, DateTime.UtcNow);

        var path = AreaPage(docs, "30-system-knowledge", Path.Combine("pipeline", "contracts.md"));
        var text = File.ReadAllText(path);
        Assert.Contains("last-updated-from: AGT-1987", text);
        Assert.Contains("**Last Updated From:** AGT-1987", text);
        Assert.Contains("Version two", text);
        Assert.DoesNotContain("Version one", text);
    }

    [Fact]
    public void Apply_RejectsDepthOverTwo_AndCapsPerRunBudget()
    {
        var docs = PrepareDocs();
        var items = Enumerable.Range(0, WorkstreamCollectorPostStepRunner.MaxItemsPerRun + 2)
            .Select(i => Item("40-decision-log", "decision-" + i, "Decision " + i, "Chosen direction"))
            .ToList();
        items[0] = Item("40-decision-log", "too/deep/page", "Invalid", "Must not escape the depth budget");

        var result = WorkstreamCollectorPostStepRunner.Apply(docs, Context(), new() { Items = items }, DateTime.UtcNow);

        Assert.Equal(3, result.Writes); // per-area budget is the tighter bound
        Assert.True(result.Rejected >= items.Count - 3);
        Assert.False(File.Exists(AreaPage(docs, "40-decision-log", Path.Combine("too", "deep", "page.md"))));
    }

    [Fact]
    public void Parse_RequiresMarkerAndJsonBlock()
    {
        var parsed = WorkstreamCollectorPostStepRunner.Parse("""
            <!-- WORKSTREAM_COLLECTOR_JSON -->
            ```json
            {"items":[{"area":"50-workstream-log","identity":"outcome","title":"Done","content":"Shipped."}]}
            ```
            """);

        Assert.NotNull(parsed);
        Assert.Single(parsed!.Items);
        Assert.Null(WorkstreamCollectorPostStepRunner.Parse("{\"items\":[]}"));
    }

    [Fact]
    public void RecordOnboarding_SeedsFrame_AndReplacesCurrentState()
    {
        Directory.CreateDirectory(_root);
        var first = Context().Task with { Key = "AGT-1", Title = "First stream", ProjectName = "agent-taskboard" };
        var second = Context().Task with { Key = "AGT-2", Title = "Second stream", ProjectName = "agent-taskboard" };

        WorkstreamCollectorPostStepRunner.RecordOnboarding(
            _root, first, EngineeringWorkstreamFrameLanguage.English, DateTime.UtcNow);
        WorkstreamCollectorPostStepRunner.RecordOnboarding(
            _root, second, EngineeringWorkstreamFrameLanguage.English, DateTime.UtcNow);

        var current = File.ReadAllText(Path.Combine(_root, "docs", "engineering-workstream",
            "10-current-development-state", "current.md"));
        Assert.Contains("AGT-2", current);
        Assert.DoesNotContain("AGT-1", current);
        Assert.True(File.Exists(Path.Combine(_root, "docs", "engineering-workstream", "00-overview.html")));
    }

    private string PrepareDocs()
    {
        var docs = Path.Combine(_root, "docs");
        EngineeringWorkstreamFrameSeeder.EnsureFrame(docs, EngineeringWorkstreamFrameLanguage.English);
        return docs;
    }

    private WorkstreamCollectorContext Context(string key = "AGT-1987") => new()
    {
        Task = new TaskInfo { Id = "task-1", Key = key, Title = "EW-2", FolderPath = _root },
        Project = new WatchPathEntry { Name = "agent-taskboard", RootPath = _root, Path = _root },
        Model = "test-model",
    };

    private static WorkstreamCollectorProposal Proposal(params WorkstreamCollectorItem[] items) => new() { Items = [.. items] };

    private static WorkstreamCollectorItem Item(
        string area, string identity, string title, string content, string? frequency = null) => new()
    {
        Area = area, Identity = identity, Title = title, Content = content, Frequency = frequency,
    };

    private static string AreaPage(string docs, string area, string relative) =>
        Path.Combine(docs, "engineering-workstream", area, relative);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
