using System.Text;
using System.Text.RegularExpressions;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Deterministic post-aspect gate that refuses to accept a run on a bare
/// success claim. It closes the ASS-764 gap where a Markdown-rendering bug was
/// waved through as "accept-with-concerns" despite zero visual evidence, a red
/// build, red unit tests, and a +0/-0 "test" commit.
///
/// <para>
/// Two blocking categories, both turned into a verification-demanding reissue
/// (or an escalation when the shared reissue budget is spent) instead of an
/// accept-with-concerns:
/// </para>
/// <list type="number">
/// <item>
///   <b>Missing visual evidence (requirement 1):</b> a task that is a
///   <see cref="TaskTypes.Bug"/> or recognisably UI/frontend work that ended
///   with no screenshot / e2e capture under <c>results/</c> and no
///   review-evidence entry. A claimed fix must be proven, not asserted.
/// </item>
/// <item>
///   <b>Unclean tests-and-evidence aspect (requirement 2):</b> the
///   <c>tests-and-evidence</c> aspect raised a concern (failing build / failing
///   tests / missing evidence / +0/-0 "test" commit). That category is blocking,
///   not a soft concern the orchestrator advances past.
/// </item>
/// </list>
///
/// <para>
/// The static <see cref="CompletionGate"/> already runs BEFORE the aspects and
/// catches self-reported build/test failures in the run's own close-out. This
/// gate runs AFTER the aspects and upgrades the residual accept/accept-with-
/// concerns decision.
/// </para>
/// </summary>
public static class EvidenceGate
{
    /// <summary>
    /// Catalogue id of the aspect whose concern this gate treats as blocking.
    /// Must match <c>AspectRunnerService</c>'s <c>tests-and-evidence</c> entry.
    /// </summary>
    public const string TestsAndEvidenceAspectId = "tests-and-evidence";

