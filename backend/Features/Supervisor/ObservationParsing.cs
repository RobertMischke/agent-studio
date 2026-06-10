
namespace AgentStudio.Supervisor;

/// <summary>
/// Pure static helpers for turning a parsed <see cref="CliOutputLine"/> stream
/// into supervisor observation fields. Kept separate from the service shell so
/// the parsing rules are unit-testable without DI, file I/O, or runner state.
/// </summary>
public static class ObservationParsing
{
    public static IReadOnlyList<SupervisorRecentDecision> ExtractRecentDecisions(
        IReadOnlyList<CliOutputLine> lines, int max = 10)
    {
        var result = new List<SupervisorRecentDecision>();
        for (int i = lines.Count - 1; i >= 0 && result.Count < max; i--)
        {
            var line = lines[i];
            if (!IsOrchestratorStream(line.Stream)) continue;
            var (kind, summary) = SplitDecisionTag(line.Text);
            result.Add(new SupervisorRecentDecision(line.Timestamp, kind, summary));
        }
        result.Reverse();
        return result;
    }

    public static IReadOnlyList<string> ExtractRecentAgentSamples(
        IReadOnlyList<CliOutputLine> lines, int max = 20)
    {
        var result = new List<string>();
        for (int i = lines.Count - 1; i >= 0 && result.Count < max; i--)
        {
            var line = lines[i];
            if (IsOrchestratorStream(line.Stream)) continue;
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
            result.Add(line.Text);
        }
        result.Reverse();
        return result;
    }

    public static SupervisorErrorCounts CountErrors(
        IReadOnlyList<CliOutputLine> lines, DateTime now, TimeSpan window)
    {
        var cutoff = now - window;
        int cli = 0, orch = 0, runFail = 0;
        foreach (var line in lines)
        {
            if (line.Timestamp < cutoff) continue;
            var text = line.Text ?? string.Empty;
            if (LooksLikeError(text))
            {
                if (IsOrchestratorStream(line.Stream)) orch++;
                else cli++;
                if (LooksLikeRunFailure(text)) runFail++;
            }
        }
        return new SupervisorErrorCounts(cli, orch, runFail);
    }

    public static DateTime? LatestTimestamp(IReadOnlyList<CliOutputLine> lines)
    {
        DateTime? best = null;
        foreach (var line in lines)
        {
            if (best == null || line.Timestamp > best) best = line.Timestamp;
        }
        return best;
    }

    private static bool IsOrchestratorStream(string? stream) =>
        string.Equals(stream, "orchestrator", StringComparison.OrdinalIgnoreCase);

    private static (string Kind, string Summary) SplitDecisionTag(string text)
    {
        // Orchestrator messages persist as "[<kind>] <summary>". Extract.
        if (string.IsNullOrEmpty(text)) return ("decision", string.Empty);
        if (text.Length > 1 && text[0] == '[')
        {
            var close = text.IndexOf(']', 1);
            if (close > 1)
            {
                var kind = text.Substring(1, close - 1);
                var rest = close + 1 < text.Length ? text[(close + 1)..].TrimStart() : string.Empty;
                return (kind, rest);
            }
        }
        return ("decision", text);
    }

    private static bool LooksLikeError(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("exception", StringComparison.OrdinalIgnoreCase)
            || text.Contains("fail", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeRunFailure(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("[[TASK_BLOCKED", StringComparison.Ordinal)
            || text.Contains("run failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("task failed", StringComparison.OrdinalIgnoreCase);
    }
}
