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

    /// <summary>
    /// The resume-continuation prompt only pays off when there's something to
    /// reconstruct — a real session to load, or a dropped foreign-CLI session
    /// whose files we can re-read. A 3-progress job with no captured UUID and
    /// no dropped session is effectively a fresh start; sending the resume
    /// prompt there used to make the agent quit with "I don't see an
    /// interrupted task".
    /// </summary>
    [Theory]
    // (initialState, resume, sessionDropped, expectedUseResumePrompt)
    [InlineData("2-ready",    false, false, false)] // brand-new fresh start
    [InlineData("2-ready",    true,  false, false)] // continue with captured UUID; not interrupted
    [InlineData("3-progress", false, false, false)] // crashed before capturing a session — no context to recover
    [InlineData("3-progress", true,  false, true)]  // genuine interrupted resume — load `-r` and re-anchor
    [InlineData("3-progress", false, true,  true)]  // foreign session dropped — reconstruct from files
    [InlineData("2-ready",    false, true,  true)]  // foreign session dropped on a fresh-looking start
    public void ShouldUseResumePrompt_OnlyFiresWhenContextExists(
        string initialState, bool resume, bool sessionDropped, bool expected)
    {
        Assert.Equal(expected, ProjectRunner.ShouldUseResumePrompt(initialState, resume, sessionDropped));
    }

    /// <summary>
    /// Recovery-continuation prompt fires when the user clicks Continue but
    /// the previous CLI session is gone. Must (a) acknowledge the loss so the
    /// agent doesn't treat the run as a brand-new task, (b) point at the job
    /// folder and instruct it to read prompt/status/log + run git, (c) include
    /// the user's actual follow-up so the run continues with what they asked
    /// for, and (d) bound the log read so we don't blow context on a giant
    /// log. English-only because the prompt has to work across CLIs.
    /// </summary>
    [Fact]
    public void BuildRecoveryContinuationPrompt_AcknowledgesLossAndIncludesFollowup()
    {
        var p = ProjectRunner.BuildRecoveryContinuationPrompt(
            @"C:\jobs\fix-bug",
            "Please continue with adding the chat compose box.");

        Assert.Contains("session was lost", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\jobs\fix-bug", p);
        Assert.Contains("prompt.md", p);
        Assert.Contains("status.md", p);
        Assert.Contains("cli-output.log", p);
        Assert.Contains("git status", p);
        Assert.Contains("git diff", p);
        Assert.Contains("Please continue with adding the chat compose box.", p);
        // Bounded log read: the prompt must not just say "read logs/" — it has to cap the read.
        Assert.Contains("200 lines", p);
        // Treat as continuation, not restart.
        Assert.Contains("continuation", p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Empty / whitespace follow-up: prompt should still be valid (no
    /// crashing TrimEnd, no trailing "User follow-up:" with nothing under it
    /// that the agent would fixate on). The user-follow-up section just
    /// degrades to an empty trailer.
    /// </summary>
    [Fact]
    public void BuildRecoveryContinuationPrompt_HandlesEmptyFollowupGracefully()
    {
        var p = ProjectRunner.BuildRecoveryContinuationPrompt(@"C:\jobs\fix-bug", "");
        Assert.Contains(@"C:\jobs\fix-bug", p);
        Assert.Contains("User follow-up:", p);
    }
}
