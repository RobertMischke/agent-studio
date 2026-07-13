using System.Diagnostics;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ReadOnlyGitRefFingerprintTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "git-ref-fingerprint-" + Guid.NewGuid().ToString("N"));

    public ReadOnlyGitRefFingerprintTests()
    {
        Directory.CreateDirectory(_root);
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "test");
        File.WriteAllText(Path.Combine(_root, "seed.txt"), "seed");
        Git("add", "-A");
        Git("commit", "-q", "-m", "seed");
        Git("branch", "develop");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Capture_IsStableForWorkingTreeAndUnrelatedTaskBranchChanges()
    {
        var before = ReadOnlyGitRefFingerprint.Capture(_root, ["develop", "main"]);

        File.WriteAllText(Path.Combine(_root, "uncommitted.txt"), "working tree only");
        Git("branch", "task/unrelated");
        var after = ReadOnlyGitRefFingerprint.Capture(_root, ["develop", "main"]);

        Assert.Equal(before, after);
    }

    [Fact]
    public void Capture_ChangesWhenTrackedBranchOrTagMoves()
    {
        var before = ReadOnlyGitRefFingerprint.Capture(
            _root, ["develop", "main"], includeTags: true);

        Git("checkout", "-q", "develop");
        File.WriteAllText(Path.Combine(_root, "develop.txt"), "change");
        Git("add", "-A");
        Git("commit", "-q", "-m", "develop change");
        var afterBranch = ReadOnlyGitRefFingerprint.Capture(
            _root, ["develop", "main"], includeTags: true);
        Assert.NotEqual(before, afterBranch);

        Git("tag", "v1.0.0");
        var afterTag = ReadOnlyGitRefFingerprint.Capture(
            _root, ["develop", "main"], includeTags: true);
        Assert.NotEqual(afterBranch, afterTag);
    }

    private void Git(params string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
    }
}
