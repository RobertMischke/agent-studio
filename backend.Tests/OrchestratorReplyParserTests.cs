using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the orchestrator's reply contract: <c>{REPLY | STEER | BLOCK}</c>.
/// The parser is the load-bearing seam between what the orchestrator emits
/// as free-form Markdown / plain text and the structured action the runner
/// takes (re-issue / surface a steer card / leave the question for the user).
/// Malformed input must never crash; it falls back to BLOCK with a parse
/// warning so the runner has a graceful path.
/// </summary>
public class OrchestratorReplyParserTests
{
    [Fact]
    public void PlainText_IsClassifiedAsReply()
    {
        var reply = OrchestratorReplyParser.Parse("Use option A and rerun the build.");
        Assert.Equal(OrchestratorReplyKind.Reply, reply.Kind);
        Assert.Equal("Use option A and rerun the build.", reply.ReplyText);
    }

    [Fact]
    public void BareBlock_IsClassifiedAsBlock()
    {
        var reply = OrchestratorReplyParser.Parse("BLOCK");
        Assert.Equal(OrchestratorReplyKind.Block, reply.Kind);
    }

    [Fact]
    public void BlockMixedCase_IsClassifiedAsBlock()
    {
        var reply = OrchestratorReplyParser.Parse("  Block  ");
        Assert.Equal(OrchestratorReplyKind.Block, reply.Kind);
    }

    [Fact]
    public void EmptyInput_IsClassifiedAsBlock()
    {
        Assert.Equal(OrchestratorReplyKind.Block, OrchestratorReplyParser.Parse("").Kind);
        Assert.Equal(OrchestratorReplyKind.Block, OrchestratorReplyParser.Parse(null).Kind);
        Assert.Equal(OrchestratorReplyKind.Block, OrchestratorReplyParser.Parse("   \n  \n").Kind);
    }

    [Fact]
    public void Reply_That_MentionsBlock_InProse_IsStillReply()
    {
        // Anchor-locked: only a bare BLOCK at the start of the reply counts.
        // Prose that merely references the word does not escalate.
        var reply = OrchestratorReplyParser.Parse(
            "If the user mentions BLOCK, run the lint step. Otherwise continue.");
        Assert.Equal(OrchestratorReplyKind.Reply, reply.Kind);
    }

    [Fact]
    public void MinimalSteer_NeedOnly_Parses()
    {
        var reply = OrchestratorReplyParser.Parse("STEER\nNeed: screenshot of the affected column");
        Assert.Equal(OrchestratorReplyKind.Steer, reply.Kind);
        Assert.Equal("screenshot of the affected column", reply.Need);
        Assert.Null(reply.Why);
        Assert.Null(reply.Options);
    }

