using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.DemoReplay;

/// <summary>The last frame this instance accepted. Absent before the first cycle.</summary>
public sealed record DemoReplayCursor(long Epoch, long Sequence);

/// <summary>Everything the admission decision depends on, already read off the wire.</summary>
public sealed record DemoReplayAdmissionRequest(
    string? TraceId,
    string? TraceDigest,
    long Epoch,
    long Sequence,
    string? TaskKey,
    string? Kind,
    bool SignatureValid);

/// <summary>Outcome of the admission decision. A denial always carries a typed code.</summary>
public readonly record struct DemoReplayAdmission(bool Admitted, string? DenialCode, string? Message)
{
    public static DemoReplayAdmission Admit() => new(true, null, null);

    public static DemoReplayAdmission Deny(string code, string message) => new(false, code, message);
}

/// <summary>
/// The one pure decision behind the public-demo replay scope. It admits a frame
/// only for the pinned trace, the pinned digest, a fixture task key, a
/// simulatable kind, a valid seal, the current replay epoch, and a strictly
/// monotonic sequence. Everything else is a typed denial. The policy has no
/// dependencies and no side effects so the matrix can be tested directly.
/// </summary>
public static class DemoReplayAdmissionPolicy
{
    public static DemoReplayAdmission Evaluate(
        DemoReplayOptions options,
        DemoReplayCursor? cursor,
        DemoReplayAdmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);

        if (!options.IsUsable)
            return DemoReplayAdmission.Deny(
                DemoReplayDenialCodes.Disabled,
                "The public-demo replay scope is not enabled on this instance.");

        if (string.IsNullOrWhiteSpace(request.TraceId)
            || string.IsNullOrWhiteSpace(request.TraceDigest)
            || string.IsNullOrWhiteSpace(request.TaskKey)
            || string.IsNullOrWhiteSpace(request.Kind)
            || request.Epoch <= 0
            || request.Sequence <= 0)
            return DemoReplayAdmission.Deny(
                DemoReplayDenialCodes.RequestInvalid,
                "A replay frame requires a trace, a digest, a task key, a kind, a positive epoch, and a positive sequence.");

        if (!string.Equals(request.TraceId.Trim(), options.TraceId, StringComparison.Ordinal))
            return DemoReplayAdmission.Deny(
                DemoReplayDenialCodes.TraceMismatch,
                "The frame does not belong to the pinned replay trace.");

        if (!DemoReplayTraceSignature.MatchesDigest(request.TraceDigest, options.TraceDigest))
            return DemoReplayAdmission.Deny(
                DemoReplayDenialCodes.DigestMismatch,
                "The frame does not carry the pinned replay trace digest.");

        if (!IsFixtureKey(request.TaskKey, options.TaskKeyPrefixes))
            return DemoReplayAdmission.Deny(
                DemoReplayDenialCodes.SceneKeyDenied,
                "Replay may only move task keys inside the pinned demo scene.");

        if (!DemoReplayFrameKinds.IsValid(request.Kind.Trim()))
            return DemoReplayAdmission.Deny(
                DemoReplayDenialCodes.KindDenied,
                "Replay may only emit simulated runner lifecycle and diagnostic events.");

        if (!request.SignatureValid)
            return DemoReplayAdmission.Deny(
                DemoReplayDenialCodes.SignatureInvalid,
                "The replay frame seal did not verify against the pinned signing key.");

        if (cursor is not null)
        {
            if (request.Epoch < cursor.Epoch)
                return DemoReplayAdmission.Deny(
                    DemoReplayDenialCodes.EpochStale,
                    "The frame belongs to a replay epoch this instance has already left behind.");
            if (request.Epoch == cursor.Epoch && request.Sequence <= cursor.Sequence)
                return DemoReplayAdmission.Deny(
                    DemoReplayDenialCodes.SequenceNotMonotonic,
                    "Replay sequences must increase strictly inside one epoch.");
        }

        return DemoReplayAdmission.Admit();
    }

    private static bool IsFixtureKey(string taskKey, IReadOnlyList<string> prefixes)
    {
        var candidate = taskKey.Trim();
        foreach (var prefix in prefixes)
        {
            if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && candidate.Length > prefix.Length)
                return true;
        }
        return false;
    }
}
