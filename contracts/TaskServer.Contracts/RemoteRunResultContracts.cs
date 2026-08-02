namespace AgentStudio.TaskServer.Contracts;

public static class RemoteRunResultProtocol
{
    public const string V1 = "remote-run-result/v1";
    public const string V0 = "remote-run-result/v0";
}

public sealed record RemoteRunComponentVersion(string Name, string? Image, string? Commit);
public sealed record RemoteRunIdentity(string Id, string Name);

public sealed record RemoteRunPhaseTiming(
    string Phase,
    string Status,
    DateTime QueuedAt,
    DateTime? StartedAt,
    DateTime FinishedAt,
    long QueueDurationMs,
    long ExecutionDurationMs,
    string DurationSource,
    string? Reason = null);

public sealed record RemoteRunTokenValue(
    string Status,
    long? Value = null,
    string? UnavailableReason = null)
{
    public static RemoteRunTokenValue Available(long value) => new("Available", value);
    public static RemoteRunTokenValue Unavailable(string reason) => new("Unavailable", null, reason);
}

public sealed record RemoteRunPhaseTokens(
    string Phase,
    RemoteRunTokenValue Input,
    RemoteRunTokenValue Output,
    RemoteRunTokenValue Cached,
    RemoteRunTokenValue Total);

public sealed record RemoteRunTokenTelemetry(
    RemoteRunTokenValue Input,
    RemoteRunTokenValue Output,
    RemoteRunTokenValue Cached,
    RemoteRunTokenValue Total,
    IReadOnlyList<RemoteRunPhaseTokens> ByPhase);

public sealed record RemoteRunFault(
    string FaultId,
    string AtPhase,
    long OffsetMs,
    string Action);

public sealed record RemoteRunIncident(
    string Id,
    IReadOnlyList<RemoteRunFault> FaultSchedule);

public sealed record RemoteRunOutcome(string Expected, string Actual);

public sealed record RemoteRunAssertion(
    string AssertionId,
    bool Passed,
    IReadOnlyList<string> EvidenceRefs,
    string? Detail = null);

public sealed record RemoteRunTaskEvidence(
    string TaskKey,
    string BaseSha,
    string ResultSha,
    string ReviewedSha,
    string FinalLane);

public sealed record RemoteRunAttemptEvidence(
    string Kind,
    string AttemptId,
    long LeaseFence,
    long AuthorityEpoch);

public sealed record RemoteRunArtifactReference(
    string EvidenceId,
    string Source,
    string Uri,
    string Sha256);

public sealed record RemoteRunEvidenceAuthority(long AuthorityEpoch, long MaxLeaseFence);

public sealed record RemoteRunResult(
    string SchemaVersion,
    string ScenarioId,
    string RunId,
    long Seed,
    DateTime StartedAt,
    DateTime FinishedAt,
    long WallClockDurationMs,
    IReadOnlyList<RemoteRunComponentVersion> Components,
    RemoteRunIdentity Host,
    RemoteRunIdentity Runner,
    IReadOnlyList<RemoteRunPhaseTiming> Phases,
    RemoteRunTokenTelemetry Tokens,
    RemoteRunIncident Incident,
    RemoteRunOutcome Outcome,
    IReadOnlyList<RemoteRunAssertion> Assertions,
    IReadOnlyList<string> ChronicleLinks,
    RemoteRunTaskEvidence Task,
    IReadOnlyList<RemoteRunAttemptEvidence> Attempts,
    IReadOnlyList<RemoteRunArtifactReference> Artifacts,
    RemoteRunEvidenceAuthority EvidenceAuthority,
    DateTime CollectedAt,
    string ContentSha256);

/// <summary>
/// Final Task Server facts. These are control-plane evidence, not collector
/// guesses: task lane, attempt authority, result/review SHAs, and assertions
/// must already be settled before collection.
/// </summary>
public sealed record TaskServerRemoteRunEvidence(
    string ScenarioId,
    string RunId,
    long Seed,
    DateTime StartedAt,
    DateTime FinishedAt,
    RemoteRunTaskEvidence Task,
    IReadOnlyList<RemoteRunAttemptEvidence> Attempts,
    IReadOnlyList<RemoteRunPhaseTiming> Phases,
    RemoteRunOutcome Outcome,
    IReadOnlyList<RemoteRunAssertion> Assertions,
    IReadOnlyList<string> ChronicleLinks,
    IReadOnlyList<RemoteRunArtifactReference> Artifacts,
    RemoteRunEvidenceAuthority EvidenceAuthority);

/// <summary>
/// Runner-observed execution facts. Monotonic timings take precedence over
/// wall-clock derivation, but never over Task Server identity or authority.
/// </summary>
public sealed record RunnerRemoteRunEvidence(
    string RunId,
    RemoteRunIdentity Host,
    RemoteRunIdentity Runner,
    IReadOnlyList<RemoteRunComponentVersion> Components,
    RemoteRunIncident Incident,
    IReadOnlyList<RemoteRunPhaseTiming> Phases,
    RemoteRunTokenTelemetry Tokens,
    IReadOnlyList<RemoteRunArtifactReference> Artifacts,
    long? MonotonicWallDurationMs = null);

public enum RemoteRunResultWriteStatus
{
    Created,
    IdempotentReplay,
}
