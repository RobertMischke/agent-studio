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

    /// <summary>Per-boot marker (capturedAt + pid). Overwritten on every boot; read by the next boot to bound the crash/shutdown markers to the run they belong to.</summary>
    public string StartupMarkerPath => Path.Combine(Path.GetFullPath(_options.LogDirectory), "startup.json");

    /// <summary>Graceful-shutdown marker written by the ProcessExit handler. Read here to tell a clean teardown apart from a silent death.</summary>
    public string ShutdownMarkerPath => Path.Combine(Path.GetFullPath(_options.LogDirectory), "last-shutdown.json");

    /// <summary>Written only when the previous run was classified <see cref="PreviousRunVerdict.SilentKill"/> so an operator finds the named verdict in one glance.</summary>
    public string SilentKillMarkerPath => Path.Combine(Path.GetFullPath(_options.LogDirectory), "last-silent-kill.json");

    /// <summary>
    /// Boot-time forensics: classify how the <i>previous</i> run ended by
    /// diffing this boot's view of the startup / shutdown / crash markers,
    /// log a structured verdict, persist a <c>last-silent-kill.json</c> when
    /// the previous run vanished silently, then arm a fresh
    /// <c>startup.json</c> so the <i>next</i> boot can do the same.
    ///
    /// <para>
    /// This is the only thing that can surface the silent class — a
    /// StackOverflowException, OS OOM-kill, or native PTY crash terminates the
    /// host before <c>AppDomain.UnhandledException</c> /
    /// <c>TaskScheduler.UnobservedTaskException</c> / <c>ProcessExit</c> can
    /// run, so no in-process handler ever sees it. Everything here is
    /// best-effort and fully swallowed: boot diagnostics must never block boot.
    /// </para>
    /// </summary>
    public PreviousRunReport ClassifyPreviousRunAndArm()
    {
        DateTime? prevStarted = null;
        int? prevPid = null;
        DateTime? lastShutdown = null;
        DateTime? lastCrash = null;

        // Boot diagnostics must never block boot and must not re-enter the
        // logging pipeline (this is part of it), so each best-effort step
        // reports a swallowed failure via Console.Error rather than ILogger.
        try { (prevStarted, prevPid) = ReadStartupMarker(); } catch (Exception ex) { Console.Error.WriteLine($"[CrashRecorder] ReadStartupMarker failed: {ex.Message}"); }
        try { lastShutdown = ReadCapturedAt(ShutdownMarkerPath); } catch (Exception ex) { Console.Error.WriteLine($"[CrashRecorder] ReadCapturedAt(shutdown) failed: {ex.Message}"); }
        try { lastCrash = ReadCapturedAt(MarkerPath); } catch (Exception ex) { Console.Error.WriteLine($"[CrashRecorder] ReadCapturedAt(crash) failed: {ex.Message}"); }

        var verdict = CrashForensics.Classify(prevStarted, lastShutdown, lastCrash);
        var report = new PreviousRunReport(verdict, prevStarted, prevPid, lastShutdown, lastCrash);

        try { LogVerdict(report); } catch (Exception ex) { Console.Error.WriteLine($"[CrashRecorder] LogVerdict failed: {ex.Message}"); }
        if (verdict == PreviousRunVerdict.SilentKill)
        {
            try { WriteSilentKillMarker(report); } catch (Exception ex) { Console.Error.WriteLine($"[CrashRecorder] WriteSilentKillMarker failed: {ex.Message}"); }
        }
        try { ArmStartupMarker(); } catch (Exception ex) { Console.Error.WriteLine($"[CrashRecorder] ArmStartupMarker failed: {ex.Message}"); }

        return report;
    }

    private (DateTime?, int?) ReadStartupMarker()
    {
        if (!File.Exists(StartupMarkerPath)) return (null, null);
        using var doc = JsonDocument.Parse(File.ReadAllText(StartupMarkerPath));
        var root = doc.RootElement;
        DateTime? started = root.TryGetProperty("capturedAt", out var ca)
            && DateTime.TryParse(ca.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
                ? t : null;
        int? pid = root.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pv) ? pv : null;
        return (started, pid);
    }

    private static DateTime? ReadCapturedAt(string path)
    {
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty("capturedAt", out var ca)
            && DateTime.TryParse(ca.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
                ? t : null;
    }

    private void ArmStartupMarker()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StartupMarkerPath)!);
        var json = JsonSerializer.Serialize(new
        {
            capturedAt = DateTime.UtcNow.ToString("O"),
            pid = Environment.ProcessId,
        }, JsonOptions);
        lock (_writeLock) File.WriteAllText(StartupMarkerPath, json, Encoding.UTF8);
    }

    private void WriteSilentKillMarker(PreviousRunReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SilentKillMarkerPath)!);
        var json = JsonSerializer.Serialize(new
        {
            capturedAt = DateTime.UtcNow.ToString("O"),
            verdict = report.Verdict.ToString(),
            previousPid = report.PreviousPid,
            previousStartedAt = report.PreviousStartedAt?.ToString("O"),
            note = "Previous backend run ended without a shutdown or crash marker. "
                 + "Likely native crash / OOM / StackOverflow / external kill — no in-process handler witnessed it.",
        }, JsonOptions);
        lock (_writeLock) File.WriteAllText(SilentKillMarkerPath, json, Encoding.UTF8);
    }

    private void LogVerdict(PreviousRunReport report)
    {
        var (level, headline) = report.Verdict switch
        {
            PreviousRunVerdict.SilentKill => ("FATAL",
                "previous backend run died SILENTLY (no shutdown or crash marker) — likely native crash / OOM / StackOverflow / external kill"),
            PreviousRunVerdict.ManagedCrash => ("ERROR",
                "previous backend run ended on a managed crash (last-crash.json present, no shutdown marker)"),
            PreviousRunVerdict.GracefulShutdown => ("INFO ",
                "previous backend run shut down gracefully"),
            _ => ("INFO ", "no previous-run marker found (first boot in this log directory)"),
        };

        var sb = new StringBuilder();
        sb.Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        sb.Append(' ').Append(level);
        sb.Append(' ').Append("Backend.Startup");
        sb.Append(" [").Append(report.Verdict).Append("] ").Append(headline);
        if (report.PreviousPid is int pid) sb.Append(" previousPid=").Append(pid);
        if (report.PreviousStartedAt is DateTime s) sb.Append(" previousStartedAt=").Append(s.ToString("O"));
        if (report.LastCrashAt is DateTime c) sb.Append(" lastCrashAt=").Append(c.ToString("O"));
        if (report.LastShutdownAt is DateTime d) sb.Append(" lastShutdownAt=").Append(d.ToString("O"));
        _sink.WriteRaw(sb.ToString());
    }

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
        catch (Exception logEx)
        {
            // Logging the crash must never crash. Marker still gets a chance
            // below. Report via Console.Error, not the logging pipeline.
            Console.Error.WriteLine($"[CrashRecorder] WriteLogEntry failed for {source}: {logEx.Message}");
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
        catch (Exception ex)
        {
            // Same reason as above: this path runs from a process-wide handler,
            // so report via Console.Error rather than the logging pipeline.
            Console.Error.WriteLine($"[CrashRecorder] WriteMarker failed: {ex.Message}");
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
