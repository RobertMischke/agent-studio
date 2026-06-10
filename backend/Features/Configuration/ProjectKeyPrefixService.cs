using System.Globalization;
using System.Text;

namespace AgentStudio.Configuration;

/// <summary>
/// F33: maps a project name (the <see cref="WatchPathEntry.Name"/> field
/// on a watched workspace) to a short uppercase prefix used to mint
/// stable Linear-style task reference keys (<c>ATP-130</c>, <c>RB-42</c>).
///
/// <para>Resolution order for each project:
/// <list type="number">
/// <item>An explicit entry under <c>KeyPrefixes</c> in app configuration
///   (case-insensitive key match) wins.</item>
/// <item>Otherwise a deterministic heuristic derives a 2-4 character
///   prefix from the words in the project name (initials for multi-word
///   names; first three letters for single-word names).</item>
/// </list>
/// </para>
///
/// <para>Conflicts (two projects resolving to the same prefix) throw at
/// boot with a directed message naming the colliding projects so the
/// operator can add a disambiguating entry to
/// <c>appsettings.Local.json</c>. Blocking boot is intentional: silently
/// minting two <c>ATP-1</c>s in different projects would defeat the
/// whole point of stable reference keys.</para>
/// </summary>
public sealed class ProjectKeyPrefixService
{
    private readonly Dictionary<string, string> _byProjectName;

