using System.Collections.Concurrent;
using System.Text.Json;

namespace AgentStudio.Projects;

public sealed record ProjectDeploymentCommit(
    string Sha,
    string ShortSha,
    string Subject,
    DateTime AuthorDateUtc);

public sealed record ProjectDeploymentRun(
    DateTime At,
    string Status,
    string HeadBefore,
    string HeadAfter,
    double DurationSeconds,
    int JobsSinceLastRestart,
    int ReviewCountAfter,
    IReadOnlyList<ProjectDeploymentCommit> Commits);

public sealed record ProjectDeploymentSummary
{
    public string Project { get; init; } = "";
    public bool Available { get; init; }
    public string? Reason { get; init; }
    public string Source { get; init; } = ProjectDeploymentSummaryService.SourceName;
    public ProjectDeploymentRun? LastDeployment { get; init; }
    public int? PendingCount { get; init; }
    public IReadOnlyList<ProjectDeploymentCommit> PendingCommits { get; init; } = [];
}

/// <summary>
/// Read-only deploy-stable projection. The restart JSONL is the run truth; two
/// fixed git range reads enrich its latest row with deployed and pending commits.
/// Results are briefly cached so the Project Overview poll never repeats git work.
/// </summary>
public sealed class ProjectDeploymentSummaryService
{
    internal const string SourceName = "logs/stable-restarts.jsonl";
    internal const int MaxPendingCommits = 12;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly ILogger<ProjectDeploymentSummaryService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime At, ProjectDeploymentSummary Value)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public ProjectDeploymentSummaryService(
        IConfiguration config,
        TaskScannerService scanner,
        GitService git,
        ProjectSettingsService settings,
        ILogger<ProjectDeploymentSummaryService> logger)
    {
        _config = config;
        _scanner = scanner;
        _git = git;
        _settings = settings;
        _logger = logger;
    }

    public ProjectDeploymentSummary? Build(string projectName)
    {
        if (!_scanner.GetWatchPaths().Any(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase)))
            return null;

        if (_cache.TryGetValue(projectName, out var cached)
            && DateTime.UtcNow - cached.At < CacheTtl)
            return cached.Value;

        var value = BuildUncached(projectName);
        _cache[projectName] = (DateTime.UtcNow, value);
        return value;
    }

    internal void InvalidateCache() => _cache.Clear();

    private ProjectDeploymentSummary BuildUncached(string projectName)
    {
        var taskRepository = _config["TaskRepository"];
        if (string.IsNullOrWhiteSpace(taskRepository))
            return Unavailable(projectName, "Task repository is not configured.");

        var path = Path.Combine(taskRepository, "logs", "stable-restarts.jsonl");
        var latest = ReadLatestRestart(path);
        if (latest is null)
            return Unavailable(projectName, File.Exists(path)
                ? "No valid deploy-stable restart record is available."
                : "No deploy-stable history is available.");

        var repoRoot = _git.ResolveRepoRootForProject(projectName);
        if (string.IsNullOrWhiteSpace(repoRoot) || !_git.IsGitRepo(repoRoot))
            return Unavailable(projectName, "Project repository is unavailable.");

        // stable-restarts.jsonl is workspace-wide and predates project identity.
        // Reject a row whose deployed revision is not part of this repository so
        // one project's latest stable deploy can never be attributed to another.
        if (!_git.IsAncestor(repoRoot, latest.HeadBefore, latest.HeadAfter))
            return Unavailable(projectName, "Latest deploy-stable revision range does not belong to this project repository.");

        var integrationBranch = _git.ResolveIntegrationBranch(
            repoRoot, _settings.Get(projectName).IntegrationBranch);
        var integrationTip = _git.GetBranchTip(repoRoot, integrationBranch);

        var deployed = _git.GetCommitsInRangeAtRoot(repoRoot, latest.HeadBefore, latest.HeadAfter)
            .Select(ToCompact)
            .ToList();

        IReadOnlyList<ProjectDeploymentCommit> pendingItems = [];
        int? pendingCount = null;
        string? reason = null;
        if (integrationTip is null)
        {
            reason = $"Integration branch '{integrationBranch}' is unavailable; pending deployment delta is unknown.";
        }
        else
        {
            var pending = _git.GetCommitsInRangeAtRoot(repoRoot, latest.HeadAfter, integrationTip);
            pendingCount = pending.Count;
            pendingItems = pending.Take(MaxPendingCommits).Select(ToCompact).ToList();
        }

        _logger.LogInformation(
            "project-deployment-summary-read project={Project} status={Status} deployedCommits={DeployedCommits} pendingCommits={PendingCommits}",
            projectName, latest.Status, deployed.Count, pendingCount);

        return new ProjectDeploymentSummary
        {
            Project = projectName,
            Available = true,
            Reason = reason,
            LastDeployment = new ProjectDeploymentRun(
                latest.At,
                latest.Status,
                latest.HeadBefore,
                latest.HeadAfter,
                latest.DurationSeconds,
                latest.JobsSinceLastRestart,
                latest.ReviewCountAfter,
                deployed),
            PendingCount = pendingCount,
            PendingCommits = pendingItems,
        };
    }

    internal static RestartRecord? ReadLatestRestart(string path)
    {
        if (!File.Exists(path)) return null;
        RestartRecord? latest = null;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    var eventName = String(root, "event");
                    if (eventName is not null
                        && !string.Equals(eventName, "restart", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var timestamp = String(root, "ts");
                    if (!DateTime.TryParse(
                            timestamp,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal
                            | System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var at))
                        continue;
                    var before = String(root, "headBefore");
                    var after = String(root, "headAfter");
                    if (string.IsNullOrWhiteSpace(before) || string.IsNullOrWhiteSpace(after)) continue;

                    var row = new RestartRecord(
                        at.ToUniversalTime(),
                        String(root, "status") ?? "unknown",
                        before,
                        after,
                        Double(root, "durationSeconds"),
                        Int(root, "jobsSinceLastRestart"),
                        Int(root, "reviewCountAfter"));
                    if (latest is null || row.At > latest.At) latest = row;
                }
                catch (JsonException ex)
                {
                    // JSONL is append-only. A torn row must not hide older truth.
                    SilentCatch.Note(ex, "ProjectDeploymentSummaryService: torn restart-history row ignored.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return latest;
    }

    private static ProjectDeploymentSummary Unavailable(string project, string reason) => new()
    {
        Project = project,
        Available = false,
        Reason = reason,
    };

    private static ProjectDeploymentCommit ToCompact(GitCommitInfo commit)
        => new(commit.Sha, commit.ShortSha, commit.Subject, commit.AuthorDateUtc);

    private static string? String(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt32(out var result) ? result : 0;

    private static double Double(JsonElement root, string name)
        => root.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var result) ? result : 0;

    internal sealed record RestartRecord(
        DateTime At,
        string Status,
        string HeadBefore,
        string HeadAfter,
        double DurationSeconds,
        int JobsSinceLastRestart,
        int ReviewCountAfter);
}
