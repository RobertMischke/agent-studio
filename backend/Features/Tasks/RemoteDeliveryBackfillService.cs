namespace AgentStudio.Tasks;

/// <summary>
/// One-time repair for the remote cards identified in the 2026-07-28 system
/// review. It replays the latest fenced completion through the same remote
/// range and foreign-task guard used by live completion.
/// </summary>
public sealed class RemoteDeliveryBackfillService
{
    private static readonly string[] ReviewedTaskKeys =
        ["AGT-2242", "AGT-2386", "AGT-2387", "AGT-2389", "AGT-2305"];

    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TimelineLog _timeline;
    private readonly GitService _git;
    private readonly ProjectRegistry _projects;
    private readonly ProjectSettingsService _settings;
    private readonly ILogger<RemoteDeliveryBackfillService> _logger;

    public RemoteDeliveryBackfillService(
        TaskScannerService scanner,
        TaskMutationService mutations,
        TimelineLog timeline,
        GitService git,
        ProjectRegistry projects,
        ProjectSettingsService settings,
        ILogger<RemoteDeliveryBackfillService> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _timeline = timeline;
        _git = git;
        _projects = projects;
        _settings = settings;
        _logger = logger;
    }

    public RemoteDeliveryBackfillResult RunReviewedCards()
    {
        var repaired = 0;
        var warnings = new List<string>();
        var tasks = _scanner.ScanAllAutomationJobs();
        foreach (var key in ReviewedTaskKeys)
        {
            var task = tasks.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Id, key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.TaskKey, key, StringComparison.OrdinalIgnoreCase));
            if (task is null)
            {
                warnings.Add($"{key}: task not found");
                continue;
            }

            var completion = LatestRemoteCompletion(task);
            if (completion is null)
            {
                warnings.Add($"{key}: remote completion evidence not found");
                continue;
            }

            var details = completion.Details!;
            if (_timeline.ReadAll(task.FolderPath).Any(item =>
                    string.Equals(
                        item.Kind,
                        TimelineEventKinds.RemoteDeliveryBackfilled,
                        StringComparison.Ordinal)
                    && item.Details?.GetValueOrDefault("resultSha")
                    is { Length: > 0 } repairedSha
                    && string.Equals(
                        repairedSha,
                        details.GetValueOrDefault("resultSha"),
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var branch = details.GetValueOrDefault("salvageBranch");
            var resultSha = details.GetValueOrDefault("resultSha");
            var repoRoot = _git.ResolveRepoRootForWatchPath(task.WatchPath);
            if (string.IsNullOrWhiteSpace(branch)
                || string.IsNullOrWhiteSpace(resultSha)
                || string.IsNullOrWhiteSpace(repoRoot))
            {
                warnings.Add($"{key}: branch, result SHA, or repository unavailable");
                continue;
            }

            var range = _git.InspectRemoteDeliveryCommitRange(
                repoRoot,
                branch,
                resultSha,
                details.GetValueOrDefault("integrationBranch"));
            if (!range.Success)
            {
                warnings.Add($"{key}: {range.Warning}");
                continue;
            }

            var attribution = RemoteCommitAttributionGuard.Attribute(key, branch, range.Commits);
            _mutations.SetRunIntegrationBranchOnFolder(task.FolderPath, range.IntegrationBranch!);
            _mutations.SetRemoteCommitAttributionOnFolder(
                task.FolderPath,
                completion.RunId ?? $"legacy-backfill:{key}",
                details.GetValueOrDefault("runner") ?? "remote-runner",
                resultSha,
                attribution.Commits,
                replaceUnscopedLegacyAttribution: true);
            if (!attribution.Accepted)
            {
                warnings.Add($"{key}: {attribution.Warning}");
                continue;
            }

            var project = _projects.FindByStorageLocation(task.WatchPath)
                          ?? _projects.FindByIdOrDisplayName(task.ProjectName);
            var repository = RemoteProjectRepositoryResolver.Resolve(
                project,
                TaskIntegrationBranch.Name(
                    range.IntegrationBranch,
                    _settings.Get(task.ProjectName).IntegrationBranch));
            var attemptChainId = details.GetValueOrDefault("attemptChainId")
                                 ?? completion.RunId
                                 ?? $"legacy-backfill:{key}";
            _ = long.TryParse(details.GetValueOrDefault("fence"), out var fence);
            ReviewSubjectStore.Write(task.FolderPath, new ReviewSubjectRecord
            {
                TaskKey = key,
                Project = task.ProjectName,
                Repository = repository?.RepositoryUrl ?? repoRoot,
                ResultSha = resultSha,
                AttemptChainId = attemptChainId,
                Executor = details.GetValueOrDefault("runner") ?? "remote-runner",
                LeaseId = attemptChainId,
                FencingToken = fence,
                ResultRef = branch,
                IntegrationBranch = range.IntegrationBranch,
                CompletedAtUtc = completion.Ts,
            });
            _timeline.Append(
                task.FolderPath,
                TimelineEventKinds.RemoteDeliveryBackfilled,
                TimelineActors.System,
                $"Remote delivery attribution backfilled from {branch}.",
                runId: completion.RunId,
                details: new Dictionary<string, string>
                {
                    ["deliveryBranch"] = branch,
                    ["resultSha"] = resultSha,
                    ["integrationBranch"] = range.IntegrationBranch!,
                    ["commitCount"] = attribution.Commits.Count.ToString(),
                });
            repaired++;
        }

        foreach (var warning in warnings)
            _logger.LogWarning("remote-delivery-backfill warning={Warning}", warning);
        if (repaired > 0)
            _logger.LogInformation("remote-delivery-backfill repaired={Count}", repaired);
        return new RemoteDeliveryBackfillResult(repaired, warnings);
    }

    private TimelineEvent? LatestRemoteCompletion(TaskInfo task)
        => _timeline.ReadAll(task.FolderPath)
            .Where(item =>
                string.Equals(item.Kind, TimelineEventKinds.AgentRunFinished, StringComparison.Ordinal)
                && item.Details is not null
                && item.Details.ContainsKey("salvageBranch")
                && item.Details.ContainsKey("resultSha"))
            .OrderByDescending(item => item.Ts)
            .FirstOrDefault();
}

public sealed record RemoteDeliveryBackfillResult(
    int Repaired,
    IReadOnlyList<string> Warnings);
