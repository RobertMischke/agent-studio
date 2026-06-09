using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.RegressionRadar;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

public sealed class RegressionRadarServiceTests
{
    // --- IsSpecFile ---

    [Theory]
    [InlineData("src/app/task.service.spec.ts", true)]
    [InlineData("src/app/task.service.test.ts", true)]
    [InlineData("src/app/task.service.tests.ts", true)]
    [InlineData("e2e/board.spec.ts", true)]
    [InlineData("backend.Tests/FooTests.cs", true)]
    [InlineData("src/helpers.test.tsx", true)]
    [InlineData("src/app/task.service.ts", false)]
    [InlineData("backend/Services/Foo.cs", false)]
    [InlineData("docs/readme.md", false)]
    [InlineData("package.json", false)]
    public void IsSpecFile_ClassifiesCorrectly(string path, bool expected)
    {
        Assert.Equal(expected, RegressionRadarService.IsSpecFile(path));
    }

    // --- ResolveCompanionPath ---

    [Theory]
    [InlineData("src/app/task.service.spec.ts", "src/app/task.service.ts")]
    [InlineData("src/app/task.service.test.ts", "src/app/task.service.ts")]
    [InlineData("e2e/board.spec.tsx", "e2e/board.tsx")]
    [InlineData("src/helpers.test.js", "src/helpers.js")]
    public void ResolveCompanionPath_TypeScript(string specPath, string expected)
    {
        Assert.Equal(expected, RegressionRadarService.ResolveCompanionPath(specPath));
    }

    [Fact]
    public void ResolveCompanionPath_DotNet_ReturnsBaseName()
    {
        var result = RegressionRadarService.ResolveCompanionPath("backend.Tests/Services/FooServiceTests.cs");
        Assert.Equal("FooService.cs", result);
    }

    [Fact]
    public void ResolveCompanionPath_NonSpec_ReturnsNull()
    {
        Assert.Null(RegressionRadarService.ResolveCompanionPath("src/app/task.service.ts"));
    }

    // --- ResolveCompanion (path + changed) ---

    [Fact]
    public void ResolveCompanion_DotNet_ParallelDir_MatchesByBasename()
    {
        // Regression: the test and its implementation live in parallel directory
        // trees, so an exact full-path match never succeeds. The companion must
        // be matched by filename and resolved to the actual changed impl path.
        var nonSpecPaths = new[] { "backend/Services/Runner/PhaseAwareWatchdog.cs" };
        var (companion, changed) = RegressionRadarService.ResolveCompanion(
            "backend.Tests/PhaseAwareWatchdogTests.cs", nonSpecPaths);

        Assert.True(changed);
        Assert.Equal("backend/Services/Runner/PhaseAwareWatchdog.cs", companion);
    }

    [Fact]
    public void ResolveCompanion_DotNet_NoMatchingImpl_NotChanged()
    {
        var nonSpecPaths = new[] { "backend/Services/Other.cs" };
        var (companion, changed) = RegressionRadarService.ResolveCompanion(
            "backend.Tests/PhaseAwareWatchdogTests.cs", nonSpecPaths);

        Assert.False(changed);
        Assert.Equal("PhaseAwareWatchdog.cs", companion);
    }

    [Fact]
    public void ResolveCompanion_TypeScript_SubDir_MatchesExactPath()
    {
        var nonSpecPaths = new[] { "src/app/task.service.ts" };
        var (companion, changed) = RegressionRadarService.ResolveCompanion(
            "src/app/task.service.spec.ts", nonSpecPaths);

        Assert.True(changed);
        Assert.Equal("src/app/task.service.ts", companion);
    }

    [Fact]
    public void ResolveCompanion_TypeScript_SameBasenameDifferentDir_NotChanged()
    {
        // A same-named impl in a different directory must NOT count for TS, where
        // the companion path is known exactly.
        var nonSpecPaths = new[] { "lib/task.service.ts" };
        var (companion, changed) = RegressionRadarService.ResolveCompanion(
            "src/app/task.service.spec.ts", nonSpecPaths);

        Assert.False(changed);
        Assert.Equal("src/app/task.service.ts", companion);
    }

