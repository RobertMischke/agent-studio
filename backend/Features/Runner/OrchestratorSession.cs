using System.Text;
using System.Text.Json;

namespace AgentStudio.Runner;

/// <summary>
/// Per-project orchestrator session: a long-lived Claude session UUID
/// the orchestrator boots once at app start and resumes for every later
/// decision via <c>claude -r &lt;sessionId&gt;</c>. Lives on disk so it
/// survives backend restarts (no re-boot cost when nothing changed).
///
/// <para>
/// This is the data half of ADR-0007: orchestrator decisions share a
/// warm context so the user can ask "what have you read?" and get a
/// real answer, instead of an opaque one-shot LLM call. The session
/// also accumulates token totals, which gives a single ledger for the
/// orchestrator's spend on this project.
/// </para>
/// </summary>
public sealed record OrchestratorSession(
    string SessionId,
    string Model,
    DateTime BootedAt,
    string BootPromptPreview,
    string BootReplyPreview,
    long CumulativeInputTokens,
    long CumulativeOutputTokens,
    long CumulativeCacheReadTokens,
    long CumulativeCacheCreationTokens,
    int Calls,
    DateTime LastUsedAt,
    string? LastError);

/// <summary>
/// File-backed store for one <see cref="OrchestratorSession"/> per
/// watched project. Persisted at
/// <c>&lt;watchPath&gt;/.orchestrator/orchestrator-session.json</c>.
/// </summary>
public class OrchestratorSessionStore
{
    private readonly ILogger<OrchestratorSessionStore> _logger;

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OrchestratorSessionStore(ILogger<OrchestratorSessionStore> logger)
    {
        _logger = logger;
    }

    public OrchestratorSession? Read(string watchPath)
    {
        if (string.IsNullOrWhiteSpace(watchPath)) return null;
        var path = ResolvePath(watchPath);
        if (!File.Exists(path)) return null;
        try
        {
            var raw = File.ReadAllText(path);
            return JsonSerializer.Deserialize<OrchestratorSession>(raw, ReadOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read orchestrator-session.json under {WatchPath}", watchPath);
            return null;
        }
    }

    public bool Write(string watchPath, OrchestratorSession session)
    {
        if (string.IsNullOrWhiteSpace(watchPath)) return false;
        try
        {
            var path = ResolvePath(watchPath);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(session, WriteOpts), Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write orchestrator-session.json under {WatchPath}", watchPath);
            return false;
        }
    }

    /// <summary>
    /// Drops the persisted session, e.g. when the CLI reports the
    /// session id is no longer resumable. The next decision call
    /// re-boots a fresh session.
    /// </summary>
    public void Clear(string watchPath)
    {
        if (string.IsNullOrWhiteSpace(watchPath)) return;
        var path = ResolvePath(watchPath);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete {Path}", path); }
    }

    /// <summary>
    /// Adds the deltas from a single decision call onto the running
    /// totals. Returns the updated session record (caller is responsible
    /// for persisting it).
    /// </summary>
    public static OrchestratorSession AccumulateUsage(OrchestratorSession s, OrchestratorTokenUsage? usage, string? error)
    {
        if (usage == null)
        {
            return s with { Calls = s.Calls + 1, LastUsedAt = DateTime.UtcNow, LastError = error };
        }
        return s with
        {
            CumulativeInputTokens = s.CumulativeInputTokens + usage.InputTokens,
            CumulativeOutputTokens = s.CumulativeOutputTokens + usage.OutputTokens,
            CumulativeCacheReadTokens = s.CumulativeCacheReadTokens + usage.CacheReadTokens,
            CumulativeCacheCreationTokens = s.CumulativeCacheCreationTokens + usage.CacheCreationTokens,
            Calls = s.Calls + 1,
            LastUsedAt = DateTime.UtcNow,
            LastError = error
        };
    }

    private static string ResolvePath(string watchPath) =>
        Path.Combine(watchPath, ".orchestrator", "orchestrator-session.json");
}
