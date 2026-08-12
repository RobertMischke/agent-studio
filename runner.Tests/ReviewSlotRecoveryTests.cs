using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewSlotRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "review-slot-recovery-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Mixed_restart_counts_only_the_live_slot_and_deletes_stale_authority()
    {
        var state = new ReviewStateStore(_root);
        var live = state.Create(Claim("live"), Workspace("live"));
        var stale = state.Create(Claim("stale"), Workspace("stale"));
        var probed = new List<string>();
        var logs = new List<string>();

        var result = await ReviewSlotRecovery.ReconcileAsync(
            [live, stale],
            state,
            slot => slot.AttemptId == "live",
            (slot, _) =>
            {
                probed.Add(slot.AttemptId);
                return Task.FromResult(ReviewLeaseRecoveryProbe.Invalid("expired"));
            },
            logs.Add,
            CancellationToken.None);

        var recovered = Assert.Single(result.Active);
        Assert.Equal("live", recovered.Slot.AttemptId);
        Assert.Equal(ReviewSlotRecoveryAction.KeepLive, recovered.Basis);
        Assert.Equal(["stale"], probed);
        Assert.Equal(["live"], state.LoadAll().Select(slot => slot.AttemptId));
        Assert.Contains(logs, line => line.Contains(
            "review-slot-reconciliation inspected=2 active=1 live=1 leaseValid=0 purged=1 deferred=0",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dead_slot_with_exact_server_lease_remains_recoverable()
    {
        var state = new ReviewStateStore(_root);
        var slot = state.Create(Claim("lease-valid"), Workspace("lease-valid"));
        var renewed = slot.Claim.Lease! with { ExpiresAt = DateTime.UtcNow.AddMinutes(15) };

        var result = await ReviewSlotRecovery.ReconcileAsync(
            [slot],
            state,
            _ => false,
            (_, _) => Task.FromResult(ReviewLeaseRecoveryProbe.Valid(renewed)),
            _ => { },
            CancellationToken.None);

        var recovered = Assert.Single(result.Active);
        Assert.Equal(ReviewSlotRecoveryAction.KeepLease, recovered.Basis);
        Assert.Equal(renewed.ExpiresAt, Assert.Single(state.LoadAll()).Claim.Lease!.ExpiresAt);
        Assert.Equal(1, result.LeaseValid);
        Assert.Equal(0, result.Purged);
    }

    [Fact]
    public void Aging_sweep_purges_old_dead_record_but_retains_old_live_record()
    {
        var state = new ReviewStateStore(_root);
        var now = new DateTime(2026, 8, 12, 2, 0, 0, DateTimeKind.Utc);
        var live = state.Create(Claim("old-live"), Workspace("old-live"));
        var stale = state.Create(Claim("old-stale"), Workspace("old-stale"));
        RewriteUpdatedAt(state, live, now.AddHours(-25));
        RewriteUpdatedAt(state, stale, now.AddHours(-25));

        var sweep = state.PurgeStale(
            now,
            TimeSpan.FromHours(24),
            slot => slot.AttemptId == "old-live");

        Assert.Equal(2, sweep.Inspected);
        Assert.Equal(1, sweep.Purged);
        Assert.Equal(1, sweep.RetainedLive);
        Assert.Equal(0, sweep.Failed);
        Assert.Equal(["old-live"], state.LoadAll().Select(slot => slot.AttemptId));
    }

    [Theory]
    [InlineData(true, null, (int)ReviewSlotRecoveryAction.KeepLive)]
    [InlineData(false, null, (int)ReviewSlotRecoveryAction.ProbeLease)]
    [InlineData(false, (int)ReviewLeaseRecoveryStatus.Valid, (int)ReviewSlotRecoveryAction.KeepLease)]
    [InlineData(false, (int)ReviewLeaseRecoveryStatus.Invalid, (int)ReviewSlotRecoveryAction.DeleteInvalid)]
    [InlineData(false, (int)ReviewLeaseRecoveryStatus.Unknown, (int)ReviewSlotRecoveryAction.DeferUnknown)]
    public void Recovery_policy_has_one_explicit_action_for_each_authority_state(
        bool live,
        int? leaseStatus,
        int expected)
        => Assert.Equal(
            (ReviewSlotRecoveryAction)expected,
            ReviewSlotRecoveryPolicy.Decide(
                live,
                leaseStatus is null ? null : (ReviewLeaseRecoveryStatus)leaseStatus.Value));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private string Workspace(string attemptId)
        => Path.Combine(_root, "work", attemptId, "repository");

    private static void RewriteUpdatedAt(
        ReviewStateStore state,
        PersistedReviewSlot slot,
        DateTime updatedAtUtc)
    {
        var path = Path.Combine(
            state.Root,
            $"{RemoteReviewWorkspace.SafeSegment(slot.AttemptId)}.review-slot.json");
        var json = File.ReadAllText(path);
        var current = System.Text.Json.JsonSerializer.Deserialize<PersistedReviewSlot>(
                          json,
                          new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
                      ?? throw new InvalidDataException("Test review slot could not be deserialized.");
        File.WriteAllText(
            path,
            System.Text.Json.JsonSerializer.Serialize(
                current with { UpdatedAtUtc = updatedAtUtc },
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
    }

    private static ReviewClaimResponse Claim(string attemptId)
    {
        var now = new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc);
        var subjectId = $"subject-{attemptId}";
        var attempt = new ReviewAttemptDto(
            attemptId,
            subjectId,
            $"task-{attemptId}",
            1,
            "leased",
            "review-runner",
            "review-host",
            7,
            now,
            null,
            null,
            null,
            null);
        var subject = new ReviewSubjectDto(
            subjectId,
            attempt.TaskId,
            "run-1",
            "example/repository",
            null,
            new string('a', 40),
            null,
            "bundle",
            new string('b', 64),
            "coding-host",
            "policy-v1",
            new ReviewPlanDto([], []),
            now);
        var lease = new ReviewLeaseDto(
            $"lease-{attemptId}",
            attemptId,
            subjectId,
            "review-runner",
            "instance-1",
            "review-host",
            7,
            now,
            now.AddMinutes(15),
            "active",
            $"review-{attemptId}-f7",
            25000,
            3);
        return new ReviewClaimResponse("claimed", attempt, subject, lease);
    }
}
