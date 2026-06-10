
namespace AgentStudio.Tasks;

/// <summary>
/// Cheap heuristic detector for the "agent claimed done but isn't"
/// quality loop. Reads the prompt + status + commit chain for one card
/// and returns a verdict + per-claim diagnostics. No LLM call - this
/// runs over hundreds of cards in the completed-lane audit pass.
///
/// <para>The detector is intentionally conservative: ambiguity returns
/// <see cref="AuditVerdicts.Inconclusive"/>, never <see cref="AuditVerdicts.NotReallyDone"/>.
/// Only a hard signal (no commits when prompt asks for code change,
/// claimed file missing, status says "blocked" / "failed") forces a reopen.</para>
/// </summary>
public sealed class AcceptanceEvidenceDetector
{
    private static readonly string[] CodeChangeKeywords =
    [
        "implement", "fix", "add", "remove", "rename", "refactor", "migrate",
        "change", "update", "create", "delete", "write", "build", "introduce",
        "patch", "extract", "extend",
    ];

    private static readonly string[] TestKeywords =
    [
        "test", "tests", "playwright", "spec", "xunit", "regression test",
    ];

    private static readonly string[] FailureMarkers =
    [
        "[[TASK_BLOCKED:", "[[TASK_NEEDS_INPUT:", "blocked:", "failed:", "could not",
        "permission denied", "watchdog", "stuck-loop",
    ];

    /// <summary>
    /// Run the heuristic suite against one job. The job must be in
    /// <see cref="TaskStates.Completed"/> or <see cref="TaskStates.Archive"/>;
    /// the caller is responsible for filtering.
    /// </summary>
    public (string verdict, List<EvidenceDiagnostic> diagnostics) Evaluate(TaskInfo job)
    {
        var diagnostics = new List<EvidenceDiagnostic>();
        if (job == null) return (AuditVerdicts.Inconclusive, diagnostics);

        var promptText = SafeRead(Path.Combine(job.FolderPath, "prompt.md"));
        var statusText = SafeRead(Path.Combine(job.FolderPath, "status.md"));
        var cliOutput = SafeRead(TaskPaths.CliOutputLog(job.FolderPath));

        var promptLower = promptText.ToLowerInvariant();
        var asksForCode = CodeChangeKeywords.Any(k => promptLower.Contains(k));
        var asksForTests = TestKeywords.Any(k => promptLower.Contains(k));

        // Signal 1: empty prompt / status. Cannot judge.
        if (promptText.Length < 10)
        {
            diagnostics.Add(new EvidenceDiagnostic
            {
                Kind = "empty-prompt",
                Level = AuditSignalLevels.Warn,
                Detail = "prompt.md is empty or missing.",
            });
        }

        // Signal 2: status / cli-output reports failure or block. Hard fail.
        var combined = (statusText + "\n" + cliOutput).ToLowerInvariant();
        foreach (var marker in FailureMarkers)
        {
            if (combined.Contains(marker.ToLowerInvariant()))
            {
                diagnostics.Add(new EvidenceDiagnostic
                {
                    Kind = "failure-marker",
                    Level = AuditSignalLevels.Fail,
                    Detail = $"Run output contains '{marker}'. Task probably did not complete.",
                });
                break;
            }
        }

        // Signal 3: prompt asks for code but no commit attached. Hard fail.
        if (asksForCode)
        {
            var commitCount = job.Commits?.Count ?? 0;
            if (commitCount == 0 && job.Commit == null)
            {
                diagnostics.Add(new EvidenceDiagnostic
                {
                    Kind = "missing-commit",
                    Level = AuditSignalLevels.Fail,
                    Detail = "Prompt asks for a code change but no commit was attributed to this task.",
                });
            }
        }

        // Signal 4: claimed file references in status.md - check the
        // backtick-quoted paths actually exist somewhere under the project.
        var claimedPaths = ExtractBacktickedPaths(statusText);
        var watchRoot = TryResolveProjectRoot(job.WatchPath);
        var missing = new List<string>();
        if (watchRoot != null)
        {
            foreach (var rel in claimedPaths.Take(20))
            {
                if (!LooksLikeRelativeFilePath(rel)) continue;
                var candidate = Path.Combine(watchRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) missing.Add(rel);
            }
        }
        if (missing.Count > 0)
        {
            diagnostics.Add(new EvidenceDiagnostic
            {
                Kind = "claimed-file-missing",
                Level = missing.Count >= 3 ? AuditSignalLevels.Fail : AuditSignalLevels.Warn,
                Detail = $"status.md mentions {missing.Count} path(s) that do not exist: {string.Join(", ", missing.Take(5))}.",
            });
        }

        // Signal 5: prompt asks for tests but status never mentions tests.
        if (asksForTests && !statusText.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new EvidenceDiagnostic
            {
                Kind = "missing-test-mention",
                Level = AuditSignalLevels.Warn,
                Detail = "Prompt asks for tests but status.md does not mention them.",
            });
        }

        // Signal 6: status / output empty altogether. Cannot judge but
        // suspicious.
        if (statusText.Length < 20 && cliOutput.Length < 100)
        {
            diagnostics.Add(new EvidenceDiagnostic
            {
                Kind = "no-evidence",
                Level = AuditSignalLevels.Warn,
                Detail = "status.md and cli-output.log are essentially empty; no evidence of work.",
            });
        }

        // Verdict mapping: any Fail -> NotReallyDone; >= 2 Warn -> Inconclusive;
        // otherwise Ok.
        var failCount = diagnostics.Count(d => d.Level == AuditSignalLevels.Fail);
        var warnCount = diagnostics.Count(d => d.Level == AuditSignalLevels.Warn);
        var verdict = failCount > 0
            ? AuditVerdicts.NotReallyDone
            : warnCount >= 2
                ? AuditVerdicts.Inconclusive
                : AuditVerdicts.Ok;

        return (verdict, diagnostics);
    }

    private static string SafeRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : string.Empty; }
        catch { return string.Empty; }
    }

    private static IEnumerable<string> ExtractBacktickedPaths(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        int i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('`', i);
            if (open < 0) yield break;
            var close = text.IndexOf('`', open + 1);
            if (close < 0) yield break;
            var content = text.Substring(open + 1, close - open - 1).Trim();
            if (content.Length is > 0 and < 200) yield return content;
            i = close + 1;
        }
    }

    private static bool LooksLikeRelativeFilePath(string s)
    {
        if (s.Length is < 2 or > 200) return false;
        if (s.Contains(' ')) return false;
        if (s.Contains('\n')) return false;
        if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        // Must look like a path (contains `/` or `\`, or has a file extension)
        return s.Contains('/') || s.Contains('\\') || (s.Contains('.') && s.IndexOf('.') > 0);
    }

    private static string? TryResolveProjectRoot(string watchPath)
    {
        // The watchPath looks like .../projects/<projectKey>. The repo
        // root for path-existence checks is project-dependent; we cannot
        // know it from here, so we treat the watchPath as the lookup
        // root. That gives a fair "do these paths exist *somewhere we can
        // see*" check without false positives from cross-project paths.
        return Directory.Exists(watchPath) ? watchPath : null;
    }
}
