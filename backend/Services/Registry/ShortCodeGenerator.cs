using System.Text;
using System.Text.RegularExpressions;

namespace OrchestratorApi.Services.Registry;

/// <summary>
/// F45a — derives a default <c>shortCode</c> from a display name and
/// validates user-provided codes. The short code becomes the prefix of
/// every task display key in the project (<c>ATP-130</c>).
///
/// <list type="bullet">
/// <item>1 word → first 3 letters, upper-case ("Runbook" → "RUN").</item>
/// <item>2-3 words → first letter of each word ("Agent Task Processor" → "ATP").</item>
/// <item>4+ words → first letter of the first three words ("Agent Software Studio Project" → "ASS"; the user can override).</item>
/// </list>
///
/// Collisions against an existing-codes set are resolved with a numeric
/// suffix ("ATP", "ATP2", "ATP3"). The user can override via the project
/// PUT endpoint as long as the result passes <see cref="ValidateFormat"/>.
/// </summary>
public static class ShortCodeGenerator
{
    private static readonly Regex AlnumOnly = new("[^A-Za-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex ValidCode = new("^[A-Z][A-Z0-9]{1,5}$", RegexOptions.Compiled);

    /// <summary>
    /// Derives a default code from <paramref name="displayName"/> and, if
    /// the candidate already exists in <paramref name="existingCodes"/>,
    /// suffixes a counter until it does not. Returns <c>"PROJ"</c> as a
    /// safe fallback when the input has no usable letters.
    /// </summary>
    public static string Derive(string displayName, IEnumerable<string> existingCodes)
    {
        var taken = new HashSet<string>(existingCodes ?? [], StringComparer.OrdinalIgnoreCase);
        var seed = DeriveSeed(displayName);
        if (string.IsNullOrEmpty(seed)) seed = "PROJ";

        if (!taken.Contains(seed)) return seed;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{seed}{i}";
            // The derived seed plus a counter can overflow the 6-char
            // format limit (e.g. "ABCDEF" + "10" = 8 chars). Trim from
            // the right of the seed to stay within range.
            if (candidate.Length > 6) candidate = $"{seed[..(6 - $"{i}".Length)]}{i}";
            if (!taken.Contains(candidate) && ValidCode.IsMatch(candidate)) return candidate;
        }
        // Pathological case: 1000 collisions. Fall back to a guid-shaped
        // suffix rather than throwing - the user can rename.
        return ($"PROJ{Guid.NewGuid().ToString("N")[..2].ToUpperInvariant()}");
    }

    /// <summary>
    /// Returns true when <paramref name="code"/> matches the published
    /// constraint (2-6 chars, starts with a letter, A-Z + 0-9 only).
    /// </summary>
    public static bool ValidateFormat(string? code) =>
        !string.IsNullOrEmpty(code) && ValidCode.IsMatch(code);

    private static string DeriveSeed(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "";

        var cleaned = AlnumOnly.Replace(displayName, " ").Trim();
        if (cleaned.Length == 0) return "";

        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "";

        if (words.Length == 1)
        {
            var w = words[0];
            var take = Math.Min(3, w.Length);
            return w[..take].ToUpperInvariant();
        }

        var initials = new StringBuilder();
        var max = Math.Min(words.Length, 3);
        for (var i = 0; i < max; i++)
        {
            if (words[i].Length == 0) continue;
            initials.Append(char.ToUpperInvariant(words[i][0]));
        }
        var result = initials.ToString();
        // 2-3 letters is the common case; only one usable word collapses
        // to a 1-letter code which fails ValidateFormat. Pad from the
        // remaining letters of the first word.
        if (result.Length < 2)
        {
            var first = words[0];
            for (var j = 1; j < first.Length && result.Length < 2; j++)
            {
                result += char.ToUpperInvariant(first[j]);
            }
        }
        return result;
    }
}
