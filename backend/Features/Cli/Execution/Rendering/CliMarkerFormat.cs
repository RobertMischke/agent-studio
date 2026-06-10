namespace AgentStudio.Cli;

/// <summary>
/// Low-level string primitives shared by every <see cref="ICliOutputRenderer"/>.
/// These exist so the marker-line vocabulary (the <c>● &lt;verb&gt; &lt;target&gt;</c>
/// convention the frontend activity-log parser classifies) stays byte-identical
/// across CLIs instead of each driver re-deriving "split a model message into
/// lines" or "cap a command at 200 chars" slightly differently. A new CLI
/// adapter reuses these rather than copy-pasting helpers.
/// </summary>
public static class CliMarkerFormat
{
    /// <summary>
    /// The leading glyph every marker line starts with. The frontend's
    /// <c>activity-log.parser</c> keys its action classifier on a leading
    /// non-word marker; this is the one we emit.
    /// </summary>
    public const string Bullet = "●";

    /// <summary>
    /// Normalise CRLF to LF and split a (possibly multi-line) model message
    /// into individual lines, each emitted as its own <c>CliOutputLine</c> so
    /// the parser groups them as continuation lines under one turn. Empty input
    /// yields nothing.
    /// </summary>
    public static IEnumerable<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            yield return line;
    }

    /// <summary>
    /// Collapse newlines to spaces and cap at <paramref name="max"/> chars with
    /// an ellipsis. Used for command markers so a multi-line shell script stays
    /// one Activity-Log line; the full text is still in the persisted JSONL via
    /// the raw frame.
    /// </summary>
    public static string TrimSingleLine(string s, int max = 200)
    {
        var t = (s ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        return t.Length > max ? t[..max] + "…" : t;
    }

    /// <summary>
    /// Cap an already-single-line string at <paramref name="max"/> chars with an
    /// ellipsis (no whitespace collapsing). Used for tool-result first-lines.
    /// </summary>
    public static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
        return s.Length > max ? s[..max] + "…" : s;
    }

    /// <summary>
    /// Human-friendly relative duration: <c>now</c>, <c>12s</c>, <c>9 min</c>,
    /// <c>1.5 h</c>, <c>2 d</c>.
    /// </summary>
    public static string FormatRelative(TimeSpan ts)
    {
        if (ts.TotalSeconds <= 0) return "now";
        if (ts.TotalMinutes < 2)  return $"{(int)ts.TotalSeconds}s";
        if (ts.TotalHours < 2)    return $"{(int)ts.TotalMinutes} min";
        if (ts.TotalDays < 2)     return $"{ts.TotalHours:0.#} h";
        return $"{ts.TotalDays:0.#} d";
    }
}
