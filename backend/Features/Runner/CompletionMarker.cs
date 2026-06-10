using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Runner;

/// <summary>
/// Crash-recovery marker dropped into a job folder once the runner has
/// decided the job's CLI run is complete (sentinel matched, status terminal)
/// and is about to call <see cref="AgentStudio.Tasks.TaskTransitionService.MoveAsync"/>.
/// The marker survives a backend crash so the next boot can finish the
/// transition without losing the agent's evidence. See
/// <see cref="CrashRecoveryService"/> for the boot-time scan and ADR-0020
/// for the rules.
///
/// <para>
/// Lifecycle: write the marker just before the move, delete it after a
/// successful move. A marker that survives into the next boot signals
/// that the runner crashed between "decided" and "moved".
/// </para>
/// </summary>
public sealed record CompletionMarker
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "ready-to-transition";
    [JsonPropertyName("writtenAt")] public DateTime WrittenAt { get; init; } = DateTime.UtcNow;
    [JsonPropertyName("targetState")] public string TargetState { get; init; } = TaskStates.AutoReview;
    [JsonPropertyName("executionStatus")] public string? ExecutionStatus { get; init; }
    [JsonPropertyName("agentOutcome")] public string? AgentOutcome { get; init; }

    public const string FileName = "completion-marker.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Resolve the marker path inside a job folder.</summary>
    public static string PathFor(string jobFolder) => Path.Combine(jobFolder, FileName);

    /// <summary>
    /// Write the marker. Best-effort: a failure here must never block the
    /// transition the runner is about to perform.
    /// </summary>
    public static void Write(string jobFolder, CompletionMarker marker, ILogger? logger = null)
    {
        try
        {
            if (!Directory.Exists(jobFolder)) return;
            var json = JsonSerializer.Serialize(marker, JsonOptions);
            File.WriteAllText(PathFor(jobFolder), json, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to write completion-marker.json in {Folder}", jobFolder);
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
            logger?.LogWarning(ex, "Failed to clear completion-marker.json in {Folder}", jobFolder);
        }
    }

    /// <summary>Read the marker, returning null when missing or unreadable.</summary>
    public static CompletionMarker? TryRead(string jobFolder, ILogger? logger = null)
    {
        try
        {
            var path = PathFor(jobFolder);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CompletionMarker>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read completion-marker.json in {Folder}", jobFolder);
            return null;
        }
    }
}
