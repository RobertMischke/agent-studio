using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.Shared;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteCommitAttributionGuardTests
{
    [Fact]
    public void Attribute_ExactRunnerBranch_PersistsEveryBranchCommitAsAutomatic()
    {
        var commits = new[]
        {
            Commit("1111111111111111111111111111111111111111", "feat: first change"),
            Commit("2222222222222222222222222222222222222222", "test(AGT-2389): cover the change"),
        };

        var result = RemoteCommitAttributionGuard.Attribute(
            "AGT-2389",
            "runner/agent-runner-01/AGT-2389",
            commits);

        Assert.True(result.Accepted, result.Warning);
        Assert.Equal(2, result.Commits.Count);
        Assert.All(result.Commits, commit =>
        {
            Assert.Equal(CommitAttributionKinds.Automatic, commit.Attribution);
            Assert.Equal(1.0, commit.Confidence);
        });
        Assert.Equal(commits.Select(commit => commit.Sha), result.Commits.Select(commit => commit.Sha));
    }

    [Fact]
    public void Attribute_ImmutableResultEnvelopeRef_IsAcceptedAsTaskNeutral()
    {
        // The envelope ref carries the run id + result SHA, never the task key.
        // Its identity is the fenced result SHA the caller verified, so the
        // task-key suffix rule must not reject it (AGT-2434/AGT-2445: every
        // canonical remote card lost its attributed commits).
        var result = RemoteCommitAttributionGuard.Attribute(
            "AGT-2445",
            "refs/heads/agent-studio/results/run_26f460aab3b64600837bd8a54745bd37/3cd7da14403e934f1fc00a4782a24bb799a398a1",
            [Commit("3cd7da14403e934f1fc00a4782a24bb799a398a1", "docs: link research convention")]);

        Assert.True(result.Accepted, result.Warning);
        Assert.Single(result.Commits);
        Assert.Equal(CommitAttributionKinds.Automatic, result.Commits[0].Attribution);
    }

    [Fact]
    public void Attribute_ImmutableResultEnvelopeRef_StillRejectsForeignTaskSubjects()
    {
        var result = RemoteCommitAttributionGuard.Attribute(
            "AGT-2445",
            "agent-studio/results/run_26f460aab3b64600837bd8a54745bd37/3cd7da14403e934f1fc00a4782a24bb799a398a1",
            [Commit("1111111111111111111111111111111111111111", "fix(AGT-9999): foreign change")]);

        Assert.False(result.Accepted);
        Assert.Contains("AGT-9999", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Attribute_ForeignTaskKeyInAnySubject_RejectsTheWholeRange()
    {
        var result = RemoteCommitAttributionGuard.Attribute(
            "AGT-2242",
            "runner/agent-runner-01/AGT-2242",
            [
                Commit("1111111111111111111111111111111111111111", "fix(AGT-2242): own change"),
                Commit("2222222222222222222222222222222222222222", "docs(AGT-2240): foreign change"),
            ]);

        Assert.False(result.Accepted);
        Assert.Empty(result.Commits);
        Assert.Contains("AGT-2240", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Attribute_BranchForAnotherTask_RejectsTheWholeRange()
    {
        var result = RemoteCommitAttributionGuard.Attribute(
            "AGT-2386",
            "runner/agent-runner-01/AGT-2387",
            [Commit("1111111111111111111111111111111111111111", "feat: change")]);

        Assert.False(result.Accepted);
        Assert.Empty(result.Commits);
        Assert.Contains("AGT-2386", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void InspectRemoteDeliveryCommitRange_FetchesExactMergeBaseToPushedTip()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "remote-attribution-range-" + Guid.NewGuid().ToString("N"));
        var remote = Path.Combine(root, "origin.git");
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(root);
        try
        {
            RunGit(root, $"init -q --bare \"{remote}\"");
            RunGit(root, $"clone -q \"{remote}\" \"{repo}\"");
            RunGit(repo, "config user.email test@example.com");
            RunGit(repo, "config user.name Test");
            RunGit(repo, "checkout -q -b main");
            File.WriteAllText(Path.Combine(repo, "base.txt"), "base");
            RunGit(repo, "add -A");
            RunGit(repo, "commit -q -m \"chore: base\"");
            var baseSha = RunGit(repo, "rev-parse HEAD");
            RunGit(repo, "push -q origin main");
            RunGit(repo, "checkout -q -b develop");
            RunGit(repo, "push -q origin develop");
            RunGit(repo, "checkout -q -b runner/agent-runner-01/AGT-2389 main");
            File.WriteAllText(Path.Combine(repo, "first.txt"), "first");
            RunGit(repo, "add -A");
            RunGit(repo, "commit -q -m \"feat: first\"");
            var first = RunGit(repo, "rev-parse HEAD");
            File.WriteAllText(Path.Combine(repo, "second.txt"), "second");
            RunGit(repo, "add -A");
            RunGit(repo, "commit -q -m \"test(AGT-2389): second\"");
            var tip = RunGit(repo, "rev-parse HEAD");
            RunGit(repo, "push -q origin runner/agent-runner-01/AGT-2389");

            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["WatchPaths:0:Name"] = "Fixture",
                    ["WatchPaths:0:RootPath"] = repo,
                    ["WatchPaths:0:RepositoryPath"] = repo,
                    ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
                }).Build();
            var summary = new SummaryGenerationService(
                NullLogger<SummaryGenerationService>.Instance,
                config);
            var scanner = new TaskScannerService(
                config,
                NullLogger<TaskScannerService>.Instance,
                summary);
            var git = new GitService(NullLogger<GitService>.Instance, scanner, config);

            var range = git.InspectRemoteDeliveryCommitRange(
                repo,
                "runner/agent-runner-01/AGT-2389",
                tip,
                "refs/heads/main");

            Assert.True(range.Success, range.Warning);
            Assert.Equal("refs/heads/main", range.IntegrationBranch);
            Assert.Equal(baseSha, range.MergeBaseSha);
            Assert.Equal(tip, range.TipSha);
            Assert.Equal([first, tip], range.Commits.Select(commit => commit.Sha));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static GitCommitInfo Commit(string sha, string subject) =>
        new(
            sha,
            sha[..8],
            DateTime.SpecifyKind(new DateTime(2026, 7, 28), DateTimeKind.Utc),
            "Agent Studio Runner",
            subject,
            1,
            1,
            0);

    private static string RunGit(string cwd, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {arguments} failed: {error}");
        return output.Trim();
    }
}
