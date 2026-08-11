namespace AgentStudio.Pipeline;

public sealed record ConceptWorkbenchPublishResult(
    bool Success,
    string Summary,
    ConceptWorkbenchReview Review,
    string? CommitSha = null);

public interface IConceptWorkbenchPublisher
{
    Task<ConceptWorkbenchPublishResult> PublishAsync(
        TaskInfo task,
        string worktreeRoot,
        CancellationToken ct);
}

/// <summary>
/// The Dossier-placement step for concept cards. It validates that the
/// isolated task worktree changed exactly one docs topic, then copies that
/// dossier through the platform-owned managed project commit/push
/// boundary. Product code is never merged from the task branch.
/// </summary>
public sealed class ConceptWorkbenchPublisher : IConceptWorkbenchPublisher
{
    private readonly GitService _git;
    private readonly IManagedProjectArtifactCommitService _managedArtifacts;
    private readonly ILogger<ConceptWorkbenchPublisher> _logger;

    public ConceptWorkbenchPublisher(
        GitService git,
        IManagedProjectArtifactCommitService managedArtifacts,
        ILogger<ConceptWorkbenchPublisher> logger)
    {
        _git = git;
        _managedArtifacts = managedArtifacts;
        _logger = logger;
    }

    public async Task<ConceptWorkbenchPublishResult> PublishAsync(
        TaskInfo task,
        string worktreeRoot,
        CancellationToken ct)
    {
        if (!TaskModes.IsConcept(task.Mode))
        {
            var notConcept = new ConceptWorkbenchReview(
                false, null, null, null, ["Dossier placement applies only to concept cards."]);
            return new ConceptWorkbenchPublishResult(false, notConcept.Summary, notConcept);
        }
        if (string.IsNullOrWhiteSpace(worktreeRoot) || !Directory.Exists(worktreeRoot))
        {
            var missing = new ConceptWorkbenchReview(
                false, null, null, null, ["Concept worktree is unavailable."]);
            return new ConceptWorkbenchPublishResult(false, missing.Summary, missing);
        }

        var status = _git.GetStatus(task.Id, task.WatchPath, preferRunLocation: true);
        var review = status.IsRepo
            ? ConceptWorkbenchContract.ReviewChangedFiles(
                worktreeRoot,
                status.Files.Select(file => file.Path).ToList(),
                task.Key ?? task.Id)
            : new ConceptWorkbenchReview(
                false, null, null, null, [status.Error ?? "Concept worktree is not a git repository."]);
        if (!review.IsComplete || review.RepoRelativeDirectory is null || review.Descriptor is null)
            return new ConceptWorkbenchPublishResult(false, review.Summary, review);

        var repoRelativeEntrypoint =
            review.RepoRelativeDirectory + "/" + ConceptWorkbenchContract.EntryFileName;
        var referenceFindings = AgentStudio.Tasks.ConceptDossierContract.ReviewAgentReferences(
            task.FolderPath,
            repoRelativeEntrypoint);
        if (referenceFindings.Count > 0)
        {
            review = review with
            {
                IsComplete = false,
                Findings = [.. review.Findings, .. referenceFindings],
            };
            return new ConceptWorkbenchPublishResult(false, review.Summary, review);
        }

        var sourceDirectory = Path.Combine(
            worktreeRoot,
            review.RepoRelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        var repositoryRoot = _git.ResolveRepoRootForWatchPath(task.WatchPath);
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            var noRepo = review with
            {
                IsComplete = false,
                Findings = [.. review.Findings, "Managed project repository is unavailable for Dossier placement."],
            };
            return new ConceptWorkbenchPublishResult(false, noRepo.Summary, noRepo);
        }
        var targetDirectory = Path.Combine(
            repositoryRoot,
            review.RepoRelativeDirectory.Replace('/', Path.DirectorySeparatorChar));

        var durable = await _managedArtifacts.ExecuteAsync(
            task,
            PipelineCatalogue.ConceptWorkbenchPlacementStepId,
            () =>
            {
                CopyDirectory(sourceDirectory, targetDirectory);
                return new ManagedProjectArtifactOutput(
                    "Ok",
                    review.Summary,
                    repoRelativeEntrypoint);
            },
            ct);
        if (!durable.Success)
        {
            var failed = review with
            {
                IsComplete = false,
                Findings = [.. review.Findings, durable.Error ?? "Managed Dossier commit failed."],
            };
            return new ConceptWorkbenchPublishResult(false, failed.Summary, failed, durable.CommitSha);
        }

        var record = new ConceptWorkbenchRecord
        {
            RepoRelativeDirectory = review.RepoRelativeDirectory,
            RepoRelativeEntrypoint = repoRelativeEntrypoint,
            Title = review.Descriptor.Title,
            PublishedAt = DateTime.UtcNow,
            CommitSha = durable.CommitSha,
        };
        if (!ConceptWorkbenchStore.Write(task.FolderPath, record, _logger))
        {
            return new ConceptWorkbenchPublishResult(
                false,
                "Dossier was committed, but its task metadata reference could not be persisted.",
                review,
                durable.CommitSha);
        }

        AgentStudio.Tasks.ConceptDossierClosureStore.Clear(task.FolderPath, _logger);

        return new ConceptWorkbenchPublishResult(true, review.Summary, review, durable.CommitSha);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
