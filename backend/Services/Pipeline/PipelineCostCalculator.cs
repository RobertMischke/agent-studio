using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Services.Pipeline;

/// <summary>
/// Per-step cost (USD) breakdown for one step's recorded token usage.
/// <see cref="ModelKnown"/> is false when the step's model is not in the
/// <see cref="TokenPricing"/> catalogue (e.g. a Codex / Gemini step), so
/// the UI can render "n/a" instead of a misleading $0.00.
/// </summary>
public sealed record PipelineStepCost(
    string StepId,
    StepKind Kind,
    string? Model,
    bool ModelKnown,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long TotalTokens,
    decimal CostUsd);

/// <summary>
/// Cost summary for one task's whole pipeline run: per-step rows plus the
/// task total (sum across all pre-steps + the core run + all post-steps),
/// which is the single number the Overview "task total" line shows.
/// </summary>
public sealed record PipelineCostSummary(
    IReadOnlyList<PipelineStepCost> Steps,
    long TotalTokens,
    decimal TotalCostUsd,
    bool AnyModelUnknown);

/// <summary>
/// Derives per-step and task-total cost from an already-recorded
/// <see cref="PipelineExecutionRecord"/> using the single price table in
/// <see cref="TokenPricing"/>. Pure and cheap: a task has a handful of
/// steps, so this runs on read without a disk scan. Project-level
/// aggregation across many tasks goes through a separate cached path so
/// the Overview poll never triggers an O(N) price scan.
/// </summary>
public static class PipelineCostCalculator
{
    public static PipelineCostSummary Summarize(PipelineExecutionRecord? record)
    {
        if (record == null || record.Steps.Count == 0)
        {
            return new PipelineCostSummary(Array.Empty<PipelineStepCost>(), 0, 0m, false);
        }

        var steps = new List<PipelineStepCost>(record.Steps.Count);
        long totalTokens = 0;
        decimal totalCost = 0m;
        var anyUnknown = false;

        foreach (var s in record.Steps)
        {
            var est = TokenPricing.Estimate(
                s.Model, s.InputTokens, s.OutputTokens, s.CacheReadTokens, s.CacheCreationTokens);
            var stepTokens = s.InputTokens + s.OutputTokens + s.CacheReadTokens + s.CacheCreationTokens;
            // Only a step that actually consumed tokens but has an unknown
            // model should flag "n/a"; a tool step with 0 tokens is not a
            // pricing gap.
            if (stepTokens > 0 && !est.ModelKnown) anyUnknown = true;

            steps.Add(new PipelineStepCost(
                StepId: s.StepId,
                Kind: s.Kind,
                Model: s.Model,
                ModelKnown: est.ModelKnown,
                InputTokens: s.InputTokens,
                OutputTokens: s.OutputTokens,
                CacheReadTokens: s.CacheReadTokens,
                CacheCreationTokens: s.CacheCreationTokens,
                TotalTokens: stepTokens,
                CostUsd: Round(est.Total)));

            totalTokens += stepTokens;
            totalCost += est.Total;
        }

        return new PipelineCostSummary(steps, totalTokens, Round(totalCost), anyUnknown);
    }

    // Costs are fractions of a cent for a single task; keep 6 dp so the
    // sub-cent detail survives the round-trip and the UI decides display
    // precision.
    private static decimal Round(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}
