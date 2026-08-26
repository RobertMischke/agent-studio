using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Tasks;

public enum TaskIntegrationRecoveryStatus
{
    Queued,
    AlreadyQueued,
    NotFound,
    NotEligible,
    BudgetExhausted,
    Failed,
}

public sealed record TaskIntegrationRecoveryResult(
    TaskIntegrationRecoveryStatus Status,
    string Message,
    int ConflictRequeues,
    int? Position = null,
    string? DeliveryRef = null,
    string? ResultSha = null,
    string? IntegrationBranch = null,
    string? FailureCode = null)
{
    public bool Queued => Status is TaskIntegrationRecoveryStatus.Queued
        or TaskIntegrationRecoveryStatus.AlreadyQueued;
}

public sealed record TaskIntegrationRecoveryRequest(
    string JobId,
    string? WatchPath,
    bool Automatic,
    int? MaxRequeues = null,
    string Source = "operator");

internal sealed record AcceptanceRailRecoveryState
{
    public int Version { get; init; } = 1;
    public int ConflictRequeues { get; init; }
    public string? PendingFingerprint { get; init; }
    public string? LastCompletedFingerprint { get; init; }
    public bool BudgetEscalated { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

/// <summary>
/// Restart-safe state for the bounded conflict recovery rail. The sidecar
/// travels with the task folder across lane moves. A pending fingerprint means
/// a process stopped after reserving a retry but before the Ready transition;
/// the next sweep resumes that retry without incrementing the budget again.
/// </summary>
internal static class AcceptanceRailRecoveryStateStore
{
    internal const string FileName = "acceptance-rail-recovery.json";
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static AcceptanceRailRecoveryState Read(string taskFolder, ILogger? logger = null)
    {
        var path = PathFor(taskFolder);
        if (!File.Exists(path)) return new AcceptanceRailRecoveryState();
        try
        {
            return JsonSerializer.Deserialize<AcceptanceRailRecoveryState>(
                       File.ReadAllText(path),
                       ReadOptions)
                   ?? new AcceptanceRailRecoveryState();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "acceptance-rail-state-read-failed path={Path}", path);
            return new AcceptanceRailRecoveryState();
        }
    }

    public static bool Write(
        string taskFolder,
        AcceptanceRailRecoveryState state,
        ILogger? logger = null)
    {
        var path = PathFor(taskFolder);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, WriteOptions));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "acceptance-rail-state-write-failed path={Path}", path);
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "AcceptanceRailRecoveryStateStore: temporary file cleanup");
            }
        }
    }

    private static string PathFor(string taskFolder)
        => Path.Combine(TaskPaths.LogsDir(taskFolder), FileName);
}

