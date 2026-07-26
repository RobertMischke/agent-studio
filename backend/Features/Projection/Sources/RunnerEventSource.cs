
using System.Globalization;
using System.Text.Json;

namespace AgentStudio.Projection;

/// <summary>
/// Reads the append-only <c>logs/runner-events.jsonl</c> journal produced by
/// typed remote-runner protocol adapters. Protocol envelopes are normalized
/// into <see cref="RunnerRecordedEvent"/> records once, at the backend edge.
/// Diagnostics remain in that normalized replay for the Trace channel but are
/// intentionally excluded from the main conversation projection.
/// </summary>
public sealed class RunnerEventSource : IConversationEventSource
{
    public const string RelativePath = "logs/runner-events.jsonl";

    public string SourceKind => "runner-event";

    public Task<IReadOnlyList<RawSourceEvent>> ReadAsync(TaskInfo jobInfo, CancellationToken ct)
    {
        var records = ReadRecords(jobInfo, ct);
        var events = records
            .Where(record => !string.Equals(record.Kind, "diagnostic", StringComparison.Ordinal))
            .Select(ToProjectionEvent)
            .ToList();
        return Task.FromResult<IReadOnlyList<RawSourceEvent>>(events);
    }

    public DateTime GetSourceMTimeUtc(TaskInfo jobInfo)
    {
        var path = GetPath(jobInfo);
        if (path is null || !File.Exists(path)) return DateTime.MinValue;
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    public static IReadOnlyList<RunnerRecordedEvent> ReadRecords(TaskInfo jobInfo, CancellationToken ct = default)
    {
        var path = GetPath(jobInfo);
        return path is null ? [] : ReadRecords(path, ct);
    }

    internal static IReadOnlyList<RunnerRecordedEvent> ReadRecords(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return [];
        var events = new List<RunnerRecordedEvent>();
        try
        {
            var lineIndex = 0;
            foreach (var line in File.ReadLines(path))
            {
                ct.ThrowIfCancellationRequested();
                lineIndex++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (TryNormalize(document.RootElement, lineIndex) is { } normalized)
                        events.Add(normalized);
                }
                catch (JsonException ex)
                {
                    // A writer may be appending the final row while Studio reads.
                    // Earlier complete records remain a valid replay snapshot.
                    SilentCatch.Note(ex, "RunnerEventSource: torn runner-events row ignored.");
                }
            }
        }
        catch (IOException)
        {
            return events;
        }
        // Delivery is retry-safe by event id. Keep the first durable row so a
        // retransmit cannot create duplicate lifecycle cards or token metrics.
        return events
            .GroupBy(record => record.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static RawSourceEvent ToProjectionEvent(RunnerRecordedEvent record)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["sessionId"] = record.SessionId,
            ["turnId"] = record.TurnId,
            ["runIndex"] = record.RunIndex,
            ["cli"] = record.Cli,
            ["model"] = record.Model,
            ["thinkingLevel"] = record.ThinkingLevel,
            ["durationMs"] = record.DurationMs,
            ["inputTokens"] = record.InputTokens,
            ["outputTokens"] = record.OutputTokens,
            ["reasoningTokens"] = record.ReasoningTokens,
            ["implementationStatus"] = record.ImplementationStatus,
            ["pipelineStatus"] = record.PipelineStatus,
        };
        return new RawSourceEvent
        {
            Id = record.Id,
            Kind = record.Kind switch
            {
                "session.started" or "turn.started" => "runMarker",
                "session.completed" or "turn.completed" => "system.status",
                _ => "system.status",
            },
            SourceKind = "runner-event",
            TimestampUtc = record.Timestamp,
            BodyMarkdown = "",
            Summary = record.Kind,
            Metadata = metadata,
        };
    }

