

namespace AgentStudio.Pipeline;

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
    string? TokenUsageSource,
    bool ModelKnown,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long TotalTokens,
    decimal InputCostUsd,
    decimal OutputCostUsd,
    decimal CacheReadCostUsd,
    decimal CacheCreationCostUsd,
    decimal CostUsd);

/// <summary>
/// Cost summary for one task's whole pipeline run: per-step rows plus the
/// task total (sum across all pre-steps + the core run + all post-steps),
/// which is the single number the Overview "task total" line shows.
/// </summary>
public sealed record PipelineCostSummary(
    IReadOnlyList<PipelineStepCost> Steps,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalCacheReadTokens,
    long TotalCacheCreationTokens,
    long TotalTokens,
    decimal TotalInputCostUsd,
    decimal TotalOutputCostUsd,
    decimal TotalCacheReadCostUsd,
    decimal TotalCacheCreationCostUsd,
    decimal TotalCostUsd,
    bool AnyModelUnknown);

/// <summary>
/// Token + cost rollup for a single model, summed across the steps that ran
/// on it. A run uses several models (the core agent model, the aspect
/// reviewer's Haiku, an orchestrator decision model), so the Overview RUNS
/// view groups a run's step tokens by model into these rows.
/// <see cref="ModelKnown"/> is false when the model is absent from the
/// <see cref="TokenPricing"/> catalogue so the UI renders "n/a" cost.
/// </summary>
public sealed record PipelineModelTokenUsage(
    string Model,
    bool ModelKnown,
    int Steps,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long TotalTokens,
    decimal CostUsd);

/// <summary>
/// One pipeline run (a <see cref="PipelineExecutionRecord"/> attempt) with
/// its tokens grouped per model. <see cref="Current"/> marks the live run;
/// older runs come from <see cref="PipelineExecutionRecord.PreviousAttempts"/>.
/// </summary>
public sealed record PipelineRunTokenUsage(
    int Attempt,
    bool Current,
    DateTime StartedAt,
    DateTime? CompletedAt,
    IReadOnlyList<PipelineModelTokenUsage> Models,
    long TotalTokens,
    decimal TotalCostUsd,
    bool AnyModelUnknown);

