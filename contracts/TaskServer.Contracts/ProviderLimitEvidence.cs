using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Provider-owned rate/session-limit evidence shared by local execution and
/// remote capability advertisement.
/// </summary>
public sealed record ProviderLimitEvidence(
    DateTimeOffset ObservedAt,
    DateTimeOffset RetryAt,
    string Detail,
    bool ResetTimeReported);

public static partial class ProviderLimitEvidenceParser
{
    public static readonly TimeSpan UnknownResetRetry = TimeSpan.FromMinutes(15);

    private static readonly string[] ExhaustedNeedles =
    [
        "hit your session limit",
        "session limit",
        "session limit reached",
        "usage limit",
        "usage limit reached",
        "you've reached your usage limit",
        "quota exceeded",
        "rate limit exceeded",
        "rate_limit_exceeded",
        "insufficient_quota",
        "too many requests",
        "429",
        "status=rejected",
        "· rejected ·",
    ];

    public static ProviderLimitEvidence? Detect(
        IEnumerable<string?> output,
        DateTimeOffset observedAt,
        TimeZoneInfo? localZone = null)
    {
        var text = string.Join('\n', output.Where(line => !string.IsNullOrWhiteSpace(line)));
        if (!ExhaustedNeedles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            return null;

        var resetAt = ParseResetAt(text, observedAt, localZone ?? TimeZoneInfo.Local);
        return new ProviderLimitEvidence(
            observedAt,
            resetAt ?? observedAt.Add(UnknownResetRetry),
            FirstLimitLine(text),
            resetAt is not null);
    }

    private static DateTimeOffset? ParseResetAt(
        string text,
        DateTimeOffset observedAt,
        TimeZoneInfo localZone)
    {
        var epoch = EpochResetRegex().Match(text);
        if (epoch.Success
            && long.TryParse(epoch.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds);

        var iso = IsoResetRegex().Match(text);
        if (iso.Success
            && DateTimeOffset.TryParse(
                iso.Groups["value"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var absolute))
            return absolute;

        var relative = RelativeResetRegex().Match(text);
        if (relative.Success
            && double.TryParse(
                relative.Groups["value"].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var amount))
        {
            var delay = relative.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "s" or "sec" or "second" or "seconds" => TimeSpan.FromSeconds(amount),
                "m" or "min" or "minute" or "minutes" => TimeSpan.FromMinutes(amount),
                "h" or "hr" or "hour" or "hours" => TimeSpan.FromHours(amount),
                _ => TimeSpan.Zero,
            };
            if (delay > TimeSpan.Zero) return observedAt.Add(delay);
        }

        var clock = ClockResetRegex().Match(text);
        if (!clock.Success) return null;
        if (!DateTime.TryParse(
                clock.Groups["value"].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
            return null;

        var localNow = TimeZoneInfo.ConvertTimeFromUtc(observedAt.UtcDateTime, localZone);
        var localReset = new DateTime(
            localNow.Year,
            localNow.Month,
            localNow.Day,
            parsed.Hour,
            parsed.Minute,
            0,
            DateTimeKind.Unspecified);
        if (localReset <= localNow) localReset = localReset.AddDays(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localReset, localZone));
    }

    private static string FirstLimitLine(string text)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ExhaustedNeedles.Any(needle => line.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                continue;
            var trimmed = line.Trim();
            return trimmed.Length <= 300 ? trimmed : trimmed[..300];
        }
        return "provider rejected the request at the account limit";
    }

    [GeneratedRegex(@"\bresets?(?:\s+(?:at|on))?\s+(?<value>\d{4}-\d{2}-\d{2}[T ]\d{1,2}:\d{2}(?::\d{2})?(?:\s*Z|\s*[+-]\d{2}:?\d{2})?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IsoResetRegex();

    [GeneratedRegex(@"\bresetsAt\s*=\s*(?<value>\d{10})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpochResetRegex();

    [GeneratedRegex(@"\b(?:retry|resets?)\s+(?:after|in)\s+(?<value>\d+(?:\.\d+)?)\s*(?<unit>s|sec|seconds?|m|min|minutes?|h|hr|hours?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativeResetRegex();

    [GeneratedRegex(@"\bresets?(?:\s+(?:at|on))?\s+(?<value>\d{1,2}:\d{2}\s*(?:a\.?m\.?|p\.?m\.?)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClockResetRegex();
}
