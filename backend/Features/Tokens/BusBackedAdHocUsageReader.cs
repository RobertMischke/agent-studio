

namespace AgentStudio.Tokens;

/// <summary>
/// Phase-4 bus-backed read path for ad-hoc Haiku usage. Queries the workspace
/// bus for <c>kind=token-usage</c> messages emitted by
/// <see cref="AdHocUsageRecorder"/> (participantId <c>support:adhoc</c>),
/// converts each into a transient <see cref="AdHocUsageRecord"/>, and
/// folds them through the existing pure-function aggregator on
/// <see cref="AdHocUsageService"/>. This guarantees byte-identical output to
/// the legacy JSONL reader for every record written since Phase 2 began
/// mirroring writes onto the bus.
/// </summary>
/// <remarks>
/// <para>
/// The aggregator stays in <see cref="AdHocUsageService"/>; only the source
/// changes. The legacy <c>adhoc-usage.jsonl</c> reader is the fallback for
/// historical records (pre-Phase-2). The parity test
/// <c>AdHocUsageBusParityTests</c> proves that for any new record both readers
/// produce identical aggregates.
/// </para>
/// <para>
/// The bus-side rollup is workspace-scoped: ad-hoc records are not bound to a
/// single project, so they live under <see cref="AgentMessageBusPaths.WorkspaceScope"/>.
/// </para>
/// </remarks>
public sealed class BusBackedAdHocUsageReader
{
    private readonly AgentMessageBusStore _store;
    private readonly IConfiguration _config;

    public BusBackedAdHocUsageReader(AgentMessageBusStore store, IConfiguration config)
    {
        _store = store;
        _config = config;
    }

    /// <summary>
    /// Read every <c>kind=token-usage</c> bus message attributed to
    /// <c>support:adhoc</c> in the workspace scope and aggregate them.
    /// Returns an empty aggregate when the workspace root is not configured.
    /// </summary>
    public AdHocUsageAggregate Aggregate(DateTime? since = null)
    {
        var workspace = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return AdHocUsageService.Aggregate(Array.Empty<AdHocUsageRecord>(), logPath: "(unconfigured)", logSizeBytes: 0, logModifiedAt: null);
        }
        return AggregateFromStore(_store, workspace!, since);
    }

    /// <summary>
    /// Pure overload: aggregate every bus message attributed to
    /// <c>support:adhoc</c> in <paramref name="workspaceRoot"/>'s
    /// <c>_workspace</c> scope. Used by the parity test so the fold can be
    /// exercised without going through DI.
    /// </summary>
    public static AdHocUsageAggregate AggregateFromStore(AgentMessageBusStore store, string workspaceRoot, DateTime? since = null)
    {
        var query = new AgentMessageQuery(
            ParticipantId: "support:adhoc",
            Kind: "token-usage",
            Since: since);
        var messages = store.Query(workspaceRoot, AgentMessageBusPaths.WorkspaceScope, query);
        var records = messages.Select(ToRecord).ToList();
        // We deliberately reuse the legacy fold so the bus path cannot drift
        // from the JSONL path on dollar quantisation, model casing, or source
        // bucket ordering. When the JSONL reader retires this is the only fold.
        return AdHocUsageService.Aggregate(records, logPath: "(bus)", logSizeBytes: 0, logModifiedAt: null);
    }

    private static AdHocUsageRecord ToRecord(AgentMessage m)
    {
        var t = m.Tokens;
        return new AdHocUsageRecord
        {
            Ts = m.CreatedAt,
            Source = m.Topic ?? AdHocUsageSources.Unknown,
            Model = t?.Model ?? "",
            InputTokens = (int)(t?.Input ?? 0),
            OutputTokens = (int)(t?.Output ?? 0),
            CacheReadTokens = (int)(t?.CacheRead ?? 0),
            CacheCreationTokens = (int)(t?.CacheWrite ?? 0),
            DurationMs = 0,
            Ok = true,
            Project = m.Project,
            JobId = m.JobId,
        };
    }
}
