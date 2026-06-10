using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Author hygiene for platform-owned landing commits. The regular completion
/// path lands a worktree run via <see cref="GitService.WorktreeRunCommit"/>,
/// which MUST use the configured git identity - not the <c>Crash Recovery</c>
/// author that <see cref="GitService.CrashRecoveryCommit"/> stamps for the
/// boot-time orphan-rescue exception net. Reusing CrashRecoveryCommit for
/// normal landings made every landing show <c>author='Crash Recovery'</c> once
/// Always-Worktree routed all runs through the worktree-integration path.
/// </summary>
public class GitServiceWorktreeRunCommitTests : IDisposable
{
    private readonly string _tempDir;

    public GitServiceWorktreeRunCommitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "git-worktree-commit-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public void WorktreeRunCommit_UsesConfiguredIdentity_NotCrashRecoveryAuthor()
    {
        var repoRoot = SeedRepo("Ada Lovelace", "ada@example.com");
        var git = BuildGitService(repoRoot);

        File.WriteAllText(Path.Combine(repoRoot, "feature.txt"), "agent work");
        var trailer = GitService.WorktreeRunCommitTrailer("my-task");
        var result = git.WorktreeRunCommit("Proj", repoRoot, $"Implement feature\n\n{trailer}");

        Assert.True(result.Success, result.Error);
        Assert.Equal("Ada Lovelace", RunGitCapture(repoRoot, "log -1 --format=%an"));
        Assert.NotEqual("Crash Recovery", RunGitCapture(repoRoot, "log -1 --format=%an"));
        // The durable per-run trailer must survive so ASS-1712 history
        // reconstruction still finds the landing.
        Assert.Contains(trailer, RunGitCapture(repoRoot, "log -1 --format=%B"));
    }

    [Fact]
    public void CrashRecoveryCommit_StillStampsCrashRecoveryAuthor()
    {
        var repoRoot = SeedRepo("Ada Lovelace", "ada@example.com");
        var git = BuildGitService(repoRoot);

        File.WriteAllText(Path.Combine(repoRoot, "orphan.txt"), "rescued work");
        var result = git.CrashRecoveryCommit("Proj", repoRoot, "chore(crash-recovery): rescue orphan changes for my-task");

        Assert.True(result.Success, result.Error);
        Assert.Equal("Crash Recovery", RunGitCapture(repoRoot, "log -1 --format=%an"));
    }

    [Fact]
    public void WorktreeRunCommit_CleanTree_ReportsNothingToCommit()
    {
        var repoRoot = SeedRepo("Ada Lovelace", "ada@example.com");
        var git = BuildGitService(repoRoot);

        var result = git.WorktreeRunCommit("Proj", repoRoot, "no changes");

        Assert.False(result.Success);
        Assert.Contains("Nothing to commit", result.Error);
    }

    private string SeedRepo(string userName, string userEmail)
    {
        var repoRoot = Path.Combine(_tempDir, "repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoRoot);
        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, $"config user.email {userEmail}");
        RunGit(repoRoot, $"config user.name \"{userName}\"");
        File.WriteAllText(Path.Combine(repoRoot, "README.md"), "seed");
        RunGit(repoRoot, "add -A");
        RunGit(repoRoot, "commit -q -m seed");
        return repoRoot;
    }

    private static GitService BuildGitService(string repoRoot)
    {
        var dict = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Proj",
            ["WatchPaths:0:RootPath"] = repoRoot,
            ["WatchPaths:0:Path"] = Path.Combine(repoRoot, ".orchestrator", "jobs"),
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private static void RunGit(string cwd, string args)
    {
        using var p = Process.Start(MakePsi(cwd, args))!;
        p.WaitForExit(15_000);
    }

    private static string RunGitCapture(string cwd, string args)
    {
        using var p = Process.Start(MakePsi(cwd, args))!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);
        return output.Trim();
    }

    private static ProcessStartInfo MakePsi(string cwd, string args) => new()
    {
        FileName = "git",
        Arguments = args,
        WorkingDirectory = cwd,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
}