    [Fact]
    public void ResolveCompanion_NonSpec_ReturnsNull()
    {
        var (companion, changed) = RegressionRadarService.ResolveCompanion(
            "src/app/task.service.ts", new[] { "src/app/task.service.ts" });

        Assert.Null(companion);
        Assert.False(changed);
    }

    // --- StripSpecSuffix ---

    [Theory]
    [InlineData("task.service.spec", "task.service")]
    [InlineData("task.service.test", "task.service")]
    [InlineData("FooServiceTests", "FooService")]
    [InlineData("task.service.tests", "task.service")]
    [InlineData("plain-name", "plain-name")]
    public void StripSpecSuffix_Works(string input, string expected)
    {
        Assert.Equal(expected, RegressionRadarService.StripSpecSuffix(input));
    }

    // --- Classify: new files ---

    [Fact]
    public void Classify_AddedSpec_IsIntended()
    {
        var spec = new GitFileChange("A", "src/app/new.spec.ts", 50, 0);
        var result = RegressionRadarService.Classify(spec, null, false, [spec]);
        Assert.Equal(SpecChangeCategory.Intended, result);
    }

    // --- Classify: deleted files ---

    [Fact]
    public void Classify_DeletedSpec_WithoutReplacement_IsDrift()
    {
        var spec = new GitFileChange("D", "src/app/old.spec.ts", 0, 100);
        var result = RegressionRadarService.Classify(spec, null, false, [spec]);
        Assert.Equal(SpecChangeCategory.Drift, result);
    }

    [Fact]
    public void Classify_DeletedSpec_WithReplacement_IsIntended()
    {
        var deleted = new GitFileChange("D", "src/app/old.spec.ts", 0, 100);
        var added = new GitFileChange("A", "src/app/old.spec.ts", 80, 0);
        // Same base name after stripping .spec
        var allSpecs = new List<GitFileChange> { deleted, added };
        var result = RegressionRadarService.Classify(deleted, null, false, allSpecs);
        Assert.Equal(SpecChangeCategory.Intended, result);
    }

    // --- Classify: modified files ---

    [Fact]
    public void Classify_ModifiedSpec_WithCompanionChange_IsIntended()
    {
        var spec = new GitFileChange("M", "src/app/task.service.spec.ts", 10, 5);
        var result = RegressionRadarService.Classify(spec, "src/app/task.service.ts", companionChanged: true, [spec]);
        Assert.Equal(SpecChangeCategory.Intended, result);
    }

    [Fact]
    public void Classify_ModifiedSpec_WithoutCompanionChange_IsAtRisk()
    {
        var spec = new GitFileChange("M", "src/app/task.service.spec.ts", 10, 5);
        var result = RegressionRadarService.Classify(spec, "src/app/task.service.ts", companionChanged: false, [spec]);
        Assert.Equal(SpecChangeCategory.AtRisk, result);
    }

    // --- Classify: renamed files ---

    [Fact]
    public void Classify_RenamedSpec_IsIntended()
    {
        var spec = new GitFileChange("R100", "src/app/new-name.spec.ts", 0, 0);
        var result = RegressionRadarService.Classify(spec, null, false, [spec]);
        Assert.Equal(SpecChangeCategory.Intended, result);
    }

    // --- ClassifyFiles: integration ---

    [Fact]
    public void ClassifyFiles_MixedChanges_ProducesCorrectAggregates()
    {
        var allFiles = new List<GitFileChange>
        {
            new("A", "src/app/new-feature.spec.ts", 50, 0),
            new("A", "src/app/new-feature.ts", 200, 0),
            new("M", "src/app/task.service.spec.ts", 5, 3),
            new("M", "src/app/task.service.ts", 20, 10),
            new("M", "src/app/standalone.spec.ts", 8, 2),
            new("D", "src/app/removed.spec.ts", 0, 80),
        };

        var service = new RegressionRadarService(null!, null!, null!, NullLogger<RegressionRadarService>.Instance);
        var result = service.ClassifyFiles(allFiles, "abc123", "def456", "test-job");

        Assert.Equal(4, result.TotalSpecChanges);
        Assert.Equal(2, result.IntendedCount);   // new + modified-with-companion
        Assert.Equal(1, result.AtRiskCount);      // standalone modified
        Assert.Equal(1, result.DriftCount);        // deleted without replacement
        Assert.Equal(SpecChangeCategory.Drift, result.OverallStatus);
        Assert.Equal("abc123", result.BaselineSha);
        Assert.Equal("def456", result.HeadSha);
    }

