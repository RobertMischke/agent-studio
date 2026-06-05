using System.Text;
using System.Text.RegularExpressions;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Deterministic post-processing gate for a run that emitted
/// <c>[[TASK_DONE]]</c>. It reads the generated result/status surface and the
/// final log tail for explicit "not finished" evidence before auto-review may
/// accept the task. This closes the silent-completion gap where an agent says
/// done while its own close-out still lists open items.
/// </summary>
public static class CompletionGate
{
    public const int MaxFindings = 8;

    private const int MaxFindingLength = 220;

    private static readonly Regex ResultLineRegex = new(
        @"^\s*[-*]?\s*Result:\s*(?<result>[A-Za-z]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex UncheckedItemRegex = new(
        @"^\s*[-*+]\s*\[\s*\]\s*(?<text>\S.*)$",
        RegexOptions.Compiled);

    private static readonly Regex BulletRegex = new(
        @"^\s*[-*+]\s+(?<text>\S.*)$",
        RegexOptions.Compiled);

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    // The build / compile / test-failure vocabulary. Kept as one fragment so the
    // generic incomplete-work scan and the "claims success but build failed"
    // contradiction check (see BuildErrorEvidenceRegex) share exactly one
    // definition and cannot drift. Extended mode (?x): literal spaces are
    // ignored, so spacing is written with \s.
    private const string BuildErrorEvidence = @"
        build\s+fail(?:s|ed|ing)? |
        build\s+(?:is\s+)?broken |
        failed\s+to\s+build |
        compilation\s+failed |
        compile\s+errors? |
        does(?:n'?t|\s+not)\s+compile |
        wo(?:n'?t|uld\s+not)\s+compile |
        error\s+TS\d+ |
        error\s+CS\d+ |
        error\s+NG\d+ |
        typescript\s+errors? |
        npm\s+ERR |
        application\s+bundle\s+generation\s+failed |
        tests?\s+fail(?:s|ed|ing)?";

    /// <summary>
    /// Build / compile / test-failure evidence on its own. Used by the
    /// contradiction rule: a run that claims success while one of these signals
    /// appears in its own close-out is reported as a finding.
    /// </summary>
    private static readonly Regex BuildErrorEvidenceRegex = new(
        @"(?ix)\b(?:" + BuildErrorEvidence + @")\b",
        RegexOptions.Compiled);

    private static readonly Regex IncompleteEvidenceRegex = new(
        @"(?ix)
        \b(?:
            incomplete |
            unfinished |
            not\s+finished |
            not\s+complete |
            pending |
            file-state\s+mismatch |
            route[-\s]?wiring\s+pending |
            apply_patch\b.{0,80}\b(?:failed|mismatch|reject(?:ed)?) |
            patch\b.{0,80}\b(?:failed|did\s+not\s+apply|reject(?:ed)?) |"
        + BuildErrorEvidence + @"
        )\b",
        RegexOptions.Compiled);

    private static readonly Regex SuccessResultRegex = new(
        @"^(?:success|succeeded|done|complete|completed|pass|passed)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public enum CompletionGateAction
    {
        Pass,
        Reissue,
        Escalate,
    }

    public sealed record Decision
    {
        public CompletionGateAction Action { get; init; } = CompletionGateAction.Pass;
        public IReadOnlyList<string> Findings { get; init; } = [];
        public string Reason { get; init; } = "No unfinished-work evidence found.";

        public bool IsIncomplete => Action != CompletionGateAction.Pass;
    }

    /// <summary>
    /// Evaluate the close-out evidence and apply the bounded retry policy. The
    /// caller supplies the shared auto-review reissue count so this gate cannot
    /// spin independently of NEEDS_INPUT / NOOP / aspect-block recovery.
    /// </summary>
    public static Decision Evaluate(string? statusMarkdown, string? recentLog, int priorReissues, int maxReissues)
    {
        var findings = ExtractFindings(statusMarkdown, recentLog);
        if (findings.Count == 0)
        {
            return new Decision();
        }

        if (priorReissues >= maxReissues)
        {
            return new Decision
            {
                Action = CompletionGateAction.Escalate,
                Findings = findings,
                Reason = $"Completion gate found unfinished-work evidence after {priorReissues} prior orchestrator reissue(s); user attention required.",
            };
        }

        return new Decision
        {
            Action = CompletionGateAction.Reissue,
            Findings = findings,
            Reason = $"Completion gate found {findings.Count} unfinished-work item(s); reissuing with the items foregrounded.",
        };
    }

    public static string BuildFollowUp(IReadOnlyList<string> findings, IReadOnlyList<string>? priorCommits = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(RunOutcomePolicy.DiffOnlySteeringRule);
        sb.AppendLine();
        sb.AppendLine("The Orchestrator Completion-Gate found unfinished work in the previous run's own result/status evidence.");
        sb.AppendLine("Resolve these items before doing anything else, then end with [[TASK_DONE]] only when the task is actually complete.");
        sb.AppendLine();
        foreach (var finding in findings.Take(MaxFindings))
        {
            sb.AppendLine($"- [ ] {finding}");
        }
        var commitsBlock = RunOutcomePolicy.RenderPriorCommitsBlock(priorCommits);
        if (commitsBlock.Length > 0)
        {
            sb.Append(commitsBlock);
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("If any item cannot be completed, stop and end with [[TASK_BLOCKED:<short reason>]] instead of claiming done.");
        return sb.ToString();
    }

    /// <summary>
    /// Pull the agent's self-reported <c>Result:</c> token (e.g. "Success",
    /// "Partial", "Failed") out of a status / close-out surface. Returns null
    /// when no <c>Result:</c> line is present. Shares the exact regex the
    /// finding scan uses so the two cannot drift; exposed so the Codex
    /// evidence-based-completion evaluator can read the same token the gate
    /// reads.
    /// </summary>
    public static string? ExtractResultToken(string? statusMarkdown)
    {
        if (string.IsNullOrEmpty(statusMarkdown)) return null;
        var match = ResultLineRegex.Match(statusMarkdown);
        if (!match.Success) return null;
        var token = match.Groups["result"].Value.Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>
    /// True when a <c>Result:</c> token denotes a successful close-out. Same
    /// vocabulary as the contradiction rule's <see cref="SuccessResultRegex"/>.
    /// </summary>
    public static bool IsSuccessResultToken(string? resultToken)
        => resultToken is not null && SuccessResultRegex.IsMatch(resultToken.Trim());

    public static IReadOnlyList<string> ExtractFindings(string? statusMarkdown, string? recentLog)
    {
        var findings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? text)
        {
            var normalized = Normalize(text);
            if (normalized.Length == 0) return;
            if (seen.Add(normalized)) findings.Add(normalized);
        }

        var status = statusMarkdown ?? string.Empty;
        var logTail = TailLines(recentLog ?? string.Empty, 80);
        var result = ResultLineRegex.Match(status);
        string? resultToken = result.Success ? result.Groups["result"].Value.Trim() : null;
        if (resultToken is not null &&
            (resultToken.Equals("Partial", StringComparison.OrdinalIgnoreCase) ||
             resultToken.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
             resultToken.Equals("Blocked", StringComparison.OrdinalIgnoreCase)))
        {
            Add($"Status result is {resultToken}.");
        }

        foreach (var item in ExtractOpenItemsSection(status))
        {
            Add(item);
        }

        foreach (var line in EvidenceLines(status))
        {
            if (IncompleteEvidenceRegex.IsMatch(line))
                Add(line);
        }

        // The status summary is the preferred result surface. The log tail is a
        // fallback for failures the summarizer omitted, especially CLI/tool
        // errors near the final DONE marker.
        foreach (var line in EvidenceLines(logTail))
        {
            if (IncompleteEvidenceRegex.IsMatch(line))
                Add(line);
        }

        // Contradiction rule: the run claims success yet its own close-out
        // reports a build / compile / test failure. The generic scan above
        // already surfaces the raw failing line, but this makes the conflict
        // explicit so the orchestrator (and the operator) sees that the success
        // claim is contradicted by the evidence rather than just "a build line".
        if (resultToken is not null && SuccessResultRegex.IsMatch(resultToken))
        {
            var contradiction = FirstBuildErrorLine(status) ?? FirstBuildErrorLine(logTail);
            if (contradiction is not null)
                Add($"Status result is {resultToken} but build/test failure evidence was found: {contradiction}");
        }

        return findings.Count > MaxFindings ? findings.Take(MaxFindings).ToList() : findings;
    }

    private static IEnumerable<string> ExtractOpenItemsSection(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) yield break;

        var inSection = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inSection = line.Trim().Equals("## Open Items", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection) continue;
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (IsNoneLine(trimmed)) continue;

            var uncheckedMatch = UncheckedItemRegex.Match(trimmed);
            if (uncheckedMatch.Success)
            {
                yield return uncheckedMatch.Groups["text"].Value;
                continue;
            }

            var bullet = BulletRegex.Match(trimmed);
            yield return bullet.Success ? bullet.Groups["text"].Value : trimmed;
        }
    }

    private static string? FirstBuildErrorLine(string text)
    {
        foreach (var line in EvidenceLines(text))
        {
            if (BuildErrorEvidenceRegex.IsMatch(line))
                return line;
        }
        return null;
    }

    private static IEnumerable<string> EvidenceLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (IsNoneLine(line)) continue;
            yield return line;
        }
    }

    private static bool IsNoneLine(string line)
    {
        var normalized = line.Trim().Trim('-', '*', '+', '.', ':').Trim();
        return normalized.Equals("none", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("n/a", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("no open items", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return string.Empty;
        var text = candidate.Replace("\r", " ").Replace("\n", " ").Trim();
        text = Regex.Replace(text, @"^[-*+]\s*(\[\s*[xX ]?\s*\]\s*)?", "");
        text = WhitespaceRun.Replace(text, " ").Trim();
        if (text.Length > MaxFindingLength)
            text = text[..(MaxFindingLength - 3)].TrimEnd() + "...";
        return text;
    }

    private static string TailLines(string text, int n)
    {
        if (string.IsNullOrEmpty(text) || n <= 0) return string.Empty;
        var lines = text.Split('\n');
        if (lines.Length <= n) return text;
        return string.Join('\n', lines[^n..]);
    }
}
