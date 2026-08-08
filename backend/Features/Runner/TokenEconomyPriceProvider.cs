using System.Collections.Concurrent;
using Serilog;
using EconomyPricing = TokenEconomy;

namespace AgentStudio.Runner;

/// <summary>
/// Adapts the published TokenEconomy package to Studio's stable pricing
/// contract. Package-specific types and cost projection stay behind
/// <see cref="ITokenPriceProvider"/>.
/// </summary>
public sealed class TokenEconomyPriceProvider : ITokenPriceProvider
{
    private static readonly EconomyPricing.ModelPriceCatalog Source =
        EconomyPricing.ModelPriceCatalog.Default;
    private readonly ConcurrentDictionary<string, byte> _warnedUnknownModels =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _warnUnknownModel;

    public TokenEconomyPriceProvider() : this(WriteUnknownModelWarning)
    {
    }

    internal TokenEconomyPriceProvider(Action<string> warnUnknownModel)
    {
        _warnUnknownModel = warnUnknownModel;
    }

    public TokenCostEstimate Estimate(
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
            new EconomyPricing.TokenUsage(
                inputTokens,
                outputTokens,
                cacheReadTokens,
                cacheCreationTokens),
            atUtc);

        if (cost.Status == EconomyPricing.PriceStatus.UnknownModel
            && HasUsage(inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens)
            && _warnedUnknownModels.TryAdd(key, 0))
        {
            _warnUnknownModel(key);
        }

        if (!cost.HasPrice || cost.Total is null || cost.Price is null)
        {
            return new TokenCostEstimate(
                0m,
                0m,
                0m,
                0m,
                0m,
                cost.ModelId ?? key,
                ModelKnown: false,
                cost.Status,
                PriceBasis: null);
        }

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

        return new TokenCostEstimate(
            cost.InputCost,
            cost.OutputCost,
            cost.CacheReadCost,
            cost.CacheWriteCost,
            cost.Total.Value,
            cost.ModelId ?? key,
            ModelKnown: true,
            cost.Status,
            basis);
    }

    private static bool HasUsage(long inputTokens, long outputTokens, long cacheReadTokens, long cacheCreationTokens)
        => inputTokens > 0 || outputTokens > 0 || cacheReadTokens > 0 || cacheCreationTokens > 0;

    private static void WriteUnknownModelWarning(string modelId)
    {
        Log.Warning(
            "Token pricing catalog drift: active model {ModelId} is unknown to the pinned TokenEconomy catalog; update the exact package pin",
            modelId);
    }
}
