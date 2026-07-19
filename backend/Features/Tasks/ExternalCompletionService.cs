using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Configuration;

namespace AgentStudio.Tasks;

/// <summary>
/// Reconciles a task that was completed outside the runner (operator chat, an
/// external agent, a remote host) in one atomic call, implementing §3 of
/// <c>docs/concepts/out-of-band-task-completion.md</c>.
///
/// <para>
/// The out-of-band completion of AGT-1917 left the card looking abandoned: the
/// work was done and committed, but <c>status.md</c> still said "escalated / no
/// summary", <c>lifecycle.json</c> was stuck in <c>post-processing-running</c>
/// (spamming scanner warnings), and the run history ended in a corpse. This
/// service is the product fix: it writes <c>status.md</c> +
/// <c>results/deliverables.md</c>, terminalizes <c>lifecycle.json</c>, records
/// the external provenance on <c>task.json</c>, appends an
/// <c>external_completion</c> row to the timeline, moves the lane, and commits
/// the workspace evidence - so the card's story is owned, not just its lane.
/// </para>
/// </summary>
public sealed class ExternalCompletionService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TaskTransitionService _transitions;
    private readonly TimelineLog _timeline;
    private readonly WorkspaceArtifactCommitService _artifactCommits;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExternalCompletionService> _logger;

    public ExternalCompletionService(
        TaskScannerService scanner,
        TaskMutationService mutations,
        TaskTransitionService transitions,
        TimelineLog timeline,
        WorkspaceArtifactCommitService artifactCommits,
        IConfiguration configuration,
        ILogger<ExternalCompletionService> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _transitions = transitions;
        _timeline = timeline;
        _artifactCommits = artifactCommits;
        _configuration = configuration;
        _logger = logger;
    }

    private static readonly JsonSerializerOptions LifecycleJsonWriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<ExternalCompletionOutcome> CompleteAsync(
        string jobId,
        string? watchPath,
        ExternalCompletionRequest request,
        string actor,
        CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Summary))
            return new ExternalCompletionOutcome(ExternalCompletionStatus.InvalidRequest, "summary is required.");

        var targetState = string.IsNullOrWhiteSpace(request.TargetState)
            ? TaskStates.HumanReview
            : request.TargetState!.Trim();
        if (!TaskStates.All.Contains(targetState))
            return new ExternalCompletionOutcome(
                ExternalCompletionStatus.InvalidRequest,
                $"Invalid targetState. Allowed: {string.Join(", ", TaskStates.All)}");

        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null)
            return new ExternalCompletionOutcome(ExternalCompletionStatus.NotFound);

        var source = string.IsNullOrWhiteSpace(request.Source) ? "external" : request.Source!.Trim();
        var summary = request.Summary!.Trim();
        var now = DateTime.UtcNow;
        var beforeFolder = info.FolderPath;

        // §3 order: write the reconciled evidence into the current folder, then
        // move the lane LAST, then commit the workspace snapshot. Every write
        // is best-effort-logged but the endpoint treats a hard failure of the
        // canonical writes (status / deliverables / task.json) as fatal so the
        // caller is not told a half-reconciled card is done.
        WriteDeliverables(beforeFolder, summary, source, now, request.Deliverables);
        WriteStatus(beforeFolder, summary, source, now);
        WriteGateItems(beforeFolder, request.GateItems);
        _mutations.SetExternalCompletionOnFolder(beforeFolder, new ExternalCompletionInfo
        {
            Source = source,
            Summary = summary,
            CompletedAt = now,
        });
        TerminalizeLifecycle(beforeFolder, targetState, source, now);

        // Append the external ingest entry to the unified timeline BEFORE the
        // move so it lands in the same folder as the rest of the evidence; the
        // subsequent lane_changed row is emitted by the state machine.
        _timeline.Append(
            beforeFolder,
            TimelineEventKinds.ExternalCompletion,
            TimelineActors.External,
            summary: $"Completed externally by {source}",
            payloadRef: "results/deliverables.md",
            details: new Dictionary<string, string>
            {
                ["source"] = source,
                ["targetState"] = targetState,
            });

        var moveCause = string.IsNullOrWhiteSpace(actor) ? TimelineActors.External : actor;
        var move = await _transitions.MoveAsync(jobId, targetState, watchPath, ct, cause: moveCause);
        var afterFolder = beforeFolder;
        switch (move.Status)
        {
            case MoveJobStatus.Success:
                afterFolder = move.NewFolderPath ?? beforeFolder;
                // Re-assert the terminal lifecycle into the moved folder: a move
                // out of 3-progress runs EnterPostProcessingPhase, which would
                // otherwise reset lifecycle.json to post-processing-running - the
                // exact stuck state this endpoint exists to retire. Idempotent for
                // every other source lane.
                TerminalizeLifecycle(afterFolder, targetState, source, now);
                break;
            case MoveJobStatus.NotFound:
                // Raced away between find and move; the evidence is already on
                // disk, so surface it as a move failure rather than a 404.
                return new ExternalCompletionOutcome(
                    ExternalCompletionStatus.MoveFailed, "Task disappeared before the lane move.", jobId);
            case MoveJobStatus.TargetFolderExists:
            case MoveJobStatus.DirectoryLocked:
                return new ExternalCompletionOutcome(ExternalCompletionStatus.MoveConflict, move.Message, jobId);
            default:
                return new ExternalCompletionOutcome(ExternalCompletionStatus.MoveFailed, move.Message, jobId);
        }

        var commit = _artifactCommits.TryCommitExternalCompletion(
            _configuration["TaskRepository"], jobId, beforeFolder, afterFolder, source);
        if (!commit.Success)
        {
            _logger.LogWarning(
                "external-completion-evidence-commit-failed project={Project} job={JobId} error={Error}",
                info.ProjectName, jobId, commit.Error);
        }

        _logger.LogInformation(
            "external-completion project={Project} job={JobId} source={Source} targetState={TargetState} evidenceSha={Sha}",
            info.ProjectName, jobId, source, targetState, commit.Sha ?? "");

        return new ExternalCompletionOutcome(
            ExternalCompletionStatus.Success,
            JobId: jobId,
            TargetState: targetState,
            EvidenceCommitSha: commit.DidCommit ? commit.Sha : null);
    }

    private void WriteGateItems(string folderPath, IReadOnlyList<string>? gateItems)
    {
        var items = (gateItems ?? Array.Empty<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Replace('\r', ' ').Replace('\n', ' ').Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (items.Count == 0) return;

        var path = Path.Combine(folderPath, "orchestrator-follow-up.md");
        try
        {
            var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            var sb = new StringBuilder(existing);
            if (sb.Length == 0)
                sb.Append("# Orchestrator follow-up\n\n");
            else if (!existing.EndsWith("\n\n", StringComparison.Ordinal))
                sb.Append(existing.EndsWith('\n') ? "\n" : "\n\n");

            foreach (var item in items)
            {
                var row = $"- [ ] {item}";
                if (!existing.Contains(row, StringComparison.Ordinal))
                    sb.Append(row).Append('\n');
            }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "external-completion: failed to write gate items for {Folder}", folderPath);
        }
    }

    /// <summary>
    /// Writes <c>results/deliverables.md</c>: what was delivered and where, by
    /// whom / which channel. This is the canonical narrative the timeline entry
    /// points at.
    /// </summary>
    private void WriteDeliverables(
        string folderPath,
        string summary,
        string source,
        DateTime now,
        IReadOnlyList<ExternalDeliverable>? deliverables)
    {
        try
        {
            var resultsDir = TaskPaths.ResultsDir(folderPath);
            Directory.CreateDirectory(resultsDir);

            var sb = new StringBuilder();
            sb.Append("# Deliverables\n\n");
            sb.Append("Executed out-of-band on ")
              .Append(now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
              .Append(" by ").Append(source).Append(".\n\n");
            sb.Append(summary).Append("\n\n");

            var items = (deliverables ?? Array.Empty<ExternalDeliverable>())
                .Where(d => d != null && (!string.IsNullOrWhiteSpace(d.Path) || !string.IsNullOrWhiteSpace(d.Url)))
                .ToList();
            if (items.Count > 0)
            {
                sb.Append("## What was delivered\n\n");
                foreach (var d in items)
                {
                    var hasPath = !string.IsNullOrWhiteSpace(d.Path);
                    var target = hasPath ? d.Path!.Trim() : d.Url!.Trim();
                    if (hasPath)
                        sb.Append("- `").Append(target).Append('`');
                    else
                        sb.Append("- [").Append(target).Append("](").Append(target).Append(')');
                    if (!string.IsNullOrWhiteSpace(d.Note))
                        sb.Append(" - ").Append(d.Note!.Trim());
                    sb.Append('\n');
                }
                sb.Append('\n');
            }

            sb.Append("## Provenance\n\n");
            sb.Append("- Completed externally by ").Append(source).Append('\n');
            sb.Append("- Recorded at ")
              .Append(now.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
              .Append('\n');
            sb.Append("- This reconciliation was written by the external-completion endpoint; ")
              .Append("files under `results/` that predate it are the dead run's drafts.\n");

            File.WriteAllText(Path.Combine(resultsDir, "deliverables.md"), sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "external-completion: failed to write deliverables.md for {Folder}", folderPath);
        }
    }

    /// <summary>
    /// Replaces the stale <c>status.md</c> with a result summary that states
    /// explicitly the task was executed out-of-band plus the date. Unlike the
    /// escalation stub, this deliberately overwrites any existing text: the
    /// whole point of the endpoint is to retire the "escalated / no summary"
    /// corpse.
    /// </summary>
    private void WriteStatus(string folderPath, string summary, string source, DateTime now)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("# Status\n\n");
            sb.Append("- Result: Completed out-of-band (").Append(source).Append(")\n\n");
            sb.Append(summary).Append("\n\n");
            sb.Append("Executed out-of-band on ")
              .Append(now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
              .Append(" by ").Append(source).Append(".\n\n");
            sb.Append("- See `results/deliverables.md` for what was delivered and where.\n");
            File.WriteAllText(Path.Combine(folderPath, "status.md"), sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "external-completion: failed to write status.md for {Folder}", folderPath);
        }
    }

    /// <summary>
    /// Terminalizes <c>lifecycle.json</c>: sets the phase to
    /// <see cref="LifecyclePhases.AwaitingReview"/> and flips every still-running
    /// check to <c>skipped</c> with a note, so a card left stuck in
    /// <c>post-processing-running</c> stops spamming the scanner warning that
    /// motivated this fix. Rewrites the sidecar even when none exists so the
    /// terminal state is explicit on disk.
    /// </summary>
    private void TerminalizeLifecycle(string folderPath, string targetState, string source, DateTime now)
    {
        try
        {
            var snapshot = ReadLifecycleSnapshot(folderPath) ?? new LifecycleSnapshot();
            var note = $"Superseded by out-of-band completion ({source}).";

            var updated = snapshot with
            {
                Phase = LifecyclePhases.AwaitingReview,
                PhaseEnteredAt = now,
                BlockingReason = null,
                IntakeChecks = TerminalizeChecks(snapshot.IntakeChecks, now, note),
                PostProcessingChecks = TerminalizeChecks(snapshot.PostProcessingChecks, now, note),
            };
            File.WriteAllText(
                Path.Combine(folderPath, "lifecycle.json"),
                JsonSerializer.Serialize(updated, LifecycleJsonWriteOpts),
                Encoding.UTF8);

            // Clear the mirrored task.json phase: the target lane
            // (5-human-review by default) carries no phase, so leaving a stale
            // running phase on the card would contradict the terminalized
            // sidecar. The lane move re-clears incompatible phases too; this
            // makes the terminal state correct even when target == source lane.
            _mutations.SetJobPhase(folderPath, LifecyclePhases.IsAllowed(targetState, LifecyclePhases.AwaitingReview)
                ? LifecyclePhases.AwaitingReview
                : "");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "external-completion: failed to terminalize lifecycle.json for {Folder}", folderPath);
        }
    }

    private static List<LifecycleCheck> TerminalizeChecks(List<LifecycleCheck> checks, DateTime now, string note)
        => checks
            .Select(c => string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Status, "pending", StringComparison.OrdinalIgnoreCase)
                ? c with { Status = "skipped", FinishedAt = now, Detail = note }
                : c)
            .ToList();

    private static LifecycleSnapshot? ReadLifecycleSnapshot(string folderPath)
    {
        var path = Path.Combine(folderPath, "lifecycle.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<LifecycleSnapshot>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }
}
