using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace OrchestratorApi.Services.Diagnostics;

/// <summary>
/// Records a fatal exception to two surfaces simultaneously: a full
/// structured trace in the rolling backend log, and a small
/// <c>last-crash.json</c> marker that an external supervisor or the
/// Layer 3 system review can read without parsing log files.
///
/// <para>
/// The marker is intentionally tiny (timestamp, type, message, top
/// frame, source) so a crashed-and-restarted backend can serve it via
/// <c>/api/diagnostics/last-crash</c> without re-running the failed
/// request. The full multi-line stack lives in the daily log.
/// </para>
/// </summary>
public sealed class CrashRecorder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly BackendFileLoggerOptions _options;
    private readonly BackendFileLogSink _sink;
    private readonly object _writeLock = new();

    public CrashRecorder(IOptions<BackendFileLoggerOptions> options, BackendFileLogSink sink)
        : this(options.Value, sink) { }

    public CrashRecorder(BackendFileLoggerOptions options, BackendFileLogSink sink)
    {
        _options = options;
        _sink = sink;
    }

    /// <summary>Absolute path of the marker file. Stable across restarts so the diagnostics endpoint can read it.</summary>
    public string MarkerPath => Path.Combine(Path.GetFullPath(_options.LogDirectory), "last-crash.json");

    /// <summary>
    /// Persist a crash. <paramref name="source"/> identifies which
    /// handler captured it (e.g. <c>UnobservedTaskException</c>) and is
    /// included verbatim in the marker so the next operator knows
    /// whether the process was killed or just lost a fire-and-forget
    /// task.
    /// </summary>
    public CrashRecord Record(string source, Exception exception, bool isTerminating = false)
    {
        // Flatten so nested AggregateExceptions become a single layer in
        // the on-disk trace. The marker still reports the *inner* type
        // because "AggregateException" alone tells the operator nothing.
        var flattened = exception is AggregateException agg ? agg.Flatten() : exception;
        var rootCause = ResolveRootCause(flattened);
        var record = BuildRecord(source, rootCause, flattened, exception, isTerminating);

        WriteLogEntry(source, flattened, isTerminating);
        WriteMarker(record);

        return record;
    }

    private static Exception ResolveRootCause(Exception ex)
    {
        if (ex is AggregateException agg && agg.InnerExceptions.Count > 0)
        {
            return agg.InnerExceptions[0];
        }
        return ex;
    }

    private void WriteLogEntry(string source, Exception ex, bool isTerminating)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            sb.Append(' ').Append(isTerminating ? "FATAL" : "ERROR");
            sb.Append(' ').Append("Backend.Crash");
            sb.Append(' ').Append('[').Append(source).Append(']');
            sb.Append(' ').Append(ex.GetType().FullName).Append(": ").Append(LogRedactor.Scrub(SingleLine(ex.Message)));
            sb.Append(" terminating=").Append(isTerminating ? "true" : "false");
            sb.Append(Environment.NewLine);
            sb.Append("    ").Append(LogRedactor.Scrub(ex.ToString()).Replace("\n", "\n    "));
            _sink.WriteRaw(sb.ToString());
        }
        catch
        {
            // Logging the crash must never crash. Marker still gets a chance below.
        }
    }

    private void WriteMarker(CrashRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            lock (_writeLock)
            {
                var json = JsonSerializer.Serialize(record, JsonOptions);
                File.WriteAllText(MarkerPath, json, Encoding.UTF8);
            }
        }
        catch
        {
            // Same reason as above: this path runs from a process-wide handler.
        }
    }

    private static CrashRecord BuildRecord(string source, Exception rootCause, Exception flattened, Exception original, bool isTerminating)
    {
        // Most useful frame: prefer the inner exception's, then the
        // original (pre-flatten) trace, then the flattened trace.
        // Flatten() returns a fresh AggregateException without a stack,
        // so it usually loses on the lookup; the original wins when the
        // outer agg was the only thing that was actually thrown.
        var topFrame = FirstStackFrame(rootCause) ?? FirstStackFrame(original) ?? FirstStackFrame(flattened);
        return new CrashRecord
        {
            CapturedAt = DateTime.UtcNow,
            Source = source,
            ExceptionType = rootCause.GetType().FullName ?? rootCause.GetType().Name,
            Message = LogRedactor.Scrub(SingleLine(rootCause.Message)),
            TopFrame = LogRedactor.Scrub(topFrame),
            IsTerminating = isTerminating,
        };
    }

    private static string SingleLine(string? message) =>
        string.IsNullOrEmpty(message) ? string.Empty : message.Replace("\r\n", " ").Replace("\n", " ").Trim();

    private static string? FirstStackFrame(Exception ex)
    {
        var stack = ex.StackTrace;
        if (string.IsNullOrEmpty(stack)) return null;
        using var reader = new StringReader(stack);
        var line = reader.ReadLine();
        return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
    }
}

/// <summary>
/// Serialised shape of <c>last-crash.json</c>. Kept narrow on purpose:
/// any caller that wants the full stack should read the daily log file.
/// </summary>
public sealed record CrashRecord
{
    [JsonPropertyName("capturedAt")] public DateTime CapturedAt { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = string.Empty;
    [JsonPropertyName("exceptionType")] public string ExceptionType { get; init; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    [JsonPropertyName("topFrame")] public string? TopFrame { get; init; }
    [JsonPropertyName("isTerminating")] public bool IsTerminating { get; init; }
}
