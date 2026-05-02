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
        Assert.Contains("Do not scan for or pick up other tasks", p);
        Assert.Contains("do not move the job folder", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("application owns pickup", p);
        Assert.Contains("git status and git diff", p);

        // Structural property: the user task header must come before the
        // run-context framing. With user content second, Claude treats the
        // bootstrap as a system stub and replies "I'll wait for your actual
        // request." This guards against the regression that broke production.
        var taskHeader = p.IndexOf("# ", StringComparison.Ordinal);
        var contextHeader = p.IndexOf("Context for this run", StringComparison.Ordinal);
        Assert.InRange(taskHeader, 0, contextHeader);
    }

    [Fact]
    public void RunnerResumeTemplate_RestoresJobContextWithoutMovingState()
    {
        var p = Prompts().Render(RuntimePromptService.RunnerResumeInterrupted, new Dictionary<string, string?>
        {
            ["prompt_path"] = @"C:\jobs\fix-bug\prompt.md",
            ["job_folder"] = @"C:\jobs\fix-bug",
            ["working_directory"] = @"C:\Projects\Runbook\App",
            ["repository_path"] = @"C:\Projects\Runbook",
            ["title"] = "Improve the layout of the taskbar.",
            ["prompt_text"] = "(body)"
        });

        Assert.Contains("Resume this interrupted task", p);
        Assert.Contains(@"C:\jobs\fix-bug", p);
        Assert.Contains(@"C:\Projects\Runbook\App", p);
        Assert.Contains(@"C:\Projects\Runbook", p);
        Assert.Contains("job.json", p);
        Assert.Contains("prompt.md", p);
        Assert.Contains("status.md", p);
        Assert.Contains("logs", p);
        Assert.Contains("do not move the job folder", p, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("do not scan", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not move the job folder", p, StringComparison.OrdinalIgnoreCase);
        // Variables are expanded.
        Assert.Contains("Improve the layout of the taskbar.", p);
        Assert.Contains("(updated body)", p);
    }

    [Fact]
    public void RunnerRecoveryTemplate_CarriesOriginalTaskAndFollowupBeforeFraming()
    {
        var p = Prompts().Render(RuntimePromptService.RunnerRecoveryContinuation, new Dictionary<string, string?>
        {
            ["title"] = "Improve the layout of the taskbar.",
            ["prompt_text"] = "The header is cramped; rearrange model and CLI selectors.",
            ["prompt_path"] = @"C:\jobs\fix-bug\prompt.md",
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
        Assert.Contains("[[TASK_BLOCKED", p);
        Assert.Contains("do not move the job folder", p, StringComparison.OrdinalIgnoreCase);

        // Both the original task body AND the follow-up must appear in the
        // rendered prompt. When the session is unrecoverable, the agent
        // must still see the task it was hired for, not just the latest
        // direction. Without this, the agent says "no context, please
        // tell me what to do" - the symptom the user reported.
        Assert.Contains("Improve the layout of the taskbar.", p);
        Assert.Contains("The header is cramped; rearrange model and CLI selectors.", p);
        Assert.Contains("Please continue with adding the chat compose box.", p);

        // Structural property: original task body comes BEFORE the
        // follow-up which comes BEFORE the framing block. The previous
        // arrangement labelled the original task "reference only" and the
        // agent treated it as ignorable.
        var taskBodyIndex = p.IndexOf("The header is cramped", StringComparison.Ordinal);
        var followupIndex = p.IndexOf("Please continue with adding the chat compose box.", StringComparison.Ordinal);
        var framingIndex = p.IndexOf("previous CLI session", StringComparison.OrdinalIgnoreCase);
        Assert.InRange(taskBodyIndex, 0, followupIndex);
        Assert.InRange(followupIndex, 0, framingIndex);

        // The "reference only" framing is gone. The original task is the
        // authoritative starting point, not a footnote.
        Assert.DoesNotContain("reference only", p, StringComparison.OrdinalIgnoreCase);
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
