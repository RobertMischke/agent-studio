using System.Text;
using System.Text.Json;

namespace AgentStudio.Cli;

/// <summary>
/// File-backed snapshot store for <see cref="QuotaService"/>'s in-memory
/// cache. Lives at <c>&lt;LocalApplicationData&gt;/agent-taskboard/cli-quota-last-good.json</c>
/// unless <c>Quota:LastGoodPath</c> explicitly overrides the location, so a backend restart does not
/// leave the header empty until the first probe completes, which can
/// take 30+ seconds per CLI.
///
/// <para>
/// Stored format: a compact list of parsed <see cref="QuotaSnapshot"/> records,
/// one per CLI. Response-only freshness fields and raw PTY samples are omitted.
/// It is cheap to read on startup and cheap to overwrite after each probe
/// outcome so failure metadata also survives restart. Tolerant to corruption: a malformed file is logged
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
        // Response-only age/stale fields keep their default values in the
        // in-memory cache and are omitted from the durable last-good file.
        // CapturedAt is non-default on successful readings and is persisted.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public QuotaCacheStore(IConfiguration config, ILogger<QuotaCacheStore> logger)
    {
        _logger = logger;
        var configuredPath = config["Quota:LastGoodPath"];
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            _path = Path.GetFullPath(configuredPath);
        }
        else
        {
            var localState = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var baseDir = !string.IsNullOrWhiteSpace(localState)
                ? Path.Combine(localState, "agent-taskboard")
                : Path.Combine(AppContext.BaseDirectory, "runtime");
            _path = Path.Combine(baseDir, "cli-quota-last-good.json");
        }

        var taskRepo = config["TaskRepository"];
        _legacyPath = !string.IsNullOrWhiteSpace(taskRepo)
            ? Path.Combine(taskRepo, ".runtime", "quota-cache.json")
            : Path.Combine(AppContext.BaseDirectory, "runtime", "quota-cache.json");

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            try { Directory.CreateDirectory(directory); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to create quota cache directory at {Path}", directory); }
        }
    }

    /// <summary>
    /// Read the persisted snapshots. Returns an empty list when the file
    /// does not exist or fails to parse.
    /// </summary>
    public List<QuotaSnapshot> Read()
    {
        var sourcePath = File.Exists(_path)
            ? _path
            : File.Exists(_legacyPath)
                ? _legacyPath
                : null;
        if (sourcePath == null) return new List<QuotaSnapshot>();
        try
        {
            var raw = File.ReadAllText(sourcePath);
            var snapshots = JsonSerializer.Deserialize<List<QuotaSnapshot>>(raw, ReadOpts)
                            ?? new List<QuotaSnapshot>();
            if (!string.Equals(sourcePath, _path, StringComparison.OrdinalIgnoreCase))
            {
                Write(snapshots);
                _logger.LogInformation(
                    "Migrated quota last-good cache from {LegacyPath} to {Path}",
                    sourcePath,
                    _path);
            }
            return snapshots;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read quota cache at {Path}", sourcePath);
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
            var persisted = snapshots
                .OrderBy(snapshot => snapshot.CliType, StringComparer.OrdinalIgnoreCase)
                .Select(snapshot => snapshot with
                {
                    AgeSeconds = null,
                    Stale = false,
                    RawSample = null
                })
                .ToList();
            var json = JsonSerializer.Serialize(persisted, WriteOpts);
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
}
