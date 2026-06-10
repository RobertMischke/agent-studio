
namespace AgentStudio.Runner;

/// <summary>
/// Per-model API price (USD per million tokens). Used to compute the
/// **theoretical** cost of orchestrator activity for transparency, not
/// the user's actual bill: Agent Software Studio runs everything through
/// CLI subscriptions (Pro / Max / Team / Enterprise plans), so real
/// dollar cost is zero on top of those plans. The estimate exists so
/// the user can sanity-check whether the orchestrator is burning a
/// reasonable amount of capacity, and to compare models.
///
/// <para>
/// Sources, current as of May 2026 from anthropic.com:
/// </para>
/// <list type="bullet">
///   <item>Opus 4.5 / 4.6 / 4.7: <c>$5</c> / <c>$25</c> per million input / output tokens.</item>
///   <item>Sonnet 4.5 / 4.6: <c>$3</c> / <c>$15</c>.</item>
///   <item>Haiku 4.5: <c>$1</c> / <c>$5</c>.</item>
/// </list>
/// <para>
/// Cache pricing follows Anthropic's published policy: prompt-cache reads
/// run at 10% of base input, 5-minute cache writes at 125% of base input.
/// (1-hour cache writes at 2x are not exposed by the orchestrator's
/// short-lived single-turn calls; we assume 5-minute writes.)
/// </para>
/// </summary>
public sealed record ModelPrice(
    string ModelId,
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal? CacheReadPerMillionOverride = null,
    decimal? CacheWritePerMillionOverride = null)
{
    /// <summary>Defaults to 10% of base input rate per Anthropic's published policy.</summary>
    public decimal CacheReadPerMillion => CacheReadPerMillionOverride ?? InputPerMillion * 0.10m;
    /// <summary>Defaults to 125% of base input rate for Anthropic 5-minute cache writes.</summary>
    public decimal CacheWritePerMillion => CacheWritePerMillionOverride ?? InputPerMillion * 1.25m;
}

/// <summary>
/// Estimated cost breakdown for a single token-usage record. All amounts
/// are USD. <see cref="Total"/> is informational; the per-bucket fields
/// let the UI explain which part of the spend came from where.
/// </summary>
public sealed record TokenCostEstimate(
    decimal InputUsd,
    decimal OutputUsd,
    decimal CacheReadUsd,
    decimal CacheWriteUsd,
    decimal Total,
    string ModelId,
    bool ModelKnown);

public static class TokenPricing
{
    /// <summary>
    /// Price view derived from <see cref="ModelMetadataRegistry"/>. Models
    /// without price metadata return null prices; callers render token amounts
    /// but suppress the cost line.
    /// </summary>
    public static IReadOnlyDictionary<string, ModelPrice> Catalog { get; } =
        ModelMetadataRegistry.All
            .Where(m => m.InputPricePerMillion is not null && m.OutputPricePerMillion is not null)
            .ToDictionary(
                m => m.Id,
                m => new ModelPrice(
                    m.Id,
                    m.InputPricePerMillion!.Value,
                    m.OutputPricePerMillion!.Value,
                    m.CacheReadPerMillionOverride,
                    m.CacheWritePerMillionOverride),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Compute the theoretical API cost for one usage record. When the
    /// model is not in <see cref="Catalog"/>, returns a zero-cost
    /// estimate with <see cref="TokenCostEstimate.ModelKnown"/> false so
    /// the UI can render "API cost: n/a" instead of misleading zeros.
    /// </summary>
    public static TokenCostEstimate Estimate(
        string? modelId,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheCreationTokens)
    {
        var key = ModelMetadataRegistry.NormalizeId(modelId);
        if (string.IsNullOrWhiteSpace(key) || !Catalog.TryGetValue(key, out var price))
        {
            return new TokenCostEstimate(0, 0, 0, 0, 0, key, ModelKnown: false);
        }

        decimal Mil(long n) => n / 1_000_000m;
        var inputUsd      = Mil(inputTokens)         * price.InputPerMillion;
        var outputUsd     = Mil(outputTokens)        * price.OutputPerMillion;
        var cacheReadUsd  = Mil(cacheReadTokens)     * price.CacheReadPerMillion;
        var cacheWriteUsd = Mil(cacheCreationTokens) * price.CacheWritePerMillion;
        return new TokenCostEstimate(
            InputUsd: inputUsd,
            OutputUsd: outputUsd,
            CacheReadUsd: cacheReadUsd,
            CacheWriteUsd: cacheWriteUsd,
            Total: inputUsd + outputUsd + cacheReadUsd + cacheWriteUsd,
            ModelId: key,
            ModelKnown: true);
    }
}
