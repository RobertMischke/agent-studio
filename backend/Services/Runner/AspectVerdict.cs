using System.Linq;
using System.Text.RegularExpressions;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Outcome of a single aspect-runner pass against a 4-auto-review job.
/// The orchestrator aggregates these across all configured aspects to
/// decide reissue / accept-as-done / accept-with-concerns.
/// </summary>
public enum AspectStatus
{
    /// <summary>The aspect found no issues. No tag is hung.</summary>
    Pass,

    /// <summary>
    /// The aspect found something worth flagging but not severe enough
    /// to send the task back to 3-progress. The tag <c>{namespace}:concerns</c>
    /// is added to the job; the user sees a small ⚠ chip on the card.
    /// </summary>
    Concerns,

    /// <summary>
    /// The aspect found a regression or missing piece that must be
    /// addressed before the task can be considered complete. Any single
    /// block triggers a reissue back to 3-progress with a follow-up
    /// summarising the per-aspect findings.
    /// </summary>
    Block
}

/// <summary>
/// One aspect runner's verdict for one job, as parsed from the fast-model
/// response and written to <c>aspect-{name}.md</c> in the job folder.
/// </summary>
/// <param name="Aspect">Aspect identifier, e.g. <c>code-quality</c>.</param>
/// <param name="Status">Pass / Concerns / Block.</param>
/// <param name="Summary">One-line summary used as the body's first line.</param>
/// <param name="Body">Full body markdown (without frontmatter).</param>
/// <param name="ConcernTagId">Tag id to hang on the job for Concerns / Block,
/// e.g. <c>quality:concerns</c>. Null on Pass.</param>
public sealed record AspectVerdict(
    string Aspect,
    AspectStatus Status,
    string Summary,
    string Body,
    string? ConcernTagId);

