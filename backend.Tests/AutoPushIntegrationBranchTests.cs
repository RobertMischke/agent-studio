using System.Diagnostics;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2688 regression: the platform-owned auto-push must publish a task commit
/// to the project's configured integration branch, not to a hard-coded
/// <c>main</c>.
///
/// <para>
/// The overnight 2026-08-27/28 incident produced 570+
/// <c>Auto-push skipped ... (completed): lineage-blocked</c> warnings. Cause:
/// <c>TaskTransitionService.TryPushCommitAsync</c> called
/// <c>GitService.PushShaAsync</c> without a target branch, so it defaulted to
/// <c>main</c>. In a repository that also carries a <c>develop</c> work line,
/// <c>ImmediateIntegrationLineagePolicy.DecideDirectMainAdvance</c> correctly
/// refuses a raw task commit at <c>main</c> (it is not the published develop
/// tip). Every completed-job auto-push therefore returned
/// <c>lineage-blocked</c> and no platform commit reached origin at all - the
/// managed repository drifted over a thousand commits behind the remote while
/// each card still reported completed.
/// </para>
///
/// <para>
/// These tests drive the real <see cref="TaskTransitionService"/> against real
/// git repositories and a real bare origin, so the branch decision and the push
/// are exercised end to end rather than mocked.
/// </para>
/// </summary>
public sealed class AutoPushIntegrationBranchTests : IDisposable
{
    private const string ProjectName = "demo";

    private readonly string _tempRoot;

    public AutoPushIntegrationBranchTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "atp-autopush-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempRoot, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* best-effort */ }
            }
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// The incident scenario: a dual-line repository whose work happens on
    /// <c>develop</c>. The auto-push must land the task commit on
    /// <c>origin/develop</c> and leave <c>main</c> alone. Before the fix this
    /// returned 0 pushes with status <c>lineage-blocked</c>.
    /// </summary>
    [Fact]
    public async Task AutoPush_DualLineRepo_PublishesTaskCommitToConfiguredIntegrationBranch()
    {
        var harness = BuildHarness("dual-line");
        var bare = SeedOrigin(harness.RepoPath, "dual-line");

        // main is published; develop is the work line and is published too.
        var publishedMain = Git(harness.RepoPath, "rev-parse main").Out.Trim();
        Assert.Equal(0, Git(harness.RepoPath, "checkout -q -b develop").Code);
        Assert.Equal(0, Git(harness.RepoPath, "push -q -u origin develop").Code);

        harness.Settings.SetIntegrationBranch(ProjectName, "develop");

        // A platform-owned task commit lands on the develop work line.
        var taskSha = CommitFile(harness.RepoPath, "task.txt", "task work", "feat: task work");

        var pushed = await harness.Transitions.PushCompletedJobCommitsAsync(
            BuildCompletedJob(harness.RepoPath, taskSha),
            AutoPushStrategies.OnCompleted);

        Assert.Equal(1, pushed);
        Assert.Equal(taskSha, RemoteTip(bare, "develop"));
        // The lineage guard still owns main: it must not have been advanced.
        Assert.Equal(publishedMain, RemoteTip(bare, "main"));
    }

    /// <summary>
    /// A single-line repository has no configured integration branch, so
    /// resolution falls back to the repository default branch and the historical
    /// push-to-main behaviour is preserved.
    /// </summary>
    [Fact]
    public async Task AutoPush_SingleLineRepo_StillPublishesToMain()
    {
        var harness = BuildHarness("single-line");
        var bare = SeedOrigin(harness.RepoPath, "single-line");

        var taskSha = CommitFile(harness.RepoPath, "task.txt", "task work", "feat: task work");

        var pushed = await harness.Transitions.PushCompletedJobCommitsAsync(
            BuildCompletedJob(harness.RepoPath, taskSha),
            AutoPushStrategies.OnCompleted);

        Assert.Equal(1, pushed);
        Assert.Equal(taskSha, RemoteTip(bare, "main"));
    }

    // ---- harness ---------------------------------------------------------

    private sealed record Harness(
        string RepoPath,
        ProjectSettingsService Settings,
        TaskTransitionService Transitions);

    private Harness BuildHarness(string slug)
    {
        var workspaceRoot = Path.Combine(_tempRoot, "ws-" + slug);
        var repoPath = Path.Combine(workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(repoPath);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(repoPath, state));

        SeedRepo(repoPath);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = repoPath,
                ["WatchPaths:0:RootPath"] = repoPath,
                ["WatchPaths:0:RepositoryPath"] = repoPath,
                ["TaskRepository"] = workspaceRoot
            })
            .Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var clients = new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance);
        var mutations = new TaskMutationService(
            scanner, clients, new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var transitions = new TaskTransitionService(
            scanner, states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);

        return new Harness(repoPath, settings, transitions);
    }

    private static TaskInfo BuildCompletedJob(string repoPath, string sha) => new()
    {
        Id = "AGT-2688",
        State = TaskStates.Completed,
        ProjectName = ProjectName,
        WatchPath = repoPath,
        Commits =
        [
            new TaskCommitInfo
            {
                Sha = sha,
                ShortSha = sha[..7],
                Message = "feat: task work",
                FilesChanged = 1,
                Files = ["task.txt"],
                At = new DateTime(2026, 8, 28, 3, 0, 0, DateTimeKind.Utc)
            }
        ]
    };

    // ---- git helpers -----------------------------------------------------

    private static void SeedRepo(string path)
    {
        Assert.Equal(0, Git(path, "init -q -b main").Code);
        Assert.Equal(0, Git(path, "config user.email test@example.com").Code);
        Assert.Equal(0, Git(path, "config user.name Test").Code);
        CommitFile(path, "seed.txt", "seed", "chore: seed");
    }

    private string SeedOrigin(string repoPath, string slug)
    {
        var bare = Path.Combine(_tempRoot, "origin-" + slug + ".git");
        Assert.Equal(0, Git(_tempRoot, $"init -q --bare \"{bare}\"").Code);
        Assert.Equal(0, Git(repoPath, $"remote add origin \"{bare}\"").Code);
        Assert.Equal(0, Git(repoPath, "push -q -u origin main").Code);
        return bare;
    }

    private static string CommitFile(string repoPath, string name, string content, string message)
    {
        File.WriteAllText(Path.Combine(repoPath, name), content);
        Assert.Equal(0, Git(repoPath, $"add \"{name}\"").Code);
        Assert.Equal(0, Git(repoPath, $"commit -q -m \"{message}\"").Code);
        return Git(repoPath, "rev-parse HEAD").Out.Trim();
    }

    /// <summary>
    /// The origin is bare; <c>safe.bareRepository=explicit</c> means the git dir
    /// must be named rather than entered.
    /// </summary>
    private string RemoteTip(string bare, string branch)
        => Git(_tempRoot, $"--git-dir=\"{bare}\" rev-parse refs/heads/{branch}").Out.Trim();

    private static (string Out, string Err, int Code) Git(string workingDirectory, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (stdout, stderr, process.ExitCode);
    }
}
