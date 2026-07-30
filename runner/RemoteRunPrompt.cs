namespace AgentRunner;

/// <summary>
/// Builds the prompt handed to a standalone remote-runner CLI.
/// <para>
/// The task server deliberately exposes the operator-authored <c>prompt.md</c>
/// verbatim. The local in-process runner adds standing model-routing guidance
/// and its completion protocol while it renders <c>runner-fresh-start.md</c>,
/// so the standalone runner must add the same instructions at its own execution
/// boundary. Keeping them here makes one-shot and daemon-claimed runs use
/// exactly the same prompt.
/// </para>
/// </summary>
public static class RemoteRunPrompt
{
    public const string ModelRoutingPolicyInstruction =
        "Consult `docs/system/domains/model-routing-policy.md` as the authoritative source whenever " +
        "you select, recommend, override, or explain a model and thinking level. Never let quota or " +
        "cost cross its correctness-risk floors.";

    public const string CompletionProtocol =
        "Orchestrator note: your reply MUST end with exactly one of " +
        "`[[TASK_DONE]]`, `[[TASK_BLOCKED:missing-dependency-xyz]]`, " +
        "`[[TASK_NEEDS_INPUT:choose-primary-column]]`, or `[[TASK_NOOP]]` as the final line. " +
        "Replace the example reason with the actual short reason; never emit the example text unchanged. " +
        "This is required, not optional. The orchestrator parses this token; " +
        "without it the run lands in review as missing-terminal-sentinel.";

    public static string Build(string taskPrompt) => Build(taskPrompt, modeFraming: null);

    /// <summary>
    /// Builds the remote prompt with the server-rendered per-mode framing block
    /// (read-only / research / concept / web contracts) between the task body and
    /// the standing instructions - the same relative position the local runner
    /// gives its <c>{{mode_framing}}</c> slot ("read after the task above").
    /// A null/empty framing keeps the historical verbatim behaviour.
    /// </summary>
    public static string Build(string taskPrompt, string? modeFraming)
    {
        ArgumentNullException.ThrowIfNull(taskPrompt);
        var framingBlock = string.IsNullOrWhiteSpace(modeFraming)
            ? string.Empty
            : modeFraming.Trim() + Environment.NewLine + Environment.NewLine;
        return taskPrompt.TrimEnd() + Environment.NewLine + Environment.NewLine
            + "---" + Environment.NewLine + Environment.NewLine
            + framingBlock
            + ModelRoutingPolicyInstruction + Environment.NewLine + Environment.NewLine
            + CompletionProtocol + Environment.NewLine;
    }
}
