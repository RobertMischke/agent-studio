using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentStudio.Docs;

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

/// <summary>
/// Confirm repeats the prepared payload: prepare is a pure validation/preview
/// step that writes nothing, so confirm is the single durable write.
/// </summary>
public sealed record ConfirmWorkbenchDecisionRequest
{
    public string OperationId { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string? ExpectedRevision { get; init; }
    public string? ExpectedFingerprint { get; init; }
    public string Actor { get; init; } = "";
    public string? ArchiveReason { get; init; }
    public WorkbenchTaskDraft? Task { get; init; }
    /// <summary>Cards the caller already created for this decision, if any.</summary>
    public string[]? SpawnedTaskKeys { get; init; }
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
    /// <summary>
    /// The server-validated draft of a feature decision. This service never
    /// creates the card: task creation stays on the existing task API, and the
    /// caller may report the resulting keys back through
    /// <see cref="ConfirmWorkbenchDecisionRequest.SpawnedTaskKeys"/>.
    /// </summary>
    public WorkbenchTaskDraft? TaskDraft { get; init; }
}

/// <summary>
/// The write half of the Workbench Sichtblick gate (AGT-2375). Deliberately
/// small: <see cref="Prepare"/> validates and fingerprints without touching the
/// disk, <see cref="Confirm"/> writes the decision straight into the Workbench's
/// own <c>workbench.json</c>. Visibility hangs on that descriptor - never on a
/// <c>.meta.json</c> sidecar, which was the archive bug this card fixes.
/// </summary>
public sealed class WorkbenchDecisionService
{
    private static readonly JsonSerializerOptions DraftJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly WorkbenchCatalogueService _catalogue;
    private readonly GitService _git;

    public WorkbenchDecisionService(WorkbenchCatalogueService catalogue, GitService git)
    {
        _catalogue = catalogue;
        _git = git;
    }

    public WorkbenchDecisionResult Prepare(
        string projectName, string id, PrepareWorkbenchDecisionRequest body)
    {
        var gate = Gate(projectName, id, body.OperationId, body.Outcome, body.Actor,
            body.ArchiveReason, body.Task, body.ExpectedRevision, body.ExpectedFingerprint);
        if (gate.Failure != null) return gate.Failure;
        var snapshot = gate.Snapshot!;
        return new WorkbenchDecisionResult
        {
            Success = true,
            WorkbenchId = id,
            OperationId = body.OperationId,
            Outcome = body.Outcome,
            DecisionStage = "prepared",
            Revision = snapshot.Revision,
            Fingerprint = snapshot.Fingerprint,
            TaskDraft = gate.Draft,
        };
    }

