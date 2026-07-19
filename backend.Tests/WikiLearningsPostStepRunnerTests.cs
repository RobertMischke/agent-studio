

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class WikiLearningsPostStepRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wiki-learnings-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Run_FirstTime_CreatesPerTaskPageAndIndex()
    {
        var projectRoot = PrepareProjectWiki();
        var runner = NewRunner();
        var task = Task() with { Commits = [Commit("abc1234deadbeef", "feat: add wiki-learnings step")] };
        var run = new WikiLearningsRun(
            Verdict: "accept-with-concerns",
            VerdictReason: "two aspects flagged minor issues",
            Findings:
            [
                new WikiLearningFinding("code-quality", "concerns", "naming nit on the runner"),
                new WikiLearningFinding("tests-and-evidence", "pass", "all green"),
            ],
            AgentNotes: "Implemented the deterministic distillation step.",
            StumblingBlock: "Locked bin during the build.",
            ChangedSummary: "1 commit; latest abc1234: feat: add wiki-learnings step");

        var result = runner.Run(task, Entry(projectRoot), run, new DateTime(2026, 06, 08, 10, 0, 0, DateTimeKind.Utc));

        Assert.Equal(WikiLearningsVerdict.Created, result.Verdict);
        Assert.Equal("ass-1694", result.Slug);

        var page = File.ReadAllText(Path.Combine(LearningsRoot(projectRoot), "ass-1694.md"));
        Assert.Contains("id: ass-1694", page);
        Assert.Contains("task-key: ASS-1694", page);
        Assert.Contains("status: accept-with-concerns", page);
        Assert.Contains("run-count: 1", page);
        Assert.Contains("## Run 2026-06-08T10:00:00Z - Accepted with concerns", page);
        Assert.Contains("<!-- wiki-learnings-sig: ", page);
        Assert.Contains("**Outcome.** Accepted with concerns - two aspects flagged minor issues", page);
        Assert.Contains("- **code-quality** [concerns]: naming nit on the runner", page);
        Assert.Contains("- **tests-and-evidence** [pass]: all green", page);
        Assert.Contains("**Stumbling blocks.** Locked bin during the build.", page);
        Assert.Contains("**Agent notes.** Implemented the deterministic distillation step.", page);
        Assert.Contains("**Changed.** 1 commit; latest abc1234: feat: add wiki-learnings step", page);

        // No em dashes leak into rendered artifacts (AGENTS.md rule).
        Assert.DoesNotContain("—", page);

        var index = File.ReadAllText(Path.Combine(LearningsRoot(projectRoot), "README.md"));
        Assert.Contains("[ASS-1694](ass-1694.md)", index);
        Assert.Contains("Last regenerated: 2026-06-08", index);
    }

    [Fact]
    public void Run_SameSignatureTwice_RefreshesWithoutDuplicateRunBlock()
    {
        var projectRoot = PrepareProjectWiki();
        var runner = NewRunner();
        var task = Task() with { Commits = [Commit("sha-stable-0001", "fix: stable")] };
        var run = SimpleRun("accept");

        runner.Run(task, Entry(projectRoot), run, new DateTime(2026, 06, 08, 10, 0, 0, DateTimeKind.Utc));
        var second = runner.Run(task, Entry(projectRoot), run, new DateTime(2026, 06, 08, 12, 30, 0, DateTimeKind.Utc));

        Assert.Equal(WikiLearningsVerdict.Updated, second.Verdict);
        var page = File.ReadAllText(Path.Combine(LearningsRoot(projectRoot), "ass-1694.md"));
        Assert.Equal(1, Count(page, "## Run "));
        Assert.Contains("run-count: 1", page);
        // The idempotent refresh advances last-distilled to the second run stamp.
        Assert.Contains("last-distilled: 2026-06-08T12:30:00Z", page);
    }

    [Fact]
    public void Run_NewSignature_AppendsFreshRunBlockNewestOnTop()
    {
        var projectRoot = PrepareProjectWiki();
        var runner = NewRunner();

        var first = Task() with { Commits = [Commit("sha-run-one", "feat: first")] };
        runner.Run(first, Entry(projectRoot), SimpleRun("accept"),
            new DateTime(2026, 06, 08, 10, 0, 0, DateTimeKind.Utc));

        var second = Task() with { Commits = [Commit("sha-run-two", "fix: follow-up")] };
        var result = runner.Run(second, Entry(projectRoot), SimpleRun("reissue"),
            new DateTime(2026, 06, 08, 11, 0, 0, DateTimeKind.Utc));

        Assert.Equal(WikiLearningsVerdict.Updated, result.Verdict);
        var page = File.ReadAllText(Path.Combine(LearningsRoot(projectRoot), "ass-1694.md"));
        Assert.Equal(2, Count(page, "## Run "));
        Assert.Contains("run-count: 2", page);
        // Newest run is prepended above the earlier one.
        var newerIdx = page.IndexOf("## Run 2026-06-08T11:00:00Z", StringComparison.Ordinal);
        var olderIdx = page.IndexOf("## Run 2026-06-08T10:00:00Z", StringComparison.Ordinal);
        Assert.True(newerIdx >= 0 && olderIdx > newerIdx,
            "the newer run block must appear above the older one");
    }

    [Fact]
    public void Run_WhenDocsEmpty_SelfProvisionsLearningsHome()
    {
        // Self-provisioning (AGT-2024): an enabled step no longer skips because
        // the wiki folder is missing - it bootstraps its own docs/operations/learnings home.
        var projectRoot = Directory.CreateDirectory(Path.Combine(_root, "empty-docs")).FullName;
        var runner = NewRunner();

        var result = runner.Run(Task(), Entry(projectRoot), SimpleRun("accept"),
            new DateTime(2026, 06, 08, 10, 0, 0, DateTimeKind.Utc));

        Assert.Equal(WikiLearningsVerdict.Created, result.Verdict);
        Assert.True(File.Exists(Path.Combine(LearningsRoot(projectRoot), "ass-1694.md")),
            "learnings page was not written");
    }

    private static WikiLearningsRun SimpleRun(string verdict) => new(
        Verdict: verdict,
        VerdictReason: null,
        Findings: [new WikiLearningFinding("code-quality", "pass", "looks fine")],
        AgentNotes: "Did the work.",
        StumblingBlock: null,
        ChangedSummary: "1 commit");

    private static WikiLearningsPostStepRunner NewRunner()
        => new(NullLogger<WikiLearningsPostStepRunner>.Instance);

    private string PrepareProjectWiki()
    {
        var projectRoot = Directory.CreateDirectory(Path.Combine(_root, "project", Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.Combine(projectRoot, "docs"));
        return projectRoot;
    }

    private static string LearningsRoot(string projectRoot)
        => Path.Combine(projectRoot, "docs", "operations", "learnings");

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
        Key = "ASS-1694",
        Title = "Wiki-Post-Processing-Step",
        ProjectName = "agent-taskboard",
        FolderPath = "unused",
        Agent = "claude",
        CliType = "claude",
        TaskType = TaskTypes.Feature,
        Tags = ["pipeline", "wiki"],
    };

    private static TaskCommitInfo Commit(string sha, string message) => new()
    {
        Sha = sha,
        ShortSha = sha.Length >= 7 ? sha[..7] : sha,
        Message = message,
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
