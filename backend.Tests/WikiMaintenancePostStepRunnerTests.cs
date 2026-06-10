

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class WikiMaintenancePostStepRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wiki-maintenance-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Run_WithOutcomeIssue_CreatesProjectScopedCommonProblemAndIndex()
    {
        var projectRoot = PrepareProjectWiki();
        var jobFolder = Directory.CreateDirectory(Path.Combine(_root, "job")).FullName;
        var runner = new WikiMaintenancePostStepRunner(NullLogger<WikiMaintenancePostStepRunner>.Instance);
        var task = Task(jobFolder) with
        {
            OutcomeIssue = new TaskOutcomeIssue
            {
                Kind = "missing-terminal-sentinel",
                Label = "Missing terminal sentinel",
                Severity = "Warn",
                Summary = "The run ended without a terminal sentinel."
            }
        };

        var result = runner.Run(task, Entry(projectRoot), new DateTime(2026, 06, 08, 10, 0, 0, DateTimeKind.Utc));

        Assert.Equal(WikiMaintenanceVerdict.Created, result.Verdict);
        Assert.Equal("missing-terminal-sentinel", result.Slug);

        var problem = Path.Combine(projectRoot, "docs", "wiki", "common-problems", "missing-terminal-sentinel");
        var readme = File.ReadAllText(Path.Combine(problem, "README.md"));
        Assert.Contains("id: missing-terminal-sentinel", readme);
        Assert.Contains("seen-count: 1", readme);
        Assert.Contains("related-tasks: [task-1]", readme);
        Assert.True(File.Exists(Path.Combine(problem, "protocol.md")));
        Assert.True(File.Exists(Path.Combine(problem, "measures.md")));
        Assert.True(File.Exists(Path.Combine(problem, "ideas.md")));
        Assert.True(File.Exists(Path.Combine(problem, "related.md")));

        var occurrences = File.ReadAllText(Path.Combine(problem, "occurrences.md"));
        Assert.Contains("| 2026-06-08T10:00:00Z | `task-1` | codex |", occurrences);

        var index = File.ReadAllText(Path.Combine(projectRoot, "docs", "wiki", "common-problems", "README.md"));
        Assert.Contains("[missing-terminal-sentinel](missing-terminal-sentinel/)", index);
        Assert.Contains("Last regenerated: 2026-06-08", index);
    }

    [Fact]
    public void Run_RepeatedSameTask_UpdatesLastSeenWithoutDuplicateOccurrence()
    {
        var projectRoot = PrepareProjectWiki();
        var jobFolder = Directory.CreateDirectory(Path.Combine(_root, "job")).FullName;
        var runner = new WikiMaintenancePostStepRunner(NullLogger<WikiMaintenancePostStepRunner>.Instance);
        var task = Task(jobFolder) with
        {
            OutcomeIssue = new TaskOutcomeIssue
            {
                Kind = "classifier-unknown",
                Label = "Classifier unknown",
                Severity = "Warn",
                Summary = "The runner could not classify the CLI result."
            }
        };

        runner.Run(task, Entry(projectRoot), new DateTime(2026, 06, 08, 10, 0, 0, DateTimeKind.Utc));
        var second = runner.Run(task, Entry(projectRoot), new DateTime(2026, 06, 08, 11, 0, 0, DateTimeKind.Utc));

        Assert.Equal(WikiMaintenanceVerdict.Updated, second.Verdict);
        var problem = Path.Combine(projectRoot, "docs", "wiki", "common-problems", "classifier-unknown");
        var readme = File.ReadAllText(Path.Combine(problem, "README.md"));
        Assert.Contains("last-seen: 2026-06-08T11:00:00Z", readme);
        Assert.Contains("seen-count: 1", readme);

        var occurrences = File.ReadAllText(Path.Combine(problem, "occurrences.md"));
        Assert.Equal(1, Count(occurrences, "`task-1`"));
    }

    private string PrepareProjectWiki()
    {
        var projectRoot = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        Directory.CreateDirectory(Path.Combine(projectRoot, "docs", "wiki", "common-problems"));
        return projectRoot;
    }

    private static WatchPathEntry Entry(string projectRoot) => new()
    {
        Name = "agent-taskboard",
        Path = projectRoot,
        RootPath = projectRoot,
        RepositoryPath = projectRoot,
    };

    private static TaskInfo Task(string jobFolder) => new()
    {
        Id = "task-1",
        Title = "Test task",
        ProjectName = "agent-taskboard",
        FolderPath = jobFolder,
        Agent = "codex",
        CliType = "codex",
    };

    private static int Count(string haystack, string needle)
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
