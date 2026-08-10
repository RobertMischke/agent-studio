namespace AgentStudio.Docs;

public sealed record DossierMaintenanceTarget(
    string Id,
    string? Key,
    string Title,
    string EntryPath);

public sealed record DossierMaintenanceReview(
    bool Required,
    bool IsComplete,
    IReadOnlyList<DossierMaintenanceTarget> Targets,
    IReadOnlyList<string> Findings)
{
    public string Summary => !Required
        ? "No Dossier reference is attached to this card."
        : IsComplete
            ? $"Implementation status appended to {Targets.Count} referenced Dossier(s)."
            : string.Join(" ", Findings);
}

/// <summary>Pure timeline projection for the mandatory maintenance step.</summary>
public static class DossierMaintenanceStepPolicy
{
    public static PipelineStepExecution ToExecution(
        DossierMaintenanceReview review,
        DateTime startedAt,
        DateTime completedAt) => new()
    {
        StepId = PipelineCatalogue.DossierMaintenanceStepId,
        Kind = StepKind.Tool,
        Status = !review.Required
            ? PipelineStepStatus.NotApplicable
            : review.IsComplete
                ? PipelineStepStatus.Passed
                : PipelineStepStatus.Failed,
        StartedAt = startedAt,
        CompletedAt = completedAt,
        DurationMs = Math.Max(0, (long)(completedAt - startedAt).TotalMilliseconds),
        Verdict = !review.Required
            ? "not-referenced"
            : review.IsComplete ? "appended" : "missing-update",
        VerdictSummary = review.Summary,
        Reason = review.IsComplete ? null : review.Summary,
    };
}

/// <summary>
/// Resolves the existing Workbench/Dossier reference graph for CORE prompt
/// framing and validates the delivered revision against the append-only HTML
/// contract. It never rewrites the Dossier itself.
/// </summary>
public sealed class DossierMaintenanceService
{
    private readonly WorkbenchCatalogueService _catalogue;
    private readonly GitService _git;

    public DossierMaintenanceService(WorkbenchCatalogueService catalogue, GitService git)
    {
        _catalogue = catalogue;
        _git = git;
    }

    public IReadOnlyList<DossierMaintenanceTarget> ResolveTargets(string projectName, TaskInfo task)
    {
        var taskKeys = new[] { task.Key, task.TaskKey, task.Id }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var workbenchKeys = task.References.Workbenches
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (_catalogue.List(projectName, includeHistory: true)?.Items ?? [])
            .Where(item => item.Valid
                && (workbenchKeys.Contains(item.Id)
                    || item.Key != null && workbenchKeys.Contains(item.Key)
                    || item.DescriptorSourceTaskKeys.Any(taskKeys.Contains)))
            .Select(item => new DossierMaintenanceTarget(
                item.Id,
                item.Key,
                item.Title,
                item.EntryPath))
            .DistinctBy(target => target.EntryPath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(target => target.EntryPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public DossierMaintenanceReview Review(
        string projectName,
        string repositoryRoot,
        TaskInfo task)
    {
        var targets = ResolveTargets(projectName, task);
        if (targets.Count == 0)
            return new DossierMaintenanceReview(false, true, targets, Array.Empty<string>());

        var taskKey = string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key.Trim();
        var findings = new List<string>();
        foreach (var target in targets)
        {
            var touchingCommits = task.Commits
                .Where(commit => commit.Files.Any(file =>
                    string.Equals(NormalizePath(file), target.EntryPath, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(commit => commit.At)
                .ToList();

            string? before;
            string? after;
            if (touchingCommits.Count > 0)
            {
                var first = touchingCommits[0];
                var last = touchingCommits[^1];
                before = _git.GetFileAtParentOfCommit(repositoryRoot, first.Sha, target.EntryPath);
                after = _git.GetFileAtCommit(
                    repositoryRoot,
                    string.IsNullOrWhiteSpace(last.ResultSha) ? last.Sha : last.ResultSha!,
                    target.EntryPath);
            }
            else
            {
                var livePath = Path.GetFullPath(Path.Combine(
                    repositoryRoot,
                    target.EntryPath.Replace('/', Path.DirectorySeparatorChar)));
                var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                after = livePath.StartsWith(root, PathComparison()) && File.Exists(livePath)
                    ? File.ReadAllText(livePath)
                    : null;
                before = after;
            }

            var review = DossierImplementationContract.Review(before, after, taskKey);
            findings.AddRange(review.Findings.Select(finding => $"{target.EntryPath}: {finding}"));
        }

        return new DossierMaintenanceReview(
            true,
            findings.Count == 0,
            targets,
            findings);
    }

    public static string RenderTargetList(IReadOnlyList<DossierMaintenanceTarget> targets) =>
        string.Join(Environment.NewLine, targets.Select(target =>
            $"- `{target.EntryPath}` ({target.Key ?? target.Id}, {target.Title})"));

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
