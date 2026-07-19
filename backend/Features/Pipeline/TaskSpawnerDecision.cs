using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>
/// The parsed outcome of the task-spawner evaluation reply (AGT-2028): whether
/// the change is relevant to the target project and, when it is, the generated
/// follow-up card's title and prompt. Pure data - <see cref="TaskSpawnerDecisionParser"/>
/// produces it and the runner acts on it.
/// </summary>
public sealed record TaskSpawnerDecision(
    bool Relevant,
    string? Reason,
    string? Title,
    string? Prompt)
{
    /// <summary>A relevant decision that actually carries a non-empty generated prompt.</summary>
    public bool CanSpawn => Relevant && !string.IsNullOrWhiteSpace(Prompt);
}

/// <summary>
/// Tolerant parser for the task-spawner model reply. Mirrors the
/// <c>[[CODE_REVIEW_GRADE: ...]]</c> / <c>[[ASPECT_VERDICT: ...]]</c> sentinel
/// grammar: a <c>[[TASK_SPAWN: relevant=&lt;yes|no&gt;; reason=&lt;...&gt;]]</c>
/// sentinel drives the decision, and - when relevant - two fenced sections
/// (<c>### SPAWN_TITLE</c> and <c>### SPAWN_PROMPT</c>) carry the generated card.
///
/// <para>
/// Conservative by construction (the whole point of the step is "no spam"): a
/// reply with no parseable sentinel, or a relevant verdict that omits the
/// generated prompt, resolves to "not relevant / cannot spawn" rather than
/// inventing a card. Pure + deterministic so it is unit-testable without a CLI.
/// </para>
/// </summary>
public static class TaskSpawnerDecisionParser
{
    private static readonly Regex Sentinel = new(
        @"\[\[TASK_SPAWN:\s*(?<body>.+?)\s*\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Section headers are matched at line start so a header mentioned inside the
    // prose does not split the body. Title runs to the next header; prompt runs
    // to the next header or the sentinel or end-of-reply.
    private static readonly Regex TitleSection = new(
        @"^[ \t]*#{2,3}[ \t]*SPAWN_TITLE[ \t]*$(?<body>.*?)(?=^[ \t]*#{2,3}[ \t]*SPAWN_PROMPT[ \t]*$|\z)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Multiline);

    private static readonly Regex PromptSection = new(
        @"^[ \t]*#{2,3}[ \t]*SPAWN_PROMPT[ \t]*$(?<body>.*?)(?=\[\[TASK_SPAWN:|\z)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Multiline);

    /// <summary>
    /// Parse a model reply into a <see cref="TaskSpawnerDecision"/>. Never throws;
    /// a null / blank / unparseable reply yields a not-relevant decision so the
    /// caller skips rather than spawns.
    /// </summary>
    public static TaskSpawnerDecision Parse(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return new TaskSpawnerDecision(false, "empty reply", null, null);

        var text = StripCodeFences(reply);

        // Last sentinel wins (the model may restate it in a summary).
        Match? last = null;
        foreach (Match m in Sentinel.Matches(text))
            last = m;

        if (last is null)
            return new TaskSpawnerDecision(false, "no [[TASK_SPAWN]] sentinel parsed", null, null);

        var fields = ParseFields(last.Groups["body"].Value);
        var relevant = fields.TryGetValue("relevant", out var rel) && IsAffirmative(rel);
        var reason = fields.TryGetValue("reason", out var r) && !string.IsNullOrWhiteSpace(r)
            ? r.Trim()
            : null;

        if (!relevant)
            return new TaskSpawnerDecision(false, reason ?? "model judged the change not relevant", null, null);

        var title = ExtractSection(TitleSection, text);
        var prompt = ExtractSection(PromptSection, text);
        return new TaskSpawnerDecision(true, reason, title, prompt);
    }

    private static string? ExtractSection(Regex section, string text)
    {
        var m = section.Match(text);
        if (!m.Success) return null;
        var body = m.Groups["body"].Value.Trim();
        return body.Length == 0 ? null : body;
    }

    // Split "key=value; key=value" into a case-insensitive dictionary. Values may
    // themselves contain '=' (kept intact by splitting on the first one only).
    private static Dictionary<string, string> ParseFields(string body)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (key.Length > 0) dict[key] = value;
        }
        return dict;
    }

    private static bool IsAffirmative(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Trim().ToLowerInvariant() switch
        {
            "yes" or "true" or "y" or "1" or "relevant" => true,
            _ => false,
        };
    }

    // Drop a single leading/trailing ``` fence pair the model may wrap the whole
    // reply in, so the sentinel/section regexes see the raw body.
    private static string StripCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return text;
        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return text;
        var withoutOpen = trimmed[(firstNewline + 1)..];
        var lastFence = withoutOpen.LastIndexOf("```", StringComparison.Ordinal);
        return lastFence >= 0 ? withoutOpen[..lastFence] : withoutOpen;
    }
}
