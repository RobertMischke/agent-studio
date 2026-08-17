using System.Security.Cryptography;
using System.Text.Json;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Detached ECDSA P-256 signatures over a replay trace and over each of its
/// frames. The private key belongs to the release bundle build, never to the
/// replay service: the demo runtime holds only pre-signed material, so a
/// compromised replay process can re-emit the recorded scene but cannot mint a
/// frame the server would accept.
/// </summary>
public static class DemoReplayTraceSignature
{
    public const string Algorithm = "ecdsa-p256-sha256";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>Canonical bytes sealed for a single frame, bound to its trace identity and digest.</summary>
    public static byte[] FramePayload(string traceId, string digest, DemoReplayFrame frame)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        ArgumentNullException.ThrowIfNull(frame);
        var sealed_ = new SealedFrame(
            traceId.Trim(),
            digest.Trim().ToLowerInvariant(),
            DemoReplayTraceDigest.CanonicalizeFrame(frame));
        return JsonSerializer.SerializeToUtf8Bytes(sealed_, Json);
    }

    /// <summary>Seals a validated trace. Used by release tooling and by tests, never by the demo runtime.</summary>
    public static DemoReplaySignedTrace Sign(DemoReplayTrace trace, ECDsa privateKey, string keyId)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        var digest = DemoReplayTraceDigest.Compute(trace);
        var traceSignature = Convert.ToBase64String(
            privateKey.SignData(Convert.FromHexString(digest), HashAlgorithmName.SHA256));
        var seals = trace.Frames
            .OrderBy(frame => frame.Sequence)
            .Select(frame => new DemoReplayFrameSeal(
                frame.Sequence,
                Convert.ToBase64String(privateKey.SignData(
                    FramePayload(trace.TraceId, digest, frame), HashAlgorithmName.SHA256))))
            .ToList();
        return new DemoReplaySignedTrace(trace, digest, Algorithm, keyId.Trim(), traceSignature, seals);
    }

    /// <summary>
    /// Full verification of a signed trace: recomputed digest, trace seal, and a
    /// seal for every frame. Fails closed on any malformed input.
    /// </summary>
    public static bool VerifyTrace(DemoReplaySignedTrace signed, string publicKeyBase64)
    {
        if (signed is null) return false;
        if (!string.Equals(signed.Algorithm, Algorithm, StringComparison.Ordinal)) return false;
        string digest;
        try { digest = DemoReplayTraceDigest.Compute(signed.Trace); }
        catch (ArgumentException) { return false; }
        if (!MatchesDigest(digest, signed.Digest)) return false;
        if (!TryImport(publicKeyBase64, out var key) || key is null) return false;
        using (key)
        {
            if (!VerifyBytes(key, Convert.FromHexString(digest), signed.TraceSignature)) return false;
            if (signed.Seals is null || signed.Seals.Count != signed.Trace.Frames.Count) return false;
            var bySequence = signed.Seals.ToDictionary(seal => seal.Sequence);
            foreach (var frame in signed.Trace.Frames)
            {
                if (!bySequence.TryGetValue(frame.Sequence, out var seal)) return false;
                if (!VerifyBytes(key, FramePayload(signed.Trace.TraceId, digest, frame), seal.Signature)) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Boundary verification of one frame against the pinned digest. This is what
    /// the ingest endpoint runs; it never needs the rest of the trace.
    /// </summary>
    public static bool VerifyFrame(
        string traceId,
        string digest,
        DemoReplayFrame frame,
        string? signature,
        string publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(traceId) || string.IsNullOrWhiteSpace(digest) || frame is null) return false;
        if (!TryImport(publicKeyBase64, out var key) || key is null) return false;
        using (key)
        {
            return VerifyBytes(key, FramePayload(traceId, digest, frame), signature);
        }
    }

    public static bool MatchesDigest(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        var a = TryHex(left);
        var b = TryHex(right);
        return a.Length > 0 && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static byte[] TryHex(string value)
    {
        try { return Convert.FromHexString(value.Trim()); }
        catch (FormatException) { return []; }
    }

    private static bool VerifyBytes(ECDsa key, byte[] payload, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;
        byte[] raw;
        try { raw = Convert.FromBase64String(signature.Trim()); }
        catch (FormatException) { return false; }
        return key.VerifyData(payload, raw, HashAlgorithmName.SHA256);
    }

    private static bool TryImport(string? publicKeyBase64, out ECDsa? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(publicKeyBase64)) return false;
        var candidate = ECDsa.Create();
        try
        {
            candidate.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64.Trim()), out _);
            key = candidate;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            candidate.Dispose();
            return false;
        }
    }

    private sealed record SealedFrame(string TraceId, string Digest, DemoReplayFrame Frame);
}
