using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace AgentStudio.Runner;

/// <summary>
/// Cross-slug Pre-Guard for infra-shaped pickup failures
/// (<c>pickup.cross-slug-infra-circuit-breaker</c> in
/// <c>docs/system/contracts/loop-inventory.md</c>).
///
/// <para>
/// The per-slug breaker (<see cref="ProjectRunner.PickupFailureThreshold"/>)
/// dead-letters a single broken job after 3 silent runs. When the underlying
/// CLI binary itself is broken (typical: half-installed <c>claude.exe</c> stub
/// from an interrupted npm postinstall), the per-slug breaker fires correctly
/// for every job in turn but the runner keeps picking the next one, so the
/// entire <c>2-ready</c> lane drains into <c>3a-failed-pickup</c> one slug at
/// a time. The 2026-05-06 incident drained 22 jobs in 13 minutes this way.
/// </para>
///
/// <para>
/// This breaker is the additional layer that recognises the cascade as
/// infra-shaped rather than task-shaped: when <see cref="SilentLimit"/>
/// distinct slugs hit the per-slug dead-letter for the same <c>cliType</c>
/// within <see cref="Window"/>, the runner pauses pickup (mode → manual),
/// raises an infrastructure-halt banner, and probes for recovery before
/// restoring the saved automatic mode. The per-slug path keeps working as
/// before; this is an additional layer, not a replacement.
/// </para>
///
/// <para>
/// Detection is deterministic. No LLM in this code path. The counter is a
/// typed sliding window keyed on <c>(projectName, cliType)</c>, the
/// thresholds are config constants, and the action is a deterministic
/// mode change. See ADR-0032 (contract-bounded agents) for why Pre-Guard
/// budgets stay in pure code.
/// </para>
/// </summary>
public sealed class CrossSlugInfraCircuitBreaker
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CrossSlugInfraCircuitBreaker> _logger;
    private readonly InfraHaltLog _haltLog;

    /// <summary>Default: 2 distinct slugs in 10 minutes for the same CLI trips the breaker.</summary>
    public const int DefaultSilentLimit = 2;
    /// <summary>Default rolling window for distinct-slug counting.</summary>
    public const int DefaultWindowMinutes = 10;
    /// <summary>Long-window cleanup: entries older than this are dropped on the next observation.</summary>
    public static readonly TimeSpan LongCleanup = TimeSpan.FromHours(24);

    // Sliding-window state per (projectName, cliType). In-memory only - a
    // backend restart resets the counter, matching the wider runner pattern
    // (a restart is itself a recovery boundary). The queue holds one entry
    // per distinct dead-lettered slug; same-slug observations within the
    // window are deduped so a single slug burning through its 3 silent
    // attempts cannot trip the cross-slug breaker on its own.
    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    private sealed class State
    {
        public readonly object Lock = new();
        public readonly List<Entry> Entries = new();
        public DateTime? LastTrippedAt;
    }

    private readonly record struct Entry(string Slug, DateTime At);

    public CrossSlugInfraCircuitBreaker(
        IConfiguration configuration,
        ILogger<CrossSlugInfraCircuitBreaker> logger,
        InfraHaltLog haltLog)
    {
        _configuration = configuration;
        _logger = logger;
        _haltLog = haltLog;
    }

    /// <summary>
    /// Effective distinct-slug limit. Reads <c>Supervisor:CrossSlugInfraSilentLimit</c>
    /// from configuration; falls back to <see cref="DefaultSilentLimit"/>.
    /// </summary>
    public int SilentLimit
    {
        get
        {
            var v = _configuration.GetValue("Supervisor:CrossSlugInfraSilentLimit", DefaultSilentLimit);
            return v < 1 ? 1 : v;
        }
    }

    /// <summary>
    /// Effective rolling window. Reads <c>Supervisor:CrossSlugInfraSilentWindowMinutes</c>
    /// from configuration; falls back to <see cref="DefaultWindowMinutes"/>.
    /// </summary>
    public TimeSpan Window
    {
        get
        {
            var minutes = _configuration.GetValue("Supervisor:CrossSlugInfraSilentWindowMinutes", DefaultWindowMinutes);
            return TimeSpan.FromMinutes(minutes < 1 ? 1 : minutes);
        }
    }

    /// <summary>
    /// Record one spawn-failed dead-letter event for
    /// <paramref name="projectName"/> + <paramref name="cliType"/> +
    /// <paramref name="slug"/>. Returns a non-null <see cref="TripOutcome"/>
    /// only on the call that just tripped the breaker (so the caller can
    /// take its one-shot action). Subsequent dead-letters while the breaker
    /// is already tripped return null - the runner has already been moved
    /// to manual, the banner is already raised.
    /// </summary>
    public TripOutcome? RecordSpawnFailedDeadLetter(string projectName, string? cliType, string slug, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(cliType) || string.IsNullOrWhiteSpace(slug))
            return null;

        var key = BuildKey(projectName, cliType!);
        var state = _states.GetOrAdd(key, _ => new State());
        var window = Window;
        var limit = SilentLimit;

        lock (state.Lock)
        {
            // Drop entries older than the long-cleanup ceiling (24h by
            // default) AND entries older than the rolling window. Both
            // cleanups happen on every observation, which keeps the
            // bounded-size invariant without a separate sweep loop.
            var cutoff = utcNow - window;
            var longCutoff = utcNow - LongCleanup;
            state.Entries.RemoveAll(e => e.At < cutoff || e.At < longCutoff);

            // Distinct-slug semantics: re-counting the same slug within the
            // window would let one broken job alone trip the breaker. The
            // 2026-05-06 incident was several DIFFERENT slugs spawn-failing
            // back-to-back; that's the signal we want.
            if (!state.Entries.Any(e => string.Equals(e.Slug, slug, StringComparison.OrdinalIgnoreCase)))
            {
                state.Entries.Add(new Entry(slug, utcNow));
            }

            if (state.Entries.Count < limit)
            {
                _logger.LogDebug(
                    "CrossSlugInfraCircuitBreaker: recorded spawn-fail for {Project}/{Cli}/{Slug} ({Count}/{Limit} within {WindowMin}min)",
                    projectName, cliType, slug, state.Entries.Count, limit, window.TotalMinutes);
                return null;
            }

            // Suppress repeat trips while we're already in a tripped state
            // within the window. The mode flip already stopped the runner;
            // a second banner adds noise.
            if (state.LastTrippedAt.HasValue && state.LastTrippedAt.Value >= cutoff)
            {
                return null;
            }

            state.LastTrippedAt = utcNow;
            var offenders = state.Entries
                .OrderBy(e => e.At)
                .Select(e => e.Slug)
                .ToList();

            var outcome = new TripOutcome(
                ProjectName: projectName,
                CliType: cliType!,
                Slugs: offenders,
                TrippedAt: utcNow,
                WindowMinutes: (int)window.TotalMinutes,
                Limit: limit);

            try
            {
                _haltLog.Append(new InfraHaltRecord
                {
                    At = utcNow,
                    Kind = InfraHaltKinds.CrossSlugSpawnFailedCascade,
                    ProjectName = projectName,
                    CliType = cliType!,
                    Slugs = offenders,
                    WindowMinutes = (int)window.TotalMinutes,
                    Limit = limit,
                    Reason = BuildReason(cliType!, offenders, limit, (int)window.TotalMinutes)
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CrossSlugInfraCircuitBreaker: infra-halts.jsonl append failed for {Project}", projectName);
            }

            _logger.LogWarning(
                "CrossSlugInfraCircuitBreaker tripped for {Project}/{Cli}: {Count} distinct slugs spawn-failed within {WindowMin}min ({Offenders})",
                projectName, cliType, offenders.Count, window.TotalMinutes, string.Join(", ", offenders));

            return outcome;
        }
    }

    /// <summary>
    /// Successful pickup signal. ≥ 1 streamed CLI output line on a job means
    /// the infra is healthy again - reset the counter for that
    /// <c>(projectName, cliType)</c>.
    /// </summary>
    public void OnProductivePickup(string projectName, string? cliType)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(cliType)) return;
        var key = BuildKey(projectName, cliType!);
        if (_states.TryGetValue(key, out var state))
        {
            lock (state.Lock)
            {
                if (state.Entries.Count == 0 && state.LastTrippedAt == null) return;
                state.Entries.Clear();
                state.LastTrippedAt = null;
            }
            _logger.LogDebug(
                "CrossSlugInfraCircuitBreaker: productive pickup cleared counter for {Project}/{Cli}",
                projectName, cliType);
        }
    }

    /// <summary>
    /// Operator-initiated reset. When the user flips the project back to an
    /// auto mode, the counter is cleared across all <c>cliType</c>s for that
    /// project - the operator's intent is "infra is fixed, run again".
    /// </summary>
    public void OnOperatorResumeAuto(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName)) return;
        var prefix = projectName + "|";
        var keysToReset = _states.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in keysToReset)
        {
            if (_states.TryGetValue(key, out var state))
            {
                lock (state.Lock)
                {
                    state.Entries.Clear();
                    state.LastTrippedAt = null;
                }
            }
        }
        if (keysToReset.Count > 0)
        {
            _logger.LogDebug(
                "CrossSlugInfraCircuitBreaker: operator resume cleared {Count} CLI counters for {Project}",
                keysToReset.Count, projectName);
        }
    }

    /// <summary>Test seam: read the current distinct-slug count for a CLI on a project.</summary>
    internal int GetEntryCount(string projectName, string cliType)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(cliType)) return 0;
        var key = BuildKey(projectName, cliType);
        return _states.TryGetValue(key, out var state)
            ? state.Entries.Count
            : 0;
    }

    /// <summary>Test seam: is the breaker currently in a tripped state for this CLI/project?</summary>
    internal bool IsTripped(string projectName, string cliType)
    {
        if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(cliType)) return false;
        var key = BuildKey(projectName, cliType);
        if (!_states.TryGetValue(key, out var state)) return false;
        lock (state.Lock)
        {
            return state.LastTrippedAt.HasValue;
        }
    }

    private static string BuildKey(string projectName, string cliType)
        => $"{projectName}|{cliType}";

    private static string BuildReason(string cliType, IReadOnlyList<string> slugs, int limit, int windowMinutes)
    {
        var slugList = string.Join(", ", slugs);
        return
            $"{cliType} CLI suspected broken: {slugs.Count} distinct slugs spawn-failed " +
            $"({slugList}) within {windowMinutes} minutes (limit {limit}). " +
            $"Runner switched to manual mode while recovery is probed automatically. " +
            $"Suggested action if recovery does not arrive: run tools/check-cli-shims.sh " +
            $"and restart stable, or check {cliType} --version manually.";
    }
}

