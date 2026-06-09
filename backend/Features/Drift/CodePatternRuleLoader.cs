using System.Text.RegularExpressions;

namespace OrchestratorApi.Services.Drift;

/// <summary>
/// Parses additional <see cref="CodePatternRule"/>s out of
/// <c>docs/code-patterns.md</c> so reviewers can extend the watchlist
/// without a backend rebuild.
/// </summary>
/// <remarks>
/// <para>
/// File grammar: zero or more fenced <c>```yaml</c> blocks containing one
/// rule each. The YAML is intentionally a minimal flat key:value form,
/// not a full YAML 1.2 parser — strings only, no nesting, no anchors.
/// That keeps the dependency footprint to <c>System.Text.RegularExpressions</c>
/// and avoids dragging in YamlDotNet for a five-field record.
/// </para>
/// <para>
/// Malformed blocks are skipped with a warning; one bad rule never breaks
/// the watchlist. The loader is also tolerant of missing optional keys
/// (badVariant, goodVariant, excludeFilePattern, severityIfBad).
/// </para>
/// </remarks>
public static class CodePatternRuleLoader
{
    private static readonly Regex YamlBlockRegex = new(
        @"```\s*yaml\s*\r?\n(?<body>[\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Load rules from the given Markdown file. Returns an empty list when
    /// the file does not exist (the catalog defaults to the hardcoded rule
    /// set in that case).
    /// </summary>
    public static IReadOnlyList<CodePatternRule> LoadFromFile(string path, ILogger? logger = null)
    {
        if (!File.Exists(path)) return Array.Empty<CodePatternRule>();
        try
        {
            var text = File.ReadAllText(path);
            return ParseRules(text, logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to load code-pattern rules from {Path}", path);
            return Array.Empty<CodePatternRule>();
        }
    }

    /// <summary>Parse rules from raw Markdown text. Public for tests.</summary>
    public static IReadOnlyList<CodePatternRule> ParseRules(string markdown, ILogger? logger = null)
    {
        var rules = new List<CodePatternRule>();
        foreach (Match block in YamlBlockRegex.Matches(markdown))
        {
            var body = block.Groups["body"].Value;
            try
            {
                var fields = ParseFlatYaml(body);
                if (!fields.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
                {
                    logger?.LogWarning("Skipping rule block without 'id'");
                    continue;
                }
                var rule = BuildRule(fields, logger);
                if (rule != null) rules.Add(rule);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Skipping malformed rule block: {Snippet}",
                    body.Length > 80 ? body[..80] + "…" : body);
            }
        }
        return rules;
    }

    private static CodePatternRule? BuildRule(Dictionary<string, string> f, ILogger? logger)
    {
        var id = f.GetValueOrDefault("id") ?? string.Empty;
        var title = f.GetValueOrDefault("title") ?? id;
        var description = f.GetValueOrDefault("description") ?? string.Empty;
        var filePattern = f.GetValueOrDefault("filePattern");
        var candidate = f.GetValueOrDefault("candidateMarker");

        if (string.IsNullOrWhiteSpace(filePattern) || string.IsNullOrWhiteSpace(candidate))
        {
            logger?.LogWarning("Rule '{Id}' missing filePattern or candidateMarker; skipping", id);
            return null;
        }

        var bad = f.GetValueOrDefault("badVariant");
        var good = f.GetValueOrDefault("goodVariant");
        var exclude = f.GetValueOrDefault("excludeFilePattern");
        var severityRaw = f.GetValueOrDefault("severityIfBad") ?? "Warn";

        if (string.IsNullOrWhiteSpace(bad) && string.IsNullOrWhiteSpace(good))
        {
            logger?.LogWarning("Rule '{Id}' must define at least one of badVariant or goodVariant; skipping", id);
            return null;
        }

        if (!Enum.TryParse<DriftSeverity>(severityRaw, ignoreCase: true, out var severity))
            severity = DriftSeverity.Warn;

        try
        {
            return new CodePatternRule(
                Id: id,
                Title: title,
                CanonicalDescription: description,
                FilePattern: filePattern!,
                ExcludeFilePattern: exclude,
                CandidateMarker: new Regex(candidate!, RegexOptions.Compiled),
                BadVariant: string.IsNullOrWhiteSpace(bad) ? null : new Regex(bad!, RegexOptions.Compiled),
                GoodVariant: string.IsNullOrWhiteSpace(good) ? null : new Regex(good!, RegexOptions.Compiled),
                SeverityIfBad: severity);
        }
        catch (ArgumentException ex)
        {
            logger?.LogWarning(ex, "Rule '{Id}' contains invalid regex; skipping", id);
            return null;
        }
    }

    /// <summary>
    /// Parse the flat <c>key: value</c> form we accept inside the
    /// fenced YAML blocks. Supports single-line strings and folded scalars
    /// (<c>key: &gt;</c> followed by indented continuation lines).
    /// </summary>
    private static Dictionary<string, string> ParseFlatYaml(string body)
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
            if (line.TrimStart().StartsWith("#")) continue;

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
