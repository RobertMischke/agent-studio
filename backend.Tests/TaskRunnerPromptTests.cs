using OrchestratorApi.Services;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks in the two prompt shapes the runner injects into the CLI process.
/// The fresh-start prompt is what every brand-new job sees; the resume
/// continuation prompt is the recovery message after an interrupted run, and
/// it must mention the job folder, the four key files (job.json, prompt.md,
/// status.md, logs), and tell the agent not to ask what to do — otherwise the
/// CLI session resumes without context and falls back to the generic repo
/// prompt that triggered the original "What should I continue with?" bug.
/// </summary>
public class TaskRunnerPromptTests
{
    [Fact]
    public void BuildFreshStartPrompt_PointsAtPromptFileAndJobFolder()
    {
        var p = ProjectRunner.BuildFreshStartPrompt(
            @"C:\jobs\fix-bug\prompt.md",
            @"C:\jobs\fix-bug");

        Assert.Contains(@"prompt.md", p);
        Assert.Contains(@"C:\jobs\fix-bug", p);
        Assert.Contains("Lies", p);
    }

    [Fact]
    public void BuildResumeContinuationPrompt_RestoresJobContext()
    {
        var p = ProjectRunner.BuildResumeContinuationPrompt(@"C:\jobs\fix-bug");

        Assert.Contains("Resume the interrupted task", p);
        Assert.Contains(@"C:\jobs\fix-bug", p);
        Assert.Contains("job.json", p);
        Assert.Contains("prompt.md", p);
        Assert.Contains("status.md", p);
        Assert.Contains("logs", p);
        Assert.Contains("Reconstruct progress", p);
        Assert.Contains("Do not ask what to do", p);
    }

    [Fact]
    public void BuildResumeContinuationPrompt_DiffersFromFreshStartPrompt()
    {
        var fresh = ProjectRunner.BuildFreshStartPrompt(@"C:\jobs\x\prompt.md", @"C:\jobs\x");
        var resume = ProjectRunner.BuildResumeContinuationPrompt(@"C:\jobs\x");

        Assert.NotEqual(fresh, resume);
    }

    /// <summary>
    /// Pre-generated slugs in the <c>taskboard-{jobId}-{yyyyMMddHHmm}</c> shape are
    /// placeholders we wrote ourselves before a real session UUID was captured.
    /// They must be recognised so the cross-CLI guard drops them silently — otherwise
    /// every Claude job whose first run didn't capture a UUID would receive the
    /// resume continuation prompt on its next start and reply "I don't see an
    /// interrupted task to resume".
    /// </summary>
    [Theory]
    [InlineData("taskboard-verbessere-den-task-log-202604282114", true)]
    [InlineData("taskboard-action-failed-202604282112", true)]
    [InlineData("taskboard-x-202604282114", true)]
    [InlineData("a1b2c3d4-e5f6-7890-abcd-ef0123456789", false)] // real UUID
    [InlineData("rollover_2025-04-01T12:00:00Z", false)]        // foreign-shaped slug
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("taskboard-no-timestamp", false)]
    public void IsPlaceholderSessionSlug_RecognisesGeneratedShape(string? input, bool expected)
    {
        Assert.Equal(expected, ProjectRunner.IsPlaceholderSessionSlug(input));
    }
}
