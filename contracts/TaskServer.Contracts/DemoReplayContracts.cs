namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// One sealed frame offered to the public-demo replay ingest scope. The server
/// pins the trace digest and public key; the caller proves nothing beyond
/// possession of pre-signed material.
/// </summary>
public sealed record DemoReplayEventRequest(
    string TraceId,
    string TraceDigest,
    long Epoch,
    string Signature,
    DemoReplayFrame Frame,
    DateTime OccurredAt);

/// <summary>Accepted replay frame. <see cref="Origin"/> is always the simulated marker.</summary>
public sealed record DemoReplayEventAccepted(
    long Epoch,
    long Sequence,
    string TaskKey,
    string Kind,
    string Origin);

/// <summary>Typed denial codes for the replay ingest scope. Every one is a hard stop.</summary>
public static class DemoReplayDenialCodes
{
    public const string Disabled = "replay-disabled";
    public const string RequestInvalid = "replay-request-invalid";
    public const string TraceMismatch = "replay-trace-mismatch";
    public const string DigestMismatch = "replay-trace-digest-mismatch";
    public const string SignatureInvalid = "replay-signature-invalid";
    public const string EpochStale = "replay-epoch-stale";
    public const string SequenceNotMonotonic = "replay-sequence-not-monotonic";
    public const string SceneKeyDenied = "replay-task-not-in-scene";
    public const string KindDenied = "replay-kind-not-simulatable";
}

/// <summary>Origin marker carried by every event this scope writes.</summary>
public static class DemoReplayOrigins
{
    public const string Simulated = "simulated";
}
