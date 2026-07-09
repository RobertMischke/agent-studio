using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public class SentinelScannerTests
{
    [Fact]
    public void Done_sentinel_is_recognised()
    {
        var outcome = SentinelScanner.Scan("all good\n[[TASK_DONE]]\n");
        Assert.Equal(RunOutcomeKind.Done, outcome.Kind);
        Assert.Null(outcome.Reason);
    }

    [Fact]
    public void Blocked_sentinel_carries_reason()
    {
        var outcome = SentinelScanner.Scan("[[TASK_BLOCKED: missing credentials]]");
        Assert.Equal(RunOutcomeKind.Blocked, outcome.Kind);
        Assert.Equal("missing credentials", outcome.Reason);
    }

    [Fact]
    public void Needs_input_underscore_and_dash_forms_normalise()
    {
        Assert.Equal(RunOutcomeKind.NeedsInput, SentinelScanner.Scan("[[TASK_NEEDS_INPUT: which env]]").Kind);
        Assert.Equal(RunOutcomeKind.NeedsInput, SentinelScanner.Scan("[[TASK NEEDS-INPUT]]").Kind);
    }

    [Fact]
    public void Last_sentinel_wins_matching_server_semantics()
    {
        var outcome = SentinelScanner.Scan("[[TASK_BLOCKED: early]]\nlater\n[[TASK_DONE]]");
        Assert.Equal(RunOutcomeKind.Done, outcome.Kind);
    }

    [Fact]
    public void Prose_mentioning_the_token_does_not_match()
    {
        var outcome = SentinelScanner.Scan("I will emit TASK_DONE when the work is finished.");
        Assert.Equal(RunOutcomeKind.Unknown, outcome.Kind);
    }

    [Fact]
    public void Missing_sentinel_is_unknown_and_routes_to_human_review()
    {
        var outcome = SentinelScanner.Scan("the CLI produced no sign-off");
        Assert.Equal(RunOutcomeKind.Unknown, outcome.Kind);
        Assert.Equal("5-human-review", outcome.TargetState);
    }
}
