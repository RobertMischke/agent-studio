using System.Text.RegularExpressions;

namespace AgentStudio.Prompts;

/// <summary>One inline-prompt finding: a source location where a multi-line
/// instruction-shaped string literal sits in code instead of the runtime prompt
/// template registry.</summary>
/// <param name="File">Repo-relative path with forward slashes.</param>
/// <param name="Line">1-based line of the offending literal's opening quote.</param>
/// <param name="Signal">The instruction phrase that matched (e.g. "You are").</param>
/// <param name="Snippet">First ~120 chars of the literal, newlines flattened.</param>
public sealed record InlinePromptFinding(string File, int Line, string Signal, string Snippet);

/// <summary>
/// Deterministic detector behind the prompt-coverage guard (T3b). The hard rule
/// from the 2026-06-10 prompt-management review: no instruction text is composed
/// inline in code - every prompt lives in the runtime template registry
/// (<see cref="RuntimePromptService"/>) as a template with named <c>{{slots}}</c>.
/// This scanner is the standing detector for the crudest violation of that rule:
/// a whole multi-line block of agent-instruction prose pasted into a product
/// <c>.cs</c> file instead of being moved to a template.
/// </summary>
/// <remarks>
/// <para>
/// <b>Heuristic - deliberately narrow.</b> A finding is exactly ONE string literal
/// that is BOTH
/// <list type="bullet">
///   <item>multi-line: a verbatim (<c>@"..."</c>) or raw (<c>"""..."""</c>) literal
///   whose body spans at least three source lines, and</item>
///   <item>instruction-shaped: its text contains a second-person agent-instruction
///   signal ("You are", "Your task", ...).</item>
/// </list>
/// Short single-line instruction fragments, and prompts assembled from many
/// concatenated fragments, are out of scope on purpose - the rule targets the
/// "pasted block" shape, not every string that addresses the agent. That keeps
/// the build-breaking guard free of false positives on the post-T3a tree while
/// still catching a regression where someone inlines a fresh prompt block.
/// </para>
/// <para>
/// <b>Escape hatch.</b> A literal carrying a <c>prompt-coverage:allow</c> marker on
/// its opening line or the line above is skipped (mirrors the SilentCatch marker),
/// for the rare genuinely-non-prompt multi-line block.
/// </para>
/// </remarks>
public static class PromptCoverageScanner
{
    /// <summary>Inline marker that suppresses a single literal from the guard.</summary>
    public const string AllowMarker = "prompt-coverage:allow";

    /// <summary>Minimum number of body lines for a literal to count as multi-line.</summary>
    private const int MinBodyLines = 3;

    /// <summary>
    /// Second-person agent-instruction openers. These are the signal the review
    /// doc called the "'You are ...' hits" - phrases that mark a string as prompt
    /// prose rather than data assembly, a report template, a log message, or a
    /// rule description.
    /// </summary>
    private static readonly string[] InstructionSignals =
    {
        "You are", "You will", "You must", "You should", "You may not",
        "Your task", "Your job", "Your role", "Your goal",
        "Act as", "Respond with", "Respond only", "Return only", "Reply with",
    };

