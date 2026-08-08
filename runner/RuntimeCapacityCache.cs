using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Durable last-known Task Server capacity for bootstrap while the control
/// plane is reconnecting. The cache is never an authority: any server response
/// replaces it, including a lower version after an authoritative backup restore.
/// </summary>
internal static class RuntimeCapacityCache
{
    private const string FileName = "runtime-capacity-cache.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    internal static RuntimeCapacityCacheEntry? Load(string stateDirectory, string hostId)
    {
        var path = Path.Combine(stateDirectory, FileName);
        try
        {
            if (!File.Exists(path)) return null;
            var entry = JsonSerializer.Deserialize<RuntimeCapacityCacheEntry>(
                File.ReadAllText(path),
                Json);
            return entry is not null
                   && string.Equals(entry.Capacity.HostId, hostId, StringComparison.Ordinal)
                   && entry.Capacity.MaxParallelism is >= 1 and <= 256
                   && entry.Capacity.Version > 0
                ? entry
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static void Save(
        string stateDirectory,
        RuntimeCapacitySettingsDto capacity,
        DateTime appliedAt)
    {
        try
        {
            Directory.CreateDirectory(stateDirectory);
            var path = Path.Combine(stateDirectory, FileName);
            var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(JsonSerializer.Serialize(
                    new RuntimeCapacityCacheEntry(capacity, appliedAt.ToUniversalTime()),
                    Json));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache persistence must never interrupt already-authorized work.
            // The in-memory server value remains effective for this process.
        }
    }
}

internal sealed record RuntimeCapacityCacheEntry(
    RuntimeCapacitySettingsDto Capacity,
    DateTime AppliedAt);
