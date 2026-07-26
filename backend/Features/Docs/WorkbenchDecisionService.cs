using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentStudio.Docs;

public static class WorkbenchDecisionContracts
{
    public static bool SafeOperationId(string? value) =>
        value is { Length: >= 8 and <= 128 }
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    public static string? ValidateTaskDraft(WorkbenchTaskDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Trim().Length > 240)
            return "task.title is required and must be at most 240 characters.";
        if (string.IsNullOrWhiteSpace(draft.Goal) || draft.Goal.Trim().Length > 20_000)
            return "task.goal is required and must be at most 20000 characters.";
        if (draft.AcceptanceCriteria.Count is 0 or > 100
            || draft.AcceptanceCriteria.Any(item =>
                string.IsNullOrWhiteSpace(item) || item.Trim().Length > 2000))
            return "task.acceptanceCriteria needs 1-100 non-empty bounded items.";
        if (draft.EvidenceLinks.Count > 100
            || draft.EvidenceLinks.Any(item =>
                string.IsNullOrWhiteSpace(item) || item.Trim().Length > 2000))
            return "task.evidenceLinks contains an invalid item.";
        if (draft.RelatedTaskKeys.Count > 100
            || draft.RelatedTaskKeys.Any(item =>
                string.IsNullOrWhiteSpace(item) || item.Trim().Length > 100))
            return "task.relatedTaskKeys contains an invalid item.";
        if (!TaskStates.All.Contains(draft.InitialLane, StringComparer.Ordinal))
            return "task.initialLane is invalid.";
        if (!TaskModes.IsValid(draft.Mode))
            return "task.mode is invalid.";
        if (!TaskTypes.All.Contains(draft.TaskType, StringComparer.Ordinal))
            return "task.taskType is invalid.";
        return null;
    }
}

public sealed record WorkbenchTaskDraft
{
    public string Title { get; init; } = "";
    public string Goal { get; init; } = "";
    public List<string> AcceptanceCriteria { get; init; } = [];
    public List<string> EvidenceLinks { get; init; } = [];
    public string? ChosenOption { get; init; }
    public List<string> RelatedTaskKeys { get; init; } = [];
    public string? TargetProject { get; init; }
    public string InitialLane { get; init; } = TaskStates.Preparation;
    public string Mode { get; init; } = TaskModes.Coding;
    public string TaskType { get; init; } = TaskTypes.Feature;
    public string? Agent { get; init; }
    public string? CliType { get; init; }
    public string? Model { get; init; }
    public string? ThinkingLevel { get; init; }
}

public sealed record PrepareWorkbenchDecisionRequest
{
    public string OperationId { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string? ExpectedRevision { get; init; }
    public string? ExpectedFingerprint { get; init; }
    public string Actor { get; init; } = "";
    public string? ArchiveReason { get; init; }
    public WorkbenchTaskDraft? Task { get; init; }
}

public sealed record ConfirmWorkbenchDecisionRequest
{
    public string OperationId { get; init; } = "";
    public string? ExpectedRevision { get; init; }
    public string? ExpectedFingerprint { get; init; }
    public string Actor { get; init; } = "";
    public bool Confirmed { get; init; }
}

public sealed record WorkbenchDecisionResult
{
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
    public string WorkbenchId { get; init; } = "";
    public string OperationId { get; init; } = "";
    public string? Outcome { get; init; }
    public string? DecisionStage { get; init; }
    public string? Revision { get; init; }
    public string? Fingerprint { get; init; }
    public string[] SpawnedTaskKeys { get; init; } = [];
    public bool Idempotent { get; init; }
}

public sealed record WorkbenchTaskReceipt(string JobId, string TaskKey);

/// <summary>
/// Failure-injection seam for the repository half of a Workbench decision.
/// Production writes are atomic and flushed before the bounded path commit.
/// </summary>
public interface IWorkbenchDecisionRepository
{
    void WriteDescriptorDurably(string descriptorPath, string content);
    GitCommitResult CommitDescriptor(string root, string descriptorRelPath, string message);
}

public sealed class WorkbenchDecisionRepository : IWorkbenchDecisionRepository
{
    private readonly GitService _git;

    public WorkbenchDecisionRepository(GitService git) => _git = git;

