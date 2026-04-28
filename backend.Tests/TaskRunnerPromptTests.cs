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
}
