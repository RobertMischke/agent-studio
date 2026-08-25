using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

public sealed record ProviderLimitDetection(
    string Provider,
    DateTime ObservedAt,
    DateTime RetryAt,
    string Reason,
    string? ReportedReset = null);

/// <summary>Parses account-wide, resettable provider-limit responses.</summary>
public static partial class ProviderLimitParser
{
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMinutes(15);

    [GeneratedRegex(@"(?:you(?:'|’)ve\s+hit\s+your\s+session\s+limit|\bsession\s+limit\b|\busage\s+limit\s+(?:reached|exceeded)|\brate[ -]?limit(?:ed|\s+exceeded)?\b|\btoo\s+many\s+requests\b|\b429\b)", RegexOptions.IgnoreCase)]
    private static partial Regex LimitPattern();

    [GeneratedRegex(@"\bresets?\s+(?:at\s+)?(?<reset>[^\r\n·;,.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResetPattern();

    public static ProviderLimitDetection? Detect(
        string provider,
        string? output,
        DateTime? observedAtUtc = null,
        TimeZoneInfo? providerTimeZone = null)
    {
        if (!string.Equals(provider, "claude", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(output)
            || !LimitPattern().IsMatch(output))
            return null;

        var observed = (observedAtUtc ?? DateTime.UtcNow).ToUniversalTime();
        var match = ResetPattern().Match(output);
        var reportedReset = match.Success ? match.Groups["reset"].Value.Trim() : null;
        var retryAt = TryParseReset(reportedReset, observed, providerTimeZone ?? TimeZoneInfo.Local)
                      ?? observed.Add(DefaultRetryDelay);
        var reason = reportedReset is null
            ? "Claude account session limit reached; retrying after a bounded cooldown."
            : $"Claude account session limit reached; resets {reportedReset}.";
        return new ProviderLimitDetection("claude", observed, retryAt, reason, reportedReset);
    }

    private static DateTime? TryParseReset(string? value, DateTime observedUtc, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var absolute)
            && (value.Contains('Z') || Regex.IsMatch(value, @"[+-]\d\d:?\d\d")))
            return absolute.UtcDateTime;

        var localObserved = TimeZoneInfo.ConvertTimeFromUtc(observedUtc, zone);
        var cleaned = Regex.Replace(value, @"\s*\([^)]*\)\s*$", "").Trim();
        foreach (var format in new[] { "h:mmtt", "h:mm tt", "H:mm", "HH:mm" })
        {
            if (!DateTime.TryParseExact(cleaned, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var parsed)) continue;
            var localReset = localObserved.Date.Add(parsed.TimeOfDay);
            if (localReset <= localObserved) localReset = localReset.AddDays(1);
            return TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(localReset, DateTimeKind.Unspecified), zone);
        }
        return null;
    }
}
