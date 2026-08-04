namespace AgentStudio.Tasks;

/// <summary>
/// What the recall sweep found for one parked card. The shape is the guarantee:
/// there is no target lane and no requeue instruction anywhere on this record,
/// so a resolvable blocker can only ever be REPORTED. A card leaves a
/// human-decision lane when a person moves it, exactly as before.
/// </summary>
/// <param name="Status">One of <see cref="ParkedBlockerStatuses"/>.</param>
/// <param name="ParkedForSeconds">How long the card has been in its lane.</param>
public sealed record ParkedCardRecall(
    string ProjectName,
    string JobId,
    string TaskKey,
    string Title,
    string Lane,
    string BlockerType,
    string ConditionKind,
    string ConditionDescription,
    string Status,
    DateTime ParkedAt,
    long ParkedForSeconds,
    string Reason,
    string Detail)
{
    /// <summary>True when the recorded precondition is provably gone and the card
    /// is ready for a person to re-queue.</summary>
    public bool IsRecallable => string.Equals(Status, ParkedBlockerStatuses.Recallable, StringComparison.Ordinal);
}

/// <summary>The card facts the policy needs; no filesystem, no Git.</summary>
public sealed record ParkedCardCandidate(
    string ProjectName,
    string JobId,
    string TaskKey,
    string Title,
    string Lane,
    DateTime EnteredLaneAt);

/// <summary>
/// Pure decision layer of the recall sweep. Every branch is a total function of
/// (marker, probe verdict, clock), so the matrix tests cover the behaviour
/// without a workspace on disk.
/// </summary>
public static class ParkedCardRecallPolicy
{
    /// <summary>
    /// Folds a probe verdict into a reportable recall entry. The marker's park
    /// timestamp wins over the lane-entry stamp when it is present, because a
    /// card can be re-stamped by unrelated metadata repairs while the marker is
    /// written exactly once per park.
    /// </summary>
    public static ParkedCardRecall Decide(
        ParkedCardCandidate candidate,
        ParkedBlockerRecord record,
        ParkedBlockerEvaluation evaluation,
        DateTime now)
    {
        var parkedAt = record.ParkedAt == default
            ? candidate.EnteredLaneAt
            : record.ParkedAt;
        parkedAt = parkedAt == default ? now : parkedAt.ToUniversalTime();

        return new ParkedCardRecall(
            candidate.ProjectName,
            candidate.JobId,
            candidate.TaskKey,
            candidate.Title,
            candidate.Lane,
            record.BlockerType,
            record.Condition.Kind,
            record.Condition.Description,
            evaluation.Status,
            parkedAt,
            (long)Math.Max(0, (now - parkedAt).TotalSeconds),
            record.Reason,
            evaluation.Detail);
    }

    /// <summary>
    /// Whether this tick should announce the recall on the card's timeline.
    /// A resolved blocker is announced once; a blocker that goes back to
    /// blocked clears the announcement so a later re-resolution is announced
    /// again. Without this the sweep would repeat the same row every interval
    /// and the ledger would become unreadable noise.
    /// </summary>
    public static bool ShouldAnnounce(ParkedBlockerRecord record, ParkedBlockerEvaluation evaluation)
        => string.Equals(evaluation.Status, ParkedBlockerStatuses.Recallable, StringComparison.Ordinal)
            && record.ReportedRecallableAt is null;

    /// <summary>
    /// Whether the folded marker is worth writing to disk.
    ///
    /// <para>A steady-state parked card must produce ZERO writes per tick. The
    /// job folder's mtime feeds <c>TaskInfo.LastActivity</c>, so re-writing an
    /// unchanged marker every 30 minutes would make a card that has sat
    /// untouched for five days read as freshly active on the board - the exact
    /// opposite of the aging signal this feature adds. The evaluation timestamp
    /// alone therefore does not justify a write; only a changed verdict,
    /// changed detail, or a changed announcement does.</para>
    /// </summary>
    public static bool NeedsPersist(ParkedBlockerRecord? existing, ParkedBlockerRecord folded)
        => existing is null
            || !string.Equals(existing.LastEvaluation?.Status, folded.LastEvaluation?.Status, StringComparison.Ordinal)
            || !string.Equals(existing.LastEvaluation?.Detail, folded.LastEvaluation?.Detail, StringComparison.Ordinal)
            || existing.ReportedRecallableAt != folded.ReportedRecallableAt;

    /// <summary>
    /// The marker to persist after this tick. Pure: the sweep writes whatever
    /// comes back, so "what gets remembered" stays covered by the matrix tests.
    /// </summary>
    public static ParkedBlockerRecord Fold(
        ParkedBlockerRecord record,
        ParkedBlockerEvaluation evaluation,
        bool announced,
        DateTime now)
    {
        var recallable = string.Equals(evaluation.Status, ParkedBlockerStatuses.Recallable, StringComparison.Ordinal);
        return record with
        {
            LastEvaluation = evaluation,
            ReportedRecallableAt = recallable
                ? (announced ? now : record.ReportedRecallableAt)
                : null,
        };
    }
}
