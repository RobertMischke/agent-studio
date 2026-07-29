using AgentStudio.Shared;

namespace AgentStudio.Git;

public static class IntegrationQueueStates
{
    public const string Merged = "merged";
    public const string Waiting = "waiting";
    public const string Conflict = "conflict";
    public const string Skipped = "skipped";
}

public sealed record IntegrationQueueItem(
    string TaskId,
    string TaskKey,
    string Title,
    string Lane,
    DateTime StateSince,
    string Status,
    string? MergeSha,
    string? Reason);

public sealed record PublisherMergeItem(
    string TaskKey,
    string? Title,
    string Sha,
    string ShortSha,
    DateTime IntegratedAt,
    string Publisher,
    string Subject);

public sealed record PromotionTaskItem(
    string TaskKey,
    string? Title,
    string Sha,
    string ShortSha,
    string Subject);

public sealed record PromotionDiffView(
    string FromRef,
    string ToRef,
    string? FromSha,
    string? ToSha,
    IReadOnlyList<PromotionTaskItem> Tasks,
    IReadOnlyList<GitFileChange> Files,
    int FilesChanged,
    int Added,
    int Removed);

public sealed record ProjectIntegrationView(
    string Project,
    bool IsRepo,
    string IntegrationRef,
    string ReleaseRef,
    string? IntegrationHeadSha,
    string? ReleaseHeadSha,
    DateTime CapturedAt,
    IReadOnlyList<IntegrationQueueItem> Queue,
    IReadOnlyList<PublisherMergeItem> PublisherMerges,
    PromotionDiffView Promotion,
    string? Error);

/// <summary>
/// First-class, read-only integration projection for a project. It deliberately
/// observes remote-tracking refs first: acceptance is task workflow state, while
/// integration is only true after the corresponding change is visible in the
/// <c>origin/develop</c> graph.
/// </summary>
public sealed class ProjectIntegrationViewService
{
    /// <summary>
    /// Queue membership starts only after the operator accepted the card.
    /// Human Review may already carry a task-level integration preview, but it
    /// is not an accepted-card queue item until it reaches Completed.
    /// </summary>
    internal static readonly HashSet<string> AcceptedQueueLanes = new(StringComparer.Ordinal)
    {
        TaskStates.Completed,
        TaskStates.Archive,
    };

    private readonly GitService _git;
    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _settings;
    private readonly TaskIntegrationStatusService _taskIntegration;

    public ProjectIntegrationViewService(
        GitService git,
        TaskScannerService scanner,
        ProjectSettingsService settings,
        TaskIntegrationStatusService taskIntegration)
    {
        _git = git;
        _scanner = scanner;
        _settings = settings;
        _taskIntegration = taskIntegration;
    }

