

namespace AgentStudio.Pipeline;

/// <summary>
/// One model/reason pair that prevented historical price resolution. The run
/// count lets aggregate UIs distinguish an unavailable amount from a priced
/// subtotal without treating either state as zero dollars.
/// </summary>
public sealed record PipelinePricingGap(
    string ModelId,
    string Reason,
    int AffectedRuns);

/// <summary>
/// Per-step cost (USD) breakdown for one step's recorded token usage.
/// <see cref="ModelKnown"/> is false when the historical resolver has no
/// price for the model and run date. <see cref="PricingGaps"/> carries the
/// exact model id and resolver reason for an honest unavailable-price state.
/// </summary>
public sealed record PipelineStepCost(
    string StepId,
    StepKind Kind,
    string? Model,
    string? TokenUsageSource,
    bool ModelKnown,
    IReadOnlyList<PipelinePricingGap> PricingGaps,
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
    bool AnyModelUnknown,
    int UnpricedRuns,
    IReadOnlyList<PipelinePricingGap> PricingGaps);

/// <summary>
/// Token + cost rollup for a single model, summed across the steps that ran
/// on it. A run uses several models (the core agent model, the aspect
/// reviewer's Haiku, an orchestrator decision model), so the Overview RUNS
/// view groups a run's step tokens by model into these rows.
/// <see cref="ModelKnown"/> is false when the historical resolver has no
/// price for at least one contributing run.
/// </summary>
public sealed record PipelineModelTokenUsage(
    string Model,
    bool ModelKnown,
    int UnpricedRuns,
    IReadOnlyList<PipelinePricingGap> PricingGaps,
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
    bool AnyModelUnknown,
    IReadOnlyList<PipelinePricingGap> PricingGaps);

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
    bool AnyModelUnknown,
    int UnpricedRuns,
    IReadOnlyList<PipelinePricingGap> PricingGaps);

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
                false, 0, Array.Empty<PipelinePricingGap>());
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
                s.Model, s.InputTokens, s.OutputTokens, s.CacheReadTokens, s.CacheCreationTokens,
                record.StartedAt);
            var stepTokens = s.InputTokens + s.OutputTokens + s.CacheReadTokens + s.CacheCreationTokens;
            // Only a step that actually consumed tokens but has no resolved
            // historical price should flag a gap; a 0-token tool step is not a
            // pricing gap.
            if (stepTokens > 0 && !est.ModelKnown) anyUnknown = true;

            steps.Add(new PipelineStepCost(
                StepId: s.StepId,
                Kind: s.Kind,
                Model: s.Model,
                TokenUsageSource: s.TokenUsageSource,
                ModelKnown: est.ModelKnown,
                PricingGaps: PricingGapsFor(est, stepTokens, s.Model),
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
            anyUnknown,
            anyUnknown ? 1 : 0,
            MergePricingGaps(steps.SelectMany(step => step.PricingGaps), oneRun: true));
    }

    /// <summary>
    /// Replaces the cost rows for read-projected remote steps with the
    /// canonical per-call token-ledger values. Local rows remain derived from
    /// <paramref name="record"/> exactly as before. This keeps historical
    /// per-call pricing and call counts out of <c>pipeline-execution.json</c>
    /// while making the task total reconcile with the ledger-backed Task tab.
    /// </summary>
    public static PipelineCostSummary SummarizeWithLedger(
        PipelineExecutionRecord? record,
        IReadOnlyDictionary<string, IReadOnlyList<TaskTokenCall>> ledgerCalls)
    {
        var baseline = Summarize(record);
        if (ledgerCalls.Count == 0) return baseline;

        var steps = baseline.Steps
            .Select(step => ledgerCalls.TryGetValue(step.StepId, out var calls) && calls.Count > 0
                ? CostFromLedger(step, calls)
                : step)
            .ToList();
        return new PipelineCostSummary(
            steps,
            steps.Sum(step => step.InputTokens),
            steps.Sum(step => step.OutputTokens),
            steps.Sum(step => step.CacheReadTokens),
            steps.Sum(step => step.CacheCreationTokens),
            steps.Sum(step => step.TotalTokens),
            Round(steps.Sum(step => step.InputCostUsd)),
            Round(steps.Sum(step => step.OutputCostUsd)),
            Round(steps.Sum(step => step.CacheReadCostUsd)),
            Round(steps.Sum(step => step.CacheCreationCostUsd)),
            Round(steps.Sum(step => step.CostUsd)),
            steps.Any(step => step.TotalTokens > 0 && !step.ModelKnown),
            steps.Any(step => step.TotalTokens > 0 && !step.ModelKnown) ? 1 : 0,
            MergePricingGaps(steps.SelectMany(step => step.PricingGaps), oneRun: true));
    }

    private static PipelineStepCost CostFromLedger(
        PipelineStepCost baseline,
        IReadOnlyList<TaskTokenCall> calls)
    {
        long input = 0;
        long output = 0;
        long cacheRead = 0;
        long cacheCreation = 0;
        decimal inputCost = 0;
        decimal outputCost = 0;
        decimal cacheReadCost = 0;
        decimal cacheCreationCost = 0;
        var allPriced = true;
        var pricingGaps = new List<PipelinePricingGap>();

        foreach (var call in calls)
        {
            input += call.InputTokens;
            output += call.OutputTokens;
            cacheRead += call.CacheReadTokens;
            cacheCreation += call.CacheCreationTokens;

            var estimate = TokenPricing.Estimate(
                call.Model,
                call.InputTokens,
                call.OutputTokens,
                call.CacheReadTokens,
                call.CacheCreationTokens,
                call.Ts);
            var priceResolved = call.ModelPriced || estimate.ModelKnown;
            allPriced &= priceResolved;
            if (!priceResolved)
            {
                pricingGaps.AddRange(PricingGapsFor(
                    estimate,
                    call.InputTokens + call.OutputTokens
                        + call.CacheReadTokens + call.CacheCreationTokens,
                    call.Model));
            }
            if (estimate.ModelKnown && estimate.Total > 0)
            {
                // Preserve a historical ledger amount when one was recorded.
                // If an older ledger row was unpriced but a newer catalogue
                // version now resolves the same run date, use that historical
                // catalogue estimate so rollout removes the missing-price state.
                var scale = call.ModelPriced
                    ? call.EstimatedApiCostUsd / estimate.Total
                    : 1m;
                inputCost += estimate.InputUsd * scale;
                outputCost += estimate.OutputUsd * scale;
                cacheReadCost += estimate.CacheReadUsd * scale;
                cacheCreationCost += estimate.CacheWriteUsd * scale;
                continue;
            }

            // The task ledger has already priced this historical call. If its
            // display model no longer resolves back to a catalogue id, preserve
            // the authoritative total and distribute it by token share so the
            // four visible components still sum exactly to the row.
            var tokens = call.InputTokens + call.OutputTokens
                + call.CacheReadTokens + call.CacheCreationTokens;
            if (call.ModelPriced && tokens > 0)
            {
                inputCost += call.EstimatedApiCostUsd * call.InputTokens / tokens;
                outputCost += call.EstimatedApiCostUsd * call.OutputTokens / tokens;
                cacheReadCost += call.EstimatedApiCostUsd * call.CacheReadTokens / tokens;
                cacheCreationCost += call.EstimatedApiCostUsd * call.CacheCreationTokens / tokens;
            }
        }

        return baseline with
        {
            TokenUsageSource =
                $"Remote token ledger · {calls.Count} call{(calls.Count == 1 ? "" : "s")}",
            ModelKnown = allPriced,
            PricingGaps = MergePricingGaps(pricingGaps, oneRun: true),
            InputTokens = input,
            OutputTokens = output,
            CacheReadTokens = cacheRead,
            CacheCreationTokens = cacheCreation,
            TotalTokens = input + output + cacheRead + cacheCreation,
            InputCostUsd = Round(inputCost),
            OutputCostUsd = Round(outputCost),
            CacheReadCostUsd = Round(cacheReadCost),
            CacheCreationCostUsd = Round(cacheCreationCost),
            CostUsd = Round(inputCost + outputCost + cacheReadCost + cacheCreationCost),
        };
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
                0, 0m, false, 0, Array.Empty<PipelinePricingGap>());
        }

        // Oldest first: archived attempts (newest-first on disk) ascending by
        // attempt, then the live record last.
        var runs = new List<PipelineRunTokenUsage>();
        foreach (var prev in record.PreviousAttempts.OrderBy(p => p.Attempt))
        {
            runs.Add(BuildRun(prev, current: false));
        }
        runs.Add(BuildRun(record, current: true));

        var totalByModel = runs
            .SelectMany(r => r.Models)
            .GroupBy(m => m.Model, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PipelineModelTokenUsage(
                g.Key,
                g.All(m => m.ModelKnown),
                g.Sum(m => m.UnpricedRuns),
                MergePricingGaps(g.SelectMany(m => m.PricingGaps)),
                g.Sum(m => m.Steps),
                g.Sum(m => m.InputTokens),
                g.Sum(m => m.OutputTokens),
                g.Sum(m => m.CacheReadTokens),
                g.Sum(m => m.CacheCreationTokens),
                g.Sum(m => m.TotalTokens),
                Round(g.Sum(m => m.CostUsd))))
            .OrderByDescending(m => m.TotalTokens)
            .ThenBy(m => m.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();

        long totalTokens = totalByModel.Sum(m => m.TotalTokens);
        decimal totalCost = Round(totalByModel.Sum(m => m.CostUsd));
        bool anyUnknown = totalByModel.Any(m => m.TotalTokens > 0 && !m.ModelKnown);
        var unpricedRuns = runs.Count(run => run.AnyModelUnknown);
        var pricingGaps = MergePricingGaps(runs.SelectMany(run => run.PricingGaps));

        return new PipelineModelUsageSummary(
            runs, totalByModel, totalTokens, totalCost, anyUnknown, unpricedRuns, pricingGaps);
    }

    private static PipelineRunTokenUsage BuildRun(PipelineExecutionRecord run, bool current)
    {
        var models = GroupByModel(run.Steps, run.StartedAt);
        return new PipelineRunTokenUsage(
            Attempt: run.Attempt,
            Current: current,
            StartedAt: run.StartedAt,
            CompletedAt: run.CompletedAt,
            Models: models,
            TotalTokens: models.Sum(m => m.TotalTokens),
            TotalCostUsd: Round(models.Sum(m => m.CostUsd)),
            AnyModelUnknown: models.Any(m => m.TotalTokens > 0 && !m.ModelKnown),
            PricingGaps: MergePricingGaps(
                models.SelectMany(model => model.PricingGaps), oneRun: true));
    }

    // Sum a flat list of steps into per-model rows, busiest model first.
    private static IReadOnlyList<PipelineModelTokenUsage> GroupByModel(
        IEnumerable<PipelineStepExecution> steps,
        DateTime recordedAt)
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
            var est = TokenPricing.Estimate(g.Key, input, output, cacheRead, cacheCreation, recordedAt);

            byModel.Add(new PipelineModelTokenUsage(
                Model: g.Key,
                ModelKnown: est.ModelKnown,
                UnpricedRuns: est.ModelKnown ? 0 : 1,
                PricingGaps: PricingGapsFor(
                    est, input + output + cacheRead + cacheCreation, g.Key),
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

    private static IReadOnlyList<PipelinePricingGap> PricingGapsFor(
        TokenCostEstimate estimate,
        long totalTokens,
        string? displayModel)
    {
        if (totalTokens <= 0 || estimate.ModelKnown)
            return Array.Empty<PipelinePricingGap>();
        var modelId = !string.IsNullOrWhiteSpace(displayModel)
            ? displayModel.Trim()
            : string.IsNullOrWhiteSpace(estimate.ModelId) ? "unknown" : estimate.ModelId.Trim();
        return [new PipelinePricingGap(modelId, estimate.Status.ToString(), 1)];
    }

    private static IReadOnlyList<PipelinePricingGap> MergePricingGaps(
        IEnumerable<PipelinePricingGap> gaps,
        bool oneRun = false)
        => gaps
            .GroupBy(gap => (gap.ModelId, gap.Reason))
            .Select(group => new PipelinePricingGap(
                group.Key.ModelId,
                group.Key.Reason,
                oneRun ? 1 : group.Sum(gap => gap.AffectedRuns)))
            .OrderBy(gap => gap.ModelId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(gap => gap.Reason, StringComparer.Ordinal)
            .ToList();

    // Costs are fractions of a cent for a single task; keep 6 dp so the
    // sub-cent detail survives the round-trip and the UI decides display
    // precision.
    private static decimal Round(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}
