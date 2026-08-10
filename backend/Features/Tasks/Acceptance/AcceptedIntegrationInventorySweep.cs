namespace AgentStudio.Tasks;

/// <summary>
/// Read-only startup inventory for accepted coding cards that have no proof of
/// integration or whose latest attempt ended in Error or NoTaskBranch.
/// </summary>
public sealed class AcceptedIntegrationInventorySweep
{
    private readonly TaskScannerService _scanner;
    private readonly TaskIntegrationStatusService _integrationStatus;
    private readonly ILogger<AcceptedIntegrationInventorySweep> _logger;

    public AcceptedIntegrationInventorySweep(
        TaskScannerService scanner,
        TaskIntegrationStatusService integrationStatus,
        ILogger<AcceptedIntegrationInventorySweep> logger)
    {
        _scanner = scanner;
        _integrationStatus = integrationStatus;
        _logger = logger;
    }

    public IReadOnlyList<AcceptedIntegrationInventoryItem> Run()
    {
        var accepted = _scanner.ScanAllAutomationJobsWithArchive()
            .Where(task => task.State is TaskStates.Completed or TaskStates.Archive)
            .Where(AcceptanceIntegrationPolicy.IsIntegrationRequired)
            .OrderBy(task => task.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var statusByKey = _integrationStatus.BuildLookup(accepted);
        var findings = new List<AcceptedIntegrationInventoryItem>();

        foreach (var task in accepted)
        {
            statusByKey.TryGetValue(task.TaskKey, out var status);
            var step = _integrationStatus.ReadLatestMergeStep(task);
            if (string.Equals(step?.Verdict, "operator-override", StringComparison.OrdinalIgnoreCase))
                continue;

            var outcome = ClassifyFinding(step, status);
            if (outcome is null) continue;

            var item = new AcceptedIntegrationInventoryItem(
                task.ProjectName,
                task.Id,
                task.State,
                outcome,
                status?.Status,
                step?.Reason ?? step?.VerdictSummary,
                task.FolderPath);
            findings.Add(item);
            _logger.LogWarning(
                "accepted-integration-inventory project={Project} job={JobId} lane={Lane} outcome={Outcome} status={Status} detail={Detail}",
                item.Project,
                item.JobId,
                item.Lane,
                item.Outcome,
                item.IntegrationStatus ?? "null",
                item.Detail ?? "none");
        }

        _logger.LogInformation(
            "accepted-integration-inventory completed scanned={Scanned} findings={Findings}",
            accepted.Count,
            findings.Count);
        return findings;
    }

    internal static string? ClassifyFinding(
        PipelineStepExecution? step,
        TaskIntegrationStatus? status) => step?.Verdict?.ToLowerInvariant() switch
    {
        "error" => "Error",
        "no-branch" => "NoTaskBranch",
        _ when step is null && status?.Status != IntegrationStatuses.Integrated => "Null",
        _ => null,
    };
}

public sealed record AcceptedIntegrationInventoryItem(
    string Project,
    string JobId,
    string Lane,
    string Outcome,
    string? IntegrationStatus,
    string? Detail,
    string FolderPath);
