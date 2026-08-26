using System.Collections.Concurrent;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Host-local, CLI-scoped provider-limit state. A detected account limit closes
/// only the matching provider capability. At the reported reset it exposes one
/// half-open claim as the recovery probe; success clears the limit and another
/// limit response closes it again with the new reset.
/// </summary>
public sealed class ProviderLimitState
{
    public const string LimitedStatus = "limited";
    public static readonly TimeSpan FallbackProbeDelay = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _persistenceGate = new();
    private readonly string? _persistencePath;
    private readonly Action<string>? _log;
    private int _changed;

    public ProviderLimitState(string? persistenceRoot = null, Action<string>? log = null)
    {
        _log = log;
        if (string.IsNullOrWhiteSpace(persistenceRoot)) return;
        var root = Path.GetFullPath(persistenceRoot);
        Directory.CreateDirectory(root);
        _persistencePath = Path.Combine(root, "provider-limits.json");
        if (!File.Exists(_persistencePath)) return;
        try
        {
            var snapshots = JsonSerializer.Deserialize<List<ProviderLimitSnapshot>>(
                                File.ReadAllText(_persistencePath),
                                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                            ?? [];
            foreach (var snapshot in snapshots)
            {
                var key = Normalize(snapshot.CliType);
                _entries[key] = new Entry(
                    key,
                    snapshot.DetectedAt,
                    snapshot.RetryAt,
                    snapshot.Reason,
                    snapshot.State);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            throw new InvalidDataException(
                $"Provider-limit state is unreadable: {_persistencePath}",
                ex);
        }
    }

    public ProviderLimitSnapshot? Current(string? cliType, DateTimeOffset now)
    {
        var key = Normalize(cliType);
        if (!_entries.TryGetValue(key, out var entry)) return null;
        if (entry.State == ProviderLimitStates.Limited && entry.RetryAt <= now)
        {
            entry = entry with { State = ProviderLimitStates.HalfOpen };
            _entries[key] = entry;
            Persist();
            MarkChanged();
        }
        return ToSnapshot(entry);
    }

    public ProviderLimitSnapshot ObserveLimit(
        string? cliType,
        string? output,
        DateTimeOffset observedAt)
    {
        var key = Normalize(cliType);
        var evidence = ProviderLimitParser.Parse(output, observedAt);
        var retryAt = evidence.ResetAt ?? observedAt.Add(FallbackProbeDelay);
        if (retryAt <= observedAt) retryAt = observedAt.AddMinutes(1);
        var next = new Entry(
            key,
            observedAt,
            retryAt,
            evidence.ResetAt is null
                ? $"{key}: provider account limited; probing again at {retryAt.UtcDateTime:O}"
                : $"{key}: limited until {retryAt.UtcDateTime:O}",
            ProviderLimitStates.Limited);
        _entries.AddOrUpdate(
            key,
            next,
            (_, current) => current.RetryAt >= next.RetryAt ? current : next);
        Persist();
        MarkChanged();
        return ToSnapshot(_entries[key]);
    }

    /// <summary>
    /// Marks the single claim admitted while a reset window is half-open. The
    /// daemon immediately re-advertises the capability as limited so another
    /// matching claim cannot join the probe.
    /// </summary>
    public bool TryBeginHalfOpenClaim(string? cliType, DateTimeOffset now)
    {
        var key = Normalize(cliType);
        var snapshot = Current(key, now);
        if (snapshot?.State != ProviderLimitStates.HalfOpen) return false;
        if (!_entries.TryGetValue(key, out var entry)) return false;
        _entries[key] = entry with { State = ProviderLimitStates.Probing };
        Persist();
        MarkChanged();
        return true;
    }

    public void ObserveOutcome(
        string? cliType,
        ExecutionOutcomeDecision decision,
        string? output,
        DateTimeOffset observedAt)
    {
        var key = Normalize(cliType);
        if (decision.Outcome == ExecutionOutcomeKind.QuotaExceeded)
        {
            ObserveLimit(key, output, observedAt);
            return;
        }
        if (decision.Outcome == ExecutionOutcomeKind.SuccessfulCompletion
            && _entries.TryGetValue(key, out var entry)
            && entry.State is ProviderLimitStates.HalfOpen or ProviderLimitStates.Probing)
        {
            _entries.TryRemove(key, out _);
            Persist();
            MarkChanged();
            return;
        }
        if (_entries.TryGetValue(key, out entry)
            && entry.State == ProviderLimitStates.Probing)
        {
            var retryAt = observedAt.AddMinutes(5);
            _entries[key] = entry with
            {
                RetryAt = retryAt,
                Reason = $"{key}: recovery probe was inconclusive; probing again at {retryAt.UtcDateTime:O}",
                State = ProviderLimitStates.Limited,
            };
            Persist();
            MarkChanged();
        }
    }

    public bool ConsumeChanged() => Interlocked.Exchange(ref _changed, 0) != 0;

    private void MarkChanged() => Interlocked.Exchange(ref _changed, 1);

    private void Persist()
    {
        if (_persistencePath is null) return;
        var temp = $"{_persistencePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            lock (_persistenceGate)
            {
                File.WriteAllText(
                    temp,
                    JsonSerializer.Serialize(
                        _entries.Values.Select(ToSnapshot).OrderBy(item => item.CliType),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
                File.Move(temp, _persistencePath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"provider-limit state persistence failed path={_persistencePath}: {ex.Message}");
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException cleanupError)
            {
                _log?.Invoke($"provider-limit temporary state cleanup failed path={temp}: {cleanupError.Message}");
            }
        }
    }

    private static string Normalize(string? cliType)
        => AgentCliProcess.NormalizeCliType(cliType) ?? AgentCliProcess.ClaudeCli;

    private static ProviderLimitSnapshot ToSnapshot(Entry entry) => new(
        entry.CliType,
        entry.DetectedAt,
        entry.RetryAt,
        entry.Reason,
        entry.State,
        entry.State is ProviderLimitStates.HalfOpen);

    private sealed record Entry(
        string CliType,
        DateTimeOffset DetectedAt,
        DateTimeOffset RetryAt,
        string Reason,
        string State);
}

public sealed record ProviderLimitSnapshot(
    string CliType,
    DateTimeOffset DetectedAt,
    DateTimeOffset RetryAt,
    string Reason,
    string State,
    bool ClaimEligible);

public static class ProviderLimitStates
{
    public const string Limited = "limited";
    public const string HalfOpen = "half-open";
    public const string Probing = "probing";
}
