namespace AgentStudio.Shared;

/// <summary>
/// Canonical equality for watch-path (project) addressing. Every
/// <c>watchPath</c> the API accepts from a client, and every
/// <see cref="WatchPathEntry.Path"/>/<c>TaskInfo.WatchPath</c> the scanner
/// stamps onto a card, is a <em>filesystem path</em>. Equality therefore has
/// to be path-aware, not a byte-for-byte string compare — the same directory
/// has many equal spellings (separator style, trailing separator, relative vs
/// absolute) and case-sensitivity is an OS property, not a constant.
///
/// <para>Two regressions this closes (AGT-1940, watch-path addressing):</para>
/// <list type="bullet">
/// <item><b>POST /api/tasks → 409 "already exists or invalid input".</b>
/// <c>CreateJob</c> resolved the target project with a raw ordinal,
/// case-sensitive <c>==</c> against the <em>resolved</em> entry path
/// (<see cref="System.IO.Path.GetFullPath"/>, back-slashed on Windows). A
/// client that posted the same directory spelled with forward slashes, a
/// trailing separator, or a different drive-letter case matched no entry and
/// got a spurious conflict.</item>
/// <item><b>PUT …/state &amp; DELETE → 404, GET filter → wrong project on
/// Linux.</b> <c>FindJob</c> and the filters compared with
/// <see cref="StringComparison.OrdinalIgnoreCase"/> everywhere, but Linux
/// filesystems are case-sensitive: two projects whose paths differ only in
/// case collapsed to one, so the request resolved the wrong project (or none).</item>
/// </list>
///
/// <see cref="Normalize"/> collapses separator style and trailing separators
/// via <see cref="System.IO.Path.GetFullPath"/>; <see cref="PathsEqual"/> then
/// compares with the OS-appropriate rule — case-insensitive on Windows,
/// case-sensitive elsewhere — matching real filesystem semantics.
/// </summary>
public static class WatchPathComparison
{
    // Case rule follows the host filesystem: Windows is case-insensitive, so
    // two spellings that differ only in case are the same directory; Linux is
    // case-sensitive, so they are NOT — folding them was the wrong-project bug.
    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// True when both strings address the same watch-path directory. A
    /// null/blank operand is treated as "no path": it matches only another
    /// null/blank operand, never a real path (callers that mean "don't filter
    /// by project" short-circuit on blank before reaching here).
    /// </summary>
    public static bool PathsEqual(string? a, string? b)
    {
        var aBlank = string.IsNullOrWhiteSpace(a);
        var bBlank = string.IsNullOrWhiteSpace(b);
        if (aBlank || bBlank) return aBlank && bBlank;
        return string.Equals(Normalize(a), Normalize(b), Comparison);
    }

    /// <summary>
    /// Full-path + trailing-separator normalization. Falls back to a trimmed
    /// raw value when <see cref="System.IO.Path.GetFullPath"/> throws on a
    /// malformed input, so a bad path still compares equal to an identical bad
    /// path instead of throwing from inside a LINQ predicate.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
