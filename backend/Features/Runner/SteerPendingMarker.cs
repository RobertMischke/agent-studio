using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Runner;

/// <summary>
/// Durable steer-pending record dropped into a job folder when an auto-mode run
/// enters an unattended wait on a steer / <c>[[TASK_NEEDS_INPUT]]</c> question
/// that the orchestrator could not answer on its own (it STEERed, BLOCKed, or
/// hit the auto-loop circuit breaker). This is the "steer-pending" sub-state of
/// Run-Liveness Rule 1, minimally pulled forward for Slice B (concept
/// <c>docs/concepts/run-liveness-and-slot-semantics.md</c>, Rule 2).
///
/// <para>
/// <b>Why it must be durable.</b> Before this, the only trace of a waiting
/// NeedsInput card was the <c>[[TASK_NEEDS_INPUT]]</c> line in
/// <c>cli-output.log</c> plus in-memory runner state that is wiped on restart -
/// so a card could wait 5 hours across restarts with nothing tracking when the
/// wait started (belegt 2062/2067/2068, 2026-07-10). The record captures the
/// <see cref="WaitStartedAt"/> so <see cref="SteerTimeoutMonitor"/> can enforce
/// a bounded wait and the board can show "waiting for answer since mm:ss".
/// </para>
///
/// <para>
/// Lifecycle (see <see cref="SteerPendingMarker"/>): write the record when the
/// run is left waiting; delete it when a new run starts on the job (the user
/// answered, the timeout auto-answered, or a reissue took over) or when the
/// timeout routes the card to a blocked escalation.
/// </para>
/// </summary>
public sealed record SteerPendingRecord
{
    [JsonPropertyName("waitStartedAt")] public DateTime WaitStartedAt { get; init; } = DateTime.UtcNow;

    /// <summary>What kind of wait this is: <c>steer</c> (orchestrator asked a concrete question),
    /// <c>needs-input</c> (raw agent question left for the user), or <c>blocked-deferral</c>
    /// (orchestrator declined / gave up and deferred to the user).</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = SteerPendingKinds.Steer;

    /// <summary>The agent's original <c>[[TASK_NEEDS_INPUT:...]]</c> reason - the question being waited on.</summary>
    [JsonPropertyName("question")] public string? Question { get; init; }

    /// <summary>The orchestrator's concrete steer ask (the STEER <c>Need:</c> line), when present.</summary>
    [JsonPropertyName("ask")] public string? Ask { get; init; }

    /// <summary>
    /// Optional per-card override for the bounded wait before the steer-timeout
    /// fires, in seconds. <c>0</c> (the default) means "inherit the monitor's
    /// configured default" (<c>Runner:SteerTimeout:TimeoutSeconds</c>, default
    /// <see cref="SteerPendingDefaults.TimeoutSeconds"/>), so the timeout stays
    /// configurable in one place.
    /// </summary>
    [JsonPropertyName("timeoutSeconds")] public double TimeoutSeconds { get; init; }

    /// <summary>The active CLI type, so the resolver / continue path can resume the same runner.</summary>
    [JsonPropertyName("cliType")] public string? CliType { get; init; }
}

/// <summary>
/// Read / write / clear helper for the durable <c>steer-pending.json</c> marker
/// file that carries a <see cref="SteerPendingRecord"/> in a job folder. Same
/// best-effort, static-helper shape as <see cref="CompletionMarker"/> and
/// <see cref="PickupLockFile"/>: a persistence failure logs and returns rather
/// than throwing, since the marker is observability + a monitor input, not a
/// state-machine gate.
/// </summary>
public static class SteerPendingMarker
{
    public const string FileName = "steer-pending.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Resolve the marker path inside a job folder.</summary>
    public static string PathFor(string jobFolder) => Path.Combine(jobFolder, FileName);

    /// <summary>
    /// Write the marker. Best-effort: a failure here must never block the run
    /// path that is leaving the card waiting.
    /// </summary>
    public static void Write(string jobFolder, SteerPendingRecord marker, ILogger? logger = null)
    {
        try
        {
            if (!Directory.Exists(jobFolder)) return;
            var json = JsonSerializer.Serialize(marker, JsonOptions);
            File.WriteAllText(PathFor(jobFolder), json, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to write steer-pending.json in {Folder}", jobFolder);
        }
    }

    /// <summary>Delete the marker if it exists. Idempotent.</summary>
    public static void Clear(string jobFolder, ILogger? logger = null)
    {
        try
        {
            var path = PathFor(jobFolder);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to clear steer-pending.json in {Folder}", jobFolder);
        }
    }

    /// <summary>True when a marker file is present in the job folder.</summary>
    public static bool Exists(string jobFolder) => File.Exists(PathFor(jobFolder));

    /// <summary>Read the marker, returning null when missing or unreadable.</summary>
    public static SteerPendingRecord? TryRead(string jobFolder, ILogger? logger = null)
    {
        try
        {
            var path = PathFor(jobFolder);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SteerPendingRecord>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read steer-pending.json in {Folder}", jobFolder);
            return null;
        }
    }
}

/// <summary>Stable <see cref="SteerPendingRecord.Kind"/> values.</summary>
public static class SteerPendingKinds
{
    public const string Steer = "steer";
    public const string NeedsInput = "needs-input";
    public const string BlockedDeferral = "blocked-deferral";
}

/// <summary>Shared defaults for the steer-timeout so the marker, policy, and config agree on one number.</summary>
public static class SteerPendingDefaults
{
    /// <summary>Default bounded wait before an unanswered steer times out (concept Rule 2: "Default 120s, konfigurierbar").</summary>
    public const double TimeoutSeconds = 120;
}
