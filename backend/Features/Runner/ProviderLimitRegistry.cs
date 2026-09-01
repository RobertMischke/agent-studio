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
    public static readonly TimeSpan UnknownResetRetry = ProviderLimitEvidenceParser.UnknownResetRetry;

    public static ProviderLimitStatus? Detect(
        string? cliType,
        IEnumerable<string?> output,
        DateTime utcNow,
        TimeZoneInfo? localZone = null)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return null;
        var observedAt = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        var evidence = ProviderLimitEvidenceParser.Detect(
            output,
            new DateTimeOffset(observedAt),
            localZone);
        if (evidence is null) return null;
        var limitedUntil = evidence.RetryAt.UtcDateTime;
        var reason = evidence.ResetTimeReported
            ? $"{cliType.Trim().ToLowerInvariant()}: limited until {limitedUntil:O} ({evidence.Detail})"
            : $"{cliType.Trim().ToLowerInvariant()}: provider limit detected; retry probe at {limitedUntil:O} ({evidence.Detail})";
        return new ProviderLimitStatus(
            cliType.Trim().ToLowerInvariant(),
            observedAt,
            limitedUntil,
            reason,
            evidence.ResetTimeReported);
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
