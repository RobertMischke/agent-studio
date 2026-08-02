namespace AgentRunner;

/// <summary>
/// Builds the prompt handed to a standalone remote-runner CLI.
/// <para>
/// The task server deliberately exposes the operator-authored <c>prompt.md</c>
/// verbatim. The local in-process runner adds standing model-routing and
/// contribution guidance plus its completion protocol while it renders
/// <c>runner-fresh-start.md</c>, so the standalone runner must add the same
/// instructions at its own execution boundary. Keeping them here makes one-shot
/// and daemon-claimed runs use exactly the same prompt.
/// </para>
/// </summary>
public static class RemoteRunPrompt
{
    public const string ModelRoutingPolicyInstruction =
        "Consult `docs/system/domains/model-routing-policy.md` as the authoritative source whenever " +
        "you select, recommend, override, or explain a model and thinking level. Never let quota or " +
        "cost cross its correctness-risk floors.";

    public const string ContributionGuideInstruction =
        "Consult `docs/start/contribution-and-style-guide.html` and treat it as the authoritative " +
        "source for contribution and style conventions.";

    public const string CompletionProtocol =
        "Orchestrator note: your reply MUST end with exactly one of " +
        "`[[TASK_DONE]]`, `[[TASK_BLOCKED:missing-dependency-xyz]]`, " +
        "`[[TASK_NEEDS_INPUT:choose-primary-column]]`, or `[[TASK_NOOP]]` as the final line. " +
        "Replace the example reason with the actual short reason; never emit the example text unchanged. " +
        "This is required, not optional. The orchestrator parses this token; " +
        "without it the run lands in review as missing-terminal-sentinel.";

    public static string Build(string taskPrompt) => Build(taskPrompt, modeFraming: null, resultsDirectory: null);

    /// <summary>
    /// Builds the remote prompt with the server-rendered per-mode framing block
    /// (read-only / research / concept / web contracts) between the task body and
    /// the standing instructions - the same relative position the local runner
    /// gives its <c>{{mode_framing}}</c> slot ("read after the task above").
    /// A null/empty framing keeps the historical verbatim behaviour.
    /// <para>
    /// <paramref name="resultsDirectory"/> is the host-absolute collected-results
    /// directory (also exported as <c>JOB_RESULTS_DIR</c>). The local runner's
    /// template names the job folder for evidence; a standalone remote agent has
    /// no job folder in sight, and AGT-2442 proved that a relative
    /// <c>results/</c> path silently lands in the disposable worktree and dies
    /// with it. Only files in this directory are shipped to the task server.
    /// </para>
    /// </summary>
    public static string Build(string taskPrompt, string? modeFraming, string? resultsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(taskPrompt);
        var framingBlock = string.IsNullOrWhiteSpace(modeFraming)
            ? string.Empty
            : modeFraming.Trim() + Environment.NewLine + Environment.NewLine;
        var resultsBlock = string.IsNullOrWhiteSpace(resultsDirectory)
            ? string.Empty
            : "Run context: result files (reports, screenshots, evidence - e.g. `results/report.html`) must be "
              + $"written into the absolute directory `{resultsDirectory.Trim()}` (also exported as the "
              + "`JOB_RESULTS_DIR` environment variable). Only files in that directory are collected and shipped "
              + "to the reviewer; a relative `results/` path inside the repository checkout is NOT collected and "
              + "is discarded with the temporary worktree."
              + Environment.NewLine + Environment.NewLine;
        return taskPrompt.TrimEnd() + Environment.NewLine + Environment.NewLine
            + "---" + Environment.NewLine + Environment.NewLine
            + framingBlock
            + resultsBlock
            + ModelRoutingPolicyInstruction + Environment.NewLine + Environment.NewLine
            + ContributionGuideInstruction + Environment.NewLine + Environment.NewLine
            + CompletionProtocol + Environment.NewLine;
    }
}