    public void WriteDescriptorDurably(string descriptorPath, string content)
    {
        var directory = Path.GetDirectoryName(descriptorPath)
            ?? throw new IOException("Workbench descriptor has no parent directory.");
        var temporary = Path.Combine(
            directory, $".workbench.json.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       16 * 1024, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, descriptorPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The canonical descriptor was never pointed at the temp file.
                // A later repository hygiene pass may remove this bounded peer.
                SilentCatch.Note(ex, "Workbench descriptor temp-file cleanup failed.");
            }
        }
    }

    public GitCommitResult CommitDescriptor(
        string root, string descriptorRelPath, string message) =>
        _git.CommitPaths(root, message, [descriptorRelPath]);
}

/// <summary>
/// Failure-injection seam for task creation and partial-failure reconciliation.
/// </summary>
public interface IWorkbenchDecisionTaskMutation
{
    WorkbenchTaskReceipt CreateOrFind(
        string navigationProject,
        string workbenchId,
        string operationId,
        WorkbenchTaskDraft draft,
        IReadOnlyList<string> sourceTaskKeys);
}

public sealed class WorkbenchDecisionTaskMutation : IWorkbenchDecisionTaskMutation
{
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly ProjectRegistry _registry;
    private readonly ILogger<WorkbenchDecisionTaskMutation> _logger;

    public WorkbenchDecisionTaskMutation(
        TaskScannerService scanner,
        TaskMutationService mutations,
        ProjectRegistry registry,
        ILogger<WorkbenchDecisionTaskMutation> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _registry = registry;
        _logger = logger;
    }