    public ProjectKeyPrefixService(IConfiguration configuration, IEnumerable<WatchPathEntry> watchPaths)
    {
        var explicitMap = configuration.GetSection("KeyPrefixes")
            .GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Key) && !string.IsNullOrWhiteSpace(c.Value))
            .ToDictionary(c => c.Key, c => NormalizePrefix(c.Value!), StringComparer.OrdinalIgnoreCase);

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byPrefix = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: honour explicit overrides.
        foreach (var entry in watchPaths)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            if (explicitMap.TryGetValue(entry.Name, out var explicitPrefix))
            {
                AssignOrThrow(entry.Name, explicitPrefix, resolved, byPrefix, source: "configured");
            }
        }

        // Pass 2: derive a default for the remaining projects.
        foreach (var entry in watchPaths)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;
            if (resolved.ContainsKey(entry.Name)) continue;
            var derived = DerivePrefix(entry.Name);
            // If the heuristic collides with an existing prefix we suffix a
            // numeric tail so boot still succeeds; the operator should add
            // an explicit override to <c>KeyPrefixes</c>. We still throw if
            // even the suffixed prefix collides 99 times in a row, which
            // would only happen with deeply pathological project naming.
            var candidate = derived;
            var suffix = 2;
            while (byPrefix.ContainsKey(candidate))
            {
                candidate = derived + suffix;
                if (++suffix > 99)
                {
                    throw new InvalidOperationException(
                        $"ProjectKeyPrefixService could not derive a unique prefix for '{entry.Name}'. " +
                        $"Add an explicit entry to KeyPrefixes in appsettings.");
                }
            }
            AssignOrThrow(entry.Name, candidate, resolved, byPrefix, source: "derived");
        }

        _byProjectName = resolved;
    }

    /// <summary>
    /// Snapshot of the resolved per-project prefix table. Useful for log
    /// messages and the migration sweep.
    /// </summary>
    public IReadOnlyDictionary<string, string> All => _byProjectName;

    /// <summary>
    /// Returns the prefix for <paramref name="projectName"/> or null when
    /// the project is not in the configured watch path list. Lookup is
    /// case-insensitive.
    /// </summary>
    public string? TryGetPrefix(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName)) return null;
        return _byProjectName.TryGetValue(projectName, out var p) ? p : null;
    }

    /// <summary>
    /// Resolves the project name (case-insensitive) that owns
    /// <paramref name="prefix"/>, or null when no project uses it.
    /// </summary>
    public string? TryGetProjectForPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return null;
        foreach (var kv in _byProjectName)
        {
            if (string.Equals(kv.Value, prefix, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        }
        return null;
    }

    /// <summary>
    /// Parses a reference key like <c>ATP-130</c> into its prefix
    /// (<c>ATP</c>) and numeric tail (<c>130</c>). Returns false when the
    /// input does not match the canonical shape; whitespace is trimmed.
    /// </summary>
    public static bool TryParseKey(string key, out string prefix, out int number)
    {
        prefix = "";
        number = 0;
        if (string.IsNullOrWhiteSpace(key)) return false;
        var trimmed = key.Trim();
        var dash = trimmed.IndexOf('-');
        if (dash <= 0 || dash >= trimmed.Length - 1) return false;
        var p = trimmed[..dash];
        var n = trimmed[(dash + 1)..];
        foreach (var c in p)
        {
            if (!(c is >= 'A' and <= 'Z' or >= '0' and <= '9')) return false;
        }
        if (!int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
        if (parsed <= 0) return false;
        prefix = p;
        number = parsed;
        return true;
    }

    private static void AssignOrThrow(
        string projectName,
        string prefix,
        Dictionary<string, string> resolved,
        Dictionary<string, string> byPrefix,
        string source)
    {
        if (byPrefix.TryGetValue(prefix, out var existing))
        {
            throw new InvalidOperationException(
                $"ProjectKeyPrefixService: prefix '{prefix}' is already used by project '{existing}' " +
                $"and would also apply to '{projectName}' ({source}). " +
                $"Set distinct prefixes via the KeyPrefixes section of appsettings.");
        }
        resolved[projectName] = prefix;
        byPrefix[prefix] = projectName;
    }

    /// <summary>
    /// Coerces an explicit configured value to the prefix grammar:
    /// uppercase, ASCII letters / digits only, max 6 chars, no trailing
    /// hyphen (the dash between prefix and number is added at key-mint
    /// time).
    /// </summary>
    internal static string NormalizePrefix(string raw)
    {
        var trimmed = raw.Trim().TrimEnd('-').ToUpperInvariant();
        var sb = new StringBuilder(Math.Min(trimmed.Length, 6));
        foreach (var c in trimmed)
        {
            if (sb.Length >= 6) break;
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') sb.Append(c);
        }
        if (sb.Length == 0)
        {
            throw new InvalidOperationException(
                $"ProjectKeyPrefixService: configured prefix '{raw}' has no usable [A-Z0-9] characters.");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Derives a default prefix for a project name. Strips diacritics, then:
    /// - multi-word names (split on whitespace / hyphen / underscore):
    ///   uppercase initial of each non-empty word, up to 4 chars;
    /// - single-word names: first three uppercase letters / digits.
    /// </summary>
    internal static string DerivePrefix(string projectName)
    {
        var ascii = StripDiacritics(projectName);
        var words = ascii.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);

        if (words.Length >= 2)
        {
            var sb = new StringBuilder(4);
            foreach (var w in words)
            {
                if (sb.Length >= 4) break;
                var ch = FirstAlnumUpper(w);
                if (ch.HasValue) sb.Append(ch.Value);
            }
            if (sb.Length >= 2) return sb.ToString();
        }

        var single = new StringBuilder(3);
        foreach (var c in ascii)
        {
            if (single.Length >= 3) break;
            if (c is >= 'A' and <= 'Z') single.Append(c);
            else if (c is >= 'a' and <= 'z') single.Append(char.ToUpperInvariant(c));
            else if (c is >= '0' and <= '9') single.Append(c);
        }
        if (single.Length > 0) return single.ToString();

        return "TASK";
    }

    private static char? FirstAlnumUpper(string word)
    {
        foreach (var c in word)
        {
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
            if (c is >= 'a' and <= 'z') return char.ToUpperInvariant(c);
        }
        return null;
    }

    private static string StripDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // German umlaut transliteration first so 'ü' -> 'ue', etc. The
        // generic FormD pass would otherwise strip the diacritic and
        // leave 'u', losing information.
        var pre = text
            .Replace("ä", "ae").Replace("Ä", "Ae")
            .Replace("ö", "oe").Replace("Ö", "Oe")
            .Replace("ü", "ue").Replace("Ü", "Ue")
            .Replace("ß", "ss");
        return string.Concat(pre.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark));
    }
}
