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
    CarPricing.PriceStatus Status,
    TokenPriceBasis? PriceBasis);

/// <summary>The exact historical CAR catalog entry used for a calculation.</summary>
public sealed record TokenPriceBasis(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal CacheReadPerMillion,
    decimal CacheWritePerMillion,
    string Currency,
    DateTime ValidFrom,
    string? Source,
    string? Note,
    bool Unconfirmed);

/// <summary>
/// Pricing seam owned by Studio. The CAR-backed implementation can be replaced
/// by TokenEconomy without changing aggregators or API consumers.
/// </summary>
public interface ITokenPriceProvider
{
    TokenCostEstimate Estimate(string? modelId, long inputTokens, long outputTokens,
        long cacheReadTokens, long cacheCreationTokens, DateTime? recordedAt = null);
}

public sealed class CarTokenPriceProvider : ITokenPriceProvider
{
    private static readonly CarPricing.ModelPriceCatalog Source = CarPricing.ModelPriceCatalog.Default;

    public TokenCostEstimate Estimate(string? modelId, long inputTokens, long outputTokens,
        long cacheReadTokens, long cacheCreationTokens, DateTime? recordedAt = null)
    {
        var key = modelId?.Trim() ?? "";
        var atUtc = (recordedAt ?? DateTime.UtcNow).ToUniversalTime();
        var cost = Source.ComputeCost(key,
            new CarPricing.TokenUsage(inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens),
            atUtc);

        if (!cost.HasPrice || cost.Total is null || cost.Price is null)
            return new TokenCostEstimate(0m, 0m, 0m, 0m, 0m,
                cost.ModelId ?? key, ModelKnown: false, cost.Status, PriceBasis: null);

        var price = cost.Price;
        var basis = new TokenPriceBasis(
            price.InputPerMTok,
            price.OutputPerMTok,
            price.CacheReadPerMTok ?? price.InputPerMTok,
            price.CacheWritePerMTok ?? price.InputPerMTok,
            price.Currency,
            price.ValidFrom,
            price.Source,
            price.Note,
            price.Unconfirmed);

        return new TokenCostEstimate(cost.InputCost, cost.OutputCost, cost.CacheReadCost,
            cost.CacheWriteCost, cost.Total.Value, cost.ModelId ?? key,
            ModelKnown: true, cost.Status, basis);
    }
}

/// <summary>
/// The only Studio pricing entry point. Model catalog, aliases, rates, cache
/// policy, and price history all come from CodingAgentRunner (CAR-3).
/// </summary>
public static class TokenPricing
{
    private static readonly CarPricing.ModelPriceCatalog Source = CarPricing.ModelPriceCatalog.Default;
    private static readonly ITokenPriceProvider Provider = new CarTokenPriceProvider();

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
        return Provider.Estimate(modelId, inputTokens, outputTokens, cacheReadTokens,
            cacheCreationTokens, recordedAt);
    }
}
