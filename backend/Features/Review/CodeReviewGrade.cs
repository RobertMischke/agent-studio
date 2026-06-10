using System.Linq;
using System.Text.RegularExpressions;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Services.Review;

/// <summary>
/// Quality grade assigned by the automatic code-review pipeline step
/// (post-CORE, on the task's change set). The grade is the first-class,
/// every-task signal the operator reads on the card; it sits alongside
/// the older <see cref="AspectStatus"/> pass/concerns/block verdict the
/// narrow aspect runners emit.
///
/// <para>Rubric (ASS-1657):</para>
/// <list type="bullet">
///   <item><c>A</c> — solves the goal clearly, complete, with tests / evidence.</item>
///   <item><c>B</c> — solid, small gaps.</item>
///   <item><c>C</c> — concerns: half-done or unclear.</item>
///   <item><c>D</c> — misses the goal, or redundantly redoes already-present code.</item>
/// </list>
/// </summary>
public enum CodeReviewGrade
{
    A,
    B,
    C,
    D,
}

/// <summary>
/// Pure helpers for the quality-grade review: parse the model reply for a
/// <c>[[CODE_REVIEW_GRADE: grade=&lt;A|B|C|D&gt;; summary=&lt;short&gt;]]</c>
/// sentinel, map a grade to its card tag, and derive an
/// <see cref="AspectStatus"/> so the grade can drive the existing
/// pass/concerns/block rendering paths without a parallel concept. Mirrors
/// <see cref="AspectVerdictParsing"/> so the two reviewers parse with the
/// same tolerant grammar.
/// </summary>
public static class CodeReviewGradeParsing
{
    // Canonical sentinel. Lazy + Singleline so a summary may wrap across
    // lines or contain a single ']'; the doubled-bracket terminator anchors.
    private static readonly Regex GradeSentinel = new(
        @"\[\[CODE_REVIEW_GRADE:\s*(?<body>.+?)\s*\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // Tolerant fallback: a model that drops the sentinel often still says
    // "Grade: B" on its own line. Accept optional **bold**, quoting, or a
    // trailing punctuation mark around the single A-D letter.
    private static readonly Regex LineGradeRegex = new(
        @"^\s*\**\s*grade\s*\**\s*[:=]\s*\**\s*[`'""]?(?<grade>[ABCD])\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Parse a model reply for the last <c>[[CODE_REVIEW_GRADE]]</c>
    /// sentinel (falling back to a tolerant "Grade: X" line). Returns null
    /// when no grade can be recovered so the caller can apply a
    /// deterministic fallback instead of silently waving the work through.
    /// </summary>
    public static (CodeReviewGrade Grade, string Summary)? ParseGrade(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var cleaned = StripWrappers(output);

        var matches = GradeSentinel.Matches(cleaned);
        if (matches.Count > 0)
        {
            var fields = ParseFields(matches[^1].Groups["body"].Value);
            if (fields != null)
            {
                var grade = TokenToGrade(fields.GetValueOrDefault("grade"));
                var summary = fields.GetValueOrDefault("summary")?.Trim() ?? string.Empty;
                if (grade != null) return (grade.Value, summary);
            }
        }

        var fallback = LineGradeRegex.Matches(cleaned);
        if (fallback.Count > 0)
        {
            var grade = TokenToGrade(fallback[^1].Groups["grade"].Value);
            if (grade != null) return (grade.Value, ExtractFallbackSummary(cleaned));
        }

        return null;
    }

    public static CodeReviewGrade? TokenToGrade(string? token) =>
        token?.Trim().Trim('"', '\'', '`').ToUpperInvariant() switch
        {
            "A" => CodeReviewGrade.A,
            "B" => CodeReviewGrade.B,
            "C" => CodeReviewGrade.C,
            "D" => CodeReviewGrade.D,
            _ => null,
        };

    public static string GradeToken(CodeReviewGrade grade) => grade switch
    {
        CodeReviewGrade.A => "A",
        CodeReviewGrade.B => "B",
        CodeReviewGrade.C => "C",
        CodeReviewGrade.D => "D",
        _ => "C",
    };

    /// <summary>
    /// Card tag for a grade, namespaced under <c>code-review:</c> so it sits
    /// next to the existing <c>code-review:&lt;verdict&gt;</c> tags and the
    /// frontend can detect it by prefix. A grade is always carried (every
    /// pipelined task gets one), so unlike the verdict tag this never returns
    /// null.
    /// </summary>
    public static string TagFor(CodeReviewGrade grade) => grade switch
    {
        CodeReviewGrade.A => "code-review:grade-a",
        CodeReviewGrade.B => "code-review:grade-b",
        CodeReviewGrade.C => "code-review:grade-c",
        CodeReviewGrade.D => "code-review:grade-d",
        _ => "code-review:grade-c",
    };

    /// <summary>All four grade tags, so callers can reconcile (drop a stale
    /// grade tag before merging the fresh one) without re-deriving the set.</summary>
    public static readonly IReadOnlyList<string> AllTags = new[]
    {
        "code-review:grade-a",
        "code-review:grade-b",
        "code-review:grade-c",
        "code-review:grade-d",
    };

    /// <summary>
    /// Map a grade onto the existing pass/concerns/block verdict so the grade
    /// can drive the same pill/severity rendering and pipeline-status colour
    /// without a parallel concept: A/B read as a pass, C as concerns, D as a
    /// block.
    /// </summary>
    public static AspectStatus ToAspectStatus(CodeReviewGrade grade) => grade switch
    {
        CodeReviewGrade.A or CodeReviewGrade.B => AspectStatus.Pass,
        CodeReviewGrade.C => AspectStatus.Concerns,
        CodeReviewGrade.D => AspectStatus.Block,
        _ => AspectStatus.Concerns,
    };

    private static string StripWrappers(string raw)
    {
        var withoutFences = Regex.Replace(raw, "```[a-zA-Z0-9_-]*\\r?\\n?", string.Empty);
        return Regex.Replace(withoutFences, "```", string.Empty);
    }

    private static string ExtractFallbackSummary(string cleaned)
    {
        var explicitSummary = Regex.Match(
            cleaned,
            @"^\s*(?:\*\*)?summary(?:\*\*)?\s*[:=]\s*(?<text>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (explicitSummary.Success)
            return Cap(explicitSummary.Groups["text"].Value.Trim().Trim('"', '\''), 200);

        var paragraphs = cleaned.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#") && !l.StartsWith("---"))
            .ToList();
        return paragraphs.Count == 0 ? string.Empty : Cap(paragraphs[^1], 200);
    }

    private static string Cap(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max - 1).TrimEnd() + "…";

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
}
