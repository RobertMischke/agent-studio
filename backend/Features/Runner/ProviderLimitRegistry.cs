using System.Collections.Concurrent;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Account-level CLI limit observed from a provider rejection. This is a
/// capability state, not a task outcome: every card using the same CLI shares
/// it, while cards routed to another CLI remain eligible.
/// </summary>
public sealed record ProviderLimitStatus(
    string CliType,
    DateTime ObservedAt,
    DateTime LimitedUntil,
    string Reason,
    bool ResetTimeReported);

/// <summary>Pure parser for provider session/rate-limit rejections.</summary>
public static class ProviderLimitDetector
{
    public static readonly TimeSpan UnknownResetRetry = ProviderFailureClassifier.UnknownResetRetry;

    public static ProviderLimitStatus? Detect(
        string? cliType,
        IEnumerable<string?> output,
        DateTime utcNow,
        TimeZoneInfo? localZone = null)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return null;
        var lines = output.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        var text = string.Join('\n', lines);
        var classified = ProviderFailureClassifier.Classify(1, text, null, utcNow, localZone);
        if (classified.Kind != ProviderFailureKind.Limited || classified.LimitedUntil is null)
            return null;

        var observedAt = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var limitedUntil = classified.LimitedUntil.Value;
        var reported = classified.ResetTimeReported;
        var detail = lines.FirstOrDefault(line => ProviderFailureClassifier.IndicatesLimit(line))?.Trim()
                     ?? "provider rejected the request at the account limit";
        if (detail.Length > 300) detail = detail[..300];
        var reason = reported
            ? $"{cliType.Trim().ToLowerInvariant()}: limited until {limitedUntil:O} ({detail})"
            : $"{cliType.Trim().ToLowerInvariant()}: provider limit detected; retry probe at {limitedUntil:O} ({detail})";
        return new ProviderLimitStatus(
            cliType.Trim().ToLowerInvariant(),
            observedAt,
            limitedUntil,
            reason,
            reported);
    }
}

/// <summary>Process-wide provider circuit shared by every project runner.</summary>
public sealed class ProviderLimitRegistry
{
    public static readonly TimeSpan FailedProbeRetry = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, ProviderLimitStatus> _limits =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _probes =
        new(StringComparer.OrdinalIgnoreCase);

    public ProviderLimitStatus Record(ProviderLimitStatus status)
    {
        return _limits.AddOrUpdate(
            status.CliType,
            status,
            (_, existing) => status.LimitedUntil >= existing.LimitedUntil ? status : existing);
    }

    public ProviderLimitStatus? GetActive(string? cliType, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return null;
        return _limits.TryGetValue(cliType, out var status) ? status : null;
    }

    public IReadOnlyList<ProviderLimitStatus> Active(DateTime utcNow)
    {
        return _limits.Values
            .OrderBy(status => status.CliType, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Opens exactly one reset-time quota probe per CLI. The provider remains
    /// limited while the probe runs, so another project tick cannot admit a
    /// card against the same account before recovery is confirmed.
    /// </summary>
    public bool TryBeginRecoveryProbe(string? cliType, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(cliType)
            || !_limits.TryGetValue(cliType, out var status)
            || status.LimitedUntil > utcNow)
            return false;
        return _probes.TryAdd(cliType, 0);
    }

    public void CompleteRecoveryProbe(
        string cliType,
        DateTime utcNow,
        bool recovered,
        string? failureReason = null)
    {
        try
        {
            if (recovered)
            {
                _limits.TryRemove(cliType, out _);
                return;
            }

            if (_limits.TryGetValue(cliType, out var current))
            {
                var retryAt = utcNow.Add(FailedProbeRetry);
                _limits[cliType] = current with
                {
                    LimitedUntil = retryAt,
                    Reason = $"{cliType}: recovery probe still limited; retry at {retryAt:O}"
                             + (string.IsNullOrWhiteSpace(failureReason) ? string.Empty : $" ({failureReason})"),
                    ResetTimeReported = false,
                };
            }
        }
        finally
        {
            _probes.TryRemove(cliType, out _);
        }
    }
}
