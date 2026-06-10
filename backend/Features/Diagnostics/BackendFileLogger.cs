using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace AgentStudio.Diagnostics;

/// <summary>
/// Knobs for the rolling backend file logger. Bound from the
/// <c>Logging:BackendFile</c> section if present; defaults are tuned so
/// <c>./api.sh start</c> produces a useful log without any extra config.
/// </summary>
public sealed class BackendFileLoggerOptions
{
    public const string SectionName = "Logging:BackendFile";

    /// <summary>Directory the rolling log files live in. Relative paths resolve against the process working directory.</summary>
    public string LogDirectory { get; set; } = "logs/backend";

    /// <summary>How many days of rolled files to keep. Older files are pruned on startup and on day rollover.</summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>Floor for what gets written to disk. Console / debug providers are unaffected.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
}

/// <summary>
/// One-line-per-event sink that owns the file handle, the day rollover,
/// and the retention sweep. Keeps every other piece of the file logger
/// trivial: <see cref="BackendFileLogger"/> formats, this writes;
/// <see cref="CrashRecorder"/> calls <see cref="WriteRaw"/> for the
/// crash trace alongside its marker file.
/// </summary>
public sealed class BackendFileLogSink : IDisposable
{
    private readonly BackendFileLoggerOptions _options;
    private readonly object _writeLock = new();
    private DateOnly _currentDay;
    private string? _currentPath;
    private bool _disposed;

    public BackendFileLogSink(IOptions<BackendFileLoggerOptions> options)
        : this(options.Value) { }

    public BackendFileLogSink(BackendFileLoggerOptions options)
    {
        _options = options;
        EnsureDirectory();
        Roll(DateTime.UtcNow);
        PruneOldFiles();
    }

    public bool IsEnabled(LogLevel level) =>
        level != LogLevel.None && level >= _options.MinimumLevel;

    /// <summary>Resolved absolute directory the sink writes into. Useful for diagnostics endpoints.</summary>
    public string ResolvedDirectory => Path.GetFullPath(_options.LogDirectory);

    /// <summary>Path of the file currently being appended to. Changes on day rollover.</summary>
    public string CurrentLogPath
    {
        get
        {
            lock (_writeLock)
            {
                Roll(DateTime.UtcNow);
                return _currentPath!;
            }
        }
    }

