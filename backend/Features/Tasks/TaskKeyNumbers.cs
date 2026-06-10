using System.Globalization;

namespace AgentStudio.Tasks;

/// <summary>
/// Pure parser for the numeric tail of a task display key
/// (<c>ASS-<u>594</u></c>). Used to derive a per-project counter floor
/// from the keys actually present on disk so a stale or rewound
/// in-memory counter cannot re-issue a live key. See
/// <c>TaskMutationService.MintTaskKey</c> /
/// <c>TaskMutationService.DeduplicateTaskKeys</c>.
/// </summary>
public static class TaskKeyNumbers
{
    /// <summary>
    /// Parses the integer suffix of <paramref name="key"/> when it has the
    /// shape <c>{shortCode}-{n}</c> (case-insensitive prefix, positive
    /// integer tail). Returns false for null/blank keys, a prefix mismatch,
    /// or a non-numeric / non-positive tail.
    /// </summary>
    public static bool TryParse(string? key, string shortCode, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(shortCode))
            return false;

        var prefix = shortCode + "-";
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var tail = key[prefix.Length..];
        return int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && number > 0;
    }

    /// <summary>
    /// Highest numeric tail across <paramref name="keys"/> for the given
    /// <paramref name="shortCode"/>, or 0 when none parse. The next safe key
    /// number is <c>HighestNumber(...) + 1</c>.
    /// </summary>
    public static int HighestNumber(IEnumerable<string?> keys, string shortCode)
    {
        var max = 0;
        foreach (var key in keys)
            if (TryParse(key, shortCode, out var n) && n > max)
                max = n;
        return max;
    }
}
