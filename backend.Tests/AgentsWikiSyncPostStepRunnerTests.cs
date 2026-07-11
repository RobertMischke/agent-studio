using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class AgentsWikiSyncPostStepRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "agents-wiki-sync-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Run_NoRegistry_SeedsTemplateAndIndex_AndProvisionsFrame()
    {
        var projectRoot = NewProjectRoot();
        var runner = NewRunner();

        var result = runner.Run(Task(), Entry(projectRoot), changedFiles: null, At());

        Assert.Equal(AgentsWikiSyncVerdict.Created, result.Verdict);
        Assert.Contains("seeded", result.Reason);

        var topicsDir = Path.Combine(projectRoot, "docs", "wiki", "concepts", "designated-topics");
        Assert.True(File.Exists(Path.Combine(topicsDir, "registry.json")), "registry.json was not seeded");
        Assert.True(File.Exists(Path.Combine(topicsDir, "README.md")), "index README was not written");
        // Self-provisioning: the Workstream frame is seeded like the sibling steps.
        Assert.True(File.Exists(Path.Combine(projectRoot, "docs", "engineering-workstream", "00-overview.html")));
    }

    [Fact]
    public void Run_EmptyRegistry_IsSkipped()
    {
        var projectRoot = NewProjectRoot();
        WriteRegistry(projectRoot, """{ "topics": [] }""");
        var runner = NewRunner();

        var result = runner.Run(Task(), Entry(projectRoot), changedFiles: null, At());

        Assert.Equal(AgentsWikiSyncVerdict.Skipped, result.Verdict);
        Assert.Equal(0, result.TopicCount);
    }

    [Fact]
    public void Run_TaskMatchesTopicByTag_WritesStatePageProgressRow_AndValidatesPointer()
    {
        var projectRoot = NewProjectRoot();
        var page = "docs/wiki/concepts/orchestrator-drive-to-conclusion.html";
        WritePage(projectRoot, page, "<html><body>drive to conclusion</body></html>");
        WriteRegistry(projectRoot, Registry(
            Topic("drive-to-conclusion", "Orchestrator drive-to-conclusion", page,
                tags: ["drive-to-conclusion"], pathPrefixes: [])));
        var runner = NewRunner();

        var task = Task() with
        {
            Tags = ["drive-to-conclusion"],
            Commits = [new TaskCommitInfo { Sha = "abc1234def", ShortSha = "abc1234", Message = "Harden the crash recovery path" }],
        };

        var result = runner.Run(task, Entry(projectRoot), changedFiles: null, At());

        Assert.Equal(AgentsWikiSyncVerdict.Created, result.Verdict);
        Assert.Equal(1, result.TopicCount);
        Assert.Equal(1, result.MatchedTopics);
        Assert.Equal(0, result.MissingPages);

        var topicsDir = Path.Combine(projectRoot, "docs", "wiki", "concepts", "designated-topics");
        var statePage = File.ReadAllText(Path.Combine(topicsDir, "drive-to-conclusion.md"));
        Assert.Contains("## Current State / Progress", statePage);
        Assert.Contains("entry-count: 1", statePage);
        Assert.Contains("`AGT-1`", statePage);
        Assert.Contains("tags", statePage);
        Assert.Contains("abc1234: Harden the crash recovery path", statePage);
        Assert.Contains("Latest: AGT-1", statePage);

        var index = File.ReadAllText(Path.Combine(topicsDir, "README.md"));
        Assert.Contains("[Orchestrator drive-to-conclusion](drive-to-conclusion.md)", index);
        Assert.Contains("(../orchestrator-drive-to-conclusion.html)", index);
        Assert.Contains("designated concept page(s) resolve", index);
    }

    [Fact]
    public void Run_TaskMatchesByChangedFilePath_RecordsPathMatch()
    {
        var projectRoot = NewProjectRoot();
        var page = "docs/wiki/concepts/orchestrator-supervision-loop.html";
        WritePage(projectRoot, page, "<html><body>supervision loop</body></html>");
        WriteRegistry(projectRoot, Registry(
            Topic("supervision-loop", "Supervision loop", page,
                tags: [], pathPrefixes: ["backend/Features/Runner/"])));
        var runner = NewRunner();

        var result = runner.Run(
            Task(),
            Entry(projectRoot),
            changedFiles: ["backend/Features/Runner/ReviewDecisionOrchestrator.cs", "README.md"],
            At());

        Assert.Equal(1, result.MatchedTopics);
        var statePage = File.ReadAllText(Path.Combine(
            projectRoot, "docs", "wiki", "concepts", "designated-topics", "supervision-loop.md"));
        Assert.Contains("| path |", statePage);
    }

    [Fact]
    public void Run_UnmatchedTask_StillSyncsPointer_WithoutProgressRow()
    {
        var projectRoot = NewProjectRoot();
        var page = "docs/wiki/concepts/orchestrator-drive-to-conclusion.html";
        WritePage(projectRoot, page, "<html><body>x</body></html>");
        WriteRegistry(projectRoot, Registry(
            Topic("drive-to-conclusion", "Drive to conclusion", page,
                tags: ["drive-to-conclusion"], pathPrefixes: ["backend/Features/Runner/"])));
        var runner = NewRunner();

        // No tags, unrelated changed file.
        var result = runner.Run(Task(), Entry(projectRoot), changedFiles: ["frontend/foo.ts"], At());

        Assert.Equal(0, result.MatchedTopics);
        var statePage = File.ReadAllText(Path.Combine(
            projectRoot, "docs", "wiki", "concepts", "designated-topics", "drive-to-conclusion.md"));
        Assert.Contains("No task activity recorded yet.", statePage);
        Assert.Contains("entry-count: 0", statePage);
        Assert.DoesNotContain("`AGT-1`", statePage);
    }

    [Fact]
    public void Run_MissingConceptPage_RecordsDeadPointerFinding()
    {
        var projectRoot = NewProjectRoot();
        WriteRegistry(projectRoot, Registry(
            Topic("evidence-gate", "Evidence gate", "docs/wiki/concepts/does-not-exist.html",
                tags: ["evidence-gate"], pathPrefixes: [])));
        var runner = NewRunner();

        var result = runner.Run(
            Task() with { Tags = ["evidence-gate"] }, Entry(projectRoot), changedFiles: null, At());

        Assert.Equal(1, result.MissingPages);
        Assert.Contains(result.Findings!, f => f.Contains("missing wiki page"));
        var index = File.ReadAllText(Path.Combine(
            projectRoot, "docs", "wiki", "concepts", "designated-topics", "README.md"));
        Assert.Contains("Pointer health", index);
        Assert.Contains("missing", index);
        Assert.Contains("does-not-exist.html", index);
    }

    [Fact]
    public void Run_RepeatedSameTask_DoesNotDuplicateProgressRow()
    {
        var projectRoot = NewProjectRoot();
        var page = "docs/wiki/concepts/orchestrator-drive-to-conclusion.html";
        WritePage(projectRoot, page, "<html><body>x</body></html>");
        WriteRegistry(projectRoot, Registry(
            Topic("drive-to-conclusion", "Drive to conclusion", page, tags: ["drive-to-conclusion"], pathPrefixes: [])));
        var runner = NewRunner();
        var task = Task() with { Tags = ["drive-to-conclusion"] };

        runner.Run(task, Entry(projectRoot), changedFiles: null, At());
        var second = runner.Run(task, Entry(projectRoot), changedFiles: null,
            new DateTime(2026, 07, 12, 9, 0, 0, DateTimeKind.Utc));

        Assert.Equal(AgentsWikiSyncVerdict.Updated, second.Verdict);
        var statePage = File.ReadAllText(Path.Combine(
            projectRoot, "docs", "wiki", "concepts", "designated-topics", "drive-to-conclusion.md"));
        Assert.Equal(1, CountOccurrences(statePage, "`AGT-1`"));
        Assert.Contains("entry-count: 1", statePage);
        // The validation timestamp still refreshes even on the idempotent re-run.
        Assert.Contains("last-synced: 2026-07-12", statePage);
    }

    [Fact]
    public void Run_AgentsPointerMissing_HealsByAppendingManagedBlock()
    {
        var projectRoot = NewProjectRoot();
        var page = "docs/wiki/concepts/orchestrator-drive-to-conclusion.html";
        WritePage(projectRoot, page, "<html></html>");
        WriteRegistry(projectRoot, Registry(
            Topic("drive-to-conclusion", "Drive to conclusion", page, tags: ["drive-to-conclusion"], pathPrefixes: [])));
        File.WriteAllText(Path.Combine(projectRoot, "AGENTS.md"), "# AGENTS\n\nKeep this file short.\n");
        var runner = NewRunner();

        var result = runner.Run(
            Task() with { Tags = ["drive-to-conclusion"] }, Entry(projectRoot), changedFiles: null, At());

        Assert.Equal("healed", result.AgentsPointer);
        var agents = File.ReadAllText(Path.Combine(projectRoot, "AGENTS.md"));
        Assert.Contains("designated-topics:begin", agents);
        Assert.Contains("docs/wiki/concepts/designated-topics/README.md", agents);
    }

    [Fact]
    public void Run_AgentsPointerAlreadyPresent_IsOk_AndLeavesFileUntouched()
    {
        var projectRoot = NewProjectRoot();
        var page = "docs/wiki/concepts/orchestrator-drive-to-conclusion.html";
        WritePage(projectRoot, page, "<html></html>");
        WriteRegistry(projectRoot, Registry(
            Topic("drive-to-conclusion", "Drive to conclusion", page, tags: ["drive-to-conclusion"], pathPrefixes: [])));
        var agentsBody = "# AGENTS\n\nSee docs/wiki/concepts/designated-topics/README.md for topic state.\n";
        var agentsPath = Path.Combine(projectRoot, "AGENTS.md");
        File.WriteAllText(agentsPath, agentsBody);
        var runner = NewRunner();

        var result = runner.Run(
            Task() with { Tags = ["drive-to-conclusion"] }, Entry(projectRoot), changedFiles: null, At());

        Assert.Equal("ok", result.AgentsPointer);
        Assert.Equal(agentsBody, File.ReadAllText(agentsPath));
    }

    [Fact]
    public void Run_AgentsMissingEntirely_RecordsAbsentFinding()
    {
        var projectRoot = NewProjectRoot();
        var page = "docs/wiki/concepts/orchestrator-drive-to-conclusion.html";
        WritePage(projectRoot, page, "<html></html>");
        WriteRegistry(projectRoot, Registry(
            Topic("drive-to-conclusion", "Drive to conclusion", page, tags: ["drive-to-conclusion"], pathPrefixes: [])));
        var runner = NewRunner();

        var result = runner.Run(
            Task() with { Tags = ["drive-to-conclusion"] }, Entry(projectRoot), changedFiles: null, At());

        Assert.Equal("absent", result.AgentsPointer);
        Assert.Contains(result.Findings!, f => f.Contains("AGENTS.md not found"));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static AgentsWikiSyncPostStepRunner NewRunner() =>
        new(NullLogger<AgentsWikiSyncPostStepRunner>.Instance);

    private string NewProjectRoot()
    {
        var projectRoot = Directory.CreateDirectory(
            Path.Combine(_root, Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.Combine(projectRoot, "docs", "wiki", "concepts"));
        return projectRoot;
    }

    private static void WriteRegistry(string projectRoot, string json)
    {
        var dir = Path.Combine(projectRoot, "docs", "wiki", "concepts", "designated-topics");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "registry.json"), json);
    }

    private static void WritePage(string projectRoot, string repoRelPage, string content)
    {
        var full = Path.Combine(projectRoot, repoRelPage.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string Registry(params string[] topicJson) =>
        "{ \"topics\": [" + string.Join(", ", topicJson) + "] }";

    private static string Topic(
        string slug, string title, string page,
        string[] tags, string[] pathPrefixes)
    {
        static string Arr(IEnumerable<string> xs) =>
            "[" + string.Join(", ", xs.Select(x => "\"" + x + "\"")) + "]";
        return $$"""
        { "slug": "{{slug}}", "title": "{{title}}", "page": "{{page}}", "tags": {{Arr(tags)}}, "pathPrefixes": {{Arr(pathPrefixes)}} }
        """;
    }

    private static WatchPathEntry Entry(string projectRoot) => new()
    {
        Name = "agent-taskboard",
        Path = projectRoot,
        RootPath = projectRoot,
        RepositoryPath = projectRoot,
    };

    private static TaskInfo Task() => new()
    {
        Id = "task-1",
        Key = "AGT-1",
        Title = "Test task",
        ProjectName = "agent-taskboard",
        FolderPath = "/tmp/job",
        Agent = "claude",
        CliType = "claude",
    };

    private static DateTime At() => new(2026, 07, 11, 10, 0, 0, DateTimeKind.Utc);

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
