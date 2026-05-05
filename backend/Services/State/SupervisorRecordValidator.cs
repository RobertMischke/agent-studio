using OrchestratorApi.Services.Supervisor;

namespace OrchestratorApi.Services.State;

/// <summary>
/// In-code validator that mirrors the value sets and required-field rules in
/// <c>docs/schemas/supervisor-advisory.schema.json</c> and
/// <c>docs/schemas/supervisor-intervention.schema.json</c>. Validation lives
/// here, alongside the in-memory store, so every consumer that goes through
/// the store gets the same rejection set.
/// </summary>
/// <remarks>
/// The schema is the boundary; the C# enums (<see cref="SupervisorSeverity"/>,
/// <see cref="SupervisorSource"/>, <see cref="SupervisorInterventionKind"/>)
/// are the in-memory shape. This validator checks the projection of one onto
/// the other: the enums are non-nullable so the parser already rejects
/// out-of-range values; what remains to enforce here are the string fields
/// the schema marks <c>minLength: 1</c>.
/// </remarks>
public static class SupervisorRecordValidator
{
    public static bool TryValidate(SupervisorAdvisory advisory, out string? error)
    {
        if (advisory is null) { error = "advisory is null"; return false; }
        if (string.IsNullOrWhiteSpace(advisory.Project)) { error = "project required"; return false; }
        if (string.IsNullOrWhiteSpace(advisory.Topic)) { error = "topic required"; return false; }
        if (string.IsNullOrWhiteSpace(advisory.Message)) { error = "message required"; return false; }
        if (advisory.CreatedAt == default) { error = "createdAt required"; return false; }
        error = null;
        return true;
    }

    public static bool TryValidate(SupervisorIntervention intervention, out string? error)
    {
        if (intervention is null) { error = "intervention is null"; return false; }
        if (string.IsNullOrWhiteSpace(intervention.Project)) { error = "project required"; return false; }
        if (string.IsNullOrWhiteSpace(intervention.Reason)) { error = "reason required"; return false; }
        if (intervention.CreatedAt == default) { error = "createdAt required"; return false; }

        // Schema rule: CancelRun and ForceFail require jobId; PausePickup and
        // Resume target the project, not a single run.
        switch (intervention.Kind)
        {
            case SupervisorInterventionKind.CancelRun:
            case SupervisorInterventionKind.ForceFail:
                if (string.IsNullOrWhiteSpace(intervention.JobId))
                {
                    error = $"{intervention.Kind} requires jobId";
                    return false;
                }
                break;
        }

        error = null;
        return true;
    }
}