    [Fact]
    public void ClassifyFiles_AllIntended_ReturnsIntendedOverall()
    {
        var allFiles = new List<GitFileChange>
        {
            new("A", "src/app/brand-new.spec.ts", 100, 0),
            new("M", "src/app/task.service.spec.ts", 5, 3),
            new("M", "src/app/task.service.ts", 20, 10),
        };

        var service = new RegressionRadarService(null!, null!, null!, NullLogger<RegressionRadarService>.Instance);
        var result = service.ClassifyFiles(allFiles, "aaa", "bbb", "test-job");

        Assert.Equal(2, result.TotalSpecChanges);
        Assert.Equal(2, result.IntendedCount);
        Assert.Equal(0, result.AtRiskCount);
        Assert.Equal(0, result.DriftCount);
        Assert.Equal(SpecChangeCategory.Intended, result.OverallStatus);
    }

    [Fact]
    public void ClassifyFiles_DotNetTestWithParallelImpl_IsIntended()
    {
        // Regression for the false-positive: a modified .NET test plus its impl in
        // a parallel directory tree (the exact ASS-699 / ed131575 shape). Before
        // the basename match this landed as At Risk because the full-path
        // membership test could never succeed.
        var allFiles = new List<GitFileChange>
        {
            new("M", "backend.Tests/PhaseAwareWatchdogTests.cs", 54, 0),
            new("M", "backend/Services/Runner/PhaseAwareWatchdog.cs", 35, 0),
        };

        var service = new RegressionRadarService(null!, null!, null!, NullLogger<RegressionRadarService>.Instance);
        var result = service.ClassifyFiles(allFiles, "aaa", "bbb", "test-job");

        Assert.Equal(1, result.TotalSpecChanges);
        Assert.Equal(1, result.IntendedCount);
        Assert.Equal(0, result.AtRiskCount);
        Assert.Equal(SpecChangeCategory.Intended, result.OverallStatus);

        var entry = Assert.Single(result.Entries);
        Assert.True(entry.CompanionChanged);
        Assert.Equal("backend/Services/Runner/PhaseAwareWatchdog.cs", entry.CompanionPath);
    }

    [Fact]
    public void ClassifyFiles_DotNetTestWithoutImpl_IsAtRisk()
    {
        var allFiles = new List<GitFileChange>
        {
            new("M", "backend.Tests/PhaseAwareWatchdogTests.cs", 54, 0),
            new("M", "backend/Services/Runner/Unrelated.cs", 35, 0),
        };

        var service = new RegressionRadarService(null!, null!, null!, NullLogger<RegressionRadarService>.Instance);
        var result = service.ClassifyFiles(allFiles, "aaa", "bbb", "test-job");

        Assert.Equal(1, result.TotalSpecChanges);
        Assert.Equal(0, result.IntendedCount);
        Assert.Equal(1, result.AtRiskCount);
        Assert.Equal(SpecChangeCategory.AtRisk, result.OverallStatus);
    }

    [Fact]
    public void ClassifyFiles_NoSpecChanges_ReturnsEmpty()
    {
        var allFiles = new List<GitFileChange>
        {
            new("M", "src/app/task.service.ts", 20, 10),
            new("A", "docs/readme.md", 50, 0),
        };

        var service = new RegressionRadarService(null!, null!, null!, NullLogger<RegressionRadarService>.Instance);
        var result = service.ClassifyFiles(allFiles, "aaa", "bbb", "test-job");

        Assert.Equal(0, result.TotalSpecChanges);
        Assert.Equal(SpecChangeCategory.Intended, result.OverallStatus);
    }

    // --- Analyze: metadata stamping ---