/// <summary>
/// One-shot result returned on the call that trips the breaker. The caller
/// is responsible for the runner mode flip and the supervisor chat note;
/// the persisted infra-halts.jsonl row is already written by the breaker.
/// </summary>
public sealed record TripOutcome(
    string ProjectName,
    string CliType,
    IReadOnlyList<string> Slugs,
    DateTime TrippedAt,
    int WindowMinutes,
    int Limit)
{
    /// <summary>
    /// Plain-text banner copy for the project chat surface. No HTML, no
    /// immediate-tooltips (see <c>feedback_no_html_tooltips.md</c>). One
    /// concrete suggested action.
    /// </summary>
    public string BuildSupervisorChatMessage()
    {
        var slugList = string.Join(", ", Slugs);
        return
            $"Cross-slug pickup-failure cascade detected: {CliType} CLI suspected broken " +
            $"({Slugs.Count} distinct slugs spawn-failed within {WindowMinutes} minutes: {slugList}). " +
            $"Runner switched to manual mode and will resume the saved automatic mode after a successful recovery probe. " +
            $"Suggested action if recovery does not arrive: run tools/check-cli-shims.sh and restart stable, " +
            $"or check {CliType} --version manually.";
    }
}

/// <summary>
/// Appends one row per cross-slug infra trip to
/// <c>&lt;workspace&gt;/logs/infra-halts.jsonl</c>. Pairs with
/// <c>pickup-failures.jsonl</c> (per-slug dead-letter) and
/// <c>orphan-recoveries.jsonl</c> (boot-time sweep) - same shape, same
/// home, different signal.
/// </summary>
public sealed class InfraHaltLog
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<InfraHaltLog> _logger;
    private readonly AgentStudio.Persistence.IJsonlAppender _appender;

    public InfraHaltLog(
        IConfiguration configuration,
        ILogger<InfraHaltLog> logger,
        AgentStudio.Persistence.IJsonlAppender? appender = null)
    {
        _configuration = configuration;
        _logger = logger;
        _appender = appender ?? new AgentStudio.Persistence.JsonlAppender();
    }

    public void Append(InfraHaltRecord record)
    {
        var workspaceRoot = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            _logger.LogDebug(
                "InfraHaltLog: TaskRepository not configured; skipping infra-halts.jsonl entry for {Project}.",
                record.ProjectName);
            return;
        }

        try
        {
            var path = Path.Combine(workspaceRoot, "logs", "infra-halts.jsonl");
            // IJsonlAppender holds a per-path SemaphoreSlim so concurrent
            // halts from different projects do not interleave bytes. The
            // previous comment claimed "AppendAllText is atomic at the OS
            // level" — true only for writes under 4 KB; the rest is
            // implementation-dependent. The helper makes the guarantee
            // explicit.
            _appender.AppendAsync(path, record, JsonOptions).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "InfraHaltLog: failed to append infra-halts.jsonl for {Project}", record.ProjectName);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>One row in <c>&lt;workspace&gt;/logs/infra-halts.jsonl</c>.</summary>
public sealed record InfraHaltRecord
{
    [JsonPropertyName("at")] public DateTime At { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("projectName")] public string ProjectName { get; init; } = "";
    [JsonPropertyName("cliType")] public string CliType { get; init; } = "";
    [JsonPropertyName("slugs")] public IReadOnlyList<string> Slugs { get; init; } = Array.Empty<string>();
    [JsonPropertyName("windowMinutes")] public int WindowMinutes { get; init; }
    [JsonPropertyName("limit")] public int Limit { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

/// <summary>String constants for <see cref="InfraHaltRecord.Kind"/>.</summary>
public static class InfraHaltKinds
{
    /// <summary>
    /// Two or more distinct slugs spawn-failed back-to-back for the same CLI:
    /// the 2026-05-06 incident shape.
    /// </summary>
    public const string CrossSlugSpawnFailedCascade = "cross-slug-spawn-failed-cascade";
}
