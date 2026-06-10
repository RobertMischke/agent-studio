using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Review;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Pins the load-bearing model invariant of the automatic quality-grade
/// code-review step (ASS-1657): the grade pass defaults to Claude Opus 4.8 —
/// NOT the cheap Haiku model the four aspect reviews run on — while staying
/// configurable. This is the concrete resolution of the ASS-855 (Haiku) vs
/// ASS-916 (Opus) tension, so the regression assertion below exists to stop a
/// future cost cut from silently dragging the grade back onto a weak model.
/// </summary>
public class CodeReviewGradeModelSelectorTests
{
    [Fact]
    public void Resolve_EmptyConfig_DefaultsToOpus48()
    {
        var (model, cli) = CodeReviewGradeModelSelector.Resolve(null, null);

        Assert.Equal(ClaudeCliService.DefaultOpusModel, model);
        Assert.Equal("claude-opus-4-8", model);
        Assert.Equal("claude", cli);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankModel_FallsBackToOpus_NotEmpty(string blank)
    {
        var (model, _) = CodeReviewGradeModelSelector.Resolve(blank, null);

        Assert.Equal("claude-opus-4-8", model);
    }

    [Fact]
    public void Resolve_RespectsExplicitModelOverride()
    {
        var (model, cli) = CodeReviewGradeModelSelector.Resolve("claude-sonnet-4-6", "codex");

        Assert.Equal("claude-sonnet-4-6", model);
        Assert.Equal("codex", cli);
    }

    [Fact]
    public void Resolve_TrimsWhitespaceAroundOverride()
    {
        var (model, cli) = CodeReviewGradeModelSelector.Resolve("  claude-opus-4-8  ", "  claude  ");

        Assert.Equal("claude-opus-4-8", model);
        Assert.Equal("claude", cli);
    }

    [Fact]
    public void GradeDefault_IsNotTheCheapAspectDefault()
    {
        // The whole point of ASS-1657: cheap aspect verdicts stay on Haiku, the
        // operator-facing quality grade runs on the strong model. If these two
        // ever converge, the grade has been quietly downgraded — fail loudly.
        Assert.NotEqual(OrchestratorRunner.DefaultModel, CodeReviewGradeModelSelector.DefaultModel);
        Assert.Equal("claude-opus-4-8", CodeReviewGradeModelSelector.DefaultModel);
    }
}