    [Fact]
    public void ResolveTaskCommitShas_UsesAttributedCommitChain()
    {
        var info = new TaskInfo
        {
            Id = "radar-task",
            Commit = new TaskCommitInfo { Sha = "legacy" },
            Commits =
            [
                new TaskCommitInfo { Sha = "own-a" },
                new TaskCommitInfo { Sha = "own-b" },
                new TaskCommitInfo { Sha = "own-a" },
            ],
        };

        var shas = RegressionRadarService.ResolveTaskCommitShas(info);

        Assert.Equal(["own-a", "own-b"], shas);
    }

    [Fact]
    public void ResolveTaskCommitShas_FallsBackToLegacyCommit()
    {
        var info = new TaskInfo
        {
            Id = "legacy-task",
            Commit = new TaskCommitInfo { Sha = "legacy-sha" },
        };

        var shas = RegressionRadarService.ResolveTaskCommitShas(info);

        Assert.Equal(["legacy-sha"], shas);
    }

    [Fact]
    public void Analyze_UsesOnlyAttributedTaskCommits_NotInterleavedBranchRange()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "regression-radar-scope-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (repoRoot, watchPath) = SetupRadarRepo(workspace);

            WriteFile(repoRoot, "README.md", "seed\n");
            CommitAll(repoRoot, "seed");

            WriteFile(repoRoot, "src/widget.ts", "export const widget = 1;\n");
            WriteFile(repoRoot, "src/widget.spec.ts", "it('works', () => expect(1).toBe(1));\n");
            var ownSha = CommitAll(repoRoot, "feat: widget");

            WriteFile(repoRoot, "frontend/src/app/unrelated.spec.ts", "it('belongs to another task', () => {});\n");
            var otherSha = CommitAll(repoRoot, "test: unrelated task");

            SeedJob(watchPath, "task-own", "Task with one attributed commit", [ownSha]);
            var service = BuildService(workspace, repoRoot, watchPath);

            var result = service.Analyze("task-own", watchPath);

