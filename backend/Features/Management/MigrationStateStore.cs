using System.Text.Json;

namespace AgentStudio.Management;

/// <summary>
/// Durable projection of migrations that can still affect server readiness.
/// Completed migrations disappear from the active list; failed migrations stay
/// visible until a later successful retry replaces them.
/// </summary>
public sealed class MigrationStateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IConfiguration _configuration;
    private readonly object _gate = new();

    public MigrationStateStore(IConfiguration configuration) => _configuration = configuration;

    private string PathName => Path.Combine(
        Path.GetFullPath(_configuration["TaskRepository"]
            ?? Path.Combine(AppContext.BaseDirectory, "workspace")),
        ".metadata", "migrations.json");

    public IReadOnlyList<MigrationStatus> List()
    {
        lock (_gate) return ReadLocked();
    }

    public void Begin(string id, string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Migration id is required.", nameof(id));
        lock (_gate)
        {
            var rows = ReadLocked().Where(item => !string.Equals(item.Id, id, StringComparison.Ordinal)).ToList();
            rows.Add(new MigrationStatus(id, "running", DateTime.UtcNow.ToString("O"), detail));
            WriteLocked(rows);
        }
    }

    public void Complete(string id)
    {
        lock (_gate)
        {
            var rows = ReadLocked().Where(item => !string.Equals(item.Id, id, StringComparison.Ordinal)).ToList();
            WriteLocked(rows);
        }
    }

    public void Fail(string id, string detail)
    {
        lock (_gate)
        {
            var rows = ReadLocked().ToList();
            var prior = rows.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            rows.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            rows.Add(new MigrationStatus(id, "failed", prior?.StartedAt ?? DateTime.UtcNow.ToString("O"), detail));
            WriteLocked(rows);
        }
    }

    private List<MigrationStatus> ReadLocked()
    {
        try
        {
            return File.Exists(PathName)
                ? JsonSerializer.Deserialize<List<MigrationStatus>>(File.ReadAllText(PathName), Json) ?? []
                : [];
        }
        catch (JsonException ex)
        {
            return [new MigrationStatus("migration-state", "failed", null, ex.Message)];
        }
    }

    private void WriteLocked(IReadOnlyList<MigrationStatus> rows)
    {
        var directory = Path.GetDirectoryName(PathName)!;
        Directory.CreateDirectory(directory);
        var temporary = PathName + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, rows, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, PathName, true);
    }
}
