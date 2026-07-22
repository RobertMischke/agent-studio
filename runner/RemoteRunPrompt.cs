namespace AgentRunner;

/// <summary>
/// Builds the prompt handed to a standalone remote-runner CLI.
/// <para>
/// The task server deliberately exposes the operator-authored <c>prompt.md</c>
/// verbatim. The local in-process runner adds standing model-routing and
/// contribution guidance plus its completion protocol while it renders
/// <c>runner-fresh-start.md</c>, so the standalone runner must add the same
/// instructions at its own execution
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

    public static string Build(string taskPrompt)
    {
        ArgumentNullException.ThrowIfNull(taskPrompt);
        return taskPrompt.TrimEnd() + Environment.NewLine + Environment.NewLine
            + "---" + Environment.NewLine + Environment.NewLine
            + ModelRoutingPolicyInstruction + Environment.NewLine + Environment.NewLine
            + ContributionGuideInstruction + Environment.NewLine + Environment.NewLine
            + ModelRoutingPolicyInstruction + Environment.NewLine + Environment.NewLine
            + CompletionProtocol + Environment.NewLine;
    }
}
