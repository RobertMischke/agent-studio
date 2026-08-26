using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Process-wide, provider-scoped claim pause that expires into an automatic recovery probe.</summary>
public sealed class ProviderLimitState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProviderLimitObservation> _limits = new(StringComparer.OrdinalIgnoreCase);
    private long _version;

    public static ProviderLimitState Shared { get; } = new();
    public long Version => Interlocked.Read(ref _version);

    public void Observe(ProviderLimitObservation observation)
    {
        lock (_gate)
        {
            _limits[observation.CliType] = observation;
            Interlocked.Increment(ref _version);
        }
    }

    public ProviderLimitObservation? Current(string cliType, DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            if (!_limits.TryGetValue(cliType, out var observation)) return null;
            if (observation.ResetAt > (now ?? DateTimeOffset.UtcNow)) return observation;
            _limits.Remove(cliType);
            Interlocked.Increment(ref _version);
            return null;
        }
    }
}