    [Fact]
    public void Steer_WithNeedWhyAndOptions_ParsesAllFields()
    {
        var raw = @"STEER
Need: pick the navigation pattern
Why: the agent is stuck between a tabs design and a sidebar design
Options:
  A) tabs across the top
  B) collapsible sidebar
  C) split-pane with both";
        var reply = OrchestratorReplyParser.Parse(raw);

        Assert.Equal(OrchestratorReplyKind.Steer, reply.Kind);
        Assert.Equal("pick the navigation pattern", reply.Need);
        Assert.Equal("the agent is stuck between a tabs design and a sidebar design", reply.Why);
        Assert.NotNull(reply.Options);
        Assert.Equal(3, reply.Options!.Count);
        Assert.Equal("tabs across the top", reply.Options[0]);
        Assert.Equal("collapsible sidebar", reply.Options[1]);
        Assert.Equal("split-pane with both", reply.Options[2]);
    }

    [Fact]
    public void Steer_AcceptsNumericOptionMarkers()
    {
        var raw = @"STEER
Need: pick a path
Options:
  1) ship the simpler version
  2) keep iterating";
        var reply = OrchestratorReplyParser.Parse(raw);
        Assert.Equal(OrchestratorReplyKind.Steer, reply.Kind);
        Assert.Equal(2, reply.Options!.Count);
    }

    [Fact]
    public void Steer_AcceptsDashOptionMarkers()
    {
        var raw = @"STEER
Need: choose
Options:
  - keep the legacy modal
  - migrate to the side sheet";
        var reply = OrchestratorReplyParser.Parse(raw);
        Assert.Equal(OrchestratorReplyKind.Steer, reply.Kind);
        Assert.Equal(2, reply.Options!.Count);
        Assert.Equal("keep the legacy modal", reply.Options[0]);
    }

    [Fact]
    public void MalformedSteer_NoNeed_FallsBackToBlock_WithWarning()
    {
        // The orchestrator emitted STEER but forgot the Need: line. Rather
        // than crashing the runner or feeding a blank ask to the user,
        // degrade gracefully into BLOCK and surface the warning.
        var reply = OrchestratorReplyParser.Parse("STEER\nWhy: I have nothing concrete to ask");
        Assert.Equal(OrchestratorReplyKind.Block, reply.Kind);
        Assert.NotNull(reply.ParseWarning);
        Assert.Contains("STEER", reply.ParseWarning, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Steer_OnSameLineAsBody_Parses()
    {
        // Some orchestrator replies put the STEER token inline.
        var raw = "STEER Need: look at the failing test";
        var reply = OrchestratorReplyParser.Parse(raw);
        Assert.Equal(OrchestratorReplyKind.Steer, reply.Kind);
        Assert.Equal("look at the failing test", reply.Need);
    }

    [Fact]
    public void FormatSteerForChat_RendersMarkdownWithLabels()
    {
        var reply = new OrchestratorReply(
            OrchestratorReplyKind.Steer,
            "STEER\nNeed: x\nWhy: y",
            Need: "look at the screenshot",
            Why: "the agent referenced an image we cannot see",
            Options: new[] { "rerun the build", "check the dev console" });
        var formatted = OrchestratorReplyParser.FormatSteerForChat(reply);

        Assert.Contains("**Need:** look at the screenshot", formatted);
        Assert.Contains("**Why:** the agent referenced an image we cannot see", formatted);
        Assert.Contains("**Options:**", formatted);
        Assert.Contains("A) rerun the build", formatted);
        Assert.Contains("B) check the dev console", formatted);
    }

    [Fact]
    public void FormatSteerForChat_NeedOnly_OmitsOptionalSections()
    {
        var reply = new OrchestratorReply(
            OrchestratorReplyKind.Steer,
            "STEER\nNeed: x",
            Need: "look at the screenshot");
        var formatted = OrchestratorReplyParser.FormatSteerForChat(reply);

        Assert.Contains("**Need:** look at the screenshot", formatted);
        Assert.DoesNotContain("**Why:**", formatted);
        Assert.DoesNotContain("**Options:**", formatted);
    }

    [Fact]
    public void OrchestratorPrompts_TeachSteer()
    {
        // The prompts the runner sends are the contract behind the parser.
        // Lock the words "STEER", "Need:", and the preference rule so a
        // future edit does not silently drop the steering grammar.
        var prompt = OrchestratorApi.Services.Runner.ProjectRunner.BuildOrchestratorPrompt(
            FakeJob(),
            promptText: "do the work",
            lastAgentText: "should I ship A or B?",
            attachmentsList: "(none)");

        Assert.Contains("STEER", prompt);
        Assert.Contains("Need:", prompt);
        Assert.Contains("Why:", prompt);
        Assert.Contains("Options:", prompt);
        // Block must remain documented as the last-resort path.
        Assert.Contains("BLOCK", prompt);

        var resume = OrchestratorApi.Services.Runner.ProjectRunner.BuildOrchestratorResumePrompt(
            FakeJob(), lastAgentText: "should I ship A or B?", attachmentsList: "(none)");
        Assert.Contains("STEER", resume);
        Assert.Contains("Need:", resume);
    }

    private static OrchestratorApi.Models.JobInfo FakeJob() => new()
    {
        Id = "test-job",
        Title = "Test job",
        ProjectName = "test-project",
        WatchPath = "C:/tmp/test-project",
        FolderPath = "C:/tmp/test-project/.orchestrator/jobs/test-job",
        State = "3-progress"
    };
}
