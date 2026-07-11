using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using AgentStudio.Runner;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Production resolver coverage for the named AGT-2067 contract. The monitor
/// tests use a fake resolver to isolate lane routing; this fixture proves the
/// real resolver only answers an already-implemented question after the task
/// branch is actually an ancestor of the configured integration branch.
/// </summary>
public sealed class SteerTimeoutResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _repoRoot;
    private const string TaskBranch = "task/AGT-2067";

    public SteerTimeoutResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "steer-timeout-resolver-" + Guid.NewGuid().ToString("N"));
        _repoRoot = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(_repoRoot);
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "test");
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "seed\n");
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "seed");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public void AlreadyImplementedQuestion_AnswersOnlyAfterTaskBranchIsIntegrated()
    {
        RunGit("checkout", "-q", "-b", TaskBranch);
        File.WriteAllText(Path.Combine(_repoRoot, "iframe.ts"), "export const iframe = true;\n");
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "feat: implement iframe");

        var resolver = BuildResolver();
        var context = Context("ist iframe schon implementiert?");

        var beforeMerge = resolver.Resolve(context);
        Assert.False(beforeMerge.HasAnswer);
        Assert.Contains("not yet an ancestor", beforeMerge.AmbiguityReason);

        RunGit("checkout", "-q", "main");
        RunGit("merge", "-q", "--ff-only", TaskBranch);

        var afterMerge = resolver.Resolve(context);
        Assert.True(afterMerge.HasAnswer);
        Assert.Contains("already integrated", afterMerge.AnswerText);
        Assert.Contains("[[TASK_DONE]]", afterMerge.AnswerText);
    }

    [Fact]
    public void OpenEndedQuestion_RemainsAmbiguousEvenWhenBranchIsIntegrated()
    {
        RunGit("branch", TaskBranch);

        var result = BuildResolver().Resolve(Context("Should I also refactor the shared helper?"));

        Assert.False(result.HasAnswer);
        Assert.Contains("not an 'is this already implemented?' question", result.AmbiguityReason);
    }

    private SteerResolveContext Context(string question) => new(
        Project: "demo",
        JobId: "AGT-2067",
        JobFolder: Path.Combine(_tempDir, "job"),
        WatchPath: Path.Combine(_tempDir, "tasks"),
        Question: question,
        RepoRoot: _repoRoot,
        TaskBranch: TaskBranch,
        ConfiguredIntegrationBranch: "main");

    private SteerTimeoutResolver BuildResolver()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "demo",
            ["WatchPaths:0:RootPath"] = _repoRoot,
            ["WatchPaths:0:Path"] = Path.Combine(_tempDir, "tasks"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        return new SteerTimeoutResolver(git, NullLogger<SteerTimeoutResolver>.Instance);
    }

    private void RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stdout} {stderr}");
    }
}
