using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the precondition branches of <see cref="SummaryGenerationService.GenerateInterimAsync"/>.
/// The successful Haiku path needs the Claude CLI (billable) and is exercised
/// in the Playwright spec instead; here we cover the cheap branches the user
/// would otherwise have to discover by inspection.
/// </summary>
public class SummaryGenerationInterimTests
{
    private static SummaryGenerationService BuildService()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        return new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
    }

    private static TaskInfo BuildJob(string folderPath) => new()
    {
        Id = "interim-test",
        TaskKey = $"::interim-test",
        Title = "Interim test",
        State = "3-progress",
        FolderPath = folderPath,
        WatchPath = "",
        ProjectName = "test"
    };

    [Fact]
    public async Task ReturnsFailure_WhenCliOutputLogIsMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "interim-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var svc = BuildService();
            var result = await svc.GenerateInterimAsync(BuildJob(dir));

            Assert.False(result.Ok);
            Assert.Null(result.Markdown);
            Assert.NotNull(result.Error);
            Assert.Contains("No CLI output", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task ReturnsFailure_WhenCliOutputLogIsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "interim-tests-" + Guid.NewGuid().ToString("N"));
        var logsDir = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logsDir);
        File.WriteAllText(Path.Combine(logsDir, "cli-output.log"), "");
        try
        {
            var svc = BuildService();
            var result = await svc.GenerateInterimAsync(BuildJob(dir));

            Assert.False(result.Ok);
            Assert.Null(result.Markdown);
            Assert.NotNull(result.Error);
            Assert.Contains("empty", result.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void SuccessResult_ExposesMarkdownAndDuration()
    {
        var ok = InterimSummaryResult.Success("# Status\n- Result: Partial\n", 1234);
        Assert.True(ok.Ok);
        Assert.Equal("# Status\n- Result: Partial\n", ok.Markdown);
        Assert.Null(ok.Error);
        Assert.Equal(1234, ok.DurationMs);
    }

    [Fact]
    public void FailureResult_ExposesError()
    {
        var bad = InterimSummaryResult.Failure("boom");
        Assert.False(bad.Ok);
        Assert.Null(bad.Markdown);
        Assert.Equal("boom", bad.Error);
        Assert.Equal(0, bad.DurationMs);
    }
}
