using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

internal enum ReviewLeaseRecoveryStatus
{
    Valid,
    Invalid,
    Unknown,
}

internal sealed record ReviewLeaseRecoveryProbe(
    ReviewLeaseRecoveryStatus Status,
    ReviewLeaseDto? Lease,
    string Detail)
{
    public static ReviewLeaseRecoveryProbe Valid(ReviewLeaseDto lease)
        => new(ReviewLeaseRecoveryStatus.Valid, lease, "exact server lease renewed");

    public static ReviewLeaseRecoveryProbe Invalid(string detail)
        => new(ReviewLeaseRecoveryStatus.Invalid, null, detail);

    public static ReviewLeaseRecoveryProbe Unknown(string detail)
        => new(ReviewLeaseRecoveryStatus.Unknown, null, detail);
}

internal enum ReviewSlotRecoveryAction
{
    KeepLive,
    ProbeLease,
    KeepLease,
    DeleteInvalid,
    DeferUnknown,
}

/// <summary>Pure recovery and age decisions for one persisted review slot.</summary>
internal static class ReviewSlotRecoveryPolicy
{
    public static ReviewSlotRecoveryAction Decide(
        bool liveProcess,
        ReviewLeaseRecoveryStatus? leaseStatus = null)
    {
        if (liveProcess) return ReviewSlotRecoveryAction.KeepLive;
        return leaseStatus switch
        {
            null => ReviewSlotRecoveryAction.ProbeLease,
            ReviewLeaseRecoveryStatus.Valid => ReviewSlotRecoveryAction.KeepLease,
            ReviewLeaseRecoveryStatus.Invalid => ReviewSlotRecoveryAction.DeleteInvalid,
            _ => ReviewSlotRecoveryAction.DeferUnknown,
        };
    }

    public static bool ShouldPurgeForAge(
        DateTime updatedAtUtc,
        DateTime utcNow,
        TimeSpan maxAge,
        bool liveProcess)
        => !liveProcess
           && utcNow - updatedAtUtc >= maxAge;
}

internal sealed record RecoveredReviewSlot(
    PersistedReviewSlot Slot,
    ReviewSlotRecoveryAction Basis);

internal sealed record ReviewSlotRecoveryResult(
    IReadOnlyList<RecoveredReviewSlot> Active,
    IReadOnlyList<PersistedReviewSlot> Deferred,
    int Live,
    int LeaseValid,
    int Purged);

/// <summary>
/// Reconciles host-local records against process truth and exact Task Server
/// authority before any record may consume review admission.
/// </summary>
internal static class ReviewSlotRecovery
{
    public static async Task<ReviewSlotRecoveryResult> ReconcileAsync(
        IReadOnlyList<PersistedReviewSlot> slots,
        ReviewStateStore state,
        Func<PersistedReviewSlot, bool> isLive,
        Func<PersistedReviewSlot, CancellationToken, Task<ReviewLeaseRecoveryProbe>> probeLease,
        Action<string> log,
        CancellationToken ct)
    {
        var active = new List<RecoveredReviewSlot>();
        var deferred = new List<PersistedReviewSlot>();
        var live = 0;
        var leaseValid = 0;
        var purged = 0;

        foreach (var slot in slots)
        {
            ct.ThrowIfCancellationRequested();
            var initial = ReviewSlotRecoveryPolicy.Decide(isLive(slot));
            if (initial == ReviewSlotRecoveryAction.KeepLive)
            {
                live++;
                active.Add(new RecoveredReviewSlot(slot, initial));
                log(
                    $"review-slot-reconciled attempt={slot.AttemptId} status=active " +
                    "basis=live-process");
                continue;
            }

            var probe = await probeLease(slot, ct);
            var action = ReviewSlotRecoveryPolicy.Decide(false, probe.Status);
            if (action == ReviewSlotRecoveryAction.KeepLease)
            {
                var renewed = state.Save(slot with
                {
                    Claim = slot.Claim with { Lease = probe.Lease },
                });
                leaseValid++;
                active.Add(new RecoveredReviewSlot(renewed, action));
                log(
                    $"review-slot-reconciled attempt={slot.AttemptId} status=active " +
                    "basis=server-lease");
                continue;
            }

            if (action == ReviewSlotRecoveryAction.DeleteInvalid)
            {
                if (state.TryDelete(slot))
                {
                    purged++;
                    log(
                        $"review-slot-reconciled attempt={slot.AttemptId} status=cleaned " +
                        $"basis=server-lease-invalid detail={Token(probe.Detail)}");
                }
                else
                {
                    deferred.Add(slot);
                    log(
                        $"review-slot-reconciled attempt={slot.AttemptId} status=deferred " +
                        "basis=local-delete-failed");
                }
                continue;
            }

            deferred.Add(slot);
            log(
                $"review-slot-reconciled attempt={slot.AttemptId} status=deferred " +
                $"basis=server-authority-unknown detail={Token(probe.Detail)}");
        }

        log(
            $"review-slot-reconciliation inspected={slots.Count} active={active.Count} " +
            $"live={live} leaseValid={leaseValid} purged={purged} deferred={deferred.Count}");
        return new ReviewSlotRecoveryResult(active, deferred, live, leaseValid, purged);
    }

    private static string Token(string value)
        => string.Concat(value.Select(character => char.IsWhiteSpace(character) ? '_' : character));
}
