using System.Text.RegularExpressions;

namespace OrchestratorApi.Services.Markdown;

/// <summary>
/// Single point of code for "extract the YAML frontmatter block from a
/// Markdown document". Replaces four duplicated regex+parser pairs in
/// <c>AspectVerdict.cs</c>, <c>SecurityReviewParser.cs</c>,
/// <c>DesignEvidenceParser.cs</c>, <c>RoadmapAlignmentReviewService.cs</c>
/// (each had its own copy of the same regex with slightly different
/// edge-case handling).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> We accept the same flat <c>key: value</c> form the
/// existing parsers used. No nesting, no anchors, no multi-line scalars
/// without explicit folded indicators. Producers in this codebase emit
/// frontmatter via templates with one key per line; we never had to
/// parse hand-curated YAML, so the parser stays small and deterministic.
/// </para>
/// <para>
/// <b>What about <c>YamlDotNet</c>?</b> Adding the dep would mean an
/// extra ~300 KB on every CLI ship and a new attack surface for prompt
/// injection. The flat dictionary form is enough for every producer
/// today; we ship a focused helper instead of a full YAML parser.
/// </para>
/// </remarks>
public static class FrontmatterParser
{
    private static readonly Regex FrontmatterRegex = new(
        @"\A---\s*\r?\n(?<body>[\s\S]*?)\r?\n---\s*\r?\n",
        RegexOptions.Compiled);

    /// <summary>
    /// Result of an attempted frontmatter parse. <see cref="Ok"/> is true
    /// when the leading <c>---</c> block was located AND yielded at least
    /// one recognised key. <see cref="Fields"/> is always populated (empty
    /// dictionary on failure) so callers can use the safe accessors.
    /// </summary>
    public sealed record FrontmatterResult(
        bool Ok,
        string? Error,
        IReadOnlyDictionary<string, string> Fields,
        string Body);

    /// <summary>Look for a YAML frontmatter block at the top of
    /// <paramref name="markdown"/> and parse it. Returns the residual
    /// body (the markdown after the closing <c>---</c>) so callers can
    /// continue processing the document below.</summary>
    public static FrontmatterResult Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new FrontmatterResult(
                Ok: false,
                Error: "empty input",
                Fields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Body: string.Empty);
        }

        var match = FrontmatterRegex.Match(markdown!);
        if (!match.Success)
        {
            return new FrontmatterResult(
                Ok: false,
                Error: "no frontmatter block found",
                Fields: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Body: markdown!);
        }

        var fields = ParseFlatYaml(match.Groups["body"].Value);
        var body = markdown!.Substring(match.Index + match.Length);
        if (fields.Count == 0)
        {
            return new FrontmatterResult(
                Ok: false,
                Error: "frontmatter present but yielded no recognised keys",
                Fields: fields,
                Body: body);
        }
        return new FrontmatterResult(Ok: true, Error: null, Fields: fields, Body: body);
    }

    /// <summary>Parse the flat <c>key: value</c> form. Single-line strings
    /// and folded scalars (<c>key: &gt;</c> followed by indented lines)
    /// are supported; anything more complex is rejected.</summary>
    public static IReadOnlyDictionary<string, string> ParseFlatYaml(string body)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = body.Replace("\r\n", "\n").Split('\n');
        string? currentKey = null;
        var folded = new System.Text.StringBuilder();
        var inFolded = false;

        void FlushFolded()
        {
            if (currentKey != null && inFolded)
            {
                dict[currentKey] = folded.ToString().Trim();
            }
            folded.Clear();
            inFolded = false;
            currentKey = null;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { FlushFolded(); continue; }
            if (line.TrimStart().StartsWith('#')) continue;

            if (inFolded && line.Length > 0 && char.IsWhiteSpace(line[0]))
            {
                if (folded.Length > 0) folded.Append(' ');
                folded.Append(line.TrimStart());
                continue;
            }

            FlushFolded();

            var colonIdx = line.IndexOf(':');
            if (colonIdx <= 0) continue;

            var key = line[..colonIdx].Trim();
            var value = line[(colonIdx + 1)..].Trim();

            if (value == ">" || value == "|")
            {
                currentKey = key;
                inFolded = true;
                continue;
            }
            dict[key] = StripQuotes(value);
        }
        FlushFolded();
        return dict;
    }

    private static string StripQuotes(string s)
    {
        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0])
            return s[1..^1];
        return s;
    }
}
