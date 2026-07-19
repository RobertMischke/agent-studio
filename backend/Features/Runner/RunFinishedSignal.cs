using System.Text.Json;

namespace AgentStudio.Runner;

/// <summary>
/// Durable, boot-readable answer to "did the core agent run for this
/// <c>3-progress</c> card already finish?". This is the discriminator the
/// phase-aware run-liveness recovery uses to tell AGT-2006 (execution
/// interrupted -&gt; demote to <c>2-ready</c>) apart from AGT-1932 (the run
/// finished AND merged and only post-processing died with the backend -&gt;
/// re-trigger post-processing, never re-run the completed agent).
///
/// <para>
/// Three independent on-disk signals are read, and the <b>union</b> counts as
/// "core run finished". Reading the union is deliberate: each signal can be
/// absent on its own, so no single one is a reliable "run finished" test:
/// <list type="bullet">
///   <item>an <c>agent_run_finished</c> row in the unified timeline
///   (<c>logs/timeline.jsonl</c>) - the append-only ledger the runner writes
///   the moment the CLI process exits (ProjectRunner, ADR-0049). Written before
///   the post-run policy and lane move, so it survives a crash mid-finalise;</item>
///   <item>a surviving <c>completion-marker.json</c> - the runner decided the
///   run was complete and was about to move the folder (ADR-0020). Cleared
///   after a successful move, so its presence means the move never finished;</item>
///   <item><c>phase == post-processing-running</c> on <c>task.json</c> - the
///   explicit post-processing substate (LifecyclePhases).</item>
/// </list>
/// </para>
///
/// <para>Stateless and DI-free on purpose: both the boot adoption scan and the
/// uptime sweep read it, and it stays trivially unit-testable against a temp
/// folder.</para>
/// </summary>
public static class RunFinishedSignal
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// True when any durable signal says the core agent run for the card at
    /// <paramref name="jobFolder"/> already finished. Best-effort: an unreadable
    /// or absent file contributes no signal rather than throwing.
    /// </summary>
    public static bool CoreRunFinished(string jobFolder)
    {
        if (string.IsNullOrEmpty(jobFolder)) return false;
        return TimelineHasAgentRunFinished(jobFolder)
            || File.Exists(CompletionMarker.PathFor(jobFolder))
            || PhaseIsPostProcessing(jobFolder);
    }

    private static bool TimelineHasAgentRunFinished(string jobFolder)
    {
        var path = TaskPaths.TimelineLog(jobFolder);
        if (!File.Exists(path)) return false;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                // The timeline kind enum is closed, so the literal value is a
                // cheap first filter before the per-row parse.
                if (line.IndexOf(TimelineEventKinds.AgentRunFinished, StringComparison.Ordinal) < 0) continue;
                try
                {
                    var evt = JsonSerializer.Deserialize<TimelineEvent>(line, JsonOpts);
                    if (evt != null && string.Equals(evt.Kind, TimelineEventKinds.AgentRunFinished, StringComparison.Ordinal))
                        return true;
                }
                catch (Exception __ex)
                {
                    SilentCatch.Note(__ex, "RunFinishedSignal: torn timeline row - ignore");
                }
            }
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "RunFinishedSignal: unreadable timeline - treat as no signal");
        }
        return false;
    }

    private static bool PhaseIsPostProcessing(string jobFolder)
    {
        var path = Path.Combine(jobFolder, "task.json");
        if (!File.Exists(path)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("phase", out var el)
                && el.ValueKind == JsonValueKind.String
                && string.Equals(el.GetString(), LifecyclePhases.PostProcessingRunning, StringComparison.Ordinal);
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "RunFinishedSignal: unreadable task.json phase - treat as no signal");
            return false;
        }
    }
}
