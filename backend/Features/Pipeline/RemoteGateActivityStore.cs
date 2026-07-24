using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

public sealed record RemoteGateActivity(
    string GateRunId,
    string SshHost,
    DateTimeOffset StartedAtUtc);

public sealed record RemoteGateWorkload(
    int Active,
    int Capacity,
    IReadOnlyList<RemoteGateActivity> Gates);

/// <summary>
/// Process-local read model fed by the remote gate start/completion events.
/// Remote SSH gates execute outside daemon RUN slots, so host workload views
/// must expose this pool independently instead of inferring it from CPU load.
/// </summary>
public sealed class RemoteGateActivityStore
{
    public const int Capacity = 4;

    private static readonly Regex InstanceSuffix = new(
        @"-\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ConcurrentDictionary<string, RemoteGateActivity> _active =
        new(StringComparer.Ordinal);

    public void Started(string gateRunId, string sshHost, DateTimeOffset startedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(gateRunId) || string.IsNullOrWhiteSpace(sshHost)) return;
        _active[gateRunId] = new RemoteGateActivity(gateRunId, sshHost.Trim(), startedAtUtc);
    }

    public void Completed(string gateRunId)
    {
        if (!string.IsNullOrWhiteSpace(gateRunId)) _active.TryRemove(gateRunId, out _);
    }

    public RemoteGateWorkload ForRunner(string runnerId)
    {
        var aliases = RunnerAliases(runnerId);
        var gates = _active.Values
            .Where(gate => aliases.Contains(gate.SshHost))
            .OrderBy(gate => gate.StartedAtUtc)
            .ToArray();
        return new RemoteGateWorkload(gates.Length, Capacity, gates);
    }

    internal static HashSet<string> RunnerAliases(string runnerId)
    {
        var trimmed = runnerId.Trim();
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            trimmed,
            InstanceSuffix.Replace(trimmed, string.Empty),
        };
    }
}
