using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Per-project token / cost rollup. Three independent dimensions:
/// <list type="bullet">
/// <item><b>Token amounts (real).</b> Per-model input / output / cache
/// counts pulled from <see cref="OrchestratorLogEntry.TokenUsage"/>.</item>
/// <item><b>Theoretical API cost (estimate).</b> Same amounts run
/// through <see cref="TokenPricing"/>. Useful as a comparison and a
/// sanity check; <b>not</b> what the user pays. The CLI subscriptions
/// the runner uses are billed separately and on different units.</item>
/// <item><b>Subscription quota</b> is exposed elsewhere
/// (<c>/api/cli/quota</c>) and not folded in here; aggregating across
/// CLI vendors with different quota models would mislead more than it
/// helps. The frontend points at the quota endpoint with a link.</item>
/// </list>
/// Today this service summarizes the orchestrator log only. Per-job
/// agent token totals are deliberately a separate surface (each job
/// card already carries its own <c>lastUsage</c> string from the CLI's
/// own footer; the formats vary across CLIs and combining them into one
/// number would lose information).
/// </summary>
public sealed record TokenSummary(
    string Project,
    int OrchestratorEntries,
    int OrchestratorLlmCalls,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalCacheReadTokens,
    long TotalCacheCreationTokens,
    decimal EstimatedApiCostUsd,
    bool AllModelsPriced,
    IReadOnlyList<TokenSummaryByModel> ByModel,
    string Disclaimer);

public sealed record TokenSummaryByModel(
    string Model,
    int Calls,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    decimal EstimatedApiCostUsd,
    bool ModelPriced);

public class TokenSummaryService
{
    public const string DefaultDisclaimer =
        "Theoretical API cost based on Anthropic's published per-million-token rates. " +
        "Your actual usage is billed through the CLI subscription you signed in with " +
        "(Pro / Max / Team / Enterprise), so the dollar number above is a comparison, " +
        "not a bill.";

    private readonly OrchestratorLog _log;

    public TokenSummaryService(OrchestratorLog log)
    {
        _log = log;
    }

    public TokenSummary Summarize(string projectName, string watchPath)
    {
        var entries = _log.Read(watchPath);
        return Summarize(projectName, entries);
    }

    /// <summary>
    /// Pure overload: takes the entries directly. Used by the unit tests
    /// to avoid a filesystem round-trip.
    /// </summary>
    public static TokenSummary Summarize(string projectName, IReadOnlyList<OrchestratorLogEntry> entries)
    {
        var perModel = new Dictionary<string, ModelBucket>(StringComparer.OrdinalIgnoreCase);
        long totalInput = 0, totalOutput = 0, totalCacheRead = 0, totalCacheCreate = 0;
        int callCount = 0;

        foreach (var entry in entries)
        {
            var u = entry.TokenUsage;
            if (u == null) continue;
            callCount++;
            totalInput += u.InputTokens;
            totalOutput += u.OutputTokens;
            totalCacheRead += u.CacheReadTokens;
            totalCacheCreate += u.CacheCreationTokens;

            var key = string.IsNullOrWhiteSpace(u.Model) ? "(unknown)" : u.Model!.Trim();
            if (!perModel.TryGetValue(key, out var bucket))
            {
                bucket = new ModelBucket(key);
                perModel[key] = bucket;
            }
            bucket.Calls++;
            bucket.Input += u.InputTokens;
            bucket.Output += u.OutputTokens;
            bucket.CacheRead += u.CacheReadTokens;
            bucket.CacheCreate += u.CacheCreationTokens;
        }

        var byModel = new List<TokenSummaryByModel>();
        decimal grandTotal = 0;
        bool allPriced = perModel.Count > 0;
        foreach (var bucket in perModel.Values.OrderByDescending(b => b.Input + b.Output))
        {
            var cost = TokenPricing.Estimate(bucket.Model, bucket.Input, bucket.Output, bucket.CacheRead, bucket.CacheCreate);
            grandTotal += cost.Total;
            if (!cost.ModelKnown) allPriced = false;
            byModel.Add(new TokenSummaryByModel(
                Model: bucket.Model,
                Calls: bucket.Calls,
                InputTokens: bucket.Input,
                OutputTokens: bucket.Output,
                CacheReadTokens: bucket.CacheRead,
                CacheCreationTokens: bucket.CacheCreate,
                EstimatedApiCostUsd: cost.Total,
                ModelPriced: cost.ModelKnown));
        }

        return new TokenSummary(
            Project: projectName,
            OrchestratorEntries: entries.Count,
            OrchestratorLlmCalls: callCount,
            TotalInputTokens: totalInput,
            TotalOutputTokens: totalOutput,
            TotalCacheReadTokens: totalCacheRead,
            TotalCacheCreationTokens: totalCacheCreate,
            EstimatedApiCostUsd: grandTotal,
            AllModelsPriced: allPriced,
            ByModel: byModel,
            Disclaimer: TokenSummaryService.DefaultDisclaimer);
    }

    private sealed class ModelBucket
    {
        public string Model { get; }
        public int Calls;
        public long Input;
        public long Output;
        public long CacheRead;
        public long CacheCreate;
        public ModelBucket(string model) { Model = model; }
    }
}
