using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.DemoReplayRunner;

/// <summary>
/// Loads and fully verifies the signed trace before the service emits anything.
/// Verification here is not the security boundary, the server's is; it exists so
/// a corrupted or swapped bundle fails at startup instead of producing a stream
/// of denials against the public instance.
/// </summary>
public static class ReplayTraceLoader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static DemoReplaySignedTrace Load(string path, string publicKeyBase64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new InvalidOperationException($"The replay trace '{path}' does not exist.");
        return Parse(File.ReadAllText(path), publicKeyBase64);
    }

    internal static DemoReplaySignedTrace Parse(string json, string publicKeyBase64)
    {
        DemoReplaySignedTrace? signed;
        try { signed = JsonSerializer.Deserialize<DemoReplaySignedTrace>(json, Json); }
        catch (JsonException ex) { throw new InvalidOperationException("The replay trace is not valid JSON.", ex); }
        if (signed?.Trace is null)
            throw new InvalidOperationException("The replay trace is empty.");
        if (!DemoReplayTraceSignature.VerifyTrace(signed, publicKeyBase64))
            throw new InvalidOperationException("The replay trace did not verify against the configured signing key.");
        return signed;
    }
}
