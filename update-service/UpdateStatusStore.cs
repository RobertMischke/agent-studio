using System.Text.Json;

namespace AgentTaskboard.UpdateService;

/// <summary>
/// Single source of truth for what /update/status returns. All public mutation
/// goes through SetPhase / AppendHistory; reads are lock-free snapshots so the
/// HTTP endpoint never blocks on an in-progress orchestration.
/// </summary>
public sealed class UpdateStatusStore
{
    private readonly object _lock = new();
    private readonly string _historyFile;
    private readonly ILogger<UpdateStatusStore> _logger;
    private readonly string _version;

    private UpdateStatus _status;
    private DateTime? _lastFetchAt;
    private string? _headOrigin;
    private int _behindBy;
    private bool _backendReachable;

    public UpdateStatusStore(string historyFile, string headLocal, ILogger<UpdateStatusStore> logger)
    {
        _historyFile = historyFile;
        _logger = logger;
        _version = typeof(UpdateStatusStore).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        _status = new UpdateStatus(
            Phase: "idle",
            Message: null,
            CurrentRunId: null,
            StartedAt: null,
            FinishedAt: null,
            HeadLocal: headLocal,
            HeadOrigin: null,
            BehindBy: 0,
            LastFetchAt: null,
            LastUpdateAt: null,
            LastSuccessAt: HydrateLastSuccess(),
            IsRunning: false,
            BackendReachable: false,
            Version: _version,
            Mode: "manual"
        );
    }

    public UpdateStatus Get()
    {
        lock (_lock) return _status;
    }

    public void SetHead(string headLocal)
    {
        lock (_lock)
        {
            _status = _status with { HeadLocal = headLocal };
        }
    }

    public void SetFetchResult(string headOrigin, int behindBy)
    {
        lock (_lock)
        {
            _lastFetchAt = DateTime.UtcNow;
            _headOrigin = headOrigin;
            _behindBy = behindBy;
            _status = _status with { HeadOrigin = headOrigin, BehindBy = behindBy, LastFetchAt = _lastFetchAt };
        }
    }

    public void SetBackendReachable(bool reachable)
    {
        lock (_lock)
        {
            if (_backendReachable == reachable) return;
            _backendReachable = reachable;
            _status = _status with { BackendReachable = reachable };
        }
    }

    public void SetPhase(string phase, string? message, string? runId, DateTime? startedAt, DateTime? finishedAt = null, DateTime? lastSuccessAt = null)
    {
        lock (_lock)
        {
            var running = phase != "idle" && phase != "done" && phase != "failed";
            _status = _status with
            {
                Phase = phase,
                Message = message,
                CurrentRunId = runId,
                StartedAt = startedAt,
                FinishedAt = finishedAt ?? _status.FinishedAt,
                IsRunning = running,
                LastUpdateAt = (phase == "done" || phase == "failed") ? DateTime.UtcNow : _status.LastUpdateAt,
                LastSuccessAt = lastSuccessAt ?? _status.LastSuccessAt,
            };
        }
    }

    public void AppendHistory(UpdateHistoryEntry entry)
    {
        var line = JsonSerializer.Serialize(entry);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_historyFile)!);
            File.AppendAllText(_historyFile, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "history append failed");
        }
    }

    public IReadOnlyList<UpdateHistoryEntry> ReadHistory(int max)
    {
        try
        {
            if (!File.Exists(_historyFile)) return Array.Empty<UpdateHistoryEntry>();
            var lines = File.ReadAllLines(_historyFile);
            var take = Math.Min(lines.Length, Math.Max(1, max));
            var slice = lines[^take..];
            var list = new List<UpdateHistoryEntry>(slice.Length);
            foreach (var l in slice)
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<UpdateHistoryEntry>(l);
                    if (entry is not null) list.Add(entry);
                }
                catch (JsonException) { /* skip malformed line */ }
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "history read failed");
            return Array.Empty<UpdateHistoryEntry>();
        }
    }

    private DateTime? HydrateLastSuccess()
    {
        if (!File.Exists(_historyFile)) return null;
        try
        {
            string? lastOk = null;
            foreach (var line in File.ReadAllLines(_historyFile))
            {
                if (line.Contains("\"Status\":\"ok\"") || line.Contains("\"status\":\"ok\""))
                    lastOk = line;
            }
            if (lastOk == null) return null;
            var entry = JsonSerializer.Deserialize<UpdateHistoryEntry>(lastOk);
            return entry?.FinishedAt;
        }
        catch
        {
            return null;
        }
    }
}
