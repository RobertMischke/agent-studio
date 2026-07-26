using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Durable host-local journal for accepted permits and sequenced fact reports.
/// It is deliberately not a task store: it contains only authority already
/// granted by Task Server, local queue order, and the one report awaiting an
/// acknowledgement.
/// </summary>
public sealed class HostOrchestratorJournal
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly object _gate = new();
    private readonly string _path;
    private HostJournalState _state;

    public HostOrchestratorJournal(string path)
    {
        _path = Path.GetFullPath(path);
        _state = Load(_path);
    }

    public int QueuedCount
    {
        get { lock (_gate) return _state.Work.Count(item => item.Phase == "queued"); }
    }

    public int ActiveCount
    {
        get { lock (_gate) return _state.Work.Count(item => item.Phase == "running"); }
    }

    public void Enqueue(WorkPermitAcceptanceDto acceptance)
    {
        lock (_gate)
        {
            if (_state.Work.Any(item => item.Acceptance.Task.TaskId == acceptance.Task.TaskId))
                return;
            var now = DateTime.UtcNow;
            _state.Work.Add(new HostJournalWork(acceptance, "queued", now, now, null));
            Persist();
        }
    }

    public WorkPermitAcceptanceDto? TryStartNext()
    {
        lock (_gate)
        {
            var index = _state.Work.FindIndex(item => item.Phase == "queued");
            if (index < 0) return null;
            var current = _state.Work[index];
            _state.Work[index] = current with
            {
                Phase = "running",
                LastActivityAt = DateTime.UtcNow,
                ProcessId = null,
            };
            Persist();
            return current.Acceptance;
        }
    }

    public void Complete(string taskId)
    {
        lock (_gate)
        {
            _state.Work.RemoveAll(item => item.Acceptance.Task.TaskId == taskId);
            Persist();
        }
    }

    public IReadOnlyList<WorkPermitAcceptanceDto> RecoverAcceptedWork()
    {
        lock (_gate)
            return _state.Work.Select(item => item.Acceptance).ToList();
    }

    public HostReportRequest PrepareReport(
        string runnerId,
        string hostId,
        string instanceId,
        int configuredCapacity,
        IReadOnlyList<HostCapabilityDto> capabilities,
        IReadOnlyList<HostPostProcessingStatusDto>? postProcessing = null,
        IReadOnlyList<HostFaultDto>? faults = null)
    {
        lock (_gate)
        {
            if (_state.PendingReport is not null) return _state.PendingReport;

            var active = _state.Work.Count(item => item.Phase == "running");
            var queued = _state.Work.Count(item => item.Phase == "queued");
            var effective = Math.Max(0, configuredCapacity);
            var queuedPosition = 0;
            var work = _state.Work
                .OrderBy(item => item.AcceptedAt)
                .Select(item => new HostWorkStatusDto(
                    item.Acceptance.PermitId,
                    item.Acceptance.Task.TaskId,
                    item.Acceptance.Task.TaskKey,
                    item.Acceptance.Run.RunId,
                    item.Acceptance.Lease.LeaseId,
                    item.Acceptance.Lease.Fence,
                    item.Phase,
                    item.Phase == "queued" ? queuedPosition++ : null,
                    item.ProcessId,
                    item.AcceptedAt,
                    item.LastActivityAt))
                .ToList();
            _state = _state with
            {
                PendingReport = new HostReportRequest(
                    HostOrchestratorContract.Current,
                    hostId,
                    instanceId,
                    _state.LastAcceptedSequence + 1,
                    DateTime.UtcNow,
                    new HostCapacityDto(
                        configuredCapacity,
                        effective,
                        active,
                        queued,
                        Math.Max(0, effective - active)),
                    capabilities,
                    work,
                    postProcessing ?? [],
                    faults ?? []),
            };
            Persist();
            return _state.PendingReport;
        }
    }

    public void AcknowledgeReport(long acceptedSequence)
    {
        lock (_gate)
        {
            if (_state.PendingReport is null) return;
            if (acceptedSequence < _state.PendingReport.Sequence) return;
            _state = _state with
            {
                LastAcceptedSequence = Math.Max(_state.LastAcceptedSequence, acceptedSequence),
                PendingReport = null,
            };
            Persist();
        }
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidOperationException("Journal path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_state, Json));
        File.Move(temporary, _path, overwrite: true);
    }

    private static HostJournalState Load(string path)
    {
        if (!File.Exists(path)) return new HostJournalState(0, null, []);
        try
        {
            return JsonSerializer.Deserialize<HostJournalState>(File.ReadAllText(path), Json)
                   ?? new HostJournalState(0, null, []);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Host orchestrator journal '{path}' is unreadable; refusing to lose accepted authority.",
                exception);
        }
    }
}

public sealed record HostJournalState(
    long LastAcceptedSequence,
    HostReportRequest? PendingReport,
    List<HostJournalWork> Work);

public sealed record HostJournalWork(
    WorkPermitAcceptanceDto Acceptance,
    string Phase,
    DateTime AcceptedAt,
    DateTime LastActivityAt,
    int? ProcessId);
