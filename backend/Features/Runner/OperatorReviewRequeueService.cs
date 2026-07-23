using System.Text.Json;

namespace AgentStudio.Runner;

/// <summary>
/// Opens a fresh review-attempt epoch when a human deliberately moves a task
/// out of a human-decision lane. Historical review evidence is retained under
/// <c>results/history/</c>, while the active task root is cleared of verdict
/// residue so the next auto-review pass cannot act on a prior escalation.
/// </summary>
public sealed class OperatorReviewRequeueService
{
    public const string EpochFileName = "review-attempt.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string? _workspaceRoot;
    private readonly TimelineLog? _timeline;
    private readonly ILogger<OperatorReviewRequeueService> _logger;

    public OperatorReviewRequeueService(
        IConfiguration configuration,
        ILogger<OperatorReviewRequeueService> logger,
        TimelineLog? timeline = null)
        : this(configuration["TaskRepository"], logger, timeline)
    {
    }

    internal OperatorReviewRequeueService(
        string? workspaceRoot,
        ILogger<OperatorReviewRequeueService> logger,
        TimelineLog? timeline = null)
    {
        _workspaceRoot = workspaceRoot;
        _logger = logger;
        _timeline = timeline;
    }

    public static bool IsOperatorRequeue(
        string fromState,
        string toState,
        string? cause)
    {
        var human = !string.IsNullOrWhiteSpace(cause)
            && (string.Equals(cause, "human", StringComparison.OrdinalIgnoreCase)
                || cause.StartsWith("human:", StringComparison.OrdinalIgnoreCase));
        if (!human) return false;

        var leavesDecisionLane =
            string.Equals(fromState, TaskStates.Escalated, StringComparison.Ordinal)
            || string.Equals(fromState, TaskStates.HumanReview, StringComparison.Ordinal);
        if (!leavesDecisionLane) return false;

        return !string.Equals(toState, TaskStates.Escalated, StringComparison.Ordinal)
            && !string.Equals(toState, TaskStates.HumanReview, StringComparison.Ordinal)
            && !string.Equals(toState, TaskStates.Completed, StringComparison.Ordinal)
            && !string.Equals(toState, TaskStates.Archive, StringComparison.Ordinal);
    }

