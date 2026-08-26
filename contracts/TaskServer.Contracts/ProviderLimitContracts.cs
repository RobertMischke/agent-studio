using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

public sealed record ProviderLimitObservation(
    string CliType,
    DateTimeOffset ObservedAt,
    DateTimeOffset ResetAt,
    string Reason);

/// <summary>Recognises account-level provider limits without treating ordinary task errors as quota exhaustion.</summary>
public static partial class ProviderLimitParser
{
    public static bool TryParse(
        string? cliType,
        string? output,
        DateTimeOffset observedAt,
        out ProviderLimitObservation observation)
    {
        observation = null!;
        if (!string.Equals(cliType, "claude", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(output)
            || !LimitSignal().IsMatch(output))
            return false;

        var resetAt = ParseEpoch(output) ?? ParseIso(output) ?? ParseClock(output, observedAt) ?? observedAt.AddMinutes(5);
        var signal = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => LimitSignal().IsMatch(line))?.Trim();
        observation = new ProviderLimitObservation(
            "claude",
            observedAt,
            resetAt,
            string.IsNullOrWhiteSpace(signal)
                ? $"claude: limited until {resetAt:O}"
                : $"claude: limited until {resetAt:O}; {Truncate(signal, 300)}");
        return true;
    }

    private static DateTimeOffset? ParseEpoch(string output)
    {
        var match = ResetEpoch().Match(output);
        return match.Success && long.TryParse(match.Groups[1].Value, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    private static DateTimeOffset? ParseClock(string output, DateTimeOffset observedAt)
    {
        var match = ResetClock().Match(output);
        if (!match.Success) return null;
        var value = Regex.Replace(match.Groups[1].Value, "\\s+", "").ToUpperInvariant();
        if (!DateTime.TryParseExact(
                value,
                ["h:mmtt", "hh:mmtt", "H:mm", "HH:mm"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var clock))
            return null;
        var local = observedAt.Date.Add(clock.TimeOfDay);
        var candidate = new DateTimeOffset(local, observedAt.Offset);
        return candidate <= observedAt.AddMinutes(-1) ? candidate.AddDays(1) : candidate;
    }

    private static DateTimeOffset? ParseIso(string output)
    {
        var match = ResetIso().Match(output);
        return match.Success
               && DateTimeOffset.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
    }

    private static string Truncate(string value, int length)
        => value.Length <= length ? value : value[..length];

    [GeneratedRegex(@"(?:session|usage|rate)\s*(?:_|-)?limit(?:\s+(?:reached|hit|exceeded))?|hit\s+(?:your\s+)?session\s+limit|too\s+many\s+requests|rate_limit_exceeded|insufficient_quota", RegexOptions.IgnoreCase)]
    private static partial Regex LimitSignal();

    [GeneratedRegex("""(?:resets?At|reset_at)[\s"':=]+(\d{10})(?!\d)""", RegexOptions.IgnoreCase)]
    private static partial Regex ResetEpoch();

    [GeneratedRegex(@"limited\s+until\s+(\d{4}-\d{2}-\d{2}T[^;\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResetIso();

    [GeneratedRegex(@"resets?(?:\s+at)?\s+(\d{1,2}:\d{2}\s*(?:am|pm)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ResetClock();
}