/// <summary>
/// Pure helpers for the aspect-runner pipeline: parsing the fast-model
/// reply for an <c>[[ASPECT_VERDICT]]</c> sentinel, rendering the
/// per-aspect markdown report (frontmatter + body), and parsing it back
/// when tests / future reviewers want to read the same files.
/// </summary>
public static class AspectVerdictParsing
{
    // Sentinel regex. `.+?` is lazy + Singleline so the match accepts a
    // summary that wraps across lines or contains a single `]` (e.g.
    // `summary=Found [[issue]] in fooBar`); the `\]\]` anchor still
    // requires the doubled-bracket terminator.
    private static readonly Regex VerdictRegex = new(
        @"\[\[ASPECT_VERDICT:\s*(?<body>.+?)\s*\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Tolerant fallback: a model that drops the sentinel sometimes still
    // says "Status: concerns" on its own line. We accept that as a
    // legitimate verdict (no summary captured — caller fills the gap)
    // so the operator gets a real chip instead of "no parseable verdict".
    // Tolerant: accept optional **bold** around either "Status", the
    // separator, or the whole "Status:" prefix (the markdown a model
    // often emits in narrative replies). The status token itself may
    // be quoted, backtick'd, or followed by a punctuation mark.
    private static readonly Regex LineStatusRegex = new(
        @"^\s*\**\s*status\s*\**\s*[:=]\s*\**\s*[`'""]?(?<status>pass|concerns?|block(?:ed)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Parse a fast-model reply for the last <c>[[ASPECT_VERDICT]]</c>
    /// sentinel. The grammar is
    /// <c>[[ASPECT_VERDICT: status=&lt;pass|concerns|block&gt;; summary=&lt;short&gt;]]</c>.
    /// Returns null when no parseable verdict can be recovered (not even
    /// via the tolerant fallback) so the caller can fall back to a
    /// deterministic Concerns verdict — the agent must not be silently
    /// waved through.
    /// </summary>
    public static (AspectStatus Status, string Summary)? ParseVerdict(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var cleaned = StripWrappers(output);

        // Preferred path: the canonical [[ASPECT_VERDICT: ...]] sentinel.
        var matches = VerdictRegex.Matches(cleaned);
        if (matches.Count > 0)
        {
            var last = matches[^1];
            var fields = ParseFields(last.Groups["body"].Value);
            if (fields != null)
            {
                var statusRaw = fields.GetValueOrDefault("status")?.Trim().ToLowerInvariant();
                var summary = fields.GetValueOrDefault("summary")?.Trim() ?? string.Empty;
                var status = TokenToStatus(statusRaw);
                if (status != null) return (status.Value, summary);
            }
        }

        // Tolerant fallback: scan for a "Status: <pass|concerns|block>"
        // line. Use the LAST one in the reply so trailing summary
        // statements win over earlier mentions. Build a summary from the
        // last non-empty trailing paragraph (capped at 200 chars).
        var fallback = LineStatusRegex.Matches(cleaned);
        if (fallback.Count > 0)
        {
            var token = fallback[^1].Groups["status"].Value;
            var status = TokenToStatus(token.ToLowerInvariant());
            if (status != null)
            {
                var summary = ExtractFallbackSummary(cleaned);
                return (status.Value, summary);
            }
        }

        return null;
    }

    private static AspectStatus? TokenToStatus(string? token) => token switch
    {
        "pass" => AspectStatus.Pass,
        "concerns" or "concern" => AspectStatus.Concerns,
        "block" or "blocked" => AspectStatus.Block,
        _ => null,
    };

    /// <summary>
    /// Strip common wrapper shapes the model adds around the sentinel:
    /// outer triple-backtick fences, leading "&lt;" XML-comment-style
    /// quotation, and Markdown blockquotes. Cheap pre-pass so the regex
    /// finds the sentinel even when the model wraps its reply.
    /// </summary>
    private static string StripWrappers(string raw)
    {
        // Drop fenced code blocks but keep their bodies — sentinels often
        // land inside an inline ```...``` the model added for emphasis.
        var withoutFences = Regex.Replace(raw, "```[a-zA-Z0-9_-]*\\r?\\n?", string.Empty);
        withoutFences = Regex.Replace(withoutFences, "```", string.Empty);
        return withoutFences;
    }

    private static string ExtractFallbackSummary(string cleaned)
    {
        // Look for a "Summary: ..." or "summary=..." line; otherwise grab
        // the last non-trivial paragraph and cap at 200 chars.
        var explicitSummary = Regex.Match(
            cleaned,
            @"^\s*(?:\*\*)?summary(?:\*\*)?\s*[:=]\s*(?<text>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (explicitSummary.Success)
        {
            var text = explicitSummary.Groups["text"].Value.Trim().Trim('"', '\'');
            return Cap(text, 200);
        }
        var paragraphs = cleaned.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#") && !l.StartsWith("---"))
            .ToList();
        if (paragraphs.Count == 0) return string.Empty;
        return Cap(paragraphs[^1], 200);
    }

    private static string Cap(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max - 1).TrimEnd() + "…";

    /// <summary>
    /// Render the per-aspect report markdown with a small structured
    /// frontmatter so tests, future readers, and a possible "review the
    /// review" pass can parse it without re-running the model. The body
    /// is the model's narrative reply with the verdict sentinel left
    /// intact so the chain of evidence is auditable.
    /// </summary>
    public static string RenderReport(AspectVerdict verdict, DateTime now)
    {
        return
            "---\n" +
            $"aspect: {verdict.Aspect}\n" +
            $"status: {StatusToken(verdict.Status)}\n" +
            $"summary: {EscapeYaml(verdict.Summary)}\n" +
            $"created_at: {now:O}\n" +
            (verdict.ConcernTagId is null ? string.Empty : $"tag: {verdict.ConcernTagId}\n") +
            "---\n\n" +
            $"# Aspect: {verdict.Aspect}\n\n" +
            $"**Status:** {StatusToken(verdict.Status)}\n\n" +
            (string.IsNullOrWhiteSpace(verdict.Summary) ? string.Empty : $"**Summary:** {verdict.Summary}\n\n") +
            verdict.Body;
    }

    public static string StatusToken(AspectStatus status) => status switch
    {
        AspectStatus.Pass => "pass",
        AspectStatus.Concerns => "concerns",
        AspectStatus.Block => "block",
        _ => "pass"
    };

    /// <summary>
    /// Read back the frontmatter status token from a previously written
    /// aspect report. Tolerant of missing frontmatter (returns null).
    /// Uses the canonical
    /// <see cref="OrchestratorApi.Services.Markdown.FrontmatterParser"/>
    /// so the regex+block-detection lives in exactly one place.
    /// </summary>
    public static AspectStatus? ReadStatusFromReport(string content)
    {
        var result = OrchestratorApi.Services.Markdown.FrontmatterParser.Parse(content);
        if (!result.Ok) return null;
        if (!result.Fields.TryGetValue("status", out var value)) return null;
        return value.ToLowerInvariant() switch
        {
            "pass" => AspectStatus.Pass,
            "concerns" => AspectStatus.Concerns,
            "block" => AspectStatus.Block,
            _ => null,
        };
    }

    private static Dictionary<string, string>? ParseFields(string body)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in body.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var key = trimmed[..eq].Trim();
            var value = trimmed[(eq + 1)..].Trim();
            if (key.Length == 0) continue;
            dict[key] = value;
        }
        return dict.Count == 0 ? null : dict;
    }

    private static string EscapeYaml(string s)
    {
        if (string.IsNullOrEmpty(s)) return "''";
        var oneLine = s.Replace("\r", " ").Replace("\n", " ").Trim();
        if (oneLine.Contains(':') || oneLine.Contains('#') || oneLine.StartsWith('-'))
        {
            return "\"" + oneLine.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
        return oneLine;
    }
}