    public WorkbenchDecisionResult Confirm(
        string projectName, string id, ConfirmWorkbenchDecisionRequest body)
    {
        if (!body.Confirmed)
            return Failure(id, body.OperationId, "validation",
                "The decision must be explicitly confirmed.");
        var gate = Gate(projectName, id, body.OperationId, body.Outcome, body.Actor,
            body.ArchiveReason, body.Task, body.ExpectedRevision, body.ExpectedFingerprint);
        if (gate.Failure != null) return gate.Failure;
        var snapshot = gate.Snapshot!;
        if (snapshot.Revision == null && snapshot.Fingerprint == null)
            return Failure(id, body.OperationId, "validation",
                "The Workbench has no readable revision or fingerprint provenance.");

        var archive = body.Outcome == "archive";
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var actor = body.Actor.Trim();
        var note = archive
            ? $"Archived: {body.ArchiveReason!.Trim()}"
            : $"Decided to build a feature: {gate.Draft!.Title.Trim()}";
        var spawned = (body.SpawnedTaskKeys ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .ToArray();

        var descriptor = snapshot.Descriptor;
        var receipt = new JsonObject
        {
            ["outcome"] = body.Outcome,
            ["state"] = "succeeded",
            ["operationId"] = body.OperationId,
            ["sourceRevision"] = snapshot.Revision,
            ["sourceFingerprint"] = snapshot.Fingerprint,
            ["preparedAt"] = now,
            ["preparedBy"] = actor,
            ["confirmedAt"] = now,
            ["confirmedBy"] = actor,
            ["decidedAt"] = now,
            ["spawnedTaskKeys"] = new JsonArray(spawned.Select(key => (JsonNode)key!).ToArray()),
        };
        if (archive) receipt["reason"] = body.ArchiveReason!.Trim();
        else receipt["taskDraft"] = JsonSerializer.SerializeToNode(gate.Draft, DraftJson);
        descriptor["decision"] = receipt;

        if (snapshot.SchemaVersion >= 2)
        {
            // schema v2: the lifecycle projection is the visibility source.
            // Archive settles into "done" (projected as "archived"), a feature
            // decision into "decided" - the two settled states the catalogue
            // accepts for a succeeded receipt.
            var lifecycleState = archive ? "done" : "decided";
            descriptor["lifecycleState"] = lifecycleState;
            descriptor["editedBy"] = actor;
            descriptor["editedAt"] = now;
            if (descriptor["lifecycleHistory"] is not JsonArray history)
            {
                history = [];
                descriptor["lifecycleHistory"] = history;
            }
            history.Add(new JsonObject
            {
                ["state"] = lifecycleState,
                ["editedBy"] = actor,
                ["editedAt"] = now,
                ["note"] = note,
            });
        }
        else
        {
            // schema v1 still carries most Workbenches. Its visibility hangs on
            // the flat status field; the receipt above rides along so a later
            // migration to v2 keeps the provenance.
            descriptor["status"] = archive ? "archived" : "decided";
            descriptor["updatedAt"] = now;
            descriptor["editedBy"] = actor;
        }

        try
        {
            WriteDescriptor(snapshot.DescriptorPath, descriptor.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(id, body.OperationId, "write-failed",
                $"The Workbench descriptor could not be written: {ex.Message}");
        }

        // Best effort: the durable decision is the file itself. A failing commit
        // (no repo, hook refusal) must not roll the decision back or 500.
        var commit = _git.CommitPaths(snapshot.Root,
            $"workbench: {(archive ? "archive" : "decide")} {id}", [snapshot.DescriptorRelPath]);
        var revision = commit.Success ? commit.Sha : snapshot.Revision;

        return new WorkbenchDecisionResult
        {
            Success = true,
            Error = commit.Success ? null : commit.Error,
            WorkbenchId = id,
            OperationId = body.OperationId,
            Outcome = body.Outcome,
            DecisionStage = archive ? "archived" : "succeeded",
            Revision = revision,
            Fingerprint = WorkbenchCatalogueService.ComputeWorkbenchFingerprint(
                snapshot.DescriptorPath, snapshot.EntryPath),
            SpawnedTaskKeys = spawned,
            TaskDraft = gate.Draft,
        };
    }

    private sealed record GateResult(
        WorkbenchDecisionResult? Failure,
        WorkbenchMutationSnapshot? Snapshot = null,
        WorkbenchTaskDraft? Draft = null);

    /// <summary>
    /// The shared admission check of both phases: payload shape, canonical
    /// ownership, idempotency, a clean working tree, and the caller's expected
    /// revision/fingerprint against the descriptor's current bytes.
    /// </summary>
    private GateResult Gate(
        string projectName, string id, string operationId, string outcome, string actor,
        string? archiveReason, WorkbenchTaskDraft? task,
        string? expectedRevision, string? expectedFingerprint)
    {
        if (!WorkbenchDecisionContracts.SafeOperationId(operationId))
            return new(Failure(id, operationId, "validation", "operationId is malformed."));
        if (outcome is not ("feature-spawn" or "archive"))
            return new(Failure(id, operationId, "validation", $"Unsupported outcome '{outcome}'."));
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 120)
            return new(Failure(id, operationId, "validation", "actor is required."));

        WorkbenchTaskDraft? draft = null;
        if (outcome == "archive")
        {
            if (string.IsNullOrWhiteSpace(archiveReason) || archiveReason.Trim().Length > 20_000)
                return new(Failure(id, operationId, "validation",
                    "archiveReason is required for an archive decision."));
            if (task != null)
                return new(Failure(id, operationId, "validation",
                    "An archive decision cannot carry a task draft."));
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(archiveReason))
                return new(Failure(id, operationId, "validation",
                    "A feature decision cannot carry an archive reason."));
            var draftError = WorkbenchDecisionContracts.ValidateTaskDraft(task);
            if (draftError != null)
                return new(Failure(id, operationId, "validation", $"Feature decision {draftError}"));
            draft = task;
        }

        var snapshot = _catalogue.ResolveCanonicalForMutation(projectName, id);
        if (snapshot == null)
            return new(Failure(id, operationId, "not-canonical",
                "No single canonical Workbench descriptor owns this id."));

        var stored = snapshot.Item.Decision;
        if (stored is { State: "succeeded" })
            return stored.OperationId == operationId
                ? new(new WorkbenchDecisionResult
                {
                    Success = true,
                    WorkbenchId = id,
                    OperationId = operationId,
                    Outcome = stored.Outcome,
                    DecisionStage = stored.Outcome == "archive" ? "archived" : "succeeded",
                    Revision = snapshot.Revision,
                    Fingerprint = snapshot.Fingerprint,
                    SpawnedTaskKeys = stored.SpawnedTaskKeys,
                    Idempotent = true,
                })
                : new(Failure(id, operationId, "already-settled",
                    "This Workbench already carries a settled decision."));
        if (snapshot.Item.Status is "decided" or "archived")
            return new(Failure(id, operationId, "already-settled",
                "This Workbench is already decided or archived."));
        if (_catalogue.OperationIdOwnedByAnotherWorkbench(projectName, operationId, id))
            return new(Failure(id, operationId, "operation-id-conflict",
                "This operationId belongs to a different Workbench."));
        if (snapshot.Dirty)
            return new(Failure(id, operationId, "dirty-descriptor",
                "Commit the Workbench descriptor and artifact before deciding."));
        if (expectedRevision == null && expectedFingerprint == null)
            return new(Failure(id, operationId, "stale-revision",
                "A decision must name the revision or fingerprint it was taken on."));
        if (expectedRevision != null && expectedRevision != snapshot.Revision)
            return new(Failure(id, operationId, "stale-revision",
                "The Workbench moved since the decision was taken."));
        if (expectedFingerprint != null && expectedFingerprint != snapshot.Fingerprint)
            return new(Failure(id, operationId, "stale-revision",
                "The Workbench content changed since the decision was taken."));

        return new(null, snapshot, draft);
    }

    private static void WriteDescriptor(string descriptorPath, string content)
    {
        var directory = Path.GetDirectoryName(descriptorPath)
            ?? throw new IOException("The Workbench descriptor has no parent directory.");
        var temporary = Path.Combine(directory, $".workbench.json.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, descriptorPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SilentCatch.Note(ex, "Workbench descriptor temp file could not be removed.");
            }
        }
    }

    private static WorkbenchDecisionResult Failure(
        string id, string operationId, string code, string error) =>
        new()
        {
            Success = false,
            ErrorCode = code,
            Error = error,
            WorkbenchId = id,
            OperationId = operationId,
        };
}