    public WorkbenchTaskReceipt CreateOrFind(
        string navigationProject,
        string workbenchId,
        string operationId,
        WorkbenchTaskDraft draft,
        IReadOnlyList<string> sourceTaskKeys)
    {
        var targetProject = string.IsNullOrWhiteSpace(draft.TargetProject)
            ? navigationProject
            : draft.TargetProject.Trim();
        var targetWatchPath = ResolveTargetWatchPath(targetProject);
        var requestedId = RequestedTaskId(workbenchId, operationId);
        var marker = $"workbench-operation:{operationId}";
        var existing = targetWatchPath == null
            ? null
            : _scanner.ScanAllJobsWithArchive()
                .FirstOrDefault(task =>
                    string.Equals(task.Id, requestedId, StringComparison.Ordinal)
                    && WatchPathComparison.PathsEqual(task.WatchPath, targetWatchPath));
        if (existing != null)
        {
            var promptPath = Path.Combine(existing.FolderPath, "prompt.md");
            var prompt = File.Exists(promptPath) ? File.ReadAllText(promptPath) : "";
            if (!prompt.Contains(marker, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The deterministic task id is owned by a different operation.");
            RepairRelationshipsAndLedger(
                existing, targetProject, workbenchId, operationId,
                sourceTaskKeys.Concat(draft.RelatedTaskKeys).ToArray());
            return new(existing.Id, existing.Key ?? existing.TaskKey ?? existing.Id);
        }

        var promptMarkdown = BuildPrompt(workbenchId, operationId, draft);
        var createRequest = new CreateTaskRequest
        {
            Id = requestedId,
            Title = draft.Title.Trim(),
            Project = targetProject,
            TargetState = draft.InitialLane,
            CliType = NullIfBlank(draft.CliType),
            Model = NullIfBlank(draft.Model),
            ThinkingLevel = NullIfBlank(draft.ThinkingLevel),
            Mode = draft.Mode,
            TaskType = draft.TaskType,
            PromptMarkdown = promptMarkdown,
        };
        if (!string.IsNullOrWhiteSpace(draft.Agent))
            createRequest = createRequest with { Agent = draft.Agent.Trim() };
        var createdId = _mutations.CreateJob(createRequest);
        if (string.IsNullOrWhiteSpace(createdId))
            throw new InvalidOperationException("The feature task could not be created.");
        var created = _scanner.ScanAllJobs()
            .FirstOrDefault(task =>
                string.Equals(task.Id, createdId, StringComparison.Ordinal)
                && (targetWatchPath == null
                    || WatchPathComparison.PathsEqual(task.WatchPath, targetWatchPath)))
            ?? throw new IOException("The created feature task receipt could not be read.");
        RepairRelationshipsAndLedger(
            created, targetProject, workbenchId, operationId,
            sourceTaskKeys.Concat(draft.RelatedTaskKeys).ToArray());
        return new(created.Id, created.Key ?? created.TaskKey ?? created.Id);
    }

    internal static string RequestedTaskId(string workbenchId, string operationId)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(operationId))).ToLowerInvariant()[..16];
        var prefix = workbenchId.Length > 40 ? workbenchId[..40] : workbenchId;
        return $"workbench-{prefix}-{hash}";
    }

    private string? ResolveTargetWatchPath(string targetProject)
    {
        var record = targetProject.StartsWith("PROJ-", StringComparison.OrdinalIgnoreCase)
            ? _registry.FindById(targetProject)
            : _registry.FindByShortCode(targetProject)
              ?? _registry.FindByIdOrDisplayName(targetProject);
        if (!string.IsNullOrWhiteSpace(record?.StorageLocation))
            return record.StorageLocation;
        return _scanner.GetWatchPaths()
            .FirstOrDefault(entry =>
                string.Equals(entry.Name, targetProject, StringComparison.OrdinalIgnoreCase))
            ?.Path;
    }

    private void RepairRelationshipsAndLedger(
        TaskInfo created,
        string targetProject,
        string workbenchId,
        string operationId,
        IReadOnlyList<string> relatedTaskKeys)
    {
        var related = relatedTaskKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (related.Count > 0)
        {
            var merged = created.References with
            {
                RelatedTo = created.References.RelatedTo
                    .Concat(related)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
            if (!_mutations.SetTaskReferences(created.Id, merged, created.WatchPath))
                throw new IOException("The feature task relationship could not be persisted.");
        }

        var reason = $"workbench-decision:{workbenchId}:{operationId}";
        foreach (var sourceKey in related)
        {
            var source = _scanner.FindJob(sourceKey);
            if (source == null || source.Mode != TaskModes.Planning) continue;
            if (SpawnedTaskLedger.Read(source.FolderPath, _logger)
                .Any(record => string.Equals(record.Reason, reason, StringComparison.Ordinal)))
                continue;
            if (!SpawnedTaskLedger.Append(source.FolderPath, new SpawnedTaskRecord
                {
                    At = DateTime.UtcNow,
                    SourceKey = source.Key ?? source.TaskKey,
                    TargetProject = targetProject,
                    TargetKey = created.Key ?? created.TaskKey,
                    TargetJobId = created.Id,
                    Reason = reason,
                }, _logger))
                throw new IOException("The planning task spawn ledger could not be persisted.");
        }
    }

    private static string BuildPrompt(
        string workbenchId, string operationId, WorkbenchTaskDraft draft)
    {
        var acceptance = string.Join(
            Environment.NewLine,
            draft.AcceptanceCriteria.Select(item => $"- {item.Trim()}"));
        var evidence = draft.EvidenceLinks.Count == 0
            ? ""
            : $"{Environment.NewLine}{Environment.NewLine}## Evidence{Environment.NewLine}{Environment.NewLine}"
              + string.Join(Environment.NewLine, draft.EvidenceLinks.Select(item => $"- {item.Trim()}"));
        var option = string.IsNullOrWhiteSpace(draft.ChosenOption)
            ? ""
            : $"{Environment.NewLine}{Environment.NewLine}Chosen option: {draft.ChosenOption.Trim()}";
        return $"""
               <!-- workbench-operation:{operationId} -->
               Implement the confirmed feature decision from Workbench `{workbenchId}`.

               ## Goal

               {draft.Goal.Trim()}

               ## Acceptance criteria

               {acceptance}{option}{evidence}
               """;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Repository-backed Workbench decision lifecycle. Every project is serialized
/// so one Workbench cannot race itself and operation-id ownership checks remain
/// atomic across sibling Workbenches.
/// </summary>
public sealed class WorkbenchDecisionService
{
    private static readonly ConcurrentDictionary<string, object> ProjectGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions DescriptorJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly WorkbenchCatalogueService _catalogue;
    private readonly IWorkbenchDecisionRepository _repository;
    private readonly IWorkbenchDecisionTaskMutation _tasks;
    private readonly GitService _git;
    private readonly ILogger<WorkbenchDecisionService> _logger;

    public WorkbenchDecisionService(
        WorkbenchCatalogueService catalogue,
        IWorkbenchDecisionRepository repository,
        IWorkbenchDecisionTaskMutation tasks,
        GitService git,
        ILogger<WorkbenchDecisionService> logger)
    {
        _catalogue = catalogue;
        _repository = repository;
        _tasks = tasks;
        _git = git;
        _logger = logger;
    }

    public WorkbenchDecisionResult Prepare(
        string projectName, string workbenchId, PrepareWorkbenchDecisionRequest request)
    {
        var started = Stopwatch.GetTimestamp();
        WorkbenchDecisionResult result;
        lock (ProjectGates.GetOrAdd(projectName, _ => new object()))
        {
            result = PrepareLocked(projectName, workbenchId, request);
        }
        LogOutcome("prepare", projectName, workbenchId, request.OperationId,
            request.Outcome, result, started);
        return result;
    }

    public WorkbenchDecisionResult Confirm(
        string projectName, string workbenchId, ConfirmWorkbenchDecisionRequest request)
    {
        var started = Stopwatch.GetTimestamp();
        WorkbenchDecisionResult result;
        lock (ProjectGates.GetOrAdd(projectName, _ => new object()))
        {
            result = ConfirmLocked(projectName, workbenchId, request);
        }
        LogOutcome("confirm", projectName, workbenchId, request.OperationId,
            result.Outcome, result, started);
        return result;
    }

    private WorkbenchDecisionResult PrepareLocked(
        string projectName, string workbenchId, PrepareWorkbenchDecisionRequest request)
    {
        var validation = ValidatePrepare(request);
        if (validation != null) return Error(workbenchId, request.OperationId, "validation", validation);
        var snapshot = _catalogue.ResolveCanonicalForMutation(projectName, workbenchId);
        if (snapshot == null)
            return Error(workbenchId, request.OperationId, "not-canonical",
                "Workbench is missing, invalid, ambiguous, or is a read-only legacy pilot.");
        if (snapshot.Revision == null)
            return Error(workbenchId, request.OperationId, "repository-not-git",
                "Workbench decisions require a readable Git repository.");
        if (_catalogue.OperationIdOwnedByAnotherWorkbench(
                projectName, request.OperationId, workbenchId))
            return Error(workbenchId, request.OperationId, "operation-id-conflict",
                "operationId is already owned by another Workbench.");

        var existing = snapshot.Descriptor["decision"] as JsonObject;
        if (existing != null)
        {
            if (String(existing, "operationId") != request.OperationId
                || String(existing, "outcome") != request.Outcome)
                return Error(workbenchId, request.OperationId, "operation-id-conflict",
                    "Workbench already owns a different durable decision operation.");
            if (!EquivalentPreparedDecision(existing, request))
                return Error(workbenchId, request.OperationId, "operation-id-conflict",
                    "operationId was retried with different decision content.");
            if (!SourceFenceMatches(existing, request.ExpectedRevision, request.ExpectedFingerprint))
                return Error(workbenchId, request.OperationId, "stale-revision",
                    "Preparation retry does not match the original source fence.");
            if (snapshot.Dirty)
                return RecoverCommit(snapshot, workbenchId, request.OperationId,
                    request.Outcome, DecisionStage(existing), "prepare");
            return Success(snapshot, workbenchId, request.OperationId,
                request.Outcome, DecisionStage(existing), idempotent: true);
        }

        if (snapshot.Dirty)
            return Error(workbenchId, request.OperationId, "dirty-descriptor",
                "Workbench descriptor or entrypoint has uncommitted changes.");
        if (!FenceMatches(snapshot, request.ExpectedRevision, request.ExpectedFingerprint))
            return Error(workbenchId, request.OperationId, "stale-revision",
                "Expected Workbench revision or fingerprint is stale.");
        var lifecycle = String(snapshot.Descriptor, "lifecycleState");
        if (lifecycle is "decided" or "done")
            return Error(workbenchId, request.OperationId, "already-settled",
                "A settled Workbench cannot prepare another decision.");

        var now = Timestamp();
        var decision = new JsonObject
        {
            ["outcome"] = request.Outcome,
            ["state"] = "pending",
            ["operationId"] = request.OperationId,
            ["sourceRevision"] = snapshot.Revision,
            ["sourceFingerprint"] = snapshot.Fingerprint,
            ["preparedAt"] = now,
            ["preparedBy"] = request.Actor.Trim(),
            ["confirmedAt"] = null,
            ["confirmedBy"] = null,
            ["decidedAt"] = null,
            ["spawnedTaskKeys"] = new JsonArray(),
        };
        if (request.Outcome == "archive")
            decision["reason"] = request.ArchiveReason!.Trim();
        else
            decision["taskDraft"] = JsonSerializer.SerializeToNode(request.Task, DescriptorJson);
        snapshot.Descriptor["decision"] = decision;
        Transition(snapshot.Descriptor, "review-requested", request.Actor,
            now, $"Decision {request.OperationId} prepared for {request.Outcome}.");
        return Persist(snapshot, workbenchId, request.OperationId, request.Outcome,
            "prepared", $"workbench: prepare {workbenchId} decision");
    }

    private WorkbenchDecisionResult ConfirmLocked(
        string projectName, string workbenchId, ConfirmWorkbenchDecisionRequest request)
    {
        var validation = ValidateConfirm(request);
        if (validation != null) return Error(workbenchId, request.OperationId, "validation", validation);
        var snapshot = _catalogue.ResolveCanonicalForMutation(projectName, workbenchId);
        if (snapshot == null)
            return Error(workbenchId, request.OperationId, "not-canonical",
                "Workbench is missing, invalid, ambiguous, or is a read-only legacy pilot.");
        if (snapshot.Revision == null)
            return Error(workbenchId, request.OperationId, "repository-not-git",
                "Workbench decisions require a readable Git repository.");
        var decision = snapshot.Descriptor["decision"] as JsonObject;
        if (decision == null || String(decision, "operationId") != request.OperationId)
            return Error(workbenchId, request.OperationId, "operation-id-conflict",
                "Prepared decision operation was not found.");
        var outcome = String(decision, "outcome")!;

        if (String(decision, "state") == "succeeded")
        {
            if (!StoredConfirmationFenceMatches(decision, request))
                return Error(workbenchId, request.OperationId, "stale-revision",
                    "Confirmation retry does not match the original confirmation fence.");
            if (snapshot.Dirty)
                return RecoverCommit(snapshot, workbenchId, request.OperationId,
                    outcome, DecisionStage(decision), "confirm");
            return Success(snapshot, workbenchId, request.OperationId,
                outcome, DecisionStage(decision), idempotent: true);
        }

        var alreadyConfirmed = String(decision, "confirmedAt") != null;
        if (alreadyConfirmed)
        {
            if (!StoredConfirmationFenceMatches(decision, request))
                return Error(workbenchId, request.OperationId, "stale-revision",
                    "Confirmation retry does not match the original confirmation fence.");
            if (snapshot.Dirty)
            {
                var recovery = RecoverCommit(snapshot, workbenchId, request.OperationId,
                    outcome, DecisionStage(decision), "confirm-pending");
                if (!recovery.Success) return recovery;
                snapshot = _catalogue.ResolveCanonicalForMutation(projectName, workbenchId)!;
                decision = (JsonObject)snapshot.Descriptor["decision"]!;
            }
        }
        else
        {
            if (snapshot.Dirty)
                return Error(workbenchId, request.OperationId, "dirty-descriptor",
                    "Workbench descriptor or entrypoint has uncommitted changes.");
            if (!FenceMatches(snapshot, request.ExpectedRevision, request.ExpectedFingerprint))
                return Error(workbenchId, request.OperationId, "stale-revision",
                    "Expected prepared Workbench revision or fingerprint is stale.");
            var confirmedAt = Timestamp();
            decision["confirmedAt"] = confirmedAt;
            decision["confirmedBy"] = request.Actor.Trim();
            decision["confirmationExpectedRevision"] = NullIfBlank(request.ExpectedRevision);
            decision["confirmationExpectedFingerprint"] = NullIfBlank(request.ExpectedFingerprint);
            decision["state"] = "pending";
            decision.Remove("failure");
            decision.Remove("failedAt");
            Transition(snapshot.Descriptor, "review-requested", request.Actor,
                confirmedAt, $"Decision {request.OperationId} visibly confirmed.");
            var pending = Persist(snapshot, workbenchId, request.OperationId, outcome,
                "pending", $"workbench: confirm {workbenchId} decision");
            if (!pending.Success) return pending;
            snapshot = _catalogue.ResolveCanonicalForMutation(projectName, workbenchId)!;
            decision = (JsonObject)snapshot.Descriptor["decision"]!;
        }

        if (String(decision, "state") == "failed")
        {
            var retryAt = Timestamp();
            decision["state"] = "pending";
            decision.Remove("failure");
            decision.Remove("failedAt");
            Transition(snapshot.Descriptor, "review-requested", request.Actor,
                retryAt, $"Decision {request.OperationId} retry started.");
            var pending = Persist(snapshot, workbenchId, request.OperationId, outcome,
                "pending", $"workbench: retry {workbenchId} decision");
            if (!pending.Success) return pending;
            snapshot = _catalogue.ResolveCanonicalForMutation(projectName, workbenchId)!;
            decision = (JsonObject)snapshot.Descriptor["decision"]!;
        }

        string[] spawnedTaskKeys = [];
        if (outcome == "feature-spawn")
        {
            try
            {
                var draft = decision["taskDraft"]?.Deserialize<WorkbenchTaskDraft>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("Prepared task draft is missing.");
                var receipt = _tasks.CreateOrFind(
                    projectName, workbenchId, request.OperationId, draft,
                    snapshot.Item.SourceTaskKeys);
                spawnedTaskKeys = [receipt.TaskKey];
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
            {
                var failedAt = Timestamp();
                decision["state"] = "failed";
                decision["failure"] = ex.Message;
                decision["failedAt"] = failedAt;
                Transition(snapshot.Descriptor, "review-requested", request.Actor,
                    failedAt, $"Decision {request.OperationId} failed: {ex.Message}");
                var failed = Persist(snapshot, workbenchId, request.OperationId, outcome,
                    "failed", $"workbench: record {workbenchId} decision failure");
                return failed.Success
                    ? failed with
                    {
                        Success = false,
                        ErrorCode = "task-mutation-failed",
                        Error = ex.Message,
                    }
                    : failed;
            }
        }

        var decidedAt = Timestamp();
        decision["state"] = "succeeded";
        decision["decidedAt"] = decidedAt;
        decision["spawnedTaskKeys"] = new JsonArray(
            spawnedTaskKeys.Select(key => (JsonNode?)key).ToArray());
        decision.Remove("failure");
        decision.Remove("failedAt");
        var settledLifecycle = outcome == "archive" ? "done" : "decided";
        Transition(snapshot.Descriptor, settledLifecycle, request.Actor, decidedAt,
            outcome == "archive"
                ? $"Archived by decision {request.OperationId}: {String(decision, "reason")}"
                : $"Feature spawned by decision {request.OperationId}.");
        var stage = outcome == "archive" ? "archived" : "succeeded";
        return Persist(snapshot, workbenchId, request.OperationId, outcome,
            stage, $"workbench: settle {workbenchId} decision", spawnedTaskKeys);
    }

    private WorkbenchDecisionResult Persist(
        WorkbenchMutationSnapshot snapshot,
        string workbenchId,
        string operationId,
        string outcome,
        string stage,
        string commitMessage,
        string[]? spawnedTaskKeys = null)
    {
        string content;
        try
        {
            content = snapshot.Descriptor.ToJsonString(DescriptorJson) + Environment.NewLine;
            _repository.WriteDescriptorDurably(snapshot.DescriptorPath, content);
            var actual = File.ReadAllText(snapshot.DescriptorPath);
            if (WorkbenchCatalogueService.ComputeDescriptorFingerprint(actual)
                != WorkbenchCatalogueService.ComputeDescriptorFingerprint(content))
                throw new IOException("Durable descriptor verification failed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error(workbenchId, operationId, "descriptor-write-failed", ex.Message,
                outcome, stage, snapshot.Revision, snapshot.Fingerprint, spawnedTaskKeys);
        }

        var commit = _repository.CommitDescriptor(
            snapshot.Root, snapshot.DescriptorRelPath, commitMessage);
        if (!commit.Success)
            return Error(workbenchId, operationId, "commit-failed",
                commit.Error ?? "Workbench descriptor commit failed.",
                outcome, stage, snapshot.Revision,
                WorkbenchCatalogueService.ComputeWorkbenchFingerprint(
                    snapshot.DescriptorPath, snapshot.EntryPath),
                spawnedTaskKeys);
        return new WorkbenchDecisionResult
        {
            Success = true,
            WorkbenchId = workbenchId,
            OperationId = operationId,
            Outcome = outcome,
            DecisionStage = stage,
            Revision = _git.ReadHeadShaAt(snapshot.Root) ?? commit.Sha,
            Fingerprint = WorkbenchCatalogueService.ComputeWorkbenchFingerprint(
                snapshot.DescriptorPath, snapshot.EntryPath),
            SpawnedTaskKeys = spawnedTaskKeys ?? [],
        };
    }

    private WorkbenchDecisionResult RecoverCommit(
        WorkbenchMutationSnapshot snapshot,
        string workbenchId,
        string operationId,
        string outcome,
        string stage,
        string phase)
    {
        var commit = _repository.CommitDescriptor(
            snapshot.Root, snapshot.DescriptorRelPath,
            $"workbench: recover {workbenchId} {phase}");
        if (!commit.Success)
            return Error(workbenchId, operationId, "commit-failed",
                commit.Error ?? "Workbench descriptor recovery commit failed.",
                outcome, stage, snapshot.Revision, snapshot.Fingerprint);
        return new WorkbenchDecisionResult
        {
            Success = true,
            WorkbenchId = workbenchId,
            OperationId = operationId,
            Outcome = outcome,
            DecisionStage = stage,
            Revision = _git.ReadHeadShaAt(snapshot.Root) ?? commit.Sha,
            Fingerprint = WorkbenchCatalogueService.ComputeWorkbenchFingerprint(
                snapshot.DescriptorPath, snapshot.EntryPath),
            SpawnedTaskKeys = StringArray(
                (JsonObject)snapshot.Descriptor["decision"]!, "spawnedTaskKeys"),
            Idempotent = true,
        };
    }

    private static string? ValidatePrepare(PrepareWorkbenchDecisionRequest request)
    {
        if (!WorkbenchDecisionContracts.SafeOperationId(request.OperationId))
            return "operationId must be 8-128 ASCII letters, digits, '.', '-' or '_'.";
        if (request.Outcome is not ("feature-spawn" or "archive"))
            return "outcome must be 'feature-spawn' or 'archive'.";
        if (!ValidActor(request.Actor)) return "actor is required and must be at most 200 characters.";
        if (!ValidFence(request.ExpectedRevision, request.ExpectedFingerprint))
            return "expectedRevision or a 64-character expectedFingerprint is required.";
        if (request.Outcome == "archive")
        {
            if (string.IsNullOrWhiteSpace(request.ArchiveReason))
                return "archiveReason is required.";
            if (request.ArchiveReason.Trim().Length > 4000)
                return "archiveReason must be at most 4000 characters.";
            if (request.Task != null) return "Archive decisions cannot include a task draft.";
        }
        else
        {
            if (request.Task == null) return "Feature decisions require a task draft.";
            var draftError = WorkbenchDecisionContracts.ValidateTaskDraft(request.Task);
            if (draftError != null) return draftError;
            if (!string.IsNullOrWhiteSpace(request.ArchiveReason))
                return "Feature decisions cannot include archiveReason.";
        }
        return null;
    }

    private static string? ValidateConfirm(ConfirmWorkbenchDecisionRequest request)
    {
        if (!request.Confirmed) return "confirmed must be true.";
        if (!WorkbenchDecisionContracts.SafeOperationId(request.OperationId))
            return "operationId is malformed.";
        if (!ValidActor(request.Actor)) return "actor is required and must be at most 200 characters.";
        if (!ValidFence(request.ExpectedRevision, request.ExpectedFingerprint))
            return "expectedRevision or a 64-character expectedFingerprint is required.";
        return null;
    }

    private static bool ValidActor(string? actor) =>
        !string.IsNullOrWhiteSpace(actor) && actor.Trim().Length <= 200;

    private static bool ValidFence(string? revision, string? fingerprint)
    {
        var hasRevision = !string.IsNullOrWhiteSpace(revision)
            && revision.Trim().Length is >= 7 and <= 64
            && revision.Trim().All(Uri.IsHexDigit);
        var hasFingerprint = !string.IsNullOrWhiteSpace(fingerprint)
            && fingerprint.Trim().Length == 64
            && fingerprint.Trim().All(Uri.IsHexDigit);
        return hasRevision || hasFingerprint;
    }

    private static bool FenceMatches(
        WorkbenchMutationSnapshot snapshot, string? expectedRevision, string? expectedFingerprint)
    {
        if (!string.IsNullOrWhiteSpace(expectedRevision)
            && (snapshot.Revision == null
                || !snapshot.Revision.StartsWith(expectedRevision.Trim(), StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!string.IsNullOrWhiteSpace(expectedFingerprint)
            && !string.Equals(snapshot.Fingerprint, expectedFingerprint.Trim(),
                StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static bool StoredConfirmationFenceMatches(
        JsonObject decision, ConfirmWorkbenchDecisionRequest request) =>
        string.Equals(
            String(decision, "confirmationExpectedRevision"),
            NullIfBlank(request.ExpectedRevision), StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            String(decision, "confirmationExpectedFingerprint"),
            NullIfBlank(request.ExpectedFingerprint), StringComparison.OrdinalIgnoreCase);

    private static bool SourceFenceMatches(
        JsonObject decision, string? expectedRevision, string? expectedFingerprint)
    {
        if (!string.IsNullOrWhiteSpace(expectedRevision)
            && (String(decision, "sourceRevision") is not { } sourceRevision
                || !sourceRevision.StartsWith(
                    expectedRevision.Trim(), StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!string.IsNullOrWhiteSpace(expectedFingerprint)
            && !string.Equals(
                String(decision, "sourceFingerprint"),
                expectedFingerprint.Trim(),
                StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static bool EquivalentPreparedDecision(
        JsonObject decision, PrepareWorkbenchDecisionRequest request)
    {
        if (request.Outcome == "archive")
            return string.Equals(String(decision, "reason"), request.ArchiveReason?.Trim(),
                StringComparison.Ordinal);
        var stored = decision["taskDraft"]?.Deserialize<WorkbenchTaskDraft>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return stored != null
            && JsonSerializer.Serialize(stored, DescriptorJson)
            == JsonSerializer.Serialize(request.Task, DescriptorJson);
    }

    private static void Transition(
        JsonObject descriptor,
        string lifecycleState,
        string actor,
        string editedAt,
        string note)
    {
        descriptor["lifecycleState"] = lifecycleState;
        descriptor["editedBy"] = actor.Trim();
        descriptor["editedAt"] = editedAt;
        var history = descriptor["lifecycleHistory"] as JsonArray
            ?? throw new InvalidDataException("lifecycleHistory is missing.");
        history.Add(new JsonObject
        {
            ["state"] = lifecycleState,
            ["editedBy"] = actor.Trim(),
            ["editedAt"] = editedAt,
            ["note"] = note,
        });
    }

    private static string DecisionStage(JsonObject decision) =>
        (String(decision, "state"), String(decision, "outcome"), String(decision, "confirmedAt")) switch
        {
            ("pending", _, null) => "prepared",
            ("pending", _, _) => "pending",
            ("failed", _, _) => "failed",
            ("succeeded", "archive", _) => "archived",
            ("succeeded", _, _) => "succeeded",
            _ => "invalid",
        };

    private static string? String(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static string[] StringArray(JsonObject obj, string name) =>
        obj[name] is JsonArray array
            ? array.Select(node => node?.GetValue<string>())
                .Where(value => value != null).Cast<string>().ToArray()
            : [];

    private static string Timestamp() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static WorkbenchDecisionResult Success(
        WorkbenchMutationSnapshot snapshot,
        string workbenchId,
        string operationId,
        string outcome,
        string stage,
        bool idempotent) =>
        new()
        {
            Success = true,
            WorkbenchId = workbenchId,
            OperationId = operationId,
            Outcome = outcome,
            DecisionStage = stage,
            Revision = snapshot.Revision,
            Fingerprint = snapshot.Fingerprint,
            SpawnedTaskKeys = snapshot.Descriptor["decision"] is JsonObject decision
                ? StringArray(decision, "spawnedTaskKeys")
                : [],
            Idempotent = idempotent,
        };

    private static WorkbenchDecisionResult Error(
        string workbenchId,
        string operationId,
        string code,
        string error,
        string? outcome = null,
        string? stage = null,
        string? revision = null,
        string? fingerprint = null,
        string[]? spawnedTaskKeys = null) =>
        new()
        {
            Success = false,
            ErrorCode = code,
            Error = error,
            WorkbenchId = workbenchId,
            OperationId = operationId,
            Outcome = outcome,
            DecisionStage = stage,
            Revision = revision,
            Fingerprint = fingerprint,
            SpawnedTaskKeys = spawnedTaskKeys ?? [],
        };

    private void LogOutcome(
        string phase,
        string projectName,
        string workbenchId,
        string operationId,
        string? outcome,
        WorkbenchDecisionResult result,
        long started)
    {
        _logger.LogInformation(
            "workbench-decision phase={Phase} project={Project} workbenchId={WorkbenchId} operationId={OperationId} outcome={Outcome} result={Result} errorCode={ErrorCode} durationMs={DurationMs}",
            phase,
            projectName,
            workbenchId,
            operationId,
            outcome,
            result.Success ? "succeeded" : "failed",
            result.ErrorCode,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}
