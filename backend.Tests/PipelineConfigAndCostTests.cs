using OrchestratorApi.Models;
using OrchestratorApi.Services.Pipeline;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Tests for the two pure helpers the pre/post-step feature adds:
/// <see cref="PipelineStepConfigResolver"/> (turns per-project overrides
/// into the concrete enabled / model / mode a step runs with) and
/// <see cref="PipelineCostCalculator"/> (derives per-step + task-total
/// cost from a recorded execution via the single price table).
/// </summary>
public class PipelineConfigAndCostTests
{
    private static ProjectSettings SettingsWith(params (string StepId, PipelineStepSetting Setting)[] steps)
    {
        var map = new Dictionary<string, PipelineStepSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, s) in steps) map[id] = s;
        return new ProjectSettings { PipelineSteps = map };
    }

    [Fact]
    public void IsEnabled_DefaultsTrue_WhenNoOverride()
    {
        Assert.True(PipelineStepConfigResolver.IsEnabled(null, "aspect-code-quality"));
        Assert.True(PipelineStepConfigResolver.IsEnabled(new ProjectSettings(), "aspect-code-quality"));
    }

    [Fact]
    public void IsEnabled_RespectsExplicitDisable_ByFullIdOrBareSuffix()
    {
        var byFull = SettingsWith(("aspect-code-quality", new PipelineStepSetting { Enabled = false }));
        Assert.False(PipelineStepConfigResolver.IsEnabled(byFull, "aspect-code-quality"));
        // The aspect runner only knows the bare id; the resolver must find
        // the override stored under the full id.
        Assert.False(PipelineStepConfigResolver.IsEnabled(byFull, "code-quality"));

        var byBare = SettingsWith(("code-quality", new PipelineStepSetting { Enabled = false }));
        Assert.False(PipelineStepConfigResolver.IsEnabled(byBare, "aspect-code-quality"));
    }

    [Fact]
    public void ResolveModel_OrdersStepOverride_Then_ProjectModel_Then_RuntimeDefault()
    {
        // 1) step override wins
        var withStep = SettingsWith(("aspect-code-quality", new PipelineStepSetting { Model = "claude-haiku-4-5" }))
            with { OrchestratorModel = "claude-sonnet-4-6" };
        Assert.Equal("claude-haiku-4-5",
            PipelineStepConfigResolver.ResolveModel(withStep, "aspect-code-quality", "claude-opus-4-8"));

        // 2) no step override -> project orchestrator model
        var projectOnly = new ProjectSettings { OrchestratorModel = "claude-sonnet-4-6" };
        Assert.Equal("claude-sonnet-4-6",
            PipelineStepConfigResolver.ResolveModel(projectOnly, "aspect-code-quality", "claude-opus-4-8"));

        // 3) nothing configured -> runtime default
        Assert.Equal("claude-opus-4-8",
            PipelineStepConfigResolver.ResolveModel(null, "aspect-code-quality", "claude-opus-4-8"));
    }

    [Fact]
    public void Summarize_EmptyRecord_IsZeroCost()
    {
        var summary = PipelineCostCalculator.Summarize(null);
        Assert.Empty(summary.Steps);
        Assert.Equal(0, summary.TotalTokens);
        Assert.Equal(0m, summary.TotalCostUsd);
        Assert.False(summary.AnyModelUnknown);
    }

    [Fact]
    public void Summarize_PerStepAndTotalCost_FromPriceTable()
    {
        var record = new PipelineExecutionRecord
        {
            Steps =
            {
                new PipelineStepExecution
                {
                    StepId = "aspect-code-quality",
                    Kind = StepKind.Aspect,
                    Model = "claude-haiku-4-5", // $1 / $5 per million
                    InputTokens = 1_000_000,
                    OutputTokens = 200_000,
                },
                new PipelineStepExecution
                {
                    StepId = "aspect-requirement-fit",
                    Kind = StepKind.Aspect,
                    Model = "claude-opus-4-8", // $5 / $25 per million
                    InputTokens = 100_000,
                    OutputTokens = 10_000,
                },
            },
        };

        var summary = PipelineCostCalculator.Summarize(record);
        Assert.Equal(2, summary.Steps.Count);

        var haiku = summary.Steps[0];
        // 1M input * $1/M + 0.2M output * $5/M = $1.00 + $1.00 = $2.00
        Assert.Equal(2.00m, haiku.CostUsd);
        Assert.Equal(1_200_000, haiku.TotalTokens);
        Assert.True(haiku.ModelKnown);

        var opus = summary.Steps[1];
        // 0.1M input * $5/M + 0.01M output * $25/M = $0.50 + $0.25 = $0.75
        Assert.Equal(0.75m, opus.CostUsd);

        Assert.Equal(1_310_000, summary.TotalTokens);
        Assert.Equal(2.75m, summary.TotalCostUsd);
        Assert.False(summary.AnyModelUnknown);
    }

    [Fact]
    public void Summarize_FlagsUnknownModel_OnlyWhenStepConsumedTokens()
    {
        var record = new PipelineExecutionRecord
        {
            Steps =
            {
                // Unknown model that actually spent tokens -> flag n/a.
                new PipelineStepExecution
                {
                    StepId = "aspect-code-quality",
                    Kind = StepKind.Aspect,
                    Model = "gpt-5-codex",
                    InputTokens = 500,
                    OutputTokens = 200,
                },
                // Tool step with zero tokens and no model is NOT a pricing gap.
                new PipelineStepExecution
                {
                    StepId = "post-lint-scss",
                    Kind = StepKind.Tool,
                    Model = null,
                },
            },
        };

        var summary = PipelineCostCalculator.Summarize(record);
        Assert.True(summary.AnyModelUnknown);
        Assert.False(summary.Steps[0].ModelKnown);
        Assert.Equal(0m, summary.Steps[0].CostUsd);
        // The zero-token tool step has an unknown (null) model but, because
        // it spent no tokens, it does not flip AnyModelUnknown on its own.
        Assert.False(summary.Steps[1].ModelKnown);
        Assert.Equal(700, summary.TotalTokens);
    }
}