            Assert.Null(result.Error);
            Assert.Equal(1, result.TotalSpecChanges);
            var entry = Assert.Single(result.Entries);
            Assert.Equal("src/widget.spec.ts", entry.Path);
            Assert.DoesNotContain(result.Entries, e => e.Path == "frontend/src/app/unrelated.spec.ts");
            Assert.Equal(ownSha, result.BaselineSha);
            Assert.Equal(ownSha, result.HeadSha);
            Assert.NotEqual(otherSha, result.HeadSha);
        }
        finally
        {
            DeleteBestEffort(workspace);
        }
    }

    [Fact]
    public void AnalyzeProject_GroupsSpecChangesByEachTasksAttributedCommits()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "regression-radar-project-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (repoRoot, watchPath) = SetupRadarRepo(workspace);

            WriteFile(repoRoot, "README.md", "seed\n");
            WriteFile(repoRoot, "src/b.ts", "export const b = 1;\n");
            WriteFile(repoRoot, "src/b.spec.ts", "it('b', () => expect(b).toBe(1));\n");
            CommitAll(repoRoot, "seed");

            WriteFile(repoRoot, "src/a.ts", "export const a = 1;\n");
            WriteFile(repoRoot, "src/a.spec.ts", "it('a', () => expect(a).toBe(1));\n");
            var taskASha = CommitAll(repoRoot, "feat: task a");

            WriteFile(repoRoot, "src/b.spec.ts", "it('b changed without source', () => expect(true).toBe(true));\n");
            var taskBSha = CommitAll(repoRoot, "test: task b");

            SeedJob(watchPath, "task-a", "Task A", [taskASha]);
            SeedJob(watchPath, "task-b", "Task B", [taskBSha]);
            var service = BuildService(workspace, repoRoot, watchPath);

            var result = service.AnalyzeProject("demo");

            Assert.Null(result.Error);
            Assert.Equal(2, result.TotalSpecChanges);
            Assert.Equal(2, result.TaskGroups.Count);

            var taskA = Assert.Single(result.TaskGroups, g => g.JobId == "task-a");
            Assert.Equal("Task A", taskA.JobTitle);
            Assert.Equal("src/a.spec.ts", Assert.Single(taskA.Entries).Path);

            var taskB = Assert.Single(result.TaskGroups, g => g.JobId == "task-b");
            var taskBEntry = Assert.Single(taskB.Entries);
            Assert.Equal("src/b.spec.ts", taskBEntry.Path);
            Assert.Equal(SpecChangeCategory.AtRisk, taskBEntry.Category);
        }
        finally
        {
            DeleteBestEffort(workspace);
        }
    }

    [Fact]
    public void Analyze_StampsGeneratedAtAndDurationMs()
    {
        // Even on the error path (job not found) the analysis is timestamped and
        // timed so the UI can show "generated in N ms · when".
        var workspace = Path.Combine(Path.GetTempPath(), "regression-radar-meta-" + Guid.NewGuid().ToString("N"));
        var watchPath = Path.Combine(workspace, "projects", "demo");
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(watchPath, state));
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = workspace,
                ["WatchPaths:0:Name"] = "demo",
                ["WatchPaths:0:Path"] = watchPath,
                ["WatchPaths:0:RootPath"] = watchPath,
                ["WatchPaths:0:RepositoryPath"] = watchPath,
            }).Build();
            var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
            var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);

            var service = new RegressionRadarService(null!, null!, scanner, NullLogger<RegressionRadarService>.Instance);
            var before = DateTime.UtcNow;
            var result = service.Analyze("does-not-exist", watchPath);

            Assert.Equal("Job not found", result.Error);
            Assert.True(result.DurationMs >= 0);
            Assert.InRange(result.GeneratedAt, before.AddSeconds(-2), DateTime.UtcNow.AddSeconds(2));
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static (string RepoRoot, string WatchPath) SetupRadarRepo(string workspace)
    {
        var repoRoot = Path.Combine(workspace, "repo");
        var watchPath = Path.Combine(workspace, "jobs");
        Directory.CreateDirectory(repoRoot);
        Directory.CreateDirectory(watchPath);

        RunGit(workspace, "init", "-q", "-b", "main", repoRoot);
        RunGit(repoRoot, "config", "user.email", "test@example.com");
        RunGit(repoRoot, "config", "user.name", "test");
        RunGit(repoRoot, "config", "commit.gpgsign", "false");

        return (repoRoot, watchPath);
    }

    private static RegressionRadarService BuildService(string workspace, string repoRoot, string watchPath)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = workspace,
            ["WatchPaths:0:Name"] = "demo",
            ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = repoRoot,
            ["WatchPaths:0:RepositoryPath"] = repoRoot,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        return new RegressionRadarService(git, null!, scanner, NullLogger<RegressionRadarService>.Instance);
    }

    private static void SeedJob(string watchPath, string jobId, string title, IReadOnlyList<string> commitShas)
    {
        var jobDir = Path.Combine(watchPath, TaskStates.HumanReview, jobId);
        Directory.CreateDirectory(jobDir);
        File.WriteAllText(Path.Combine(jobDir, "prompt.md"), "fixture");

        var commits = commitShas.Select(sha => new TaskCommitInfo
        {
            Sha = sha,
            ShortSha = sha[..Math.Min(7, sha.Length)],
            Message = "fixture",
            At = DateTime.UtcNow,
            Attribution = CommitAttributionKinds.Automatic,
            Confidence = 1,
        }).ToList();

        var jobJson = new
        {
            id = jobId,
            title,
            state = TaskStates.HumanReview,
            order = 1,
            agent = "claude",
            cliType = "claude",
            createdAt = DateTime.UtcNow,
            enteredLaneAt = DateTime.UtcNow,
            commits,
        };
        File.WriteAllText(
            Path.Combine(jobDir, "task.json"),
            JsonSerializer.Serialize(jobJson, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string CommitAll(string repoRoot, string message)
    {
        RunGit(repoRoot, "add", "-A");
        RunGit(repoRoot, "commit", "-q", "-m", message);
        return RunGitCapture(repoRoot, "rev-parse", "HEAD").Trim();
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
    }

    private static string RunGitCapture(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
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
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }

    private static void DeleteBestEffort(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best-effort */ }
            }
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            /* best-effort cleanup for temp git repos on Windows */
        }
    }
}
