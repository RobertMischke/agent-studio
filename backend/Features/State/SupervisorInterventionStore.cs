using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.State;

/// <summary>
/// Typed, file-backed in-memory projection of one project's
/// <c>logs/meta/&lt;project&gt;/interventions.jsonl</c>. Companion to
/// <see cref="SupervisorAdvisoryStore"/>; same semantics, different file.
/// </summary>
/// <remarks>
/// Schema: <c>docs/app/schemas/supervisor-intervention.schema.json</c>. The store
/// does not invoke any pre-emptive primitive; it only persists the typed
/// record. <see cref="Supervisor.SupervisorInterventionService"/> remains the
/// single dispatcher and is the only writer of these records.
/// </remarks>
public sealed class SupervisorInterventionStore : InMemoryStore<SupervisorIntervention>
{
    private static readonly JsonSerializerOptions LineOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    protected override string ResolvePath(string workspaceRoot, string project)
        => SupervisorLogPaths.InterventionsFile(workspaceRoot, project);

    protected override string GetId(SupervisorIntervention item)
    {
        var ticks = item.CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture);
        return string.Concat(item.Project, "|", ticks, "|", item.Kind, "|", item.JobId ?? "-");
    }

    protected override bool TryValidate(SupervisorIntervention item, out string? error)
        => SupervisorRecordValidator.TryValidate(item, out error);

    protected override SupervisorIntervention? ParseLine(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<SupervisorIntervention>(line, LineOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
