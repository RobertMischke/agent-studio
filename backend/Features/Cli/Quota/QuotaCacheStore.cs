using System.Text;
using System.Text.Json;

namespace AgentStudio.Cli;

/// <summary>
/// File-backed snapshot store for <see cref="QuotaService"/>'s in-memory
/// cache. Lives at <c>&lt;TaskRepository&gt;/.runtime/cli-quota-last-good.json</c>
/// (or under the application's LocalApplicationData directory when no
/// TaskRepository is configured) so that a backend restart does not
/// leave the header empty until the first probe completes - which can
/// take 30+ seconds per CLI.
///
/// <para>
/// Stored format: a flat list of <see cref="QuotaSnapshot"/> records, one
/// per CLI. Cheap to read on startup, cheap to overwrite after each
/// successful probe. Tolerant to corruption: a malformed file is logged
/// and ignored, the in-memory cache simply starts empty.
/// </para>
/// </summary>
public sealed class QuotaCacheStore
{
    private readonly ILogger<QuotaCacheStore> _logger;
    private readonly string _path;
    private readonly string _legacyPath;
    private readonly object _writeLock = new();

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

    public QuotaCacheStore(IConfiguration config, ILogger<QuotaCacheStore> logger)
    {
        _logger = logger;
        var taskRepo = config["TaskRepository"];
        var baseDir = !string.IsNullOrWhiteSpace(taskRepo)
            ? Path.Combine(taskRepo, ".runtime")
            : ResolveLocalStateDirectory();
        try { Directory.CreateDirectory(baseDir); } catch (Exception __ex) { SilentCatch.Note(__ex, "QuotaCacheStore: best-effort"); /* best-effort */ }
        _path = Path.Combine(baseDir, "cli-quota-last-good.json");
        _legacyPath = Path.Combine(baseDir, "quota-cache.json");
    }

    /// <summary>
    /// Read the persisted snapshots. Returns an empty list when the file
    /// does not exist or fails to parse.
    /// </summary>
    public List<QuotaSnapshot> Read()
    {
        var readPath = File.Exists(_path) ? _path : _legacyPath;
        if (!File.Exists(readPath)) return new List<QuotaSnapshot>();
        try
        {
            var raw = File.ReadAllText(readPath);
            return JsonSerializer.Deserialize<List<QuotaSnapshot>>(raw, ReadOpts)
                   ?? new List<QuotaSnapshot>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read quota cache at {Path}", readPath);
            return new List<QuotaSnapshot>();
        }
    }

    /// <summary>
    /// Persist the snapshots atomically (write to a temp file, then rename)
    /// so a crash mid-write never leaves a half-written file.
    /// </summary>
    public void Write(IEnumerable<QuotaSnapshot> snapshots)
    {
        try
        {
            var json = JsonSerializer.Serialize(snapshots.ToList(), WriteOpts);
            lock (_writeLock)
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, _path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist quota cache to {Path}", _path);
        }
    }

    private static string ResolveLocalStateDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(local)
            ? Path.Combine(AppContext.BaseDirectory, "runtime")
            : Path.Combine(local, "agent-taskboard");
    }
}