    private static RunnerRecordedEvent? TryNormalize(JsonElement envelope, int lineIndex)
    {
        if (envelope.ValueKind != JsonValueKind.Object) return null;
        var payload = NestedObject(envelope, "payload", "data") ?? ParsePayloadJson(envelope) ?? envelope;
        var rawKind = Text(envelope, "kind", "eventKind", "type")
                      ?? Text(payload, "kind", "eventKind", "type");
        var kind = NormalizeKind(rawKind);
        if (kind is null) return null;

        var timestamp = Date(envelope, "occurredAt", "timestamp", "timestampUtc", "at")
                        ?? Date(payload, "occurredAt", "timestamp", "timestampUtc", "at")
                        ?? DateTime.UnixEpoch;
        var usage = NestedObject(payload, "usage", "tokenUsage", "tokens");
        return new RunnerRecordedEvent
        {
            Id = Text(envelope, "eventId", "id") ?? Text(payload, "eventId", "id")
                 ?? $"runner:{timestamp:O}:{lineIndex}",
            Kind = kind,
            Timestamp = timestamp,
            SessionId = Text(payload, "sessionId", "session_id"),
            TurnId = Text(payload, "turnId", "turn_id"),
            RunIndex = Integer(payload, "runIndex", "runId", "turnIndex"),
            Cli = Text(payload, "cli", "cliType"),
            Model = Text(payload, "model"),
            ThinkingLevel = Text(payload, "thinkingLevel", "reasoningEffort"),
            DurationMs = Long(payload, "durationMs") ?? SecondsAsMilliseconds(payload, "durationSeconds"),
            InputTokens = Long(usage, "inputTokens", "input_tokens") ?? Long(payload, "inputTokens", "input_tokens"),
            OutputTokens = Long(usage, "outputTokens", "output_tokens") ?? Long(payload, "outputTokens", "output_tokens"),
            ReasoningTokens = Long(usage, "reasoningTokens", "reasoning_tokens") ?? Long(payload, "reasoningTokens", "reasoning_tokens"),
            Severity = Text(payload, "severity", "level"),
            Code = Text(payload, "code", "diagnosticCode"),
            Message = Text(payload, "message", "detail"),
            ImplementationStatus = Text(payload, "implementationStatus", "implementation"),
            PipelineStatus = Text(payload, "pipelineStatus", "pipeline"),
        };
    }

    public static string? NormalizeKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        var compact = new string(kind.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (compact.EndsWith("sessionstarted", StringComparison.Ordinal)) return "session.started";
        if (compact.EndsWith("sessioncompleted", StringComparison.Ordinal)) return "session.completed";
        if (compact.EndsWith("turnstarted", StringComparison.Ordinal)) return "turn.started";
        if (compact.EndsWith("turncompleted", StringComparison.Ordinal)) return "turn.completed";
        if (compact.Contains("diagnostic", StringComparison.Ordinal)
            || compact.EndsWith("warning", StringComparison.Ordinal)) return "diagnostic";
        return null;
    }

    private static JsonElement? ParsePayloadJson(JsonElement value)
    {
        var json = Text(value, "payloadJson");
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var nested = JsonDocument.Parse(json);
            return nested.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    private static JsonElement? NestedObject(JsonElement? value, params string[] names)
    {
        if (value is not { ValueKind: JsonValueKind.Object } obj) return null;
        foreach (var property in obj.EnumerateObject())
            if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind == JsonValueKind.Object)
                return property.Value;
        return null;
    }

    private static string? Text(JsonElement? value, params string[] names)
    {
        var property = Property(value, names);
        return property?.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
    }

    private static long? Long(JsonElement? value, params string[] names)
    {
        var property = Property(value, names);
        if (property is not { } number) return null;
        if (number.ValueKind == JsonValueKind.Number && number.TryGetInt64(out var direct)) return direct;
        return number.ValueKind == JsonValueKind.String
               && long.TryParse(number.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static int? Integer(JsonElement? value, params string[] names)
    {
        var number = Long(value, names);
        return number is >= int.MinValue and <= int.MaxValue ? (int)number.Value : null;
    }

    private static long? SecondsAsMilliseconds(JsonElement? value, params string[] names)
    {
        var property = Property(value, names);
        return property is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out var seconds)
            ? (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero)
            : null;
    }

    private static DateTime? Date(JsonElement? value, params string[] names)
    {
        var text = Text(value, names);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static JsonElement? Property(JsonElement? value, params string[] names)
    {
        if (value is not { ValueKind: JsonValueKind.Object } obj) return null;
        foreach (var property in obj.EnumerateObject())
            if (names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                return property.Value;
        return null;
    }

    private static string? GetPath(TaskInfo jobInfo)
        => string.IsNullOrWhiteSpace(jobInfo.FolderPath)
            ? null
            : Path.Combine(jobInfo.FolderPath, RelativePath);
}
