using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks in the runner prompt contract without embedding the full prompt text
/// in C# code. The planner chooses templates; <see cref="RuntimePromptService"/>
/// loads the Markdown files and renders variables.
/// </summary>
public class TaskRunnerPromptTests
{
    [Fact]
    public void RunnerFreshStartTemplate_PointsAtPromptFileAndKeepsAppInControl()
    {
        var p = Prompts().Render(RuntimePromptService.RunnerFreshStart, new Dictionary<string, string?>
        {
            ["prompt_path"] = @"C:\jobs\fix-bug\prompt.md",
            ["job_folder"] = @"C:\jobs\fix-bug",
            ["working_directory"] = @"C:\Projects\Runbook\App",
            ["repository_path"] = @"C:\Projects\Runbook"
        });

        Assert.Contains(@"C:\jobs\fix-bug\prompt.md", p);
        Assert.Contains(@"C:\jobs\fix-bug", p);
        Assert.Contains(@"C:\Projects\Runbook\App", p);
        Assert.Contains(@"C:\Projects\Runbook", p);
        Assert.Contains("Do not scan for other tasks", p);
        Assert.Contains("Do not move the job folder", p);
        Assert.Contains("application as the owner", p);
        Assert.Contains("Run git status and git diff in the repository path", p);
    }

    [Fact]
    public void RunnerResumeTemplate_RestoresJobContextWithoutMovingState()
    {
        var p = Prompts().Render(RuntimePromptService.RunnerResumeInterrupted, new Dictionary<string, string?>
        {
            ["job_folder"] = @"C:\jobs\fix-bug",
            ["working_directory"] = @"C:\Projects\Runbook\App",
            ["repository_path"] = @"C:\Projects\Runbook"
        });

        Assert.Contains("Resume the interrupted task", p);
        Assert.Contains(@"C:\jobs\fix-bug", p);
        Assert.Contains(@"C:\Projects\Runbook\App", p);
        Assert.Contains(@"C:\Projects\Runbook", p);
        Assert.Contains("job.json", p);
        Assert.Contains("prompt.md", p);
        Assert.Contains("status.md", p);
        Assert.Contains("logs", p);
        Assert.Contains("Do not move the job folder", p);
        Assert.Contains("Do not ask what to do", p);
    }

