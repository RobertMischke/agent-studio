using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The one compact recovery line is a product contract (ASS-1730): every
/// platform recovery surfaces as a single calm chat line of the shape
/// <c>&lt;reason&gt;: &lt;what&gt; -&gt; &lt;action&gt; (attempt N/M, session ...)</c>,
/// never a fat multi-sentence block. These tests pin the head shape, the
/// optional parenthetical, the ASCII arrow (AGENTS.md forbids glyph arrows),
/// and the persisted-line stream/tag both emit paths share.
/// </summary>
public sealed class RecoveryChatLineTests
{
    [Fact]
    public void Format_WithCounterAndResumedSession_RendersHeadAndFullParenthetical()
    {
        var line = RecoveryChatLine.Format(
            RecoveryChatLine.ReasonWatchdog,
            "silence timeout",
            "reissue",
            attempt: 2,
            maxAttempts: 3,
            sessionResumed: true);

        Assert.Equal("watchdog: silence timeout -> reissue (attempt 2/3, session resumed)", line);
    }

    [Fact]
    public void Format_SessionNew_SaysSessionNew()
    {
        var line = RecoveryChatLine.Format(
            RecoveryChatLine.ReasonSystemSleep,
            "host woke from standby",
            "run resumed",
            attempt: 1,
            maxAttempts: 3,
            sessionResumed: false);

        Assert.Equal("system-sleep: host woke from standby -> run resumed (attempt 1/3, session new)", line);
    }

    [Fact]
    public void Format_NoOptionalSignals_RendersCleanHeadOnly()
    {
        var line = RecoveryChatLine.Format(
            RecoveryChatLine.ReasonCrash,
            "backend restart during run",
            "requeued to 2-ready");

        // A crash requeue has no retry counter and no session signal, so the
        // parenthetical is omitted entirely.
        Assert.Equal("crash: backend restart during run -> requeued to 2-ready", line);
        Assert.DoesNotContain("(", line);
    }

    [Fact]
    public void Format_UsesAsciiArrowNotGlyph()
    {
        var line = RecoveryChatLine.Format("crash", "x", "y");

        Assert.Contains(" -> ", line);
        Assert.DoesNotContain("→", line); // glyph arrow
        Assert.DoesNotContain("—", line); // em dash
    }

    [Theory]
    [InlineData(2, null)]
    [InlineData(null, 3)]
    [InlineData(0, 3)]
    public void Format_PartialOrZeroCounter_OmitsAttemptButKeepsSession(int? attempt, int? maxAttempts)
    {
        var line = RecoveryChatLine.Format(
            RecoveryChatLine.ReasonWatchdog,
            "silence timeout",
            "reissue",
            attempt: attempt,
            maxAttempts: maxAttempts,
            sessionResumed: true);

        Assert.DoesNotContain("attempt", line);
        Assert.EndsWith("(session resumed)", line);
    }

    [Fact]
    public void Format_StripsNewlinesFromComponents()
    {
        var line = RecoveryChatLine.Format(
            "crash",
            "first line\r\nsecond line",
            "requeued");

        // The one-line form must never carry a raw CR/LF (it would split the
        // persisted cli-output.log line); whitespace collapse is incidental.
        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("\r", line);
        Assert.Contains("first line", line);
        Assert.Contains("second line", line);
    }

    [Fact]
    public void PersistedLine_CarriesOrchestratorStreamAndRecoveryTag()
    {
        var utc = new DateTime(2026, 6, 10, 13, 5, 9, 250, DateTimeKind.Utc);

        var line = RecoveryChatLine.PersistedLine(
            utc,
            RecoveryChatLine.ReasonHostRestart,
            "run stuck in 3-progress after restart",
            "requeued to 2-ready");

        // Same persisted shape OrchestratorChatLog writes so the activity-log
        // parser and the frontend treat both emit paths identically.
        Assert.Equal(
            "[13:05:09.250] [orchestrator] [recovery] host-restart: run stuck in 3-progress after restart -> requeued to 2-ready",
            line);
    }
}