/// <summary>
/// Per-model token usage for one task across every run: a per-run breakdown
/// plus a grand total that sums each model over all runs. Powers the
/// Overview "RUNS - tokens by model" surface (per-run cards plus a visually
/// distinct lifetime total row).
/// </summary>
public sealed record PipelineModelUsageSummary(
    IReadOnlyList<PipelineRunTokenUsage> Runs,
    IReadOnlyList<PipelineModelTokenUsage> TotalByModel,
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
            return new PipelineCostSummary(
                Array.Empty<PipelineStepCost>(),
                0, 0, 0, 0, 0,
                0m, 0m, 0m, 0m, 0m,
                false);
        }

        var steps = new List<PipelineStepCost>(record.Steps.Count);
        long totalInput = 0;
        long totalOutput = 0;
        long totalCacheRead = 0;
        long totalCacheCreation = 0;
        long totalTokens = 0;
        decimal totalInputCost = 0m;
        decimal totalOutputCost = 0m;
        decimal totalCacheReadCost = 0m;
        decimal totalCacheCreationCost = 0m;
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
                TokenUsageSource: s.TokenUsageSource,
                ModelKnown: est.ModelKnown,
                InputTokens: s.InputTokens,
                OutputTokens: s.OutputTokens,
                CacheReadTokens: s.CacheReadTokens,
                CacheCreationTokens: s.CacheCreationTokens,
                TotalTokens: stepTokens,
                InputCostUsd: Round(est.InputUsd),
                OutputCostUsd: Round(est.OutputUsd),
                CacheReadCostUsd: Round(est.CacheReadUsd),
                CacheCreationCostUsd: Round(est.CacheWriteUsd),
                CostUsd: Round(est.Total)));

            totalInput += s.InputTokens;
            totalOutput += s.OutputTokens;
            totalCacheRead += s.CacheReadTokens;
            totalCacheCreation += s.CacheCreationTokens;
            totalTokens += stepTokens;
            totalInputCost += est.InputUsd;
            totalOutputCost += est.OutputUsd;
            totalCacheReadCost += est.CacheReadUsd;
            totalCacheCreationCost += est.CacheWriteUsd;
            totalCost += est.Total;
        }

        return new PipelineCostSummary(
            steps,
            totalInput,
            totalOutput,
            totalCacheRead,
            totalCacheCreation,
            totalTokens,
            Round(totalInputCost),
            Round(totalOutputCost),
            Round(totalCacheReadCost),
            Round(totalCacheCreationCost),
            Round(totalCost),
            anyUnknown);
    }

    /// <summary>
    /// Groups a task's recorded tokens by model, per run and across all runs.
    /// Runs are the current <paramref name="record"/> plus its flattened
    /// <see cref="PipelineExecutionRecord.PreviousAttempts"/>, ordered oldest
    /// first so the UI reads Run #1 -> latest top to bottom. Cost is computed
    /// on the per-model summed tokens (pricing is linear, so summing tokens
    /// then estimating equals estimating per step then summing). Steps with
    /// zero tokens are ignored; a null / empty model collapses to "unknown".
    /// </summary>
    public static PipelineModelUsageSummary SummarizeByModel(PipelineExecutionRecord? record)
    {
        if (record == null)
        {
            return new PipelineModelUsageSummary(
                Array.Empty<PipelineRunTokenUsage>(),
                Array.Empty<PipelineModelTokenUsage>(),
                0, 0m, false);
        }

        // Oldest first: archived attempts (newest-first on disk) ascending by
        // attempt, then the live record last.
        var runs = new List<PipelineRunTokenUsage>();
        foreach (var prev in record.PreviousAttempts.OrderBy(p => p.Attempt))
        {
            runs.Add(BuildRun(prev, current: false));
        }
        runs.Add(BuildRun(record, current: true));

        var allSteps = record.PreviousAttempts
            .SelectMany(p => p.Steps)
            .Concat(record.Steps);
        var totalByModel = GroupByModel(allSteps);

        long totalTokens = totalByModel.Sum(m => m.TotalTokens);
        decimal totalCost = Round(totalByModel.Sum(m => m.CostUsd));
        bool anyUnknown = totalByModel.Any(m => m.TotalTokens > 0 && !m.ModelKnown);

        return new PipelineModelUsageSummary(runs, totalByModel, totalTokens, totalCost, anyUnknown);
    }

    private static PipelineRunTokenUsage BuildRun(PipelineExecutionRecord run, bool current)
    {
        var models = GroupByModel(run.Steps);
        return new PipelineRunTokenUsage(
            Attempt: run.Attempt,
            Current: current,
            StartedAt: run.StartedAt,
            CompletedAt: run.CompletedAt,
            Models: models,
            TotalTokens: models.Sum(m => m.TotalTokens),
            TotalCostUsd: Round(models.Sum(m => m.CostUsd)),
            AnyModelUnknown: models.Any(m => m.TotalTokens > 0 && !m.ModelKnown));
    }

    // Sum a flat list of steps into per-model rows, busiest model first.
    private static IReadOnlyList<PipelineModelTokenUsage> GroupByModel(
        IEnumerable<PipelineStepExecution> steps)
    {
        var byModel = new List<PipelineModelTokenUsage>();
        var groups = steps
            .Where(s => s.InputTokens + s.OutputTokens + s.CacheReadTokens + s.CacheCreationTokens > 0)
            .GroupBy(s => string.IsNullOrWhiteSpace(s.Model) ? "unknown" : s.Model!.Trim(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var g in groups)
        {
            long input = g.Sum(s => s.InputTokens);
            long output = g.Sum(s => s.OutputTokens);
            long cacheRead = g.Sum(s => s.CacheReadTokens);
            long cacheCreation = g.Sum(s => s.CacheCreationTokens);
            var est = TokenPricing.Estimate(g.Key, input, output, cacheRead, cacheCreation);

            byModel.Add(new PipelineModelTokenUsage(
                Model: g.Key,
                ModelKnown: est.ModelKnown,
                Steps: g.Count(),
                InputTokens: input,
                OutputTokens: output,
                CacheReadTokens: cacheRead,
                CacheCreationTokens: cacheCreation,
                TotalTokens: input + output + cacheRead + cacheCreation,
                CostUsd: Round(est.Total)));
        }

        return byModel
            .OrderByDescending(m => m.TotalTokens)
            .ThenBy(m => m.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Costs are fractions of a cent for a single task; keep 6 dp so the
    // sub-cent detail survives the round-trip and the UI decides display
    // precision.
    private static decimal Round(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}
