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
}
