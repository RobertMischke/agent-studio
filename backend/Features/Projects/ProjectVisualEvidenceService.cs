using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Projects;

public sealed record ProjectVisualEvidenceItem(
    string Id,
    string JobId,
    string JobTitle,
    string WatchPath,
    string FileName,
    string RelativePath,
    string? Url,
    string Caption,
    string? TestStatus,
    string Source,
    DateTime CapturedAt,
    string ReviewStatus);

public sealed record ProjectVisualEvidenceQueue(
    string Project,
    DateTime CapturedAt,
    int UnseenCount,
    IReadOnlyList<ProjectVisualEvidenceItem> Items);

/// <summary>
/// Project projection over delivered task screenshots. Review receipts are
/// normal review-evidence entries, so task detail and Overview share one
/// append-only acknowledgement truth.
/// </summary>
public sealed class ProjectVisualEvidenceService
{
    internal const string ReceiptPrefix = "visual-screenshot-";
    private readonly TaskScannerService _scanner;
    private readonly ScreenshotIndexService _screenshots;
    private readonly ILogger<ProjectVisualEvidenceService> _logger;

    public ProjectVisualEvidenceService(
        TaskScannerService scanner,
        ScreenshotIndexService screenshots,
        ILogger<ProjectVisualEvidenceService> logger)
    {
        _scanner = scanner;
        _screenshots = screenshots;
        _logger = logger;
    }

    public ProjectVisualEvidenceQueue? Build(string projectName)
    {
        var watch = _scanner.GetWatchPaths().FirstOrDefault(entry =>
            string.Equals(entry.Name, projectName, StringComparison.OrdinalIgnoreCase));
        if (watch is null) return null;

        var items = new List<ProjectVisualEvidenceItem>();
        foreach (var task in _scanner.ScanAllJobsWithArchive()
                     .Where(task => WatchPathComparison.PathsEqual(task.WatchPath, watch.Path))
                     .Where(task => task.State is TaskStates.Completed or TaskStates.Archive))
        {
            try
            {
                AddTaskItems(task, items);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex,
                    "project-visual-evidence-task-read-failed project={Project} task={TaskId}",
                    projectName, task.Id);
            }
        }

        var ordered = items
            .OrderBy(item => item.ReviewStatus == "unseen" ? 0 : item.ReviewStatus == "reviewed" ? 1 : 2)
            .ThenByDescending(item => item.CapturedAt)
            .ToList();
        return new ProjectVisualEvidenceQueue(
            projectName,
            DateTime.UtcNow,
            ordered.Count(item => item.ReviewStatus == "unseen"),
            ordered);
    }

    public ProjectVisualEvidenceItem? Acknowledge(string projectName, string itemId)
    {
        var queue = Build(projectName);
        var item = queue?.Items.FirstOrDefault(candidate => candidate.Id == itemId);
        if (item is null || item.ReviewStatus == "unavailable") return null;
        var task = _scanner.FindJob(item.JobId, item.WatchPath);
        if (task is null) return null;

        var existing = ReviewEvidenceLog.ReadLatestPerId(task.FolderPath)
            .FirstOrDefault(entry => entry.Id == item.Id);
        var receipt = (existing ?? new ReviewEvidenceEntry
        {
            Id = item.Id,
            Source = ReviewEvidenceSources.Other,
            Severity = ReviewEvidenceSeverities.Info,
            Title = item.Caption,
            Artifacts = [item.RelativePath]
        }) with
        {
            Acknowledged = true,
            CreatedAt = DateTime.UtcNow
        };
        ReviewEvidenceLog.Append(task.FolderPath, receipt);
        return item with { ReviewStatus = "reviewed" };
    }

    private void AddTaskItems(TaskInfo task, List<ProjectVisualEvidenceItem> items)
    {
        var shots = _screenshots.ListJobScreenshots(task.Id, task.WatchPath);
        var receipts = ReviewEvidenceLog.ReadLatestPerId(task.FolderPath)
            .Where(entry => entry.Id.StartsWith(ReceiptPrefix, StringComparison.Ordinal))
            .ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var liveIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var shot in shots)
        {
            var id = StableId(task.Id, shot.RelativePath);
            liveIds.Add(id);
            receipts.TryGetValue(id, out var receipt);
            items.Add(new ProjectVisualEvidenceItem(
                id, task.Id, task.Title, task.WatchPath, shot.FileName,
                shot.RelativePath, shot.Url, shot.Caption, shot.Status, shot.Source,
                shot.TimestampUtc, receipt?.Acknowledged == true ? "reviewed" : "unseen"));
        }

        foreach (var receipt in receipts.Values.Where(receipt => !liveIds.Contains(receipt.Id)))
        {
            var path = receipt.Artifacts.FirstOrDefault() ?? "results/removed-evidence";
            items.Add(new ProjectVisualEvidenceItem(
                receipt.Id, task.Id, task.Title, task.WatchPath, Path.GetFileName(path),
                path, null, receipt.Title, null, "unavailable", receipt.CreatedAt, "unavailable"));
        }
    }

    internal static string StableId(string taskId, string relativePath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{taskId}\n{relativePath}"));
        return ReceiptPrefix + Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }
}