/// <summary>
/// Shared application boundary for a rebase-recoverable integration failure.
/// Both the operator endpoint and the deterministic acceptance rail use this
/// service, so pending intent, prompt enrichment, delivery supersession, Ready
/// ordering, retry accounting, and timeline evidence cannot drift.
/// </summary>
public sealed class TaskIntegrationRecoveryService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskIntegrationStatusService _integrationStatus;
    private readonly PipelineExecutionLog _pipeline;
    private readonly TaskMutationService _mutations;
    private readonly TaskStateMachine _states;
    private readonly TimelineLog _timeline;
    private readonly ILogger<TaskIntegrationRecoveryService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public TaskIntegrationRecoveryService(
        TaskScannerService scanner,
        TaskIntegrationStatusService integrationStatus,
        PipelineExecutionLog pipeline,
        TaskMutationService mutations,
        TaskStateMachine states,
        TimelineLog timeline,
        ILogger<TaskIntegrationRecoveryService> logger)
    {
        _scanner = scanner;
        _integrationStatus = integrationStatus;
        _pipeline = pipeline;
        _mutations = mutations;
        _states = states;
        _timeline = timeline;
        _logger = logger;
    }

    public int GetConflictRequeueCount(TaskInfo job)
    {
        var persisted = AcceptanceRailRecoveryStateStore.Read(job.FolderPath, _logger).ConflictRequeues;
        var legacy = _timeline.ReadAll(job.FolderPath).Count(entry =>
            entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued
            && !string.Equals(
                entry.Details?.GetValueOrDefault("reason"),
                IntegrationAgentRoundService.AttributionAmbiguousReason,
                StringComparison.Ordinal));
        return Math.Max(persisted, legacy);
    }

    public bool IsBudgetEscalated(TaskInfo job)
        => AcceptanceRailRecoveryStateStore.Read(job.FolderPath, _logger).BudgetEscalated;

    public bool MarkBudgetEscalated(TaskInfo job, int conflictRequeues)
    {
        var state = AcceptanceRailRecoveryStateStore.Read(job.FolderPath, _logger);
        return AcceptanceRailRecoveryStateStore.Write(
            job.FolderPath,
            state with
            {
                ConflictRequeues = Math.Max(state.ConflictRequeues, conflictRequeues),
                PendingFingerprint = null,
                BudgetEscalated = true,
                UpdatedAtUtc = DateTime.UtcNow,
            },
            _logger);
    }

    public async Task<TaskIntegrationRecoveryResult> TryQueueAsync(
        TaskIntegrationRecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        var initial = _scanner.FindJob(request.JobId, request.WatchPath);
        if (initial is null)
        {
            return new TaskIntegrationRecoveryResult(
                TaskIntegrationRecoveryStatus.NotFound,
                "Task not found.",
                0);
        }

        var gateKey = initial.WatchPath + "\0" + initial.Id;
        var gate = _gates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return QueueUnderGate(request);
        }
        finally
        {
            gate.Release();
        }
    }

    private TaskIntegrationRecoveryResult QueueUnderGate(TaskIntegrationRecoveryRequest request)
    {
        var job = _scanner.FindJob(request.JobId, request.WatchPath);
        if (job is null)
        {
            return new TaskIntegrationRecoveryResult(
                TaskIntegrationRecoveryStatus.NotFound,
                "Task not found.",
                0);
        }

        if (job.State is not (
                TaskStates.HumanReview
                or TaskStates.Escalated
                or TaskStates.Completed
                or TaskStates.Archive))
        {
            return new TaskIntegrationRecoveryResult(
                TaskIntegrationRecoveryStatus.NotEligible,
                $"Integration recovery requires a parked or terminal delivered task; the task is in {job.State}.",
                GetConflictRequeueCount(job));
        }

        // The board integration projection deliberately excludes Escalated.
        // Recovery asks the same policy by projecting that parked delivery as
        // Human Review, without changing any durable task state.
        var statusJob = job.State == TaskStates.Escalated
            ? job with { State = TaskStates.HumanReview }
            : job;
        var status = _integrationStatus.BuildLookup([statusJob]).GetValueOrDefault(job.TaskKey);
        if (status?.Status != IntegrationStatuses.ConflictSkipped
            || status.Failure?.RebaseRecoveryAvailable != true)
        {
            return new TaskIntegrationRecoveryResult(
                TaskIntegrationRecoveryStatus.NotEligible,
                "Rebase recovery is not available for this integration failure.",
                GetConflictRequeueCount(job),
                IntegrationBranch: status?.IntegrationBranch,
                FailureCode: status?.Failure?.Code);
        }

        var mergeStep = _pipeline.Read(job.FolderPath)?.Steps.LastOrDefault(
            step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        var failure = mergeStep is null
            ? null
            : AcceptedIntegrationFailurePolicy.Classify(
                mergeStep.Status,
                mergeStep.Verdict,
                mergeStep.Reason,
                mergeStep.VerdictSummary,
                mergeStep.FailureCode);
        if (failure?.RebaseRecoveryAvailable != true)
        {
            return new TaskIntegrationRecoveryResult(
                TaskIntegrationRecoveryStatus.NotEligible,
                "Rebase recovery is not available for the latest integration attempt.",
                GetConflictRequeueCount(job),
                IntegrationBranch: status.IntegrationBranch,
                FailureCode: failure?.Code);
        }

        var subject = ReviewSubjectStore.Read(job.FolderPath);
        if (subject is null || string.IsNullOrWhiteSpace(subject.ResultRef))
        {
            return new TaskIntegrationRecoveryResult(
                TaskIntegrationRecoveryStatus.NotEligible,
                "The accepted task has no fenced remote delivery ref to recover.",
                GetConflictRequeueCount(job),
                IntegrationBranch: status.IntegrationBranch,
                FailureCode: failure.Code);
        }

        var state = AcceptanceRailRecoveryStateStore.Read(job.FolderPath, _logger);
        var observedCount = Math.Max(state.ConflictRequeues, GetConflictRequeueCount(job));
        if (request.MaxRequeues is int maxRequeues
            && observedCount >= Math.Max(1, maxRequeues))
        {
            return new TaskIntegrationRecoveryResult(
                TaskIntegrationRecoveryStatus.BudgetExhausted,
                $"Integration recovery exhausted the configured limit of {Math.Max(1, maxRequeues)} conflict requeues.",
                observedCount,
                DeliveryRef: subject.ResultRef,
                ResultSha: subject.ResultSha,
                IntegrationBranch: status.IntegrationBranch,
                FailureCode: failure.Code);
        }

        var fingerprint = Fingerprint(subject, mergeStep!, failure.Code);
        var resumesPending = string.Equals(
            state.PendingFingerprint,
            fingerprint,
            StringComparison.Ordinal);
        var retry = resumesPending ? observedCount : observedCount + 1;
        if (!resumesPending)
        {
            state = state with
            {
                ConflictRequeues = retry,
                PendingFingerprint = fingerprint,
                BudgetEscalated = false,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            if (!AcceptanceRailRecoveryStateStore.Write(job.FolderPath, state, _logger))
            {
                return new TaskIntegrationRecoveryResult(
                    TaskIntegrationRecoveryStatus.Failed,
                    "The conflict retry could not be reserved durably.",
                    observedCount,
                    FailureCode: failure.Code);
            }
        }

        var marker = $"<!-- agent-studio:integration-recovery:{fingerprint} -->";
        var prompt = BuildPrompt(job, subject, status.IntegrationBranch, failure, retry, marker);
        var intent = _mutations.SavePendingIntent(
            job.Id,
            ContinueModes.Steer,
            prompt,
            reason: failure.Code,
            activeJobId: null,
            watchPath: job.WatchPath);
        if (intent is null)
        {
            return Failed("The integration recovery steer intent could not be persisted.");
        }

        var promptPath = Path.Combine(job.FolderPath, "prompt.md");
        var promptAlreadyContainsMarker = File.Exists(promptPath)
            && File.ReadAllText(promptPath).Contains(marker, StringComparison.Ordinal);
        if (!promptAlreadyContainsMarker)
        {
            _mutations.AppendContinuationNote(job.Id, prompt, job.WatchPath);
            if (!File.Exists(promptPath)
                || !File.ReadAllText(promptPath).Contains(marker, StringComparison.Ordinal))
            {
                return Failed("The recovery intent was persisted, but its prompt note could not be appended.");
            }
        }

        var supersession = _mutations.SupersedeCurrentDeliveryOnFolder(
            job.FolderPath,
            TaskCommitSupersession.PendingAttempt);
        if (!supersession.Succeeded)
        {
            return Failed("The recovery intent was persisted, but the superseded delivery history could not be marked.");
        }

        var position = _states.PromoteToReadyTop(
            job.Id,
            job.WatchPath,
            cause: TimelineActors.System,
            transitionCause: LaneChangeCauses.IntegrationRecovery,
            transitionDetail: failure.Code,
            expectedSourceState: job.State);
        var queued = _scanner.FindJob(job.Id, job.WatchPath);
        if (queued is null || queued.State != TaskStates.Ready)
        {
            return Failed("The recovery prompt was persisted, but the task could not be queued in Ready.");
        }

        _timeline.Append(
            queued.FolderPath,
            TimelineEventKinds.IntegrationRecoveryQueued,
            TimelineActors.System,
            $"Integration recovery queued: rebase {subject.ResultRef} onto {status.IntegrationBranch}.",
            payloadRef: "prompt.md",
            details: new Dictionary<string, string>
            {
                ["automatic"] = request.Automatic ? "true" : "false",
                ["source"] = request.Source,
                ["deliveryRef"] = subject.ResultRef,
                ["resultSha"] = subject.ResultSha,
                ["integrationBranch"] = status.IntegrationBranch,
                ["mode"] = ContinueModes.Steer,
                ["reason"] = failure.Code,
                ["retry"] = retry.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["maxRequeues"] = request.MaxRequeues?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                ["supersededCommits"] = supersession.MarkedCommits.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["fingerprint"] = fingerprint,
            });

        AcceptanceRailRecoveryStateStore.Write(
            queued.FolderPath,
            state with
            {
                ConflictRequeues = retry,
                PendingFingerprint = null,
                LastCompletedFingerprint = fingerprint,
                UpdatedAtUtc = DateTime.UtcNow,
            },
            _logger);
        _logger.LogInformation(
            "integration-recovery-queued project={Project} job={JobId} source={Source} retry={Retry} position={Position}",
            queued.ProjectName,
            queued.Id,
            request.Source,
            retry,
            position);

        return new TaskIntegrationRecoveryResult(
            resumesPending
                ? TaskIntegrationRecoveryStatus.AlreadyQueued
                : TaskIntegrationRecoveryStatus.Queued,
            "Integration recovery was queued in Ready.",
            retry,
            position,
            subject.ResultRef,
            subject.ResultSha,
            status.IntegrationBranch,
            failure.Code);

        TaskIntegrationRecoveryResult Failed(string message)
            => new(
                TaskIntegrationRecoveryStatus.Failed,
                message,
                retry,
                DeliveryRef: subject.ResultRef,
                ResultSha: subject.ResultSha,
                IntegrationBranch: status.IntegrationBranch,
                FailureCode: failure.Code);
    }

    private static string BuildPrompt(
        TaskInfo job,
        ReviewSubjectRecord subject,
        string integrationBranch,
        AcceptedIntegrationFailure failure,
        int retry,
        string marker)
        =>
            $"## STEER: Rebase the existing delivery for {job.Key ?? job.Id}\n\n"
            + marker + "\n\n"
            + $"Integration retry {retry} is required because the delivery could not be merged ({failure.Label}: {failure.Reason}). "
            + $"Resume the existing delivery branch '{subject.ResultRef}' at the fenced result {subject.ResultSha}. "
            + $"Fetch the latest '{integrationBranch}', rebase the delivery onto it, and resolve every conflict conservatively without dropping the task's intended changes. "
            + "Rerun the relevant gate and tests, then deliver again through the normal terminal sentinel. "
            + "Do not redo the feature work. Do not merge or push the integration branch yourself; publish only the updated delivery branch for a new delivery gate and review round.";

    private static string Fingerprint(
        ReviewSubjectRecord subject,
        PipelineStepExecution mergeStep,
        string failureCode)
    {
        var value = string.Join(
            "|",
            subject.ResultSha,
            subject.ResultRef,
            mergeStep.Attempt?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            mergeStep.CompletedAt?.ToUniversalTime().ToString("O"),
            mergeStep.Verdict,
            failureCode);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..20];
    }
}
