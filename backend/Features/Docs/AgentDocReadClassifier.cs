namespace AgentStudio.Docs;

/// <summary>
/// Pure classification of a <c>logs/tool-calls.jsonl</c> <c>started</c> row
/// into an agent-doc read: which CLI family issued the read and which candidate
/// file path(s) it targeted. Keeping this a pure static function makes the
/// heuristic unit-testable without touching disk, and keeps
/// <see cref="AgentDocsReadAnalyticsService"/> a thin fold over the result.
///
/// <para>
/// CLI attribution is derived from the tool name, not the task's configured
/// CLI, because a single job's tool-calls log can span runs of different CLIs
/// and the read tool each CLI emits is distinct:
/// </para>
/// <list type="bullet">
///   <item>Claude Code emits a dedicated <c>Read</c> tool whose argument is the
///   file path.</item>
///   <item>Gemini CLI emits <c>ReadFile</c> whose argument is the file
///   path.</item>
///   <item>Codex has no dedicated read tool; it reads through the shell, so a
///   <c>command_call</c> whose command is a read-only file reader
///   (<c>cat AGENTS.md</c>, <c>sed -n '1,40p' CLAUDE.md</c>) is a Codex
///   read.</item>
/// </list>
/// Copilot has no structured tool-read frames in the current adapter set, so it
/// is intentionally not attributed here; when a Copilot adapter starts emitting
/// read frames it can be added as one more branch.
/// </summary>
public static class AgentDocReadClassifier
{
    public const string CliClaude = "claude";
    public const string CliCodex = "codex";
    public const string CliGemini = "gemini";

    /// <summary>Shell commands that read a file given as a positional argument.</summary>
    private static readonly HashSet<string> HeadPositionalReaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "cat", "bat", "head", "tail", "nl", "less", "more", "type",
    };

    /// <summary>
    /// Shell commands where the file to read is the last positional token
    /// (<c>sed -n '1,40p' FILE</c>, <c>rg pattern FILE</c>).
    /// </summary>
    private static readonly HashSet<string> TailPositionalReaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "sed", "awk", "grep", "rg",
    };

    /// <summary>
    /// Classify a started tool row. Returns <c>null</c> when the row is not a
    /// file read this analytic can attribute (writes, edits, plan updates,
    /// non-read shell commands, unknown tools).
    /// </summary>
    public static AgentDocReadCandidate? Classify(string? tool, string? argument)
    {
        var t = (tool ?? string.Empty).Trim();
        if (t.Length == 0) return null;

        if (Eq(t, "Read"))
            return SinglePath(CliClaude, argument);

        if (Eq(t, "ReadFile") || Eq(t, "read_file") || Eq(t, "read-file"))
            return SinglePath(CliGemini, argument);

        if (Eq(t, "command_call") || Eq(t, "shell") || Eq(t, "local_shell"))
            return ClassifyShellRead(argument);

        return null;
    }

    private static AgentDocReadCandidate? SinglePath(string cli, string? argument)
    {
        var path = (argument ?? string.Empty).Trim();
        return path.Length == 0 ? null : new AgentDocReadCandidate(cli, new[] { path });
    }

    /// <summary>
    /// Extract the file path(s) a Codex shell command reads. Only recognizes
    /// read-only file readers so a build or test command is never counted as a
    /// doc read. A pipeline or redirection collapses to the segment before the
    /// first shell operator so <c>cat AGENTS.md | head</c> still resolves the
    /// file.
    /// </summary>
    private static AgentDocReadCandidate? ClassifyShellRead(string? command)
    {
        var cmd = (command ?? string.Empty).Trim();
        if (cmd.Length == 0) return null;

        // Keep only the first shell segment (up to a pipe / redirect / chain
        // operator); the reader and its file live there.
        cmd = FirstSegment(cmd);

        var tokens = Tokenize(cmd);
        if (tokens.Count == 0) return null;

        var verb = BaseVerb(tokens[0]);
        var args = tokens.Skip(1).Where(a => !IsFlag(a)).ToList();
        if (args.Count == 0) return null;

        if (HeadPositionalReaders.Contains(verb))
            return new AgentDocReadCandidate(CliCodex, args);

        if (TailPositionalReaders.Contains(verb))
            return new AgentDocReadCandidate(CliCodex, new[] { args[^1] });

        return null;
    }

    /// <summary>
    /// Find the most specific inventory relPath a candidate path refers to, or
    /// <c>null</c> when it points outside the agent-doc inventory. Absolute or
    /// nested paths match by suffix (<c>C:/repo/frontend/AGENTS.md</c> matches
    /// <c>frontend/AGENTS.md</c>); the longest matching relPath wins so a root
    /// <c>AGENTS.md</c> never shadows a scoped <c>frontend/AGENTS.md</c>.
    /// </summary>
    public static string? MatchInventory(string candidate, IReadOnlyCollection<string> inventoryRelPaths)
    {
        var norm = NormalizeCandidate(candidate);
        if (norm.Length == 0) return null;

        string? best = null;
        var bestLen = -1;
        foreach (var rel in inventoryRelPaths)
        {
            var r = (rel ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
            if (r.Length == 0) continue;
            var isMatch = norm.Equals(r, StringComparison.OrdinalIgnoreCase)
                || norm.EndsWith("/" + r, StringComparison.OrdinalIgnoreCase);
            if (isMatch && r.Length > bestLen)
            {
                best = rel;
                bestLen = r.Length;
            }
        }
        return best;
    }

    private static string NormalizeCandidate(string candidate)
    {
        var s = (candidate ?? string.Empty).Trim().Trim('"', '\'');
        s = s.Replace('\\', '/');
        while (s.StartsWith("./", StringComparison.Ordinal)) s = s[2..];
        return s.TrimStart('/');
    }

    private static string FirstSegment(string cmd)
    {
        var cut = cmd.Length;
        foreach (var op in new[] { "|", "&&", "||", ">>", ">", ";" })
        {
            var idx = cmd.IndexOf(op, StringComparison.Ordinal);
            if (idx >= 0 && idx < cut) cut = idx;
        }
        return cmd[..cut].Trim();
    }

    /// <summary>Split on whitespace, honoring single and double quotes as one token.</summary>
    private static List<string> Tokenize(string cmd)
    {
        var tokens = new List<string>();
        var cur = new System.Text.StringBuilder();
        char quote = '\0';
        foreach (var ch in cmd)
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else cur.Append(ch);
            }
            else if (ch == '"' || ch == '\'')
            {
                quote = ch;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); }
            }
            else
            {
                cur.Append(ch);
            }
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        return tokens;
    }

    private static string BaseVerb(string token)
    {
        var t = token.Replace('\\', '/');
        var slash = t.LastIndexOf('/');
        if (slash >= 0) t = t[(slash + 1)..];
        if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) t = t[..^4];
        return t;
    }

    private static bool IsFlag(string token) => token.StartsWith("-", StringComparison.Ordinal);

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Result of classifying one tool row: the CLI family and the candidate file
/// path(s) it read. A shell reader with several files (<c>cat A B</c>) yields
/// several candidates; the service matches each against the inventory.
/// </summary>
public sealed record AgentDocReadCandidate(string Cli, IReadOnlyList<string> Paths);