    [Fact]
    public void RunnerResumeRestartTemplate_TellsAgentPreviousRunIsDoneAndToActOnDelta()
    {
        var p = Prompts().Render(RuntimePromptService.RunnerResumeRestart, new Dictionary<string, string?>
        {
            ["prompt_path"] = @"C:\jobs\fix-bug\prompt.md",
            ["job_folder"] = @"C:\jobs\fix-bug",
            ["working_directory"] = @"C:\Projects\Runbook\App",
            ["repository_path"] = @"C:\Projects\Runbook",
            ["title"] = "Improve the layout of the taskbar.",
            ["prompt_text"] = "(updated body)"
        });

        // The whole reason this template exists: tell Claude the previous
        // run already finished, so re-issuing the same task body does not
        // get the "I'll wait for your request" no-op response.
        Assert.Contains("previous run", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("re-started", p, StringComparison.OrdinalIgnoreCase);
        // Must instruct the agent to re-read prompt.md and diff against
        // what it remembers, otherwise the delta is invisible.
        Assert.Contains("Re-read", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prompt.md", p);
        Assert.Contains("git status", p);
        Assert.Contains("git diff", p);
        // Standard guardrails still apply.
        Assert.Contains("Do not scan for other tasks", p);
        Assert.Contains("Do not move the job folder", p);
        // Variables are expanded.
        Assert.Contains("Improve the layout of the taskbar.", p);
        Assert.Contains("(updated body)", p);
    }

    [Fact]
    public void RunnerRecoveryTemplate_MakesFollowupThePrimaryInstruction()
    {
        var p = Prompts().Render(RuntimePromptService.RunnerRecoveryContinuation, new Dictionary<string, string?>
        {
            ["job_folder"] = @"C:\jobs\fix-bug",
            ["working_directory"] = @"C:\Projects\Runbook\App",
            ["repository_path"] = @"C:\Projects\Runbook",
            ["user_followup"] = "Please continue with adding the chat compose box."
        });

        Assert.Contains("previous CLI session", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\jobs\fix-bug", p);
        Assert.Contains(@"C:\Projects\Runbook\App", p);
        Assert.Contains(@"C:\Projects\Runbook", p);
        Assert.Contains("prompt.md", p);
        // The follow-up must be present and explicitly framed as the primary
        // instruction. The bug we are guarding against: an empty / "task done"
        // reply when the original prompt is already finished but a follow-up
        // exists.
        Assert.Contains("Please continue with adding the chat compose box.", p);
        Assert.Contains("PRIMARY INSTRUCTION", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[[TASK_BLOCKED", p);
        // Standard guardrails still apply.
        Assert.Contains("Do not move the job folder", p);
    }

    [Fact]
    public void AllRunnerTemplates_SpellOutTheOutputContract()
    {
        var prompts = Prompts();
        foreach (var template in new[]
        {
            RuntimePromptService.RunnerFreshStart,
            RuntimePromptService.RunnerResumeInterrupted,
            RuntimePromptService.RunnerResumeRestart,
            RuntimePromptService.RunnerRecoveryContinuation
        })
        {
            var rendered = prompts.Render(template, new Dictionary<string, string?>
            {
                ["prompt_path"] = "prompt.md",
                ["job_folder"] = "job",
                ["working_directory"] = "work",
                ["repository_path"] = "repo",
                ["user_followup"] = "follow up",
                ["title"] = "title",
                ["prompt_text"] = "body"
            });
            Assert.Contains("[[TASK_DONE]]", rendered);
            Assert.Contains("[[TASK_BLOCKED", rendered);
            Assert.Contains("[[TASK_NEEDS_INPUT", rendered);
        }
    }

    [Fact]
    public void RunnerTemplates_DoNotContainEmDashes()
    {
        var prompts = Prompts();
        foreach (var template in new[]
        {
            RuntimePromptService.RunnerFreshStart,
            RuntimePromptService.RunnerResumeInterrupted,
            RuntimePromptService.RunnerResumeRestart,
            RuntimePromptService.RunnerRecoveryContinuation,
            RuntimePromptService.SummaryProtocol,
            RuntimePromptService.CommitMessage
        })
        {
            var rendered = prompts.Render(template, new Dictionary<string, string?>
            {
                ["prompt_path"] = "prompt.md",
                ["job_folder"] = "job",
                ["working_directory"] = "work",
                ["repository_path"] = "repo",
                ["user_followup"] = "follow up",
                ["log"] = "log",
                ["diff"] = "diff"
            });

            Assert.DoesNotContain("\u2014", rendered);
        }
    }

    /// <summary>
    /// Pre-generated slugs in the <c>taskboard-{jobId}-{yyyyMMddHHmm}</c> shape are
    /// placeholders we wrote ourselves before a real session UUID was captured.
    /// They must be recognised so the cross-CLI guard drops them silently.
    /// </summary>
    [Theory]
    [InlineData("taskboard-verbessere-den-task-log-202604282114", true)]
    [InlineData("taskboard-action-failed-202604282112", true)]
    [InlineData("taskboard-x-202604282114", true)]
    [InlineData("a1b2c3d4-e5f6-7890-abcd-ef0123456789", false)]
    [InlineData("rollover_2025-04-01T12:00:00Z", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("taskboard-no-timestamp", false)]
    public void IsPlaceholderSessionSlug_RecognisesGeneratedShape(string? input, bool expected)
    {
        Assert.Equal(expected, RunPlanner.IsPlaceholderSessionSlug(input));
    }

    /// <summary>
    /// The resume-continuation prompt only pays off when there is something to
    /// reconstruct: a real session to load, or a dropped foreign-CLI session
    /// whose files we can re-read.
    /// </summary>
    [Theory]
    [InlineData("2-ready", false, false, false)]
    [InlineData("2-ready", true, false, false)]
    [InlineData("3-progress", false, false, false)]
    [InlineData("3-progress", true, false, true)]
    [InlineData("3-progress", false, true, true)]
    [InlineData("2-ready", false, true, true)]
    public void ShouldUseResumePrompt_OnlyFiresWhenContextExists(
        string initialState, bool resume, bool sessionDropped, bool expected)
    {
        Assert.Equal(expected, RunPlanner.ShouldUseResumePrompt(initialState, resume, sessionDropped));
    }

    private static RuntimePromptService Prompts()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PromptTemplates:RuntimePath"] = FindPromptRoot()
            })
            .Build();
        return new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
    }

    private static string FindPromptRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "prompts", "runtime");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate prompts/runtime from test base directory.");
    }
}
