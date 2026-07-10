using System.Text.RegularExpressions;

namespace AgentStudio.Review;

/// <summary>
/// Validates that every inline image reference in a generated <c>status.md</c>
/// points at a file that actually exists under the job folder. A reference
/// that resolves to a missing file would render as a silently empty
/// <c>&lt;img&gt;</c> in the protocol pane; surfacing it as a review-evidence
/// finding instead keeps the broken link visible to the reviewer.
///
/// Only job-local relative references are checked:
/// <list type="bullet">
///   <item><c>results/&lt;...&gt;</c> and <c>attachments/&lt;...&gt;</c> (including nested paths);</item>
///   <item>a bare filename (<c>foo.png</c>), which the local reader resolves under
///         <c>results/</c> as a legacy fallback.</item>
/// </list>
/// Absolute URLs, <c>data:</c> URIs, rooted paths, and anything that escapes
/// the job folder are left alone - they are not the job's to validate.
/// </summary>
public static class ProtocolImageReferenceValidator
{
    // Markdown inline image: ![alt](target "optional title"). The target is
    // everything up to the first whitespace or the closing paren; protocol
    // image paths never contain spaces, so a title clause is naturally excluded.
    private static readonly Regex MarkdownImageRegex = new(
        @"!\[[^\]]*\]\(\s*(?<path>[^)\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg"];

    // Glob / wildcard metacharacters. A reference carrying any of these is a
    // pattern (e.g. a doc example like `results/*.png`), never a concrete file,
    // so it is always rejected before an existence check.
    private static readonly char[] GlobChars = ['*', '?', '['];

    /// <summary>
    /// True when <paramref name="reference"/> is a job-local image reference that
    /// resolves to a file which actually exists under <paramref name="jobFolder"/>.
    /// Glob/wildcard patterns, external URLs, <c>data:</c> URIs, rooted/absolute
    /// paths, traversals that escape the job folder, and non-image extensions all
    /// return <c>false</c>. Used by the summary generator so only real, on-disk
    /// screenshots are injected into the protocol's Images section.
    /// </summary>
    public static bool ResolvesToExistingFile(string? reference, string jobFolder)
    {
        if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(jobFolder)) return false;

        var raw = reference.Trim();
        if (ContainsGlobChars(raw)) return false;

        var relative = NormalizeJobLocalReference(raw);
        if (relative == null) return false;

        string jobRoot;
        try { jobRoot = Path.GetFullPath(jobFolder); }
        catch { return false; }

        var combined = ResolveWithinJob(jobRoot, relative);
        return combined != null && File.Exists(combined);
    }

    /// <summary>True when the reference carries a glob/wildcard metacharacter (<c>* ? [</c>).</summary>
    public static bool ContainsGlobChars(string reference)
        => !string.IsNullOrEmpty(reference) && reference.IndexOfAny(GlobChars) >= 0;

    /// <summary>
    /// Returns the distinct job-local image references in <paramref name="markdown"/>
    /// whose target file does not exist under <paramref name="jobFolder"/>, in
    /// first-seen order. References are reported normalised to forward slashes.
    /// </summary>
    public static IReadOnlyList<string> FindBrokenReferences(string? markdown, string jobFolder)
    {
        if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(jobFolder)) return [];

        string jobRoot;
        try { jobRoot = Path.GetFullPath(jobFolder); }
        catch { return []; }

        var broken = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in MarkdownImageRegex.Matches(markdown))
        {
            var raw = match.Groups["path"].Value.Trim();
            var relative = NormalizeJobLocalReference(raw);
            if (relative == null) continue;
            if (!seen.Add(relative)) continue;

            var combined = ResolveWithinJob(jobRoot, relative);
            if (combined == null) continue; // escapes the job folder; not ours to flag
            if (!File.Exists(combined)) broken.Add(relative);
        }

        return broken;
    }

    /// <summary>
    /// Maps a markdown image target to a job-folder-relative path when it is a
    /// job-local image reference, or null when the reference should be left
    /// alone (external URL, data URI, rooted path, non-image extension).
    /// </summary>
    private static string? NormalizeJobLocalReference(string raw)
    {
        if (raw.Length == 0) return null;
        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        if (Regex.IsMatch(raw, @"^(?:[a-z]+:)?//", RegexOptions.IgnoreCase)) return null;

        var path = raw.Replace('\\', '/');
        if (path.StartsWith('/')) return null;             // rooted - not job-local
        if (Regex.IsMatch(path, @"^[a-zA-Z]:")) return null; // Windows drive - absolute

        if (!HasImageExtension(path)) return null;

        var isResults = path.StartsWith("results/", StringComparison.OrdinalIgnoreCase);
        var isAttachments = path.StartsWith("attachments/", StringComparison.OrdinalIgnoreCase);
        var isBareName = !path.Contains('/');

        if (isResults || isAttachments) return path;
        // Legacy bare-filename fallback: the reader resolves it under results/.
        if (isBareName) return $"results/{path}";
        return null;
    }

    private static bool HasImageExtension(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return Array.IndexOf(ImageExtensions, ext) >= 0;
    }

    private static string? ResolveWithinJob(string jobRoot, string relative)
    {
        if (relative.Contains("..")) return null;
        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(jobRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch
        {
            return null;
        }

        var rootWithSep = jobRoot.EndsWith(Path.DirectorySeparatorChar)
            ? jobRoot
            : jobRoot + Path.DirectorySeparatorChar;
        return combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ? combined : null;
    }
}
