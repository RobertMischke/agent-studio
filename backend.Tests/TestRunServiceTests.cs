using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TestRunServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "test-runs-" + Guid.NewGuid().ToString("N"));

    public TestRunServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void CardEvidence_DistinguishesExactContainedEarlierAndMissingRuns()
    {
        var stack = BuildStack();
        var exactCommit = RevParse(stack.Repo, "HEAD");
        stack.Service.Create("Demo", Request(exactCommit, "completed", "passed"));

        Commit(stack.Repo, "included.txt", "included", "Card included by later run");
        var cardCommit = RevParse(stack.Repo, "HEAD");
        Commit(stack.Repo, "later.txt", "later", "Later test revision");
        var laterRunCommit = RevParse(stack.Repo, "HEAD");
        stack.Service.Create("Demo", Request(laterRunCommit, "completed", "passed"));

        Commit(stack.Repo, "after.txt", "after", "Card newer than every run");
        var newerCardCommit = RevParse(stack.Repo, "HEAD");
        var jobs = new[]
        {
            Task("exact", stack.Storage, exactCommit),
            Task("contained", stack.Storage, cardCommit),
            Task("before", stack.Storage, newerCardCommit),
            Task("missing", stack.Storage, new string('f', 40)),
        };

        var evidence = stack.Service.BuildLookup(jobs);

        Assert.Equal("perfect", evidence[jobs[0].TaskKey].MatchQuality);
        Assert.Equal("proven", evidence[jobs[0].TaskKey].EvidenceState);
        Assert.True(evidence[jobs[0].TaskKey].DiffContained);

        Assert.Equal("contains-diff", evidence[jobs[1].TaskKey].MatchQuality);
        Assert.Equal(1, evidence[jobs[1].TaskKey].Distance);
        Assert.Equal("after", evidence[jobs[1].TaskKey].Direction);
        Assert.Equal("proven", evidence[jobs[1].TaskKey].EvidenceState);

        Assert.Equal("does-not-contain-diff", evidence[jobs[2].TaskKey].MatchQuality);
        Assert.False(evidence[jobs[2].TaskKey].DiffContained);
        Assert.Equal("not-proven", evidence[jobs[2].TaskKey].EvidenceState);

        Assert.Null(evidence[jobs[3].TaskKey].RunId);
        Assert.Equal("unassigned", evidence[jobs[3].TaskKey].EvidenceState);
        Assert.Contains("No matching test run", evidence[jobs[3].TaskKey].Summary);
    }

    [Fact]
    public void Lifecycle_PersistsForwardTransitions_AndRejectsBackwardState()
    {
        var stack = BuildStack();
        var commit = RevParse(stack.Repo, "HEAD");
        var planned = stack.Service.Create("Demo", Request(commit, "planned", null))!;

        var running = stack.Service.Update("Demo", planned.Id, new UpdateTestRunRequest
        {
            State = "running",
            Host = "runner-01",
        });
        var completed = stack.Service.Update("Demo", planned.Id, new UpdateTestRunRequest
        {
            State = "completed",
            Result = "passed",
            DurationSeconds = 12.5,
            Host = "runner-01",
        });

        Assert.NotNull(running!.StartedAt);
        Assert.Equal("passed", completed!.Result);
        Assert.Equal(12.5, completed.DurationSeconds);
        Assert.NotNull(completed.CompletedAt);
        Assert.Equal("passed", Assert.Single(new TestRunStore(stack.Configuration).List(stack.Project.Id)).Result);
        Assert.Throws<TestRunValidationException>(() => stack.Service.Update("Demo", planned.Id, new UpdateTestRunRequest
        {
            State = "running",
        }));
    }

    [Fact]
    public void CompletedCard_WaitsForPlannedExactRun_InsteadOfClaimingGreen()
    {
        var stack = BuildStack();
        var commit = RevParse(stack.Repo, "HEAD");
        var run = stack.Service.Create("Demo", Request(commit, "planned", null))!;
        var job = Task("done", stack.Storage, commit) with { State = TaskStates.Completed };

        var evidence = stack.Service.BuildLookup([job])[job.TaskKey];

        Assert.Equal(run.Id, evidence.RunId);
        Assert.Equal("pending", evidence.EvidenceState);
        Assert.True(evidence.AwaitingEvidence);
        Assert.StartsWith("Evidence pending", evidence.Summary);
    }

    [Fact]
    public void Deployment_DefaultsToLatestGreenRun_AndReportsDistanceToHead()
    {
        var stack = BuildStack();
        var testedCommit = RevParse(stack.Repo, "HEAD");
        var run = stack.Service.Create("Demo", Request(testedCommit, "completed", "passed"))!;
        Commit(stack.Repo, "untested.txt", "new", "Untested Head change");
        var head = RevParse(stack.Repo, "HEAD");

        var evidence = stack.Service.LastGreenForDeployment("Demo", head);

        Assert.NotNull(evidence);
        Assert.Equal(run.Id, evidence!.Id);
        Assert.Equal(testedCommit, evidence.Commit);
        Assert.Equal(1, evidence.DistanceToHead);
        Assert.Equal("head-ahead", evidence.HeadDirection);
    }

    private Stack BuildStack()
    {
        var workspace = Path.Combine(_root, Guid.NewGuid().ToString("N"), "workspace");
        var storage = Path.Combine(workspace, "projects", "demo", "tasks");
        var repo = Path.Combine(_root, Guid.NewGuid().ToString("N"), "repo");
        Directory.CreateDirectory(storage);
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-b", "develop");
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "user.name", "Test User");
        Git(repo, "config", "commit.gpgsign", "false");
        Commit(repo, "initial.txt", "initial", "Initial");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = workspace,
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:Path"] = storage,
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
        }).Build();
        var registry = new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance);
        var project = registry.EnsureProjectForStorage(storage, "Demo", DefaultWorkspace.Id);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration);
        var scanner = new TaskScannerService(configuration, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
        var store = new TestRunStore(configuration);
        return new Stack(configuration, storage, repo, project, new TestRunService(store, registry, scanner, git));
    }

    private static CreateTestRunRequest Request(string commit, string state, string? result) => new()
    {
        Trigger = "manual",
        Commit = commit,
        Branch = "develop",
        Scope = new TestRunScope { Level = "project", TestSet = "all" },
        State = state,
        Result = result,
        DurationSeconds = state == "completed" ? 4 : null,
        Host = state == "planned" ? null : "runner-01",
    };

    private static TaskInfo Task(string id, string storage, string commit) => new()
    {
        Id = id,
        TaskKey = "PROJ-001::" + id,
        Key = "DEM-" + id.Length,
        Title = id,
        State = TaskStates.HumanReview,
        WatchPath = storage,
        ProjectName = "Demo",
        Commit = new TaskCommitInfo { Sha = commit, ShortSha = commit[..7], Message = id },
        Commits = [new TaskCommitInfo { Sha = commit, ShortSha = commit[..7], Message = id }],
    };

    private static void Commit(string repo, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(repo, file), content);
        Git(repo, "add", "--", file);
        Git(repo, "commit", "-m", message);
    }

    private static string RevParse(string repo, string value) => Git(repo, "rev-parse", value).Trim();

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(15_000), "git command timed out");
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(error), error);
        return output;
    }

    private sealed record Stack(
        IConfiguration Configuration,
        string Storage,
        string Repo,
        ProjectRecord Project,
        TestRunService Service);
}
