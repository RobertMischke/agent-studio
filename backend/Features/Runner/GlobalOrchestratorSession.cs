using System.Text;
using System.Text.Json;

namespace AgentStudio.Runner;

/// <summary>
/// Singleton orchestrator that lives above the per-project orchestrators.
/// Booted once at app start with a brief of every watched project; the
/// session id is reused across backend restarts via
/// <c>&lt;TaskRepository&gt;/.runtime/global-orchestrator-session.json</c>.
///
/// <para>
/// Why a separate role from the per-project orchestrators (which are
/// covered by ADR-0007): per-project sessions answer the question
/// "what should this single agent do next on this single task?". The
/// global orchestrator answers cross-project questions: "which project
/// should the user look at first?", "which project is starving?",
/// "is something stuck across the board?". Keeping it as its own
/// session means project context never leaks across projects (ADR-0007
/// non-goal), and the global feed has its own ledger that does not get
/// drowned by per-project chatter.
/// </para>
/// </summary>
public sealed record GlobalOrchestratorSession(
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
/// File-backed store for the singleton <see cref="GlobalOrchestratorSession"/>.
/// Persisted under the central task repository so it survives backend
/// restarts; falls back gracefully when the store directory is not
/// writable (logged, treated as "no session yet").
/// </summary>
public sealed class GlobalOrchestratorSessionStore
{
    private readonly IConfiguration _config;
    private readonly ILogger<GlobalOrchestratorSessionStore> _logger;

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    public GlobalOrchestratorSessionStore(IConfiguration config, ILogger<GlobalOrchestratorSessionStore> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the on-disk path for the global session. Lives under the
    /// configured TaskRepository so a developer with several checkouts
    /// shares the same global orchestrator across them; falls back to a
    /// temp directory when no TaskRepository is configured.
    /// </summary>
    public string ResolvePath()
    {
        var repo = _config["TaskRepository"];
        var root = string.IsNullOrWhiteSpace(repo)
            ? Path.Combine(Path.GetTempPath(), "agent-taskboard")
            : repo!;
        return Path.Combine(root, ".runtime", "global-orchestrator-session.json");
    }

    public GlobalOrchestratorSession? Read()
    {
        var path = ResolvePath();
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<GlobalOrchestratorSession>(File.ReadAllText(path), ReadOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read global-orchestrator-session.json at {Path}", path);
            return null;
        }
    }

    public bool Write(GlobalOrchestratorSession session)
    {
        try
        {
            var path = ResolvePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(session, WriteOpts), Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write global-orchestrator-session.json");
            return false;
        }
    }

    public void Clear()
    {
        try
        {
            var path = ResolvePath();
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete global session file"); }
    }

    public static GlobalOrchestratorSession AccumulateUsage(
        GlobalOrchestratorSession s,
        OrchestratorTokenUsage? usage,
        string? error)
    {
        if (usage == null)
            return s with { Calls = s.Calls + 1, LastUsedAt = DateTime.UtcNow, LastError = error };
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
}
