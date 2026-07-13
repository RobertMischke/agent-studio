using System.Collections.Concurrent;

namespace AgentStudio.Pipeline;

/// <summary>
/// A project-repository write produced by one deterministic on-demand post-step.
/// The callback is invoked only while the managed checkout is clean and held by
/// the per-repository gate.
/// </summary>
public sealed record ManagedProjectArtifactOutput(
    string Status,
    string Summary,
    string? ProducedArtifact);

public sealed record ManagedProjectArtifactWriteResult(
    bool Success,
    ManagedProjectArtifactOutput? Output,
    string? Error,
    bool DidCommit,
    string? CommitSha,
    bool PushQueued)
{
    public static ManagedProjectArtifactWriteResult Failed(string error) =>
        new(false, null, error, false, null, false);
}

public interface IManagedProjectArtifactCommitService
{
    Task<ManagedProjectArtifactWriteResult> ExecuteAsync(
        TaskInfo task,
        string stepId,
        Func<ManagedProjectArtifactOutput> write,
        CancellationToken ct);
}

/// <summary>
/// Managed commit/push boundary for project artifacts written outside the
/// normal Progress -&gt; Post Processing transition. The checkout must be clean
/// before the write. Every resulting path is committed as one platform-owned
/// commit, stamped onto the task, and handed to the existing completed-push
/// queue. A commit failure rolls the bounded paths back to HEAD so an on-demand
/// wiki step never strands a dirty checkout.
/// </summary>
public sealed class ManagedProjectArtifactCommitService : IManagedProjectArtifactCommitService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repositoryGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly GitService _git;
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly ProjectSettingsService _settings;
    private readonly CompletedPushQueue _pushQueue;
    private readonly ILogger<ManagedProjectArtifactCommitService> _logger;

    public ManagedProjectArtifactCommitService(
        GitService git,
        TaskScannerService scanner,
        TaskMutationService mutations,
        ProjectSettingsService settings,
        CompletedPushQueue pushQueue,
        ILogger<ManagedProjectArtifactCommitService> logger)
    {
        _git = git;
        _scanner = scanner;
        _mutations = mutations;
        _settings = settings;
        _pushQueue = pushQueue;
        _logger = logger;
    }

    public async Task<ManagedProjectArtifactWriteResult> ExecuteAsync(
        TaskInfo task,
        string stepId,
        Func<ManagedProjectArtifactOutput> write,
        CancellationToken ct)
    {
        var repositoryRoot = _git.ResolveRepoRootForWatchPath(task.WatchPath);
        if (string.IsNullOrWhiteSpace(repositoryRoot))
            return ManagedProjectArtifactWriteResult.Failed("project repository is not configured as a managed git checkout");

        var gateKey = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var gate = _repositoryGates.GetOrAdd(gateKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var before = _git.GetStatus(task.Id, task.WatchPath);
            if (!before.IsRepo)
                return ManagedProjectArtifactWriteResult.Failed(before.Error ?? "project repository is not a git checkout");
            if (before.FilesChanged > 0)
            {
                return ManagedProjectArtifactWriteResult.Failed(
                    $"managed checkout has {before.FilesChanged} pre-existing change(s); commit or discard them before running this post-step");
            }

            ManagedProjectArtifactOutput output;
            try
            {
                output = write();
            }
            catch (Exception ex)
            {
                var dirty = _git.GetStatus(task.Id, task.WatchPath);
                if (dirty.IsRepo && dirty.Files.Count > 0)
                    _git.RestorePathsToHead(repositoryRoot, dirty.Files.Select(file => file.Path).ToArray());
                return ManagedProjectArtifactWriteResult.Failed(ex.Message);
            }

            var after = _git.GetStatus(task.Id, task.WatchPath);
            if (!after.IsRepo)
                return ManagedProjectArtifactWriteResult.Failed(after.Error ?? "project repository became unavailable after the post-step write");
            var paths = after.Files
                .Select(file => file.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (string.Equals(output.Status, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                if (paths.Length > 0)
                {
                    var restore = _git.RestorePathsToHead(repositoryRoot, paths);
                    if (!restore.Success)
                    {
                        return new ManagedProjectArtifactWriteResult(
                            false,
                            output,
                            $"post-step failed and its project artifact cleanup also failed: {restore.Error}",
                            false,
                            null,
                            false);
                    }
                }

                return new ManagedProjectArtifactWriteResult(
                    false,
                    output,
                    "post-step runner reported failure; project artifacts were not committed",
                    false,
                    null,
                    false);
            }

            if (after.FilesChanged == 0)
            {
                return new ManagedProjectArtifactWriteResult(
                    true, output, null, false, null, false);
            }

            var taskKey = string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key;
            var message = $"docs(pipeline): run {stepId} for {taskKey}";
            var commit = _git.CommitPaths(repositoryRoot, message, paths);
            if (!commit.Success || string.IsNullOrWhiteSpace(commit.Sha))
            {
                var restore = _git.RestorePathsToHead(repositoryRoot, paths);
                var cleanup = restore.Success ? string.Empty : $" Cleanup also failed: {restore.Error}";
                return ManagedProjectArtifactWriteResult.Failed(
                    $"managed artifact commit failed: {commit.Error ?? "unknown git error"}.{cleanup}".Trim());
            }

            var fullSha = _git.GetHeadSha(task.Id, task.WatchPath) ?? commit.Sha;
            var files = _git.GetCommitFiles(task.Id, task.WatchPath, fullSha);
            var commitInfo = new TaskCommitInfo
            {
                Sha = fullSha,
                ShortSha = fullSha.Length > 7 ? fullSha[..7] : fullSha,
                Message = message,
                FilesChanged = files.Count,
                Files = files.Select(file => file.Path).ToList(),
                At = DateTime.UtcNow,
            };
            if (!_mutations.AppendJobCommitOnFolder(task.FolderPath, commitInfo))
            {
                _logger.LogWarning(
                    "managed-project-artifact commit could not be stamped project={Project} job={JobId} step={StepId} sha={Sha}",
                    task.ProjectName, task.Id, stepId, fullSha);
            }

            var strategy = AutoPushStrategies.Normalize(_settings.Get(task.ProjectName).AutoPushStrategy);
            var pushQueued = false;
            if (strategy != AutoPushStrategies.Never)
            {
                var stamped = _scanner.FindJob(task.Id, task.WatchPath) ?? task with
                {
                    Commit = commitInfo,
                    Commits = [.. task.Commits, commitInfo],
                };
                pushQueued = _pushQueue.Enqueue(new CompletedPushRequest(
                    stamped,
                    strategy,
                    RequireCompletedState: false));
                if (!pushQueued)
                {
                    return new ManagedProjectArtifactWriteResult(
                        false, output, "managed artifact commit was created but could not be queued for push",
                        true, fullSha, false);
                }
            }

            _logger.LogInformation(
                "managed-project-artifact committed project={Project} job={JobId} step={StepId} sha={Sha} paths={Paths} pushQueued={PushQueued}",
                task.ProjectName, task.Id, stepId, fullSha, string.Join(",", paths), pushQueued);
            return new ManagedProjectArtifactWriteResult(
                true, output, null, true, fullSha, pushQueued);
        }
        finally
        {
            gate.Release();
        }
    }
}