    /// <summary>
    /// Records the new epoch, appends its decision-journal boundary, rotates
    /// active verdict artefacts, and emits the operator event. The lane move has
    /// already landed when this method runs, so every write is best-effort and
    /// must never undo the transition.
    /// </summary>
    public OperatorReviewRequeueResult Apply(
        string folderPath,
        string jobId,
        string project,
        string fromState,
        string toState,
        string? reason,
        string actor)
    {
        var now = DateTime.UtcNow;
        var previousEpoch = ReadEpoch(folderPath);
        var epoch = previousEpoch + 1;
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "Operator explicitly reopened the task for a fresh assessment."
            : reason.Trim();

        WriteEpoch(folderPath, new ReviewAttemptEpoch(
            Epoch: epoch,
            StartedAt: now,
            Actor: actor,
            Reason: normalizedReason,
            FromState: fromState,
            ToState: toState));

        if (!string.IsNullOrWhiteSpace(_workspaceRoot)
            && !string.IsNullOrWhiteSpace(project))
        {
            try
            {
                ReviewDecisionLog.Append(_workspaceRoot!, new ReviewDecisionRecord(
                    CreatedAt: now,
                    JobId: jobId,
                    Project: project,
                    Kind: ReviewDecisionKind.OperatorRequeue,
                    Reason: normalizedReason,
                    Prompt: "(operator lane move)",
                    Response: "(fresh review requested)",
                    FollowUp: string.Empty)
                {
                    AttemptEpoch = epoch,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "operator-requeue: failed to append epoch boundary for {Project}/{JobId}",
                    project, jobId);
            }
        }

        var rotated = RotateVerdictArtifacts(folderPath, epoch, fromState, now);
        var historyRef = rotated.Count == 0
            ? null
            : Path.GetDirectoryName(rotated[0])?.Replace('\\', '/');

        try
        {
            _timeline?.Append(
                folderPath,
                TimelineEventKinds.OperatorRequeued,
                actor,
                $"Operator reopened the task for fresh assessment: {normalizedReason}",
                payloadRef: historyRef,
                details: new Dictionary<string, string>
                {
                    ["from"] = fromState,
                    ["to"] = toState,
                    ["reason"] = normalizedReason,
                    ["attemptEpoch"] = epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["rotatedArtifacts"] = rotated.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "operator-requeue: failed to append timeline event for {Project}/{JobId}",
                project, jobId);
        }

        return new OperatorReviewRequeueResult(epoch, rotated, normalizedReason);
    }

    public static int ReadEpoch(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return 0;
        var path = EpochPath(folderPath);
        if (!File.Exists(path)) return 0;
        try
        {
            var value = JsonSerializer.Deserialize<ReviewAttemptEpoch>(File.ReadAllText(path), Json);
            return Math.Max(0, value?.Epoch ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    private void WriteEpoch(string folderPath, ReviewAttemptEpoch value)
    {
        try
        {
            var metadata = Path.Combine(folderPath, ".metadata");
            Directory.CreateDirectory(metadata);
            var path = EpochPath(folderPath);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "operator-requeue: failed to persist review attempt epoch in {Folder}",
                folderPath);
        }
    }

    private List<string> RotateVerdictArtifacts(
        string folderPath,
        int epoch,
        string fromState,
        DateTime now)
    {
        var candidates = new List<string>();
        AddMatches(candidates, folderPath, "aspect-*.md");
        AddMatches(candidates, folderPath, "aspect-*.json");
        AddMatches(candidates, folderPath, "code-review-*.md");
        AddFile(candidates, folderPath, PipelineExecutionLog.FileName);
        AddFile(candidates, folderPath, PostProcessingOutcomeLog.FileName);
        AddFile(candidates, folderPath, "lifecycle.json");
        AddFile(candidates, folderPath, "orchestrator-follow-up.md");

        var postSteps = Path.Combine(folderPath, "post-steps");
        if (Directory.Exists(postSteps)) candidates.Add(postSteps);

        var statusPath = Path.Combine(folderPath, "status.md");
        if (File.Exists(statusPath)
            && (string.Equals(fromState, TaskStates.Escalated, StringComparison.Ordinal)
                || IsEscalationStatus(statusPath)))
        {
            candidates.Add(statusPath);
        }

        if (candidates.Count == 0) return [];

        var relativeHistory = Path.Combine(
            "results",
            "history",
            $"review-epoch-{epoch:0000}",
            $"operator-requeue-{now:yyyyMMddTHHmmssfffZ}");
        var historyDir = Path.Combine(folderPath, relativeHistory);
        Directory.CreateDirectory(historyDir);

        var rotated = new List<string>();
        foreach (var source in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var name = Path.GetFileName(source);
                var destination = UniqueDestination(historyDir, name);
                if (Directory.Exists(source))
                    Directory.Move(source, destination);
                else if (File.Exists(source))
                    File.Move(source, destination);
                else
                    continue;

                rotated.Add(Path.GetRelativePath(folderPath, destination).Replace('\\', '/'));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "operator-requeue: failed to rotate review artefact {Artifact}",
                    source);
            }
        }

        return rotated;
    }

    private static bool IsEscalationStatus(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return text.Contains("Result: Escalated", StringComparison.OrdinalIgnoreCase)
                || text.Contains("routed to 5e-escalated", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void AddMatches(List<string> paths, string folderPath, string pattern)
    {
        try { paths.AddRange(Directory.EnumerateFiles(folderPath, pattern, SearchOption.TopDirectoryOnly)); }
        catch { }
    }

    private static void AddFile(List<string> paths, string folderPath, string name)
    {
        var path = Path.Combine(folderPath, name);
        if (File.Exists(path)) paths.Add(path);
    }

    private static string UniqueDestination(string directory, string name)
    {
        var candidate = Path.Combine(directory, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            candidate = Path.Combine(directory, $"{stem}-{i}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private static string EpochPath(string folderPath)
        => Path.Combine(folderPath, ".metadata", EpochFileName);

    internal sealed record ReviewAttemptEpoch(
        int Epoch,
        DateTime StartedAt,
        string Actor,
        string Reason,
        string FromState,
        string ToState);
}

public sealed record OperatorReviewRequeueResult(
    int Epoch,
    IReadOnlyList<string> RotatedArtifacts,
    string Reason);
