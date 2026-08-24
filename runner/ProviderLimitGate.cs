using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// One CLI parked because its shared provider account is out of budget.
/// </summary>
/// <param name="CliType">The CLI whose account is parked (<c>claude</c>, <c>codex</c>).</param>
/// <param name="ObservedAt">When the runner saw the rejection.</param>
/// <param name="LimitedUntil">When claims for this CLI may resume.</param>
/// <param name="Window">Provider window label when it named one.</param>
/// <param name="Evidence">The provider line the operator can act on.</param>
/// <param name="ResetWasStated">
/// False when the provider gave no reset we could resolve and
/// <see cref="ProviderLimitPolicy.DefaultPause"/> was applied instead. The
/// advertisement says so, because "limited until 00:20 (provider-stated)" and
/// "limited until 00:20 (estimated)" mean different things to an operator.
/// </param>
public sealed record ProviderLimitHold(
    [property: JsonPropertyName("cliType")] string CliType,
    [property: JsonPropertyName("observedAt")] DateTimeOffset ObservedAt,
    [property: JsonPropertyName("limitedUntil")] DateTimeOffset LimitedUntil,
    [property: JsonPropertyName("window")] string? Window,
    [property: JsonPropertyName("evidence")] string Evidence,
    [property: JsonPropertyName("resetWasStated")] bool ResetWasStated)
{
    public bool IsActiveAt(DateTimeOffset now) => now < LimitedUntil;

    public TimeSpan RemainingAt(DateTimeOffset now)
    {
        var remaining = LimitedUntil - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// The one line the capability advertisement and the board banner both show.
    /// Kept here so the two surfaces cannot drift apart.
    /// </summary>
    public string Describe()
        // Formatted from the UTC projection, never the value's own offset: the
        // label says UTC, so a hold built from a non-UTC clock must not print a
        // local wall-clock time under a UTC label.
        => $"{CliType}: limited until {LimitedUntil.UtcDateTime:HH:mm} UTC" +
           (ResetWasStated ? "" : " (estimated; the provider stated no reset)") +
           (Window is null ? "" : $" [{Window} window]");
}

/// <summary>
/// Pure decision layer: turns a classified provider signal into the hold the
/// fleet should apply, or nothing at all.
/// </summary>
public static class ProviderLimitPolicy
{
    /// <summary>
    /// Applied when the provider reported an account limit but no reset we could
    /// resolve ("resets 12:20am" with no zone). Long enough that the fleet stops
    /// hammering a dead account, short enough that one unparsed line cannot idle
    /// it all night: the gate simply re-arms if the next attempt is rejected again.
    /// </summary>
    public static readonly TimeSpan DefaultPause = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Added to a provider-stated reset. Resuming on the exact boundary races the
    /// provider's own clock and buys a second rejection, which would re-arm the
    /// gate and look like a flapping fleet.
    /// </summary>
    public static readonly TimeSpan ResetGrace = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Returns the hold to apply, or null when nothing should pause. Only
    /// <see cref="ProviderLimitScope.Account"/> pauses: a per-request throttle
    /// stays on the existing retry path, because pausing the whole CLI for one
    /// slow request would trade an escalation storm for an idle fleet.
    /// </summary>
    public static ProviderLimitHold? Decide(
        string cliType,
        ProviderLimitSignal signal,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return null;
        if (signal.Scope != ProviderLimitScope.Account) return null;

        var stated = signal.ResetAt is { } resetAt && resetAt > now;
        var until = stated ? signal.ResetAt!.Value + ResetGrace : now + DefaultPause;

        return new ProviderLimitHold(
            cliType.Trim().ToLowerInvariant(),
            now,
            until,
            signal.Window,
            signal.Evidence,
            stated);
    }
}

/// <summary>
/// The runner's per-CLI account-limit gate: the piece that turns "this card hit
/// the session limit" into "this host stops offering this CLI until the window
/// resets".
///
/// <para><b>Why a host-level gate and not a per-card retry.</b> Every run on this
/// host authenticates as the same operator account. When that account is out of
/// budget the failure is not a property of the card, it is a property of the
/// host, so the only correct reaction is to stop claiming for that CLI. On
/// 2026-08-23 the runner instead kept claiming and reported each death as an
/// unrecognised outcome, which escalated 32 cards and cost a mass requeue. Cards
/// for other CLIs are untouched, because their capability keys are separate: a
/// parked Claude account leaves <c>cli-execution:codex</c> advertised as ready.</para>
///
/// <para><b>Resume is a clock, not a handshake.</b> The hold carries its own
/// expiry, so recovery needs no operator action and no daemon restart: once
/// <see cref="ProviderLimitHold.LimitedUntil"/> passes, the next advertisement
/// reports the CLI ready again and the held cards are claimed. The
/// 09:18 operator handshake that ended the incident is exactly what this
/// removes.</para>
///
/// <para><b>Durable.</b> The holds are persisted next to the runner's slot state
/// so a daemon restart mid-outage does not forget the pause and walk straight
/// back into the storm.</para>
/// </summary>
public sealed class ProviderLimitGate
{
    public const string FileName = "provider-limits.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly Dictionary<string, ProviderLimitHold> _holds = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _statePath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _log;

    /// <summary>
    /// In-memory gate for tests and for a runner with no durable state directory.
    /// </summary>
    public ProviderLimitGate(
        string? stateDirectory = null,
        Func<DateTimeOffset>? clock = null,
        Action<string>? log = null)
    {
        _statePath = string.IsNullOrWhiteSpace(stateDirectory)
            ? null
            : Path.Combine(stateDirectory, FileName);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        Load();
    }

    /// <summary>
    /// Records an observed limit. Returns the hold when the fleet must pause this
    /// CLI, or null when the signal was not an account-level limit.
    /// </summary>
    public ProviderLimitHold? Record(string cliType, ProviderLimitSignal signal)
    {
        var now = _clock();
        var hold = ProviderLimitPolicy.Decide(cliType, signal, now);
        if (hold is null) return null;

        lock (_sync)
        {
            // A repeated rejection inside an existing hold must not shorten it:
            // the provider is still saying no, so keep whichever expiry is later.
            if (_holds.TryGetValue(hold.CliType, out var existing)
                && existing.IsActiveAt(now)
                && existing.LimitedUntil >= hold.LimitedUntil)
                return existing;

            _holds[hold.CliType] = hold;
            Persist();
        }

        _log?.Invoke($"provider-limit-armed {hold.Describe()}; evidence: {hold.Evidence}");
        return hold;
    }

    /// <summary>The active hold for one CLI, or null when it may be claimed for.</summary>
    public ProviderLimitHold? Current(string cliType)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return null;
        var now = _clock();
        lock (_sync)
        {
            if (!_holds.TryGetValue(cliType.Trim(), out var hold)) return null;
            if (hold.IsActiveAt(now)) return hold;
            // Expired: drop it here so the next advertisement is clean and the
            // resume needs no separate sweep.
            _holds.Remove(hold.CliType);
            Persist();
            _log?.Invoke($"provider-limit-lifted {hold.CliType}: window reset at {hold.LimitedUntil:o}; claims resume");
            return null;
        }
    }

    public bool IsLimited(string cliType) => Current(cliType) is not null;

    /// <summary>Every currently active hold, expired ones swept out.</summary>
    public IReadOnlyList<ProviderLimitHold> Active()
    {
        var now = _clock();
        lock (_sync)
        {
            var expired = _holds.Values.Where(hold => !hold.IsActiveAt(now)).ToList();
            foreach (var hold in expired)
            {
                _holds.Remove(hold.CliType);
                _log?.Invoke($"provider-limit-lifted {hold.CliType}: window reset at {hold.LimitedUntil:o}; claims resume");
            }
            if (expired.Count > 0) Persist();
            return _holds.Values.OrderBy(hold => hold.CliType, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Operator override: lift a hold before its stated reset.</summary>
    public bool Clear(string cliType)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return false;
        lock (_sync)
        {
            if (!_holds.Remove(cliType.Trim())) return false;
            Persist();
        }
        _log?.Invoke($"provider-limit-cleared {cliType}: lifted by an explicit override");
        return true;
    }

    private void Load()
    {
        if (_statePath is null || !File.Exists(_statePath)) return;
        try
        {
            var holds = JsonSerializer.Deserialize<List<ProviderLimitHold>>(
                File.ReadAllText(_statePath), Json);
            if (holds is null) return;
            var now = _clock();
            foreach (var hold in holds.Where(hold => hold.IsActiveAt(now)))
                _holds[hold.CliType] = hold;
        }
        catch (Exception ex)
        {
            // A corrupt gate file must not stop the daemon from starting; the
            // worst case is one more rejected run, which re-arms the gate.
            _log?.Invoke($"provider-limit-state-unreadable: {ex.Message}");
        }
    }

    private void Persist()
    {
        if (_statePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            var temp = _statePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_holds.Values.ToList(), Json));
            File.Move(temp, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"provider-limit-state-unwritable: {ex.Message}");
        }
    }
}