    private static readonly Regex SignalRegex = new(
        @"\b(?:" + string.Join("|", InstructionSignals.Select(Regex.Escape)) + @")\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Verbatim string literal: @"...", $@"...", @$"...". Inside, a quote is
    // escaped by doubling (""), so the body is (?:[^"]|"")*.
    private static readonly Regex VerbatimLiteral = new(
        @"(?:\$@|@\$|@)""(?:[^""]|"""")*""",
        RegexOptions.Compiled);

    // Raw string literal: the common three-quote form """ ... """ (incl. $"""...").
    private static readonly Regex RawLiteral = new(
        "\\$?\"\"\"[\\s\\S]*?\"\"\"",
        RegexOptions.Compiled);

    /// <summary>
    /// Scans one file's text. <paramref name="relativePath"/> is only used to
    /// label findings; the scan is purely over <paramref name="content"/>.
    /// </summary>
    public static IReadOnlyList<InlinePromptFinding> ScanText(string relativePath, string content)
    {
        if (string.IsNullOrEmpty(content)) return Array.Empty<InlinePromptFinding>();

        var findings = new List<InlinePromptFinding>();
        var seenLines = new HashSet<int>();

        foreach (var literal in EnumerateLiterals(content))
        {
            if (CountLines(literal.Body) < MinBodyLines) continue;

            var sig = SignalRegex.Match(literal.Body);
            if (!sig.Success) continue;

            if (HasAllowMarker(content, literal.Start)) continue;

            var line = LineAt(content, literal.Start);
            if (!seenLines.Add(line)) continue;

            findings.Add(new InlinePromptFinding(relativePath, line, sig.Value, Snippet(literal.Body)));
        }

        return findings;
    }

    /// <summary>
    /// Scans the product source tree (<c>{repoRoot}/backend</c>, excluding the
    /// test project and build output). Findings are labelled with paths rooted at
    /// <c>backend/</c>. Returns empty when the source tree is not on disk (e.g. a
    /// bin-only deployment) so a runtime caller degrades gracefully.
    /// </summary>
    public static IReadOnlyList<InlinePromptFinding> ScanProductSource(string repoRoot)
    {
        var backend = Path.Combine(repoRoot, "backend");
        if (!Directory.Exists(backend)) return Array.Empty<InlinePromptFinding>();

        var findings = new List<InlinePromptFinding>();
        foreach (var file in EnumerateCsFiles(backend))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; } // locked / unreadable - skip silently

            var rel = "backend/" + Path.GetRelativePath(backend, file).Replace('\\', '/');
            findings.AddRange(ScanText(rel, text));
        }

        return findings
            .OrderBy(f => f.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Line)
            .ToList();
    }

    private readonly record struct Literal(int Start, string Body);

    private static IEnumerable<Literal> EnumerateLiterals(string content)
    {
        foreach (Match m in VerbatimLiteral.Matches(content))
        {
            var openQuote = m.Value.IndexOf('"');
            var body = m.Value.Substring(openQuote + 1, m.Value.Length - openQuote - 2)
                .Replace("\"\"", "\"");
            yield return new Literal(m.Index, body);
        }

        foreach (Match m in RawLiteral.Matches(content))
        {
            var lead = m.Value.StartsWith('$') ? 4 : 3;
            var body = m.Value.Substring(lead, m.Value.Length - lead - 3);
            yield return new Literal(m.Index, body);
        }
    }

    private static int CountLines(string body)
    {
        if (body.Length == 0) return 0;
        var newlines = 0;
        foreach (var c in body) if (c == '\n') newlines++;
        return newlines + 1;
    }

    private static bool HasAllowMarker(string content, int start)
    {
        var lineStart = content.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        var lineEnd = content.IndexOf('\n', start);
        if (lineEnd < 0) lineEnd = content.Length;
        var currentLine = content.Substring(lineStart, lineEnd - lineStart);
        if (currentLine.Contains(AllowMarker, StringComparison.Ordinal)) return true;

        if (lineStart <= 1) return false;
        var prevLineStart = content.LastIndexOf('\n', lineStart - 2) + 1;
        var prevLine = content.Substring(prevLineStart, lineStart - prevLineStart);
        return prevLine.Contains(AllowMarker, StringComparison.Ordinal);
    }

    private static int LineAt(string content, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < content.Length; i++)
            if (content[i] == '\n') line++;
        return line;
    }

    private static string Snippet(string body)
    {
        var flat = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 120 ? flat : flat.Substring(0, 120) + " ...";
    }

    private static IEnumerable<string> EnumerateCsFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var name = Path.GetFileName(dir);
            if (name is "bin" or "obj" or ".git" or "node_modules" or "dist") continue;

            IEnumerable<string> subDirs;
            IEnumerable<string> files;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir, "*.cs");
            }
            catch { continue; }

            foreach (var f in files) yield return f;
            foreach (var d in subDirs) stack.Push(d);
        }
    }
}
