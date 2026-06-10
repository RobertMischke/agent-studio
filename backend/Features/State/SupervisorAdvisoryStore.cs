using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.State;

/// <summary>
/// Typed, file-backed in-memory projection of one project's
/// <c>logs/meta/&lt;project&gt;/observations.jsonl</c>. First concrete consumer
/// of <see cref="InMemoryStore{T}"/>; replaces the ad-hoc
/// <c>FileStream + StreamReader</c> bookkeeping that
/// <c>AutoInterventionHostedService</c> used to do inline.
/// </summary>
/// <remarks>
/// Schema: <c>docs/schemas/supervisor-advisory.schema.json</c>. The store is
/// append-only; the supervisor is the only legitimate writer. Pure file
/// access for the corresponding writers in
/// <c>HardHealthCheckHostedService.AppendObservationRecord</c> remains so the
/// hosted-service constructors do not all need to take a store dependency in
/// one task; both code paths read the same JSONL and the
/// <see cref="InMemoryStore{T}.InvalidateProjection"/> hook lets the consumer
/// flip whenever it needs to. Once every writer routes through this store the
/// inline file-write helpers can go away.
/// </remarks>
public sealed class SupervisorAdvisoryStore : InMemoryStore<SupervisorAdvisory>
{
    private static readonly JsonSerializerOptions LineOptions = BuildLineOptions();

    private static JsonSerializerOptions BuildLineOptions()
    {
        // The on-disk format used by HardHealthCheckHostedService and the
        // existing unit tests serialises enums as numbers (the .NET default).
        // We keep that on the wire so this store is a drop-in over existing
        // observations.jsonl files; the schema's PascalCase string spelling
        // is the human-readable contract, and a conversion sits in the parser.
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        return opts;
    }

    protected override string ResolvePath(string workspaceRoot, string project)
        => SupervisorLogPaths.ObservationsFile(workspaceRoot, project);

    protected override string GetId(SupervisorAdvisory item)
    {
        // No natural primary key; synthesise a stable identifier from the
        // fields a consumer would use to recognise a duplicate.
        var ticks = item.CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture);
        return string.Concat(item.Project, "|", ticks, "|", item.Source, "|", item.Topic);
    }

    protected override bool TryValidate(SupervisorAdvisory item, out string? error)
        => SupervisorRecordValidator.TryValidate(item, out error);

    protected override SupervisorAdvisory? ParseLine(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<SupervisorAdvisory>(line, LineOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
