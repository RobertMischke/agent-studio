using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// A fixed, pre-signed sequence of simulated runner lifecycle events for the
/// public demo instance. The trace is content addressed: the release bundle
/// pins its digest, the server admits only that digest, and every frame carries
/// its own seal so a replay process that holds no private key can emit the
/// recorded scene but cannot synthesize a new one.
/// </summary>
public sealed record DemoReplayTrace(
    int SchemaVersion,
    string TraceId,
    string SceneKey,
    IReadOnlyList<string> TaskKeys,
    IReadOnlyList<DemoReplayFrame> Frames);

/// <summary>One simulated runner event at a fixed offset from the cycle start.</summary>
public sealed record DemoReplayFrame(
    long Sequence,
    int OffsetSeconds,
    string TaskKey,
    string Kind,
    string? Message = null,
    string? SessionId = null,
    string? TurnId = null,
    int? RunIndex = null,
    string? Cli = null,
    string? Model = null,
    string? ThinkingLevel = null,
    long? DurationMs = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    long? ReasoningTokens = null);

/// <summary>A detached signature over one frame, bound to its trace.</summary>
public sealed record DemoReplayFrameSeal(long Sequence, string Signature);

/// <summary>
/// The on-disk release artifact. <see cref="Digest"/> is the canonical digest of
/// <see cref="Trace"/>, <see cref="TraceSignature"/> seals the trace as a whole,
/// and <see cref="Seals"/> carries one detached signature per frame so single
/// frames stay verifiable at the ingest boundary without shipping the trace.
/// </summary>
public sealed record DemoReplaySignedTrace(
    DemoReplayTrace Trace,
    string Digest,
    string Algorithm,
    string KeyId,
    string TraceSignature,
    IReadOnlyList<DemoReplayFrameSeal> Seals);

/// <summary>The closed set of runner event kinds a replay is allowed to move.</summary>
public static class DemoReplayFrameKinds
{
    public const string SessionStarted = "session.started";
    public const string SessionCompleted = "session.completed";
    public const string TurnStarted = "turn.started";
    public const string TurnCompleted = "turn.completed";
    public const string Diagnostic = "diagnostic";

    public static readonly string[] All =
        [SessionStarted, SessionCompleted, TurnStarted, TurnCompleted, Diagnostic];

    public static bool IsValid(string? value) => value is not null && Array.IndexOf(All, value) >= 0;
}

/// <summary>
/// Canonical digest for a replay trace. Mirrors <see cref="ResultEnvelopeDigest"/>:
/// validate first, canonicalize deterministically, then hash fixed camelCase JSON.
/// </summary>
public static class DemoReplayTraceDigest
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string Compute(DemoReplayTrace trace)
    {
        Validate(trace);
        var canonical = Canonicalize(trace);
        var payload = JsonSerializer.SerializeToUtf8Bytes(canonical, Json);
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    /// <summary>Structural validation. Namespace and scene admission stay a server policy concern.</summary>
    public static void Validate(DemoReplayTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (trace.SchemaVersion != CurrentSchemaVersion)
            throw new ArgumentException($"Replay trace schema version must be {CurrentSchemaVersion}.", nameof(trace));
        if (string.IsNullOrWhiteSpace(trace.TraceId))
            throw new ArgumentException("Replay trace requires a trace id.", nameof(trace));
        if (string.IsNullOrWhiteSpace(trace.SceneKey))
            throw new ArgumentException("Replay trace requires a scene key.", nameof(trace));
        if (trace.TaskKeys is null || trace.TaskKeys.Count == 0)
            throw new ArgumentException("Replay trace requires at least one task key.", nameof(trace));
        if (trace.TaskKeys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Replay trace task keys must not be blank.", nameof(trace));
        var declared = new HashSet<string>(trace.TaskKeys.Select(key => key.Trim()), StringComparer.Ordinal);
        if (declared.Count != trace.TaskKeys.Count)
            throw new ArgumentException("Replay trace task keys must be unique.", nameof(trace));
        if (trace.Frames is null || trace.Frames.Count == 0)
            throw new ArgumentException("Replay trace requires at least one frame.", nameof(trace));

        long previousSequence = 0;
        var previousOffset = int.MinValue;
        foreach (var frame in trace.Frames)
        {
            if (frame.Sequence <= previousSequence)
                throw new ArgumentException("Replay frame sequences must increase strictly.", nameof(trace));
            if (frame.OffsetSeconds < 0 || frame.OffsetSeconds < previousOffset)
                throw new ArgumentException("Replay frame offsets must be non-negative and non-decreasing.", nameof(trace));
            if (!declared.Contains((frame.TaskKey ?? string.Empty).Trim()))
                throw new ArgumentException($"Replay frame {frame.Sequence} targets an undeclared task key.", nameof(trace));
            if (!DemoReplayFrameKinds.IsValid(frame.Kind))
                throw new ArgumentException($"Replay frame {frame.Sequence} carries an unsupported kind.", nameof(trace));
            previousSequence = frame.Sequence;
            previousOffset = frame.OffsetSeconds;
        }
    }

    private static DemoReplayTrace Canonicalize(DemoReplayTrace trace)
        => new(
            trace.SchemaVersion,
            trace.TraceId.Trim(),
            trace.SceneKey.Trim(),
            trace.TaskKeys.Select(key => key.Trim()).OrderBy(key => key, StringComparer.Ordinal).ToList(),
            trace.Frames.OrderBy(frame => frame.Sequence).Select(CanonicalizeFrame).ToList());

    internal static DemoReplayFrame CanonicalizeFrame(DemoReplayFrame frame)
        => frame with
        {
            TaskKey = frame.TaskKey.Trim(),
            Kind = frame.Kind.Trim(),
            Message = Trim(frame.Message),
            SessionId = Trim(frame.SessionId),
            TurnId = Trim(frame.TurnId),
            Cli = Trim(frame.Cli),
            Model = Trim(frame.Model),
            ThinkingLevel = Trim(frame.ThinkingLevel),
        };

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
