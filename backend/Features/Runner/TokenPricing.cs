using CarPricing = CodingAgentRunner.Pricing;

namespace AgentStudio.Runner;

/// <summary>
/// Studio-facing projection of CodingAgentRunner's historical pricing result.
/// Decimal fields remain non-null for wire compatibility; <see cref="ModelKnown"/>
/// is the mandatory guard and is false for both unknown models and dates for
/// which the catalog has no price. Consumers must then render "unknown".
/// </summary>
public sealed record TokenCostEstimate(
    decimal InputUsd,
    decimal OutputUsd,
    decimal CacheReadUsd,
    decimal CacheWriteUsd,
    decimal Total,
    string ModelId,
    bool ModelKnown,
    CarPricing.PriceStatus Status);

/// <summary>
/// The only Studio pricing entry point. Model catalog, aliases, rates, cache
/// policy, and price history all come from CodingAgentRunner (CAR-3).
/// </summary>
public static class TokenPricing
{
    private static readonly CarPricing.ModelPriceCatalog Source = CarPricing.ModelPriceCatalog.Default;

    /// <summary>Read-only CAR catalog projection retained for catalog consumers.</summary>
    public static IReadOnlyDictionary<string, CarPricing.ModelListing> Catalog { get; } =
        Source.Listings.ToDictionary(x => x.ModelId, StringComparer.OrdinalIgnoreCase);

    public static TokenCostEstimate Estimate(
        string? modelId,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheCreationTokens,
        DateTime? recordedAt = null)
    {
        var key = modelId?.Trim() ?? "";
        var atUtc = (recordedAt ?? DateTime.UtcNow).ToUniversalTime();
        var cost = Source.ComputeCost(
            key,
            new CarPricing.TokenUsage(inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens),
            atUtc);

        if (!cost.HasPrice || cost.Total is null)
        {
            return new TokenCostEstimate(0m, 0m, 0m, 0m, 0m,
                cost.ModelId ?? key, ModelKnown: false, cost.Status);
        }

        return new TokenCostEstimate(
            cost.InputCost,
            cost.OutputCost,
            cost.CacheReadCost,
            cost.CacheWriteCost,
            cost.Total.Value,
            cost.ModelId ?? key,
            ModelKnown: true,
            cost.Status);
    }
}
