using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RemoteCompletionProtocolTests
{
    [Fact]
    public void Daemon_prompt_adds_the_terminal_contract_to_the_task_prompt()
    {
        var prompt = RemoteRunPrompt.Build("Make the requested trivial change.");

        Assert.StartsWith("Make the requested trivial change.", prompt);
        Assert.Contains("docs/system/domains/model-routing-policy.md", prompt);
        Assert.Contains("authoritative source", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("correctness-risk floors", prompt);
        Assert.Contains("MUST end with exactly one", prompt);
        Assert.Contains("[[TASK_DONE]]", prompt);
        Assert.Contains("[[TASK_BLOCKED:missing-dependency-xyz]]", prompt);
        Assert.Contains("[[TASK_NEEDS_INPUT:choose-primary-column]]", prompt);
        Assert.Contains("Replace the example reason", prompt);
        Assert.DoesNotContain("<reason>", prompt);
        Assert.Contains("[[TASK_NOOP]]", prompt);
        Assert.EndsWith(Environment.NewLine, prompt);
    }

    [Fact]
    public void Codex_exec_jsonl_done_event_is_recognized_as_a_regular_done_outcome()
    {
        const string codex0144Output = """
            {"type":"thread.started","thread_id":"019c"}
            {"type":"item.completed","item":{"id":"item_1","type":"agent_message","text":"Implemented and verified the trivial change.\n[[TASK_DONE]]"}}
            {"type":"turn.completed","usage":{"input_tokens":100,"output_tokens":20}}
            """;

        var outcome = SentinelScanner.Scan(codex0144Output);

        Assert.Equal(RunOutcomeKind.Done, outcome.Kind);
        Assert.Null(outcome.Reason);
        Assert.Equal("Remote run completed", outcome.SummaryPrefix);
    }

    [Fact]
    public void Last_terminal_event_wins_in_codex_jsonl_output()
    {
        const string output = """
            {"type":"item.completed","item":{"type":"agent_message","text":"[[TASK_NEEDS_INPUT:first question]]"}}
            {"type":"item.completed","item":{"type":"agent_message","text":"Question resolved.\n[[TASK_DONE]]"}}
            """;

        Assert.Equal(RunOutcomeKind.Done, SentinelScanner.Scan(output).Kind);
    }
}
