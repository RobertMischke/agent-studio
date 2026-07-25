using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteChatWorkBrokerTests
{
    private static readonly RemoteChatWorkRoute Route = new(
        "runner-01",
        "PROJ-002",
        "Agent Studio",
        "ssh://git.example.invalid/agent-studio.git",
        "develop");

    [Fact]
    public async Task Assigned_runner_claims_turn_and_completion_preserves_exact_host_checkout()
    {
        var broker = new RemoteChatWorkBroker(NullLogger<RemoteChatWorkBroker>.Instance);
        var pending = broker.EnqueueTurnAsync(
            Route,
            "Inspect the repository.",
            "gpt-5.5",
            "high",
            CancellationToken.None);

        var wrongRunner = broker.TryClaim(new RemoteChatWorkClaimRequest(
            "runner-02", "runner-02", "other-host"));
        Assert.Equal(RemoteChatWorkClaimStatuses.Empty, wrongRunner.Status);

        var claim = broker.TryClaim(new RemoteChatWorkClaimRequest(
            "runner-01", "agent-runner-01", "agent-runner-01"));
        Assert.Equal(RemoteChatWorkClaimStatuses.Claimed, claim.Status);
        Assert.NotNull(claim.Work);
        Assert.Equal(Route.RepositoryUrl, claim.Work!.RepositoryUrl);
        Assert.Equal(Route.DefaultBranch, claim.Work.DefaultBranch);
        Assert.Equal("Inspect the repository.", claim.Work.Prompt);

        var context = new ChatExecutionContext(
            "remote",
            "agent-runner-01",
            "/srv/agent-runner/work/PROJ-002/project-chat",
            "develop",
            "0123456789abcdef0123456789abcdef01234567",
            "ready",
            DateTime.UtcNow);
        var accepted = broker.Complete(new RemoteChatWorkCompletionRequest(
            claim.Work.WorkId,
            claim.Work.ClaimToken,
            "runner-01",
            true,
            $"tool-output cwd={context.RepoPath}",
            "gpt-5.5",
            null,
            null,
            context));

        Assert.True(accepted);
        var result = await pending;
        Assert.True(result.Success);
        Assert.Contains(context.RepoPath!, result.ReplyText);
        Assert.Equal(context, broker.GetContext(Route));

        var reassigned = Route with { RunnerId = "runner-02" };
        Assert.Null(broker.GetContext(reassigned));
    }

    [Fact]
    public async Task Cancelled_unclaimed_turn_is_removed_before_a_host_can_pick_it_up()
    {
        var broker = new RemoteChatWorkBroker(NullLogger<RemoteChatWorkBroker>.Instance);
        using var cancelled = new CancellationTokenSource();
        var pending = broker.EnqueueTurnAsync(
            Route,
            "This request will be cancelled.",
            "gpt-5.5",
            null,
            cancelled.Token);

        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var claim = broker.TryClaim(new RemoteChatWorkClaimRequest(
            "runner-01", "agent-runner-01", "agent-runner-01"));
        Assert.Equal(RemoteChatWorkClaimStatuses.Empty, claim.Status);
    }

    [Fact]
    public void Inspection_work_is_deduplicated_per_project_checkout()
    {
        var broker = new RemoteChatWorkBroker(NullLogger<RemoteChatWorkBroker>.Instance);

        broker.RequestInspection(Route);
        broker.RequestInspection(Route);

        var first = broker.TryClaim(new RemoteChatWorkClaimRequest(
            "runner-01", "agent-runner-01", "agent-runner-01"));
        var second = broker.TryClaim(new RemoteChatWorkClaimRequest(
            "runner-01", "agent-runner-01", "agent-runner-01"));

        Assert.Equal(RemoteChatWorkClaimStatuses.Claimed, first.Status);
        Assert.Equal(RemoteChatWorkKinds.Inspect, first.Work?.Kind);
        Assert.Equal(RemoteChatWorkClaimStatuses.Empty, second.Status);
    }
}
