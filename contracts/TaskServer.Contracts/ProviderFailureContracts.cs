using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

public enum ProviderFailureKind
{
    None,
    Authentication,
    Limited,
    Indeterminate,
}

public sealed record ProviderFailureClassification(
    ProviderFailureKind Kind,
    DateTime? LimitedUntil = null,
    bool ResetTimeReported = false);

/// <summary>
/// Shared, output-based provider failure classifier. Non-zero is only transport
/// evidence: it is never authentication evidence by itself.
/// </summary>
public static partial class ProviderFailureClassifier
{
    public static readonly TimeSpan UnknownResetRetry = TimeSpan.FromMinutes(15);

    private static readonly string[] LimitSignals =
    [
        "hit your session limit", "session limit", "usage limit",
        "you've reached your usage limit", "quota exceeded", "rate limit exceeded",
        "rate_limit_exceeded", "insufficient_quota", "too many requests", "429",
        "status=rejected", "· rejected ·",
    ];

    private static readonly string[] AuthenticationSignals =
    [
        "not logged in", "not signed in", "logged out", "no active session",
        "not authenticated", "no credentials", "login required", "please log in",
        "please login", "re-authenticate", "reauthenticate", "refresh token expired",
        "invalid api key", "invalid credentials", "credentials are invalid",
    ];

    private static readonly string[] ToolFailureSignals =
    [
        "apply_patch verification failed", "failed to find context",
        "context not found", "patch apply failed", "patch failed to apply",
    ];

    public static ProviderFailureClassification Classify(
        int exitCode,
        string? stdout,
        string? stderr,
        DateTime utcNow,
        TimeZoneInfo? localZone = null)
    {
        if (exitCode == 0) return new ProviderFailureClassification(ProviderFailureKind.None);
        var text = $"{stdout}\n{stderr}";
        if (Matches(text, LimitSignals))
        {
            var observedAt = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            var resetAt = ParseResetAt(text, observedAt, localZone ?? TimeZoneInfo.Local);
            return new ProviderFailureClassification(
                ProviderFailureKind.Limited,
                resetAt ?? observedAt.Add(UnknownResetRetry),
                resetAt is not null);
        }

        if (Matches(text, ToolFailureSignals))
            return new ProviderFailureClassification(ProviderFailureKind.Indeterminate);

        if (Matches(text, AuthenticationSignals) || IndicatesAuthenticationHttp401(text))
            return new ProviderFailureClassification(ProviderFailureKind.Authentication);

        return new ProviderFailureClassification(ProviderFailureKind.Indeterminate);
    }

    public static bool IndicatesAuthentication(string? text)
        => Matches(text, AuthenticationSignals) || IndicatesAuthenticationHttp401(text);

    public static bool IndicatesLimit(string? text) => Matches(text, LimitSignals);

    private static bool IndicatesAuthenticationHttp401(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && text.Contains("401", StringComparison.OrdinalIgnoreCase)
           && (text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
               || text.Contains("authentication", StringComparison.OrdinalIgnoreCase)
               || text.Contains("credential", StringComparison.OrdinalIgnoreCase)
               || text.Contains("token", StringComparison.OrdinalIgnoreCase));

    private static bool Matches(string? text, IEnumerable<string> signals)
        => !string.IsNullOrWhiteSpace(text)
           && signals.Any(signal => text.Contains(signal, StringComparison.OrdinalIgnoreCase));

    private static DateTime? ParseResetAt(string text, DateTime utcNow, TimeZoneInfo localZone)
    {
        var epoch = EpochResetRegex().Match(text);
        if (epoch.Success
            && long.TryParse(epoch.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

        var iso = IsoResetRegex().Match(text);
        if (iso.Success
            && DateTimeOffset.TryParse(
                iso.Groups["value"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var absolute))
            return absolute.UtcDateTime;

        var clock = ClockResetRegex().Match(text);
        if (!clock.Success
            || !DateTime.TryParse(
                clock.Groups["value"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
            return null;

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, localZone);
        var localReset = new DateTime(
            localNow.Year, localNow.Month, localNow.Day,
            parsed.Hour, parsed.Minute, 0, DateTimeKind.Unspecified);
        if (localReset <= localNow) localReset = localReset.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(localReset, localZone);
    }

    [GeneratedRegex(@"\bresets?(?:\s+(?:at|on))?\s+(?<value>\d{4}-\d{2}-\d{2}[T ]\d{1,2}:\d{2}(?::\d{2})?(?:\s*Z|\s*[+-]\d{2}:?\d{2})?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsoResetRegex();

    [GeneratedRegex(@"\bresetsAt\s*=\s*(?<value>\d{10})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpochResetRegex();

    [GeneratedRegex(@"\bresets?(?:\s+(?:at|on))?\s+(?<value>\d{1,2}:\d{2}\s*(?:a\.?m\.?|p\.?m\.?)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClockResetRegex();
}