    public ProjectIntegrationView Build(string projectName)
    {
        var integrationBranch = _settings.Get(projectName).IntegrationBranch;
        if (string.IsNullOrWhiteSpace(integrationBranch)) integrationBranch = "develop";
        const string releaseBranch = BoardMergeStatusService.ReleaseBranch;

        var root = _git.ResolveProjectRepoRoot(projectName);
        if (root == null)
            return Empty(projectName, integrationBranch, releaseBranch, "Project has no readable git repository.");

        var integrationRef = _git.ResolveOriginReadRef(integrationBranch);
        var releaseRef = _git.ResolveOriginReadRef(releaseBranch);
        var integrationHead = _git.GetBranchTip(root, integrationRef);
        var releaseHead = _git.GetBranchTip(root, releaseRef);
        if (integrationHead == null)
            return Empty(projectName, integrationRef, releaseRef, $"Integration ref '{integrationRef}' does not exist.");

        var accepted = _scanner.ScanAllJobsWithArchive()
            .Where(task => string.Equals(task.ProjectName, projectName, StringComparison.OrdinalIgnoreCase))
            .Where(task => AcceptedQueueLanes.Contains(task.State))
            .Where(task => !task.Fixture)
            .ToList();
        var titles = accepted
            .GroupBy(TaskKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Title, StringComparer.OrdinalIgnoreCase);

        _git.TryGetAncestorShaSet(root, [integrationRef], out var integrationAncestors);
        var releaseAncestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (releaseHead != null)
            _git.TryGetAncestorShaSet(root, [releaseRef], out releaseAncestors);

        var publisherCommits = _git.GetIntegrationMergeCommits(root, integrationRef);
        var publisherByKey = publisherCommits
            .GroupBy(commit => commit.TaskKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var fallback = _taskIntegration.BuildLookup(accepted);
        var proofByTask = new Dictionary<string, string>(StringComparer.Ordinal);

        var queue = accepted.Select(task =>
        {
            var key = TaskKey(task);
            string? proof = null;
            if (publisherByKey.TryGetValue(key, out var curated))
            {
                proof = curated.Sha;
            }
            else
            {
                var recorded = task.Provenance?.Merge?.MergeCommit;
                if (!string.IsNullOrWhiteSpace(recorded) && integrationAncestors.Contains(recorded))
                    proof = recorded;
                else
                {
                    var attributed = TaskIntegrationStatusService.AttributedCommits(task);
                    if (attributed.Count > 0
                        && attributed.All(sha =>
                            TaskIntegrationStatusService.AncestorSetContains(integrationAncestors, sha)))
                        proof = attributed[^1];
                }
            }

            if (proof != null)
            {
                proofByTask[task.TaskKey] = proof;
                return Item(task, IntegrationQueueStates.Merged, proof, null);
            }

            fallback.TryGetValue(task.TaskKey, out var localStatus);
            if (localStatus?.Status == IntegrationStatuses.ConflictSkipped)
                return Item(task, IntegrationQueueStates.Conflict, null, localStatus.Detail ?? "Integration conflict recorded.");
            if (localStatus?.Status == IntegrationStatuses.NoBranch)
                return Item(task, IntegrationQueueStates.Skipped, null, localStatus.Detail ?? "No integrable change set.");

            var reason = localStatus?.Status == IntegrationStatuses.Partial
                ? localStatus.Detail
                : $"Accepted change is not present in {integrationRef}.";
            return Item(task, IntegrationQueueStates.Waiting, null, reason);
        })
        .OrderBy(item => StatusOrder(item.Status))
        .ThenByDescending(item => item.StateSince)
        .ToList();

        var publisher = publisherCommits
            .Take(200)
            .Select(commit => new PublisherMergeItem(
                commit.TaskKey,
                titles.GetValueOrDefault(commit.TaskKey),
                commit.Sha,
                commit.ShortSha,
                commit.CommittedAtUtc,
                commit.Publisher,
                commit.Subject))
            .ToList();

        var promotionShas = new HashSet<string>(integrationAncestors, StringComparer.OrdinalIgnoreCase);
        promotionShas.ExceptWith(releaseAncestors);
        var promotionTasks = new List<PromotionTaskItem>();
        var seenPromotionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var merge in publisherCommits.Where(commit => promotionShas.Contains(commit.Sha)))
        {
            if (!seenPromotionKeys.Add(merge.TaskKey)) continue;
            promotionTasks.Add(new PromotionTaskItem(
                merge.TaskKey,
                titles.GetValueOrDefault(merge.TaskKey),
                merge.Sha,
                merge.ShortSha,
                merge.Subject));
        }
        foreach (var task in accepted)
        {
            var key = TaskKey(task);
            if (seenPromotionKeys.Contains(key)) continue;
            if (!proofByTask.TryGetValue(task.TaskKey, out var proof) || !promotionShas.Contains(proof)) continue;
            seenPromotionKeys.Add(key);
            promotionTasks.Add(new PromotionTaskItem(key, task.Title, proof, Short(proof), "Attributed task change set"));
        }

        var promotionBase = releaseHead == null
            ? null
            : _git.GetMergeBase(root, releaseHead, integrationHead);
        var files = promotionBase == null
            ? new List<GitFileChange>()
            : _git.GetFilesChangedInRangeAtRoot(root, promotionBase, integrationHead);
        var promotion = new PromotionDiffView(
            integrationRef,
            releaseRef,
            integrationHead,
            releaseHead,
            promotionTasks,
            files,
            files.Count,
            files.Sum(file => file.Added),
            files.Sum(file => file.Removed));

        return new ProjectIntegrationView(
            projectName,
            true,
            integrationRef,
            releaseRef,
            integrationHead,
            releaseHead,
            DateTime.UtcNow,
            queue,
            publisher,
            promotion,
            releaseHead == null
                ? $"Release ref '{releaseRef}' does not exist; promotion diff is unavailable."
                : promotionBase == null
                    ? $"Refs '{releaseRef}' and '{integrationRef}' have no common merge base; promotion diff is unavailable."
                    : null);
    }

    private static IntegrationQueueItem Item(TaskInfo task, string status, string? sha, string? reason)
        => new(task.Id, TaskKey(task), task.Title, task.State, task.EnteredLaneAt, status, sha, reason);

    private static string TaskKey(TaskInfo task)
        => string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key!;

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private static int StatusOrder(string status) => status switch
    {
        IntegrationQueueStates.Conflict => 0,
        IntegrationQueueStates.Waiting => 1,
        IntegrationQueueStates.Skipped => 2,
        _ => 3,
    };

    private static ProjectIntegrationView Empty(string project, string integrationRef, string releaseRef, string error)
        => new(
            project,
            false,
            integrationRef,
            releaseRef,
            null,
            null,
            DateTime.UtcNow,
            [],
            [],
            new PromotionDiffView(integrationRef, releaseRef, null, null, [], [], 0, 0, 0),
            error);
}