    public const int MaxFindings = 8;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];

    // Strong frontend/UI signal words: a task whose title carries one of these
    // is treated as visual work that should ship a screenshot or e2e capture,
    // even when its taskType is not "bug". Kept deliberately narrow so a
    // backend task that merely mentions "render a template" is not swept in.
    private static readonly Regex UiSignalRegex = new(
        @"(?ix)\b(?:
            ui | ux | frontend | front-end |
            component | css | scss | stylesheet | styling |
            button | modal | dialog | tooltip | dropdown |
            layout | screenshot | e2e | playwright |
            panel | banner | badge |
            angular
        )\b",
        RegexOptions.Compiled);

    // Tags that mark a task as visual/frontend work regardless of taskType.
    private static readonly HashSet<string> FrontendTagHints = new(StringComparer.OrdinalIgnoreCase)
    {
        "ui", "ux", "frontend", "front-end", "css", "scss", "visual",
        "e2e", "playwright", "area-frontend",
    };

    public enum EvidenceGateAction
    {
        Pass,
        Reissue,
        Escalate,
    }

    public sealed record Decision
    {
        public EvidenceGateAction Action { get; init; } = EvidenceGateAction.Pass;
        public IReadOnlyList<string> Findings { get; init; } = [];
        public string Reason { get; init; } = "No evidence concerns found.";

        /// <summary>True when the block was (also) driven by absent visual proof.</summary>
        public bool MissingVisualEvidence { get; init; }

        public bool IsBlocking => Action != EvidenceGateAction.Pass;
    }

    /// <summary>
    /// True when the task must ship visual proof of its result: a
    /// <see cref="TaskTypes.Bug"/>, a task tagged as frontend/UI work, or a task
    /// whose title carries a strong UI signal word.
    /// </summary>
    public static bool RequiresVisualEvidence(
        string? taskType, IEnumerable<string>? tags, string? title)
    {
        if (TaskTypes.Normalize(taskType) == TaskTypes.Bug) return true;

        if (tags is not null)
        {
            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag)) continue;
                if (FrontendTagHints.Contains(tag.Trim())) return true;
            }
        }

        return !string.IsNullOrWhiteSpace(title) && UiSignalRegex.IsMatch(title);
    }

    /// <summary>
    /// True when the run left visual proof on disk: any image file under the
    /// task's <c>results/</c> folder (screenshots, Playwright captures), or a
    /// non-empty <c>results/review-evidence.jsonl</c>. Best-effort: any IO
    /// failure is treated as "no evidence" so the gate fails closed.
    /// </summary>
    public static bool HasVisualEvidence(string? jobFolderPath)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath)) return false;

        var resultsDir = TaskPaths.ResultsDir(jobFolderPath);
        if (Directory.Exists(resultsDir))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(resultsDir, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    if (Array.IndexOf(ImageExtensions, ext) >= 0 && IsNonEmptyFile(path)) return true;
                }
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "EvidenceGate: best-effort; fall through to the evidence-log check");
                // best-effort; fall through to the evidence-log check
            }
        }

        var evidenceLog = TaskPaths.ReviewEvidenceLog(jobFolderPath);
        try
        {
            if (File.Exists(evidenceLog) &&
                File.ReadLines(evidenceLog).Any(line => !string.IsNullOrWhiteSpace(line)))
                return true;
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "EvidenceGate: best-effort");
            // best-effort
        }

        return false;
    }

    private static bool IsNonEmptyFile(string path)
    {
        try
        {
            return new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Apply the gate. <paramref name="requiresVisualEvidence"/> and
    /// <paramref name="hasVisualEvidence"/> are passed in (computed via
    /// <see cref="RequiresVisualEvidence"/> / <see cref="HasVisualEvidence"/>) so
    /// the policy core stays pure and unit-testable without touching disk. The
    /// shared reissue count is supplied so the gate escalates to a human once the
    /// budget is spent rather than spinning the task back to ready forever.
    /// </summary>
    public static Decision Evaluate(
        bool requiresVisualEvidence,
        bool hasVisualEvidence,
        AspectRunReport report,
        int priorReissues,
        int maxReissues)
    {
        var findings = new List<string>();
        var missingVisual = requiresVisualEvidence && !hasVisualEvidence;

        if (missingVisual)
        {
            findings.Add(
                "This is a UI/bug task but the run produced no visual evidence: no screenshot or e2e " +
                "capture under results/, and no review-evidence entry. A claimed fix must be proven, " +
                "not asserted.");
        }

        var testsAndEvidence = report.Verdicts.FirstOrDefault(v =>
            string.Equals(v.Aspect, TestsAndEvidenceAspectId, StringComparison.OrdinalIgnoreCase));
        if (testsAndEvidence is not null && testsAndEvidence.Status == AspectStatus.Concerns)
        {
            var summary = string.IsNullOrWhiteSpace(testsAndEvidence.Summary)
                ? "build / tests / evidence are not clean"
                : testsAndEvidence.Summary.Trim();
            findings.Add(
                "Tests-and-evidence review is not clean and must be resolved before acceptance " +
                $"(no accept-with-concerns for failing build/tests or missing evidence): {summary}");
        }

        if (findings.Count == 0)
        {
            return new Decision();
        }

        if (findings.Count > MaxFindings)
        {
            findings = findings.Take(MaxFindings).ToList();
        }

        if (priorReissues >= maxReissues)
        {
            return new Decision
            {
                Action = EvidenceGateAction.Escalate,
                Findings = findings,
                MissingVisualEvidence = missingVisual,
                Reason = $"Evidence gate could not clear {findings.Count} unverified item(s) after {priorReissues} prior reissue(s); user attention required.",
            };
        }

        return new Decision
        {
            Action = EvidenceGateAction.Reissue,
            Findings = findings,
            MissingVisualEvidence = missingVisual,
            Reason = $"Evidence gate found {findings.Count} unverified item(s); reissuing with a verification demand instead of accepting with concerns.",
        };
    }

    /// <summary>
    /// Compose the follow-up handed to the reissued run: list the unverified
    /// items, demand a screenshot / e2e artifact when visual proof is the gap,
    /// and require a green build + tests before the next DONE.
    /// </summary>
    public static string BuildFollowUp(Decision decision)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Auto-review will not accept this task on a bare success claim: the evidence gate found unverified or failing work.");
        sb.AppendLine("Resolve every item below, then prove the result before ending with [[TASK_DONE]]:");
        sb.AppendLine();
        foreach (var finding in decision.Findings.Take(MaxFindings))
        {
            sb.AppendLine($"- [ ] {finding}");
        }
        sb.AppendLine();
        if (decision.MissingVisualEvidence)
        {
            sb.AppendLine("Prove the fix with visual evidence: capture a screenshot or run the relevant Playwright e2e and save the artifact under this task's results/ folder (a PNG/JPG image, or results/playwright/<spec>/...). Reference it in your status before claiming done.");
        }
        sb.AppendLine("Re-run the build and the tests and confirm both are green. If any item cannot be completed or verified, stop and end with [[TASK_BLOCKED:<short reason>]] instead of claiming done.");
        return sb.ToString();
    }
}
