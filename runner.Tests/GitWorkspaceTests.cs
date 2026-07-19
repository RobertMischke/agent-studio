using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class GitWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-runner-git-" + Guid.NewGuid().ToString("N"));
    private readonly string _origin;
    private readonly string _workDir;

    public GitWorkspaceTests()
    {
        _origin = Path.Combine(_root, "origin.git");
        _workDir = Path.Combine(_root, "runner");
    }

    [Fact]
    public async Task Teardown_commits_and_pushes_uncommitted_changes_before_removal()
    {
        await SeedOriginAsync();
        var workspace = CreateWorkspace();
        await workspace.PrepareAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(workspace.RepoPath, "work.txt"), "valuable work");

        var result = await workspace.TeardownAsync("Unknown", CancellationToken.None);

        Assert.True(result.SecuredWork);
        Assert.Equal("runner/runner-test/AGT-2147", result.Branch);
        Assert.False(Directory.Exists(workspace.RepoPath));
        Assert.Equal("valuable work", (await GitAsync(_origin,
            "show", $"refs/heads/{result.Branch}:work.txt")).StdOut);
        Assert.Equal("wip(runner): salvage before teardown - outcome Unknown",
            (await GitAsync(_origin, "log", "-1", "--format=%s", $"refs/heads/{result.Branch}")).StdOut);
    }

    [Fact]
    public async Task Teardown_pushes_an_unpushed_local_commit_before_removal()
    {
        await SeedOriginAsync();
        var workspace = CreateWorkspace();
        await workspace.PrepareAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(workspace.RepoPath, "committed.txt"), "local commit");
        await GitAsync(workspace.RepoPath, "add", "--all");
        await GitAsync(workspace.RepoPath, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "local work");
        var localHead = (await GitAsync(workspace.RepoPath, "rev-parse", "HEAD")).StdOut;

        var result = await workspace.TeardownAsync("Done", CancellationToken.None);

        Assert.True(result.SecuredWork);
        Assert.False(Directory.Exists(workspace.RepoPath));
        Assert.Equal(localHead,
            (await GitAsync(_origin, "rev-parse", $"refs/heads/{result.Branch}")).StdOut);
    }

    [Fact]
    public async Task Teardown_removes_a_clean_worktree_without_creating_a_salvage_branch()
    {
        await SeedOriginAsync();
        var workspace = CreateWorkspace();
        await workspace.PrepareAsync(CancellationToken.None);

        var result = await workspace.TeardownAsync("Done", CancellationToken.None);

        Assert.False(result.SecuredWork);
        Assert.False(Directory.Exists(workspace.RepoPath));
        var branch = await RunGitAsync(_origin, "show-ref", "--verify", "--quiet",
            "refs/heads/runner/runner-test/AGT-2147");
        Assert.False(branch.Success);
    }

    [Fact]
    public async Task Teardown_keeps_the_worktree_when_the_salvage_push_fails()
    {
        await SeedOriginAsync();
        var workspace = CreateWorkspace();
        await workspace.PrepareAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(workspace.RepoPath, "work.txt"), "must survive");
        var offlineOrigin = _origin + ".offline";
        Directory.Move(_origin, offlineOrigin);

        var error = await Assert.ThrowsAsync<WorktreeSalvageException>(
            () => workspace.TeardownAsync("Unknown", CancellationToken.None));

        Assert.Equal(workspace.RepoPath, error.WorktreePath);
        Assert.True(Directory.Exists(workspace.RepoPath));
        Assert.Equal("must survive", await File.ReadAllTextAsync(Path.Combine(workspace.RepoPath, "work.txt")));
        Directory.Move(offlineOrigin, _origin);
    }

    [Fact]
    public async Task Requeue_resumes_and_relinks_the_existing_salvage_branch()
    {
        await SeedOriginAsync();
        var first = CreateWorkspace();
        await first.PrepareAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(first.RepoPath, "work.txt"), "first run");
        var salvaged = await first.TeardownAsync("Unknown", CancellationToken.None);

        var requeue = CreateWorkspace();
        var sourceBranch = await requeue.PrepareAsync(CancellationToken.None);
        Assert.Equal(salvaged.Branch, sourceBranch);
        Assert.Equal("first run", await File.ReadAllTextAsync(Path.Combine(requeue.RepoPath, "work.txt")));

        var result = await requeue.TeardownAsync("Done", CancellationToken.None);

        Assert.True(result.SecuredWork);
        Assert.Equal(salvaged.Branch, result.Branch);
        Assert.Equal(salvaged.CommitSha, result.CommitSha);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private GitWorkspace CreateWorkspace()
        => new(new RunnerOptions
        {
            ServerUrl = "http://localhost",
            RunnerId = "runner-test",
            RunnerName = "runner-test",
            Hostname = "test-host",
            BackendName = "test",
            GitRemote = _origin,
            WorkDir = _workDir,
            BaseBranch = "main",
            CliBin = "test",
            CliArgs = "",
        }, "AGT-2147", _ => { });

    private async Task SeedOriginAsync()
    {
        Directory.CreateDirectory(_root);
        var seed = Path.Combine(_root, "seed");
        await GitAsync(_root, "init", "--bare", _origin);
        await GitAsync(_root, "init", seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "seed");
        await GitAsync(seed, "add", "--all");
        await GitAsync(seed, "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-m", "seed");
        await GitAsync(seed, "branch", "-M", "main");
        await GitAsync(seed, "remote", "add", "origin", _origin);
        await GitAsync(seed, "push", "-u", "origin", "main");
    }

    private static async Task<ProcessResult> GitAsync(string workingDirectory, params string[] args)
    {
        var result = await RunGitAsync(workingDirectory, args);
        Assert.True(result.Success,
            $"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.StdErr}");
        return new ProcessResult(result.ExitCode, result.StdOut.Trim(), result.StdErr.Trim());
    }

    private static Task<ProcessResult> RunGitAsync(string workingDirectory, params string[] args)
        => ProcessRunner.RunAsync("git", args, workingDirectory: workingDirectory);
}
