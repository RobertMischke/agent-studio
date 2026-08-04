using System.Text;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Machine-readable facts carried by a review infrastructure failure reason.
/// <para>
/// A <c>ReviewInfra</c> report only transports a classification and a free-text
/// reason. That was enough to say "BaselineUnavailable" four times in a row
/// (AGT-2220, 28.07.) without ever naming the base commit or the command that
/// failed, so no operator could see that the baseline was resolved against a
/// months-old merge-base. The runner appends the facts it alone knows to the
/// human-readable sentence; the monolith parses them back out when it writes
/// the repeat diagnosis onto the card.
/// </para>
/// <para>
/// Wire shape: <c>&lt;sentence&gt; [review-diagnosis base=abc123; ref=refs/heads/develop; command=sh -lc ...]</c>.
/// Values escape <c>\</c>, <c>;</c>, and <c>]</c> so a shell command with
/// separators round-trips unchanged.
/// </para>
/// </summary>
public static class ReviewInfrastructureDiagnosis
{
    /// <summary>Resolved baseline commit, or the marker for an unresolved one.</summary>
    public const string BaseKey = "base";
    /// <summary>Integration ref the baseline was resolved against.</summary>
    public const string RefKey = "ref";
    /// <summary>Review plan step that failed.</summary>
    public const string StepKey = "step";
    /// <summary>Command line of the failing step.</summary>
    public const string CommandKey = "command";
    /// <summary>Value written when the baseline commit could not be resolved at all.</summary>
    public const string UnresolvedBase = "unresolved";

    private const string Prefix = "[review-diagnosis ";
    private const int MaxValueLength = 400;

    /// <summary>
    /// Appends the facts block to <paramref name="message"/>. Blank values are
    /// dropped; an empty fact set returns the message unchanged so callers do
    /// not have to branch.
    /// </summary>
    public static string Append(string message, IEnumerable<KeyValuePair<string, string?>> facts)
    {
        var body = new StringBuilder();
        foreach (var fact in facts)
        {
            if (string.IsNullOrWhiteSpace(fact.Key) || string.IsNullOrWhiteSpace(fact.Value)) continue;
            if (body.Length > 0) body.Append("; ");
            body.Append(fact.Key.Trim()).Append('=').Append(Escape(Truncate(fact.Value!.Trim())));
        }

        return body.Length == 0
            ? message
            : $"{message} {Prefix}{body}]";
    }

    /// <summary>
    /// Reads the facts block back out of a reason. Returns an empty map for a
    /// reason written before this convention existed, so older attempts in a
    /// retry chain stay readable.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string? reason)
    {
        var facts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(reason)) return facts;
        var start = reason.LastIndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0) return facts;
        var body = reason[(start + Prefix.Length)..];
        var end = IndexOfUnescaped(body, ']');
        if (end < 0) return facts;
        body = body[..end];

        foreach (var field in SplitUnescaped(body, ';'))
        {
            var separator = field.IndexOf('=');
            if (separator <= 0) continue;
            var key = field[..separator].Trim();
            if (key.Length == 0) continue;
            facts[key] = Unescape(field[(separator + 1)..].Trim());
        }
        return facts;
    }

    private static string Truncate(string value)
        => value.Length <= MaxValueLength ? value : value[..MaxValueLength] + "...";

    private static string Escape(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\\' or ';' or ']') text.Append('\\');
            text.Append(ch is '\r' or '\n' ? ' ' : ch);
        }
        return text.ToString();
    }

    private static string Unescape(string value)
    {
        var text = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' && index + 1 < value.Length) index++;
            text.Append(value[index]);
        }
        return text.ToString();
    }

    private static int IndexOfUnescaped(string value, char needle)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\') { index++; continue; }
            if (value[index] == needle) return index;
        }
        return -1;
    }

    private static IEnumerable<string> SplitUnescaped(string value, char separator)
    {
        var field = new StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' && index + 1 < value.Length)
            {
                field.Append(value[index]).Append(value[index + 1]);
                index++;
                continue;
            }
            if (value[index] == separator)
            {
                yield return field.ToString();
                field.Clear();
                continue;
            }
            field.Append(value[index]);
        }
        if (field.Length > 0) yield return field.ToString();
    }
}
