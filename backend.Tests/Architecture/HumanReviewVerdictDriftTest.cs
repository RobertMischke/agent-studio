using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace OrchestratorApi.Tests.Architecture;

/// <summary>
/// Mechanically enforces the rule behind the bug
/// <c>karten-landen-in-5-human-review-ohne-verdict-und-ohne-statusmarkdown</c>:
/// a card may only land in <c>5-human-review</c> through a code path that also
/// records an orchestrator verdict. A move into the human-review lane with no
/// accompanying <see cref="OrchestratorApi.Services.Runner.ReviewDecisionRecord"/>
/// produces a card the board cannot explain - <c>orchestratorVerdict == null</c>
/// and an empty <c>status.md</c>.
///
/// <para>
/// The guarantee is structural, not textual-co-location: the ONLY files allowed
/// to move a job into <c>TaskStates.HumanReview</c> are the two that pair every
/// such move with a verdict write -
/// <list type="number">
///   <item><c>ReviewDecisionOrchestrator.cs</c> - the agent-driven review path;
///   each escalate move sits next to a <c>ReviewDecisionLog.Append(Escalate)</c>.</item>
///   <item><c>HumanReviewEscalation.cs</c> - the deterministic system-escalation
///   funnel (watchdog kill, permission/environment block, auto-failure park,
///   over-budget pickup zombie); it moves, then writes the Escalate verdict and a
///   minimal <c>status.md</c> stub.</item>
/// </list>
/// Any new move into the lane from anywhere else (the historical
/// <c>ProjectRunner</c> escalations that caused this bug) trips this test, which
/// forces the author through the funnel.
/// </para>
///
/// <para>The funnel also covers the runner's stray human-decision-needed
/// relocation, which used to move cards into the now-retired
/// <c>1b-needs-human-review</c> lane and now routes them to <c>5-human-review</c>
/// through <c>HumanReviewEscalation</c> like every other system escalation.</para>
/// </summary>
public class HumanReviewVerdictDriftTest
{
    /// <summary>
    /// Files allowed to move a job into <c>5-human-review</c>, each because it
    /// writes the orchestrator verdict alongside the move.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Whitelist =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["backend/Services/Runner/ReviewDecisionOrchestrator.cs"] =
                "Agent-driven review path: every escalate move writes a ReviewDecisionLog Escalate record.",
            ["backend/Services/Runner/HumanReviewEscalation.cs"] =
                "Deterministic system-escalation funnel: move + Escalate verdict + status.md stub.",
        };

    /// <summary>
    /// Detects a move call (<c>MoveAsync</c> / <c>MoveJob</c>) whose arguments
    /// target the human-review lane, by either the typed constant
    /// <c>TaskStates.HumanReview</c> or the raw lane literal
    /// <c>"5-human-review"</c>. The <c>TaskStates\.HumanReview</c> branch is
    /// dot- and word-boundary-anchored so it only matches the exact constant,
    /// and the <c>MoveAsync|MoveJob</c> prefix keeps plain state comparisons /
    /// downstream-lane arrays from matching.
    /// </summary>
    internal static readonly Regex HumanReviewMove = new(
        @"\b(?:MoveAsync|MoveJob)\s*\([^;]*?(?:TaskStates\.HumanReview\b|""5-human-review"")",
        RegexOptions.Compiled);

    [Fact]
    public void NoMoveToHumanReview_OutsideWhitelist()
    {
        var repoRoot = ResolveRepoRoot();
        var violations = ScanForViolations(repoRoot, HumanReviewMove);

        Assert.True(
            violations.Count == 0,
            BuildFailureMessage(
                "move-to-5-human-review",
                "Route the escalation through HumanReviewEscalation (Escalate/EscalateAsync) so the move always records an orchestrator verdict and a status.md stub; otherwise the card lands in 5-human-review with orchestratorVerdict==null and empty StatusMarkdown.",
                violations));
    }

    [Fact]
    public void WhitelistEntries_StillExist()
    {
        var repoRoot = ResolveRepoRoot();
        var missing = new List<string>();
        foreach (var entry in Whitelist.Keys)
        {
            var absolute = Path.Combine(repoRoot, entry.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute)) missing.Add(entry);
        }

        Assert.True(
            missing.Count == 0,
            "Whitelist references files that no longer exist - prune the list:\n  " +
            string.Join("\n  ", missing));
    }

    [Fact]
    public void HumanReviewMoveRegex_MatchesLaneMoves_AndIgnoresComparisons()
    {
        // Moves into 5-human-review - must match.
        Assert.Matches(HumanReviewMove,
            "var move = await _transitions.MoveAsync(jobId, TaskStates.HumanReview, activeInfo.WatchPath, CancellationToken.None);");
        Assert.Matches(HumanReviewMove,
            "var move = _stateMachine.MoveJob(current.Id, TaskStates.HumanReview, entry.Path);");
        Assert.Matches(HumanReviewMove,
            "_states.MoveJob(jobId, \"5-human-review\", watchPath);");

        // Not a move - plain state comparison and downstream-lane arrays.
        Assert.DoesNotMatch(HumanReviewMove, "if (job.State == TaskStates.HumanReview) {}");
        Assert.DoesNotMatch(HumanReviewMove, "var lanes = new[] { \"4-auto-review\", \"5-human-review\" };");
    }

    private static List<Violation> ScanForViolations(string repoRoot, Regex pattern)
    {
        var backendDir = Path.Combine(repoRoot, "backend");
        var violations = new List<Violation>();

        foreach (var path in Directory.EnumerateFiles(backendDir, "*.cs", SearchOption.AllDirectories))
        {
            if (IsExcludedPath(path)) continue;

            var relative = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
            if (Whitelist.ContainsKey(relative)) continue;

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (IsLineCommentedOut(line)) continue;
                if (!pattern.IsMatch(line)) continue;

                violations.Add(new Violation(relative, i + 1, line.Trim()));
            }
        }

        return violations;
    }

    private static bool IsExcludedPath(string path)
    {
        var normalised = path.Replace('\\', '/');
        return normalised.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalised.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || normalised.Contains("/Properties/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLineCommentedOut(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("///", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal);
    }

    private static string BuildFailureMessage(string ruleName, string remediation, List<Violation> violations)
    {
        var rendered = string.Join("\n  ", violations.Select(v => $"{v.RelativePath}:{v.Line}: {v.Source}"));
        return
            $"Found {violations.Count} forbidden {ruleName} call(s) outside the HumanReviewVerdictDrift whitelist.\n" +
            $"Remediation: {remediation}\n" +
            $"Offending lines:\n  {rendered}";
    }

    // Anchored on the compiled-in source path first so the scan still resolves
    // the repo root when the test binary is redirected out of the tree (e.g.
    // `dotnet test --artifacts-path` while the dev backend locks backend/bin).
    private static string ResolveRepoRoot([CallerFilePath] string thisFile = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(thisFile), AppContext.BaseDirectory })
        {
            var current = start;
            for (var i = 0; i < 10 && !string.IsNullOrEmpty(current); i++)
            {
                var marker = Path.Combine(current, "backend", "OrchestratorApi.csproj");
                if (File.Exists(marker)) return current;
                current = Directory.GetParent(current)?.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not locate repo root from source path '{thisFile}' or base dir '{AppContext.BaseDirectory}'.");
    }

    private readonly record struct Violation(string RelativePath, int Line, string Source);
}
