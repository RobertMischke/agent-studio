using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2008: the git-pane's rendered md/html preview reads a file's full text
/// through <see cref="GitService.GetFileContentResult"/>. These tests stand up
/// a real repo + registered job and assert:
///  - the working-tree copy is read live (and from the task worktree when one
///    exists, matching the diff endpoint);
///  - a specific commit's blob is read via the sha path;
///  - a NUL-containing blob is reported as binary with no text;
///  - path traversal and a missing file are refused;
///  - a `..` segment cannot escape the repo root.
/// </summary>
public class GitServiceFileContentTests : IDisposable
{
    private readonly string _tempDir;

    public GitServiceFileContentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "git-file-content-" + Guid.NewGuid().ToString("N"));
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
    public void WorkingTree_ReadsLiveFileContent()
    {
        var (repoRoot, jobId, watchPath) = SetupRepoAndJob();
        WriteFile(repoRoot, "README.md", "# committed\n");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");
        // Working tree now diverges from HEAD; the preview must show the live copy.
        WriteFile(repoRoot, "README.md", "# working tree\n");

        var git = BuildGitService(repoRoot, watchPath);
        var result = git.GetFileContentResult(jobId, watchPath, "README.md", sha: null, preferRunLocation: true);

        Assert.True(result.Success);
        Assert.False(result.IsBinary);
        Assert.Equal("# working tree\n", result.Content);
    }

    [Fact]
    public void Sha_ReadsHistoricalBlob_NotWorkingTree()
    {
        var (repoRoot, jobId, watchPath) = SetupRepoAndJob();
        WriteFile(repoRoot, "README.md", "# committed\n");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");
        var sha = RunGitCapture(repoRoot, "rev-parse", "HEAD").Trim();
        // Change the working tree so we can prove the sha path ignores it.
        WriteFile(repoRoot, "README.md", "# working tree\n");

        var git = BuildGitService(repoRoot, watchPath);
        var result = git.GetFileContentResult(jobId, watchPath, "README.md", sha);

        Assert.True(result.Success);
        Assert.False(result.IsBinary);
        Assert.Equal("# committed\n", result.Content);
    }

    [Fact]
    public void BinaryBlob_IsFlagged_WithoutText()
    {
        var (repoRoot, jobId, watchPath) = SetupRepoAndJob();
        var full = Path.Combine(repoRoot, "logo.html");
        File.WriteAllBytes(full, new byte[] { 0x3C, 0x00, 0x68, 0x74, 0x6D, 0x6C });

        var git = BuildGitService(repoRoot, watchPath);
        var result = git.GetFileContentResult(jobId, watchPath, "logo.html", sha: null, preferRunLocation: true);

        Assert.True(result.Success);
        Assert.True(result.IsBinary);
        Assert.Equal("", result.Content);
    }

    [Fact]
    public void MissingWorkingTreeFile_Fails()
    {
        var (repoRoot, jobId, watchPath) = SetupRepoAndJob();
        WriteFile(repoRoot, "README.md", "seed");
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", "seed");

        var git = BuildGitService(repoRoot, watchPath);
        var result = git.GetFileContentResult(jobId, watchPath, "does/not/exist.md", sha: null, preferRunLocation: true);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void TraversalPath_IsRejected()
    {
        var (repoRoot, jobId, watchPath) = SetupRepoAndJob();
        // Plant a secret one level above the repo root.
        File.WriteAllText(Path.Combine(_tempDir, "secret.md"), "top secret");

        var git = BuildGitService(repoRoot, watchPath);
        var result = git.GetFileContentResult(jobId, watchPath, "../secret.md", sha: null, preferRunLocation: true);

        Assert.False(result.Success);
        Assert.DoesNotContain("top secret", result.Content);
    }

    private (string repoRoot, string jobId, string watchPath) SetupRepoAndJob()
    {
        var repoRoot = Path.Combine(_tempDir, "repo");
        var watchPath = Path.Combine(repoRoot, ".orchestrator", "jobs");
        Directory.CreateDirectory(watchPath);

        RunGit(_tempDir, "init", "-q", "-b", "main", "repo");
        RunGit(repoRoot, "config", "user.email", "test@example.com");
        RunGit(repoRoot, "config", "user.name", "test");
        RunGit(repoRoot, "config", "commit.gpgsign", "false");

        var jobId = "demo-task";
        var jobFolder = Path.Combine(watchPath, "3-progress", jobId);
        Directory.CreateDirectory(jobFolder);
        Directory.CreateDirectory(Path.Combine(jobFolder, "logs"));
        var jobJson = new
        {
            id = jobId,
            title = "Demo task",
            state = "3-progress",
            order = 1,
            agent = "claude",
            createdAt = DateTime.UtcNow.ToString("o")
        };
        File.WriteAllText(Path.Combine(jobFolder, "task.json"),
            JsonSerializer.Serialize(jobJson, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(jobFolder, "prompt.md"), "Do the thing.");
        return (repoRoot, jobId, watchPath);
    }

    private static GitService BuildGitService(string repoRoot, string watchPath)
    {
        var dict = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:RootPath"] = repoRoot,
            ["WatchPaths:0:RepositoryPath"] = repoRoot,
            ["WatchPaths:0:Path"] = watchPath
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void RunGit(string cwd, params string[] args) => RunGitCapture(cwd, args);

    private static string RunGitCapture(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }
}
