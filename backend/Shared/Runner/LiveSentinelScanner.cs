namespace AgentStudio.Shared;

/// <summary>
/// Live-stream terminal-sentinel detector for the runner. Decides, from the
/// CLI output captured SO FAR (mid-run), whether the agent has emitted its OWN
/// terminal sentinel (<c>[[TASK_DONE]]</c> / <c>[[TASK_BLOCKED]]</c> / …) and the
/// lingering process can be reaped.
///
/// <para>
/// <b>Why this is a separate, tested helper.</b> The naive version
/// (<c>SentinelRegex.IsMatch</c> on every raw output line) caused the dominant
/// "tasks never complete" incident (2026-06-23): the backend's own runner code,
/// <c>AGENTS.md</c>, and <c>docs/contracts/agent-task.md</c> are FULL of
/// <c>[[TASK_DONE]]</c> literals, so any run that merely READ such a file (the
/// file content rides the <c>user</c> / tool-result stream) tripped the scanner
/// and was killed mid-work as a false "completion". See
/// <c>docs/wiki/concepts/runner-stability-incidents.html</c>.
/// </para>
///
/// <para>
/// Two guards keep it honest: (1) only the AGENT's own stream can carry a
/// terminal sentinel — mirror <see cref="AgentOutcomeAnalyzer"/>'s JoinAgentText
/// and drop <c>system</c>/<c>user</c>(tool-result)/<c>orchestrator</c>/<c>stderr</c>
/// lines; (2) only a STANDALONE sentinel line counts — the token (with its
/// optional reason) is essentially the whole line, modulo a little markdown/quote
/// decoration — so a sentinel mentioned inside prose or quoted code does not
/// fire. Missing a real terminal sentinel is harmless (the run finalizes when the
/// CLI exits / the watchdog); a false positive kills live work, so we err toward
/// not stopping.
/// </para>
/// </summary>
public static class LiveSentinelScanner
{
    /// <summary>Decoration slack: chars of <c>** > - `</c> markdown/quote that may
    /// wrap a standalone sentinel line beyond the matched token itself.</summary>
    private const int DecorationSlack = 8;

    public static bool HasStandaloneAgentSentinel(IReadOnlyList<CliOutputLine>? snapshot)
    {
        if (snapshot == null || snapshot.Count == 0) return false;
        for (var i = snapshot.Count - 1; i >= 0; i--)
        {
            var ln = snapshot[i];
            var stream = ln?.Stream ?? string.Empty;
            if (string.Equals(stream, "system", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(stream, "user", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(stream, "orchestrator", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(stream, "stderr", System.StringComparison.OrdinalIgnoreCase)) continue;

            var text = (ln?.Text ?? string.Empty).Trim();
            if (text.Length == 0) continue;

            var m = AgentOutcomeAnalyzer.SentinelRegex.Match(text);
            if (m.Success && text.Length <= m.Length + DecorationSlack) return true;
        }
        return false;
    }
}