    /// <summary>
    /// Format and write one structured log event. Multi-line messages
    /// are folded onto a single line; an exception, when present, is
    /// appended as an indented follow-up block so a crash trace stays
    /// grep-able alongside the headline.
    /// </summary>
    public void Write(LogLevel level, string category, string message, Exception? exception, IReadOnlyDictionary<string, string?>? fields = null)
    {
        if (!IsEnabled(level)) return;

        var ts = DateTime.UtcNow;
        var sb = new StringBuilder(256);
        sb.Append(ts.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        sb.Append(' ').Append(LevelTag(level));
        sb.Append(' ').Append(category);
        sb.Append(' ').Append(LogRedactor.Scrub(FoldNewlines(message)));

        AppendField(sb, "traceId", ResolveTraceId());
        if (fields != null)
        {
            foreach (var kvp in fields)
                AppendField(sb, kvp.Key, kvp.Value);
        }

        if (exception != null)
        {
            sb.Append(Environment.NewLine);
            sb.Append("    ");
            sb.Append(LogRedactor.Scrub(FormatException(exception)).Replace("\n", "\n    "));
        }

        WriteRaw(sb.ToString(), ts);
    }

    /// <summary>
    /// Append a pre-formatted line. Used by the crash recorder so the
    /// stack frame block lines up with whatever Serilog-style format the
    /// rest of the logger produces.
    /// </summary>
    public void WriteRaw(string line, DateTime? timestamp = null)
    {
        if (_disposed) return;
        lock (_writeLock)
        {
            Roll(timestamp ?? DateTime.UtcNow);
            try
            {
                File.AppendAllText(_currentPath!, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // Logging itself must never crash the process. Report via
                // Console.Error rather than the logging pipeline (this IS the
                // file sink; routing through Serilog/ILogger could recurse).
                Console.Error.WriteLine($"[BackendFileLogger] WriteRaw failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void Roll(DateTime utcNow)
    {
        var day = DateOnly.FromDateTime(utcNow);
        if (_currentPath != null && day == _currentDay) return;
        _currentDay = day;
        _currentPath = Path.Combine(ResolvedDirectory, $"{day:yyyy-MM-dd}.log");
        EnsureDirectory();
        if (_currentPath != null && File.Exists(_currentPath) is false)
        {
            // Touch so the file appears even before the first write succeeds.
            try { File.WriteAllBytes(_currentPath, Array.Empty<byte>()); }
            catch (Exception ex) { Console.Error.WriteLine($"[BackendFileLogger] touch failed: {ex.Message}"); }
        }
        // Day rollover is the natural time to also reap old files.
        if (_currentPath != null) PruneOldFiles();
    }

    private void EnsureDirectory()
    {
        try { Directory.CreateDirectory(ResolvedDirectory); }
        catch (Exception ex) { Console.Error.WriteLine($"[BackendFileLogger] EnsureDirectory failed: {ex.Message}"); }
    }

    private void PruneOldFiles()
    {
        if (_options.RetentionDays <= 0) return;
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-_options.RetentionDays);
            foreach (var file in Directory.EnumerateFiles(ResolvedDirectory, "*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!DateOnly.TryParseExact(name, "yyyy-MM-dd", out var day)) continue;
                if (day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) < cutoff)
                {
                    try { File.Delete(file); }
                    catch (Exception ex) { Console.Error.WriteLine($"[BackendFileLogger] prune delete failed for {file}: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[BackendFileLogger] PruneOldFiles failed: {ex.Message}"); }
    }

    private static void AppendField(StringBuilder sb, string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        sb.Append(' ').Append(key).Append('=').Append(value);
    }

    private static string ResolveTraceId()
    {
        var activity = Activity.Current;
        if (activity == null) return string.Empty;
        var traceId = activity.TraceId.ToString();
        return string.IsNullOrEmpty(traceId) || traceId == "00000000000000000000000000000000" ? string.Empty : traceId;
    }

    private static string FoldNewlines(string message) =>
        string.IsNullOrEmpty(message) ? string.Empty : message.Replace("\r\n", " ").Replace("\n", " ").TrimEnd();

    private static string FormatException(Exception ex)
    {
        if (ex is AggregateException agg)
        {
            ex = agg.Flatten();
        }
        return ex.ToString();
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => "INFO ",
    };
}

/// <summary>
/// <see cref="ILoggerProvider"/> that hands out one cached
/// <see cref="BackendFileLogger"/> per category and forwards every event
/// to the shared <see cref="BackendFileLogSink"/>. Stateless, so the
/// host can pin it as a singleton without thinking about lifetimes.
/// </summary>
public sealed class BackendFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly BackendFileLogSink _sink;
    private readonly ConcurrentDictionary<string, BackendFileLogger> _loggers = new();
    private IExternalScopeProvider _scopeProvider = NullScopeProvider.Instance;

    public BackendFileLoggerProvider(BackendFileLogSink sink) { _sink = sink; }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, c => new BackendFileLogger(c, _sink, () => _scopeProvider));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
        _scopeProvider = scopeProvider ?? NullScopeProvider.Instance;

    public void Dispose() => _sink.Dispose();

    private sealed class NullScopeProvider : IExternalScopeProvider
    {
        public static readonly NullScopeProvider Instance = new();
        public void ForEachScope<TState>(Action<object?, TState> callback, TState state) { }
        public IDisposable Push(object? state) => NoopDisposable.Instance;
        private sealed class NoopDisposable : IDisposable { public static readonly NoopDisposable Instance = new(); public void Dispose() { } }
    }
}

/// <summary>
/// Per-category <see cref="ILogger"/>. Pulls scope state once per event
/// so a caller-set <c>BeginScope(new { project, jobId })</c> shows up as
/// trailing <c>project=...</c> / <c>jobId=...</c> fields without forcing
/// every call site to pass them explicitly.
/// </summary>
public sealed class BackendFileLogger : ILogger
{
    private static readonly string[] InterestingScopeKeys = { "project", "jobId", "Project", "JobId" };

    private readonly string _category;
    private readonly BackendFileLogSink _sink;
    private readonly Func<IExternalScopeProvider> _scopeProvider;

    public BackendFileLogger(string category, BackendFileLogSink sink, Func<IExternalScopeProvider> scopeProvider)
    {
        _category = category;
        _sink = sink;
        _scopeProvider = scopeProvider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel level) => _sink.IsEnabled(level);

    public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;
        if (formatter == null) return;
        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception == null) return;

        var fields = CollectFields(state);
        _sink.Write(level, _category, message ?? string.Empty, exception, fields);
    }

    private Dictionary<string, string?>? CollectFields<TState>(TState state)
    {
        Dictionary<string, string?>? fields = null;

        // Scope-provided fields (e.g. middleware-injected).
        _scopeProvider().ForEachScope((scope, acc) =>
        {
            if (scope is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                foreach (var kvp in kvps)
                {
                    if (Array.IndexOf(InterestingScopeKeys, kvp.Key) < 0) continue;
                    var key = kvp.Key.Length > 0 ? char.ToLowerInvariant(kvp.Key[0]) + kvp.Key[1..] : kvp.Key;
                    acc[key] = kvp.Value?.ToString();
                }
            }
        }, fields ??= new Dictionary<string, string?>(StringComparer.Ordinal));

        // State-provided fields (structured log: logger.LogInformation("hi {project}", "demo")).
        if (state is IEnumerable<KeyValuePair<string, object?>> stateKvps)
        {
            foreach (var kvp in stateKvps)
            {
                if (Array.IndexOf(InterestingScopeKeys, kvp.Key) < 0) continue;
                var key = kvp.Key.Length > 0 ? char.ToLowerInvariant(kvp.Key[0]) + kvp.Key[1..] : kvp.Key;
                (fields ??= new Dictionary<string, string?>(StringComparer.Ordinal))[key] = kvp.Value?.ToString();
            }
        }

        return fields is { Count: > 0 } ? fields : null;
    }
}

/// <summary>
/// Centralised redaction for anything heading to a backend log line. The
/// product writes plain ASP.NET / hosted-service logs (no chat content)
/// to disk, so the threat surface here is bearer tokens and provider
/// keys leaking through an exception or HttpClient log message.
/// </summary>
public static class LogRedactor
{
    private static readonly Regex Bearer = new(@"Bearer\s+[A-Za-z0-9._\-+/=]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AnthropicKey = new(@"sk-ant-[A-Za-z0-9_\-]+", RegexOptions.Compiled);
    private static readonly Regex OpenAiKey = new(@"sk-[A-Za-z0-9]{20,}", RegexOptions.Compiled);
    private static readonly Regex GitHubToken = new(@"gh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.Compiled);
    private static readonly Regex SessionId = new(@"sessionId=[A-Za-z0-9\-]{8,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Scrub(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        var s = input;
        s = Bearer.Replace(s, "Bearer [REDACTED]");
        s = AnthropicKey.Replace(s, "sk-ant-[REDACTED]");
        s = OpenAiKey.Replace(s, "sk-[REDACTED]");
        s = GitHubToken.Replace(s, "gh*_[REDACTED]");
        s = SessionId.Replace(s, "sessionId=[REDACTED]");
        return s;
    }
}
