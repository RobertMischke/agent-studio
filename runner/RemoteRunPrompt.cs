namespace AgentRunner;

/// <summary>
/// Builds the prompt handed to a standalone remote-runner CLI.
/// <para>
/// The task server deliberately exposes the operator-authored <c>prompt.md</c>
/// verbatim. The local in-process runner adds its completion protocol while it
/// renders <c>runner-fresh-start.md</c>, so the standalone runner must add the
/// same terminal contract at its own execution boundary. Keeping it here makes
/// one-shot and daemon-claimed runs use exactly the same prompt.
/// </para>
/// </summary>
public static class RemoteRunPrompt
{
    public const string CompletionProtocol =
        "Orchestrator note: your reply MUST end with exactly one of " +
        "`[[TASK_DONE]]`, `[[TASK_BLOCKED:<reason>]]`, " +
        "`[[TASK_NEEDS_INPUT:<reason>]]`, or `[[TASK_NOOP]]` as the final line. " +
        "This is required, not optional. The orchestrator parses this token; " +
        "without it the run lands in review as missing-terminal-sentinel.";

    public static string Build(string taskPrompt)
    {
        ArgumentNullException.ThrowIfNull(taskPrompt);
        return taskPrompt.TrimEnd() + Environment.NewLine + Environment.NewLine
            + "---" + Environment.NewLine + Environment.NewLine
            + CompletionProtocol + Environment.NewLine;
    }
}
