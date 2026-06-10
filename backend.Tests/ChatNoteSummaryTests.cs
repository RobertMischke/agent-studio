
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the pure summariser that decides whether the supervisor speaks
/// up in the chat. Quiet beats spam: an empty window must produce no
/// message at all; a populated window must surface the first warn-level
/// topic so the user can act on it without scrolling logs.
/// </summary>
public class ChatNoteSummaryTests
{
    private static readonly DateTime From = new(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddMinutes(30);

    [Fact]
    public void EmptyWindow_NoAdvisories_NoCycles_NoReviews_ReturnsNull()
    {
        var window = new ChatNoteWindow(
            From: From,
            To: To,
            Advisories: Array.Empty<SupervisorAdvisory>(),
            Cycles: Array.Empty<ChatNoteCycleEntry>(),
            JobsReachedReviewCount: 0);

        Assert.Null(ChatNoteSummary.Build(window));
    }

    [Fact]
    public void OneWarnAdvisory_BuildsMessageContainingTopic()
    {
        var advisory = new SupervisorAdvisory(
            CreatedAt: From.AddMinutes(5),
            Project: "p",
            Severity: SupervisorSeverity.Warn,
            Source: SupervisorSource.HardCheck,
            Topic: "no-progress",
            Message: "stalled");

        var window = new ChatNoteWindow(
            From: From,
            To: To,
            Advisories: new[] { advisory },
            Cycles: Array.Empty<ChatNoteCycleEntry>(),
            JobsReachedReviewCount: 0);

        var msg = ChatNoteSummary.Build(window);
        Assert.NotNull(msg);
        Assert.StartsWith("Supervisor:", msg);
        Assert.Contains("no-progress", msg!);
        Assert.Contains("warn advisory", msg);
        Assert.True(msg.Length <= ChatNoteSummary.MaxLength,
            $"message exceeded {ChatNoteSummary.MaxLength} chars: {msg}");
    }

    [Fact]
    public void HealthyCycleAndReviews_NoAdvisories_StillNotable_BuildsMessage()
    {
        var cycle = new ChatNoteCycleEntry(
            CompletedAt: From.AddMinutes(20),
            CycleId: "mc-1",
            Verdict: "Healthy",
            ActionKind: "Resume",
            ActionReason: "all-quiet");

        var window = new ChatNoteWindow(
            From: From,
            To: To,
            Advisories: Array.Empty<SupervisorAdvisory>(),
            Cycles: new[] { cycle },
            JobsReachedReviewCount: 4);

        var msg = ChatNoteSummary.Build(window);
        Assert.NotNull(msg);
        Assert.Contains("0 advisories", msg!);
        Assert.Contains("1 cycle (healthy)", msg);
        Assert.Contains("4 jobs reached review", msg);
        Assert.EndsWith("in the last 30 min.", msg);
    }

    [Fact]
    public void OverlongMessage_IsTruncatedToCap()
    {
        // Build many advisories with long topics to push the message past
        // the cap; the summariser keeps at most two topics, so we lean on
        // a long single topic to overflow.
        var topic = new string('x', 400);
        var advisory = new SupervisorAdvisory(
            CreatedAt: From.AddMinutes(1),
            Project: "p",
            Severity: SupervisorSeverity.Warn,
            Source: SupervisorSource.HardCheck,
            Topic: topic,
            Message: "");

        var window = new ChatNoteWindow(
            From: From,
            To: To,
            Advisories: new[] { advisory },
            Cycles: Array.Empty<ChatNoteCycleEntry>(),
            JobsReachedReviewCount: 0);

        var msg = ChatNoteSummary.Build(window);
        Assert.NotNull(msg);
        Assert.True(msg!.Length <= ChatNoteSummary.MaxLength);
        Assert.EndsWith("...", msg);
    }
}
