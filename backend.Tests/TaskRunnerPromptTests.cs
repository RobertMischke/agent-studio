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
    public void AllRunnerTemplates_MentionBuildTimeObservabilityWithoutDominating()
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

            // Each runner template carries the small observability nudge so
            // coding agents preserve and extend structured logging when the
            // change introduces meaningful product behavior.
            Assert.Contains("observability", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("structured log", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("stable event name", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("error context", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("timing", rendered, StringComparison.OrdinalIgnoreCase);

            // The opt-out clause must be present so trivial changes are not
            // padded with logging just to satisfy the guidance.
            Assert.Contains("Skip instrumentation", rendered, StringComparison.OrdinalIgnoreCase);

            // Guidance must stay short. The block should not dominate the
            // prompt; cap it at a small fraction of total characters so a
            // future edit cannot quietly turn it into a checklist.
            var blockStart = rendered.IndexOf("Build-time observability", StringComparison.OrdinalIgnoreCase);
            Assert.True(blockStart >= 0, "observability block must be present");
            var blockEnd = rendered.IndexOf("When you finish", blockStart, StringComparison.OrdinalIgnoreCase);
            if (blockEnd < 0) blockEnd = rendered.Length;
            var blockLength = blockEnd - blockStart;
            Assert.InRange(blockLength, 1, 1200);
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
                ["diff"] = "diff",
                ["task_title"] = "title",
                ["task_prompt_first_paragraph"] = "first paragraph",
                ["last_user_continue"] = "follow up"
            });

            Assert.DoesNotContain("\u2014", rendered);
        }
    }

    /// <summary>
    /// Slice B of the git-problem task: the commit-message template must anchor
    /// on the task's stated intent (title, first prompt paragraph, last user
    /// follow-up) so the generated subject reflects *why* the change is being
    /// recorded, not just *what* the diff touches. Pinning this here so a
    /// future template refactor cannot silently drop the intent placeholders
    /// and fall back to a diff-only summary.
    /// </summary>
    [Fact]
    public void CommitMessageTemplate_AnchorsOnTaskIntent()
    {
        var rendered = Prompts().Render(RuntimePromptService.CommitMessage, new Dictionary<string, string?>
        {
            ["diff"] = "diff --git a/x b/x",
            ["task_title"] = "Git-Problem",
            ["task_prompt_first_paragraph"] = "Orchestrator should commit and push when a task finishes.",
            ["last_user_continue"] = "Please also enrich the commit message."
        });

        // Intent anchors must reach the LLM, not just the diff.
        Assert.Contains("Git-Problem", rendered);
        Assert.Contains("Orchestrator should commit and push", rendered);
        Assert.Contains("Please also enrich the commit message.", rendered);
        Assert.Contains("diff --git a/x b/x", rendered);

        // Structural guard: the template must instruct the model to prefer
        // intent over a literal diff summary, otherwise enriching the prompt
        // is pointless.
        Assert.Contains("intent", rendered, StringComparison.OrdinalIgnoreCase);

        // The Conventional Commit shape must survive the prompt rewrite.
        Assert.Contains("Conventional Commit", rendered);
        Assert.Contains("72 characters", rendered);
    }

    /// <summary>
    /// Empty intent fields (legacy jobs without a title, or no Extend prompt)
    /// must render without leaking the literal placeholder back into the LLM
    /// input. The renderer substitutes the empty string, so the headings stay
    /// but the bodies collapse to a blank line.
    /// </summary>
    [Fact]
    public void CommitMessageTemplate_TolerantToMissingIntentFields()
    {
        var rendered = Prompts().Render(RuntimePromptService.CommitMessage, new Dictionary<string, string?>
        {
            ["diff"] = "diff --git a/x b/x",
            ["task_title"] = "",
            ["task_prompt_first_paragraph"] = "",
            ["last_user_continue"] = ""
        });

        Assert.DoesNotContain("{{task_title}}", rendered);
        Assert.DoesNotContain("{{task_prompt_first_paragraph}}", rendered);
        Assert.DoesNotContain("{{last_user_continue}}", rendered);
        Assert.Contains("diff --git a/x b/x", rendered);
    }

    /// <summary>
    /// First-paragraph extraction must trim leading whitespace, stop at the
    /// first blank line, and bound the result so a wall-of-text prompt does
    /// not dominate the LLM input. Pinned here so a future helper rewrite
    /// cannot silently regress to "send the whole prompt body".
    /// </summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("   \n  \n", "")]
    [InlineData("Hello world.", "Hello world.")]
    [InlineData("Hello world.\n\nSecond paragraph.", "Hello world.")]
    [InlineData("\n\nLeading blank lines.\n\nThen more.", "Leading blank lines.")]
    [InlineData("Line one.\nLine two of the same paragraph.\n\nNext block.",
                "Line one.\nLine two of the same paragraph.")]
    public void ExtractFirstParagraph_TrimsAndStopsAtBlankLine(string input, string expected)
    {
        Assert.Equal(expected, OrchestratorApi.Services.GitService.ExtractFirstParagraph(input));
    }

    [Fact]
    public void ExtractFirstParagraph_BoundsLongParagraphs()
    {
        var body = new string('a', 2000);
        var result = OrchestratorApi.Services.GitService.ExtractFirstParagraph(body);

        // Bounded to 1500 chars + ellipsis suffix, so the LLM call stays cheap.
        Assert.True(result.Length <= 1503, $"unexpected length: {result.Length}");
        Assert.EndsWith("...", result);
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

    // ---- Per-mode prompt framing (planning/research read-only + web hint) ----

    [Fact]
    public void RenderModeFraming_CodingWithoutWeb_IsEmpty()
    {
        // Coding with web off is the legacy default; framing must stay empty so
        // the rendered runner prompt is byte-identical to the pre-mode output.
        Assert.Equal(string.Empty, Prompts().RenderModeFraming("coding", allowWebAccess: false));
    }

    [Theory]
    [InlineData("planning")]
    [InlineData("research")]
    public void RenderModeFraming_ReadOnlyModes_CarryReadOnlyBlock(string mode)
    {
        var framing = Prompts().RenderModeFraming(mode, allowWebAccess: false);

        Assert.Contains("Read-only run", framing);
        Assert.Contains("do not write or modify source", framing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("commit", framing, StringComparison.OrdinalIgnoreCase);
        // Web hint must NOT leak in when web access is off.
        Assert.DoesNotContain("Web access is enabled", framing);
    }

    [Fact]
    public void RenderModeFraming_Research_AddsBothReadOnlyAndWebHint()
    {
        // Research is the read-only-with-web mode: it gets the read-only block
        // and the web hint, read-only first.
        var framing = Prompts().RenderModeFraming("research", allowWebAccess: true);

        Assert.Contains("Read-only run", framing);
        Assert.Contains("Web access is enabled", framing);
        Assert.InRange(framing.IndexOf("Read-only run", StringComparison.Ordinal), 0,
            framing.IndexOf("Web access is enabled", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderModeFraming_CodingWithWeb_AddsWebHintOnly()
    {
        // Decision 2: the web toggle is independent of the mode. A coding task
        // with web opted in gets the web hint but no read-only constraint.
        var framing = Prompts().RenderModeFraming("coding", allowWebAccess: true);

        Assert.Contains("Web access is enabled", framing);
        Assert.DoesNotContain("Read-only run", framing);
    }

    [Fact]
    public void FreshStartTemplate_ReadOnlyMode_InjectsFramingAndKeepsGuardrails()
    {
        var prompts = Prompts();
        var p = prompts.Render(RuntimePromptService.RunnerFreshStart, new Dictionary<string, string?>
        {
            ["prompt_path"] = "prompt.md",
            ["job_folder"] = "job",
            ["working_directory"] = "work",
            ["repository_path"] = "repo",
            ["title"] = "Investigate the slow board load",
            ["prompt_text"] = "Profile the board and propose fixes.",
            ["mode_framing"] = prompts.RenderModeFraming("planning", allowWebAccess: false)
        });

        Assert.Contains("Read-only run", p);
        Assert.DoesNotContain("{{mode_framing}}", p);
        // Standard guardrails survive the injection.
        Assert.Contains("Do not scan for or pick up other tasks", p);
        Assert.Contains("[[TASK_DONE]]", p);
        // Read-only framing sits after the task body, before the run context.
        var bodyIndex = p.IndexOf("Profile the board", StringComparison.Ordinal);
        var framingIndex = p.IndexOf("Read-only run", StringComparison.Ordinal);
        var contextIndex = p.IndexOf("Context for this run", StringComparison.Ordinal);
        Assert.InRange(framingIndex, bodyIndex, contextIndex);
    }

    [Fact]
    public void FreshStartTemplate_CodingMode_OmitsFramingWithoutLeftoverPlaceholder()
    {
        var prompts = Prompts();
        var p = prompts.Render(RuntimePromptService.RunnerFreshStart, new Dictionary<string, string?>
        {
            ["prompt_path"] = "prompt.md",
            ["job_folder"] = "job",
            ["working_directory"] = "work",
            ["repository_path"] = "repo",
            ["title"] = "Implement the widget",
            ["prompt_text"] = "Add the widget to the toolbar.",
            ["mode_framing"] = prompts.RenderModeFraming("coding", allowWebAccess: false)
        });

        Assert.DoesNotContain("Read-only run", p);
        Assert.DoesNotContain("Web access is enabled", p);
        Assert.DoesNotContain("{{mode_framing}}", p);
    }

    [Fact]
    public void ModeFramingSnippets_DoNotContainEmDashes()
    {
        var framing = Prompts().RenderModeFraming("research", allowWebAccess: true);
        Assert.DoesNotContain("—", framing);
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
