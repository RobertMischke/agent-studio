using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DurableRunOutboxTests
{
    [Fact]
    public void Sequence_and_acknowledgement_survive_process_restart()
    {
        using var temp = new TempDirectory();
        var authority = Authority();
        var first = DurableRunOutbox.Open(temp.Path, authority);
        var one = first.Enqueue("status", """{"phase":"upload"}""");
        var two = first.Enqueue("terminal", """{"outcome":"Done"}""");
        first.Acknowledge(one.Sequence);

        var restarted = DurableRunOutbox.Open(temp.Path, authority);

        Assert.Equal(2, restarted.LastSequence);
        Assert.Equal(1, restarted.LastAcknowledgedSequence);
        Assert.Equal([two.Sequence], restarted.Pending.Select(item => item.Sequence));
        Assert.Equal(2, restarted.OldestUnacknowledgedSequence);
    }

    [Fact]
    public void Server_handoff_acknowledgement_survives_process_restart()
    {
        using var temp = new TempDirectory();
        var authority = Authority();
        var outbox = DurableRunOutbox.Open(temp.Path, authority);
        var acknowledgement = Ack(new string('a', 64));
        outbox.RecordHandoffAcknowledgement(acknowledgement);

        var restarted = DurableRunOutbox.Open(temp.Path, authority);

        Assert.Equal(acknowledgement, restarted.HandoffAcknowledgement);
        Assert.Equal("acknowledged", restarted.Snapshot.FinalHandoffState);
        Assert.Equal(acknowledgement.EnvelopeDigest, restarted.Snapshot.EnvelopeDigest);
    }

    [Fact]
    public async Task Lost_response_replays_with_the_same_idempotency_key_and_acks_once()
    {
        using var temp = new TempDirectory();
        var outbox = DurableRunOutbox.Open(temp.Path, Authority());
        var item = outbox.Enqueue("final-result", """{"sha":"abc"}""");
        var received = new HashSet<string>(StringComparer.Ordinal);
        var calls = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => outbox.ReplayAsync(
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                calls++;
                received.Add(item.IdempotencyKey);
                throw new HttpRequestException("response lost");
            },
            default));

        await outbox.ReplayAsync(
            (replayed, _) =>
            {
                calls++;
                received.Add(replayed.IdempotencyKey);
                return Task.CompletedTask;
            },
            default);

        Assert.Equal(2, calls);
        Assert.Single(received);
        Assert.Equal(item.Sequence, outbox.LastAcknowledgedSequence);
        Assert.Empty(outbox.Pending);
    }

    [Fact]
    public void Cleanup_fence_rejects_missing_or_mismatched_durable_ack()
    {
        var envelope = Envelope();
        var digest = ResultEnvelopeDigest.Compute(envelope);
        var gate = new DurableHandoffGate(digest);

        Assert.Throws<InvalidOperationException>(() => gate.RequireAcknowledged(null));
        Assert.Throws<InvalidOperationException>(() => gate.RequireAcknowledged(
            Ack(new string('f', 64))));
        gate.RequireAcknowledged(Ack(digest));
    }

    [Fact]
    public void Envelope_digest_is_canonical_and_binds_dependency_identities()
    {
        var first = Envelope() with
        {
            Submodules =
            [
                new ResultDependencyIdentity("z", new string('a', 40)),
                new ResultDependencyIdentity("a", new string('b', 40)),
            ],
        };
        var reordered = first with { Submodules = first.Submodules!.Reverse().ToArray() };
        var changed = first with
        {
            Submodules = [new ResultDependencyIdentity("z", new string('c', 40))],
        };

        Assert.Equal(ResultEnvelopeDigest.Compute(first), ResultEnvelopeDigest.Compute(reordered));
        Assert.NotEqual(ResultEnvelopeDigest.Compute(first), ResultEnvelopeDigest.Compute(changed));
    }

    private static RunOutboxAuthority Authority() => new(
        "run-1", "TASK-1", "runner-a", "instance-a", "lease-a", 7);

    private static ImmutableResultEnvelope Envelope() => new(
        "repo-1",
        "run-1",
        new string('1', 40),
        new string('2', 40),
        $"refs/heads/agent-studio/results/run-1/{new string('2', 40)}",
        null,
        new string('3', 64));

    private static ResultHandoffAck Ack(string digest) => new(
        "run-1", 10, digest, "acknowledged", DateTime.UtcNow, DateTime.UtcNow.AddDays(30), false);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "runner-outbox-tests",
                Guid.NewGuid().ToString("N"));
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
