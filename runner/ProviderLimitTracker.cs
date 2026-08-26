using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

public sealed record ProviderLimitState(
    string Provider,
    DateTimeOffset ObservedAt,
    DateTimeOffset RetryAt,
    string Reason,
    int ProbeAttempts = 0);

/// <summary>
/// Durable, provider-scoped circuit state. A Claude limit closes only Claude
/// admission; other CLI capabilities remain ready. The state survives daemon
/// restarts and opens only after a successful provider request proves recovery.
/// </summary>
public sealed class ProviderLimitTracker
{
    private const string FileName = "provider-limits.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private Dictionary<string, ProviderLimitState> _states;
    private long _revision;

    public ProviderLimitTracker(string stateDirectory, Func<DateTimeOffset>? clock = null)
    {
        _path = Path.Combine(stateDirectory, FileName);
        _clock = clock ?? (() => DateTimeOffset.Now);
        _states = Load(_path);
    }

    public long Revision { get { lock (_gate) return _revision; } }

    public IReadOnlyList<ProviderLimitState> Current
    {
        get { lock (_gate) return _states.Values.OrderBy(state => state.Provider).ToArray(); }
    }

    public ProviderLimitState? Get(string provider)
    {
        lock (_gate)
            return _states.GetValueOrDefault(Normalize(provider));
    }

    public void Record(ProviderLimitInfo limit)
    {
        var provider = Normalize(limit.Provider);
        var retryAt = limit.RetryAt <= limit.ObservedAt
            ? limit.ObservedAt.AddMinutes(15)
            : limit.RetryAt;
        lock (_gate)
        {
            _states[provider] = new ProviderLimitState(
                provider,
                limit.ObservedAt,
                retryAt,
                limit.Reason);
            _revision++;
            Persist();
        }
    }

    public bool IsLimited(string provider) => Get(provider) is not null;

    public async Task<ProviderLimitProbeResult?> ProbeIfDueAsync(
        string provider,
        ProviderLimitProbeLauncher launcher,
        CancellationToken ct)
    {
        ProviderLimitState? state;
        lock (_gate) state = _states.GetValueOrDefault(Normalize(provider));
        if (state is null || _clock() < state.RetryAt) return null;

        var result = await launcher(state.Provider, ct);
        lock (_gate)
        {
            if (!_states.TryGetValue(state.Provider, out var current)
                || current.ObservedAt != state.ObservedAt)
                return result;

            if (result.Recovered)
            {
                _states.Remove(state.Provider);
            }
            else
            {
                var next = result.Limit?.RetryAt ?? _clock().AddMinutes(15);
                if (next <= _clock()) next = _clock().AddMinutes(15);
                _states[state.Provider] = current with
                {
                    RetryAt = next,
                    Reason = result.Limit?.Reason ?? result.Detail,
                    ProbeAttempts = current.ProbeAttempts + 1,
                };
            }
            _revision++;
            Persist();
        }
        return result;
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_states.Values, Json));
        File.Move(temp, _path, overwrite: true);
    }

    private static Dictionary<string, ProviderLimitState> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            return (JsonSerializer.Deserialize<List<ProviderLimitState>>(File.ReadAllText(path), Json) ?? [])
                .ToDictionary(state => Normalize(state.Provider), StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Provider limit state '{path}' is unreadable; refusing to reopen provider claims.",
                exception);
        }
    }

    private static string Normalize(string provider)
        => string.IsNullOrWhiteSpace(provider) ? "unknown" : provider.Trim().ToLowerInvariant();
}

public delegate Task<ProviderLimitProbeResult> ProviderLimitProbeLauncher(
    string provider,
    CancellationToken ct);

public sealed record ProviderLimitProbeResult(
    bool Recovered,
    string Detail,
    ProviderLimitInfo? Limit = null);
