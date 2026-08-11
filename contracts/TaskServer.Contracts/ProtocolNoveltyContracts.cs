using System.Text.Json;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Scrubbed telemetry for a provider frame that the installed CLI adapter could
/// not classify. Raw provider content remains subject to the existing runner
/// log policy; the novelty marker carries only protocol identity, counters,
/// and a content hash.
/// </summary>
public sealed record ProtocolNoveltyTelemetry(
    string Cli,
    string AdapterVersion,
    string FrameType,
    long Occurrence,
    long TotalUnknownFrames,
    string PayloadSha256)
{
    public const string MarkerPrefix = "[runner-protocol-unknown-frame] ";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToMarker()
        => MarkerPrefix + JsonSerializer.Serialize(this, Json);

    public static bool TryParseMarker(string? text, out ProtocolNoveltyTelemetry telemetry)
    {
        telemetry = null!;
        if (string.IsNullOrWhiteSpace(text)
            || !text.StartsWith(MarkerPrefix, StringComparison.Ordinal))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<ProtocolNoveltyTelemetry>(
                text[MarkerPrefix.Length..],
                Json);
            if (parsed is null
                || string.IsNullOrWhiteSpace(parsed.Cli)
                || string.IsNullOrWhiteSpace(parsed.AdapterVersion)
                || string.IsNullOrWhiteSpace(parsed.FrameType)
                || parsed.Occurrence < 1
                || parsed.TotalUnknownFrames < parsed.Occurrence
                || parsed.PayloadSha256.Length != 64)
                return false;

            telemetry = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public string ToDiagnosticMessage()
        => $"Unknown {Cli} frame '{FrameType}' observed " +
           $"(type occurrence {Occurrence}, run total {TotalUnknownFrames}, adapter {AdapterVersion}, " +
           $"payload sha256 {PayloadSha256}).";
}
