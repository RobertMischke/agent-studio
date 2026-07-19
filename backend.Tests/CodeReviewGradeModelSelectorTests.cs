

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins the load-bearing model invariant of the automatic quality-grade
/// code-review step (ASS-1657): the grade pass defaults to the live Codex
/// flagship, not the bounded gpt-5.4-mini model the four aspect reviews run on,
/// while staying configurable.
/// </summary>
public class CodeReviewGradeModelSelectorTests
{
    [Fact]
    public void Resolve_EmptyConfig_DefaultsToLiveCodexFlagship()
    {
        var (model, cli) = CodeReviewGradeModelSelector.Resolve(null, null);

        Assert.Equal(ModelMetadataRegistry.DefaultForCli(CliTypes.Codex), model);
        Assert.Equal(CliTypes.Codex, cli);
        Assert.Equal(
            ModelMetadataRegistry.DefaultThinkingLevelForCli(cli, model),
            CodeReviewGradeModelSelector.DefaultThinkingLevel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankModel_FallsBackToFlagship_NotEmpty(string blank)
    {
        var (model, _) = CodeReviewGradeModelSelector.Resolve(blank, null);

        Assert.Equal(ModelMetadataRegistry.DefaultForCli(CliTypes.Codex), model);
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
        // The whole point of ASS-1657: bounded aspect verdicts use the mini model, the
        // operator-facing quality grade runs on the strong model. If these two
        // ever converge, the grade has been quietly downgraded — fail loudly.
        Assert.NotEqual(PipelineStepModelDefaults.SupportModel, CodeReviewGradeModelSelector.DefaultModel);
        Assert.Equal(ModelMetadataRegistry.DefaultForCli(CliTypes.Codex), CodeReviewGradeModelSelector.DefaultModel);
    }
}
