using System.Text.Json;

namespace AgentStudio.Clients;

public sealed record HostTelemetrySample(DateTime Timestamp, double? CpuPercent, double? Load1, double? Load5,
    double? Load15, long? MemoryUsedBytes, long? MemoryTotalBytes, long? SwapInBytesPerSecond,
    long? SwapOutBytesPerSecond, double? CpuStealPercent, double? IoWaitPercent, int CpuCores, int ActiveSlots);

public sealed record HostTelemetryFinding(string Kind, string Label, DateTime Since, DateTime Until);
public sealed record HostTelemetryResponse(string ClientId, string Window, IReadOnlyList<HostTelemetrySample> Points,
    IReadOnlyList<HostTelemetryFinding> Findings);

/// <summary>Durable per-client telemetry series with 48h raw and 14d five-minute retention.</summary>
public sealed class HostTelemetryStore(IConfiguration config, ILogger<HostTelemetryStore> logger)
{
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private string Root => Path.Combine(config["TaskRepository"] ?? Path.Combine(AppContext.BaseDirectory, "workspace"), "telemetry");

    public void Append(string clientId, HostTelemetrySample sample)
    {
        if (sample.Timestamp == default || sample.Timestamp > DateTime.UtcNow.AddMinutes(5)) return;
        lock (_gate)
        {
            var points = Read(clientId);
            if (points.Count > 0 && sample.Timestamp <= points[^1].Timestamp) return;
            points.Add(sample);
            points = Compact(points, DateTime.UtcNow);
            Directory.CreateDirectory(Root);
            var path = PathFor(clientId); var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(points, _json));
            File.Move(temporary, path, true);
            logger.LogDebug("host-telemetry-recorded clientId={ClientId} timestamp={Timestamp} points={PointCount}", clientId, sample.Timestamp, points.Count);
        }
    }

    public HostTelemetryResponse Query(string clientId, string window)
    {
        var duration = window switch { "1h" => TimeSpan.FromHours(1), "6h" => TimeSpan.FromHours(6), "14d" => TimeSpan.FromDays(14), _ => TimeSpan.FromHours(48) };
        List<HostTelemetrySample> points;
        lock (_gate) points = Read(clientId).Where(point => point.Timestamp >= DateTime.UtcNow - duration).ToList();
        return new HostTelemetryResponse(clientId, window, points, Findings(points));
    }

    private List<HostTelemetrySample> Read(string clientId)
    {
        var path = PathFor(clientId);
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<List<HostTelemetrySample>>(File.ReadAllText(path), _json) ?? []; }
        catch (Exception ex) { logger.LogWarning(ex, "host-telemetry-read-failed clientId={ClientId} path={Path}", clientId, path); return []; }
    }

    private string PathFor(string clientId) => Path.Combine(Root, string.Concat(clientId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')) + ".json");

    internal static List<HostTelemetrySample> Compact(List<HostTelemetrySample> source, DateTime now)
    {
        var retention = now.AddDays(-14); var rawBoundary = now.AddHours(-48);
        var recent = source.Where(p => p.Timestamp >= rawBoundary).ToList();
        var historic = source.Where(p => p.Timestamp >= retention && p.Timestamp < rawBoundary)
            .GroupBy(p => new DateTime(p.Timestamp.Ticks - p.Timestamp.Ticks % TimeSpan.FromMinutes(5).Ticks, DateTimeKind.Utc))
            .Select(group => Average(group.Key, group)).ToList();
        return historic.Concat(recent).OrderBy(p => p.Timestamp).ToList();
    }

    private static HostTelemetrySample Average(DateTime timestamp, IEnumerable<HostTelemetrySample> values)
    {
        var list = values.ToList();
        double? D(Func<HostTelemetrySample, double?> get) { var v = list.Select(get).Where(x => x.HasValue).Select(x => x!.Value).ToList(); return v.Count == 0 ? null : Math.Round(v.Average(), 2); }
        long? L(Func<HostTelemetrySample, long?> get) { var v = list.Select(get).Where(x => x.HasValue).Select(x => x!.Value).ToList(); return v.Count == 0 ? null : (long)v.Average(); }
        return new(timestamp, D(x => x.CpuPercent), D(x => x.Load1), D(x => x.Load5), D(x => x.Load15), L(x => x.MemoryUsedBytes),
            L(x => x.MemoryTotalBytes), L(x => x.SwapInBytesPerSecond), L(x => x.SwapOutBytesPerSecond), D(x => x.CpuStealPercent),
            D(x => x.IoWaitPercent), (int)Math.Round(list.Average(x => x.CpuCores)), (int)Math.Round(list.Average(x => x.ActiveSlots)));
    }

    private static IReadOnlyList<HostTelemetryFinding> Findings(List<HostTelemetrySample> points)
    {
        if (points.Count < 3) return [];
        var findings = new List<HostTelemetryFinding>();
        Add("vm-throttled", "VM throttled", p => p.CpuStealPercent > 5);
        Add("oversubscribed", "Oversubscribed", p => p.Load1 > p.CpuCores);
        Add("memory-pressure", "Memory pressure", p => (p.SwapInBytesPerSecond ?? 0) + (p.SwapOutBytesPerSecond ?? 0) > 64 * 1024);
        return findings;
        void Add(string kind, string label, Func<HostTelemetrySample, bool> predicate)
        {
            List<HostTelemetrySample> current = [];
            foreach (var point in points)
            {
                if (predicate(point)) current.Add(point);
                else current = [];
            }
            if (current.Count >= 3) findings.Add(new(kind, label, current[0].Timestamp, current[^1].Timestamp));
        }
    }
}
