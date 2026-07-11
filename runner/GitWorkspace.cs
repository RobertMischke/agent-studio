namespace AgentRunner;

/// <summary>
/// Maintains one shared origin checkout and creates a linked git worktree for
/// each claimed task. Shared git metadata is mutated under a short process-wide
/// gate; agent CLIs only ever run in their own checkout.
/// </summary>
public sealed class GitWorkspace
{
    private static readonly SemaphoreSlim GitMetadataGate = new(1, 1);
    private readonly RunnerOptions _options;
    private readonly Action<string> _log;
    private readonly string _safeTaskKey;
    private readonly string? _gitRemote;
    private readonly string _baseBranch;
    private readonly string? _projectId;
    private readonly string _workBranch;
    private string? _startedHead;
    private bool _startedFromSalvage;

    public GitWorkspace(
        RunnerOptions options,
        string taskKey,
        Action<string> log,
        string? projectId = null,
        string? gitRemote = null,
        string? defaultBranch = null)
    {
        _options = options;
        _log = log;
        _safeTaskKey = SafeSegment(taskKey);
        _projectId = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
        _gitRemote = string.IsNullOrWhiteSpace(gitRemote) ? options.GitRemote : gitRemote.Trim();
        _baseBranch = string.IsNullOrWhiteSpace(defaultBranch) ? options.BaseBranch : defaultBranch.Trim();
        _workBranch = $"runner/{SafeSegment(_options.RunnerId)}/{_safeTaskKey}";
    }

    public string ProjectCachePath => CachePathForProject(_options.WorkDir, _projectId);
    public string SharedRepoPath => Path.Combine(ProjectCachePath, "repo");
    public string RepoPath => Path.Combine(ProjectCachePath, "worktrees", _safeTaskKey);

    public async Task<string> PrepareAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_gitRemote))
            throw new InvalidOperationException("The claim has no repositoryUrl and RUNNER_GIT_REMOTE is not configured as a fallback.");

        Directory.CreateDirectory(ProjectCachePath);
        Directory.CreateDirectory(Path.Combine(ProjectCachePath, "worktrees"));

        await GitMetadataGate.WaitAsync(ct);
        try
        {
            if (!Directory.Exists(Path.Combine(SharedRepoPath, ".git")))
            {
                _log($"git clone origin -> {SharedRepoPath}");
                await Git(["clone", _gitRemote, SharedRepoPath], ProjectCachePath, ct);
            }
            else
            {
                await Git(["remote", "set-url", "origin", _gitRemote], SharedRepoPath, ct);
                _log("git fetch origin --prune");
                await Git(["fetch", "origin", "--prune"], SharedRepoPath, ct);
            }

            var requested = string.IsNullOrWhiteSpace(_options.Branch) ? _baseBranch : _options.Branch!;
            string branch;
            if (await BranchExistsOnOrigin(_workBranch, ct))
            {
                // Requeues continue from the durable salvage branch. Resetting
                // to base here would make the next secure push non-fast-forward
                // and would hide the prior run from the new checkout.
                branch = _workBranch;
                _log($"resuming task from existing salvage branch 'origin/{_workBranch}'");
            }
            else
            {
                branch = await BranchExistsOnOrigin(requested, ct)
                    ? requested
                    : await OriginDefaultBranch(ct) ?? _baseBranch;
            }
            if (branch != requested && branch != _workBranch)
                _log($"branch '{requested}' not found on origin; falling back to base branch '{branch}'");

            // Reclaim debris from a process crash before creating the new
            // linked checkout. A crashed process may have left its only copy of
            // the work here, so the same salvage invariant as normal teardown
            // applies before anything is removed.
            if (Directory.Exists(RepoPath))
                await SecureAndRemoveAsync("Unknown", ct);
            await TryGit(["worktree", "prune"], SharedRepoPath, ct);

            await TryGit(["branch", "-D", _workBranch], SharedRepoPath, ct);
            _log($"git worktree add {RepoPath} on {_workBranch} from origin/{branch}");
            await Git(["worktree", "add", "-B", _workBranch, RepoPath, $"origin/{branch}"], SharedRepoPath, ct);

            _startedFromSalvage = string.Equals(branch, _workBranch, StringComparison.Ordinal);
            _startedHead = (await Git(["rev-parse", "HEAD"], RepoPath, ct)).StdOut.Trim();
            _log($"task worktree ready on '{_workBranch}' at {ShortSha(_startedHead)}");
            return branch;
        }
        finally
        {
            GitMetadataGate.Release();
        }
    }

    public async Task<WorktreeTeardownResult> TeardownAsync(string outcome, CancellationToken ct)
    {
        await GitMetadataGate.WaitAsync(ct);
        try
        {
            try
            {
                return await SecureAndRemoveAsync(outcome, ct);
            }
            catch (WorktreeSalvageException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"worktree-salvage-failed branch={_workBranch} path={RepoPath} error={OneLine(ex.Message)}");
                throw new WorktreeSalvageException(RepoPath, _workBranch, ex);
            }
        }
        finally
        {
            GitMetadataGate.Release();
        }
    }

    private async Task<WorktreeTeardownResult> SecureAndRemoveAsync(string outcome, CancellationToken ct)
    {
        if (!Directory.Exists(RepoPath))
        {
            await TryGit(["worktree", "prune"], SharedRepoPath, ct);
            return WorktreeTeardownResult.NoWork;
        }

        var status = (await Git(["status", "--porcelain=v1", "--untracked-files=all"], RepoPath, ct)).StdOut;
        var wasDirty = !string.IsNullOrWhiteSpace(status);
        _log($"worktree-salvage-status path={RepoPath} dirty={wasDirty} outcome={outcome}");

        if (wasDirty)
        {
            await Git(["add", "--all"], RepoPath, ct);
            await Git([
                "-c", "user.name=Agent Studio Runner",
                "-c", "user.email=runner@agent-studio.invalid",
                "commit", "-m", $"wip(runner): salvage before teardown - outcome {outcome}"
            ], RepoPath, ct);
            _log($"worktree-salvage-commit-created path={RepoPath} outcome={outcome}");
        }

        var head = (await Git(["rev-parse", "HEAD"], RepoPath, ct)).StdOut.Trim();
        var changedDuringRun = _startedHead is not null
            && !string.Equals(_startedHead, head, StringComparison.OrdinalIgnoreCase);
        var hasWork = wasDirty || changedDuringRun || _startedFromSalvage;
        string? remoteHead = null;

        // A checkout which is still exactly at its recorded start commit is
        // provably clean and needs no remote query. Crash debris has no start
        // marker, so inspect both reachability and the durable runner ref.
        if (hasWork || _startedHead is null)
        {
            try
            {
                var hasLocalOnlyCommits = await HasLocalOnlyCommitsAsync(ct);
                remoteHead = await RemoteBranchHeadAsync(ct);
                hasWork = hasWork || hasLocalOnlyCommits || remoteHead is not null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"worktree-salvage-remote-check-failed branch={_workBranch} path={RepoPath} error={OneLine(ex.Message)}");
                throw new WorktreeSalvageException(RepoPath, _workBranch, ex);
            }
        }

        if (hasWork && !string.Equals(remoteHead, head, StringComparison.OrdinalIgnoreCase))
        {
            _log($"worktree-salvage-push-started branch={_workBranch} sha={ShortSha(head)} path={RepoPath}");
            try
            {
                await Git(["push", "--set-upstream", "origin", $"HEAD:refs/heads/{_workBranch}"], RepoPath, ct);
                remoteHead = await RemoteBranchHeadAsync(ct);
                if (!string.Equals(remoteHead, head, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"origin/{_workBranch} resolved to '{remoteHead ?? "missing"}' after push, expected '{head}'.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"worktree-salvage-push-failed branch={_workBranch} path={RepoPath} error={OneLine(ex.Message)}");
                throw new WorktreeSalvageException(RepoPath, _workBranch, ex);
            }
            _log($"worktree-salvage-push-completed branch={_workBranch} sha={ShortSha(head)} path={RepoPath}");
        }

        // Force-removal is permitted only after all work is present on the
        // verified origin branch. A clean checkout has nothing to secure.
        await Git(["worktree", "remove", "--force", RepoPath], SharedRepoPath, ct);
        await TryGit(["worktree", "prune"], SharedRepoPath, ct);
        await TryGit(["branch", "-D", _workBranch], SharedRepoPath, ct);
        _log($"worktree-teardown-completed path={RepoPath} secured={hasWork} branch={(hasWork ? _workBranch : "none")}");

        return hasWork
            ? new WorktreeTeardownResult(true, _workBranch, head, BuildBranchUrl(_gitRemote!, _workBranch))
            : WorktreeTeardownResult.NoWork;
    }

    private async Task<bool> HasLocalOnlyCommitsAsync(CancellationToken ct)
    {
        var result = await Git(["rev-list", "--count", "HEAD", "--not", "--remotes=origin"], RepoPath, ct);
        return int.TryParse(result.StdOut.Trim(), out var count) && count > 0;
    }

    private async Task<string?> RemoteBranchHeadAsync(CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            "git", ["ls-remote", "--heads", "origin", $"refs/heads/{_workBranch}"],
            workingDirectory: RepoPath, ct: ct);
        if (!result.Success)
            throw new InvalidOperationException(
                $"git ls-remote origin {_workBranch} failed ({result.ExitCode}): {result.StdErr.Trim()}");
        var first = result.StdOut.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? null : first;
    }

    private async Task<bool> BranchExistsOnOrigin(string branch, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            "git", ["ls-remote", "--heads", "origin", branch], workingDirectory: SharedRepoPath, ct: ct);
        return result.Success && result.StdOut.Contains($"refs/heads/{branch}", StringComparison.Ordinal);
    }

    private async Task<string?> OriginDefaultBranch(CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            "git", ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"],
            workingDirectory: SharedRepoPath, ct: ct);
        if (!result.Success) return null;
        const string prefix = "origin/";
        var branch = result.StdOut.Trim();
        return branch.StartsWith(prefix, StringComparison.Ordinal) ? branch[prefix.Length..] : null;
    }

    private static async Task<ProcessResult> Git(IReadOnlyList<string> args, string workingDirectory, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("git", args, workingDirectory: workingDirectory, ct: ct);
        if (!result.Success)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.StdErr.Trim()}");
        return result;
    }

    private static async Task TryGit(IReadOnlyList<string> args, string workingDirectory, CancellationToken ct)
    {
        if (!Directory.Exists(workingDirectory)) return;
        await ProcessRunner.RunAsync("git", args, workingDirectory: workingDirectory, ct: ct);
    }

    private static string? BuildBranchUrl(string remote, string branch)
    {
        var value = remote.Trim();
        if (value.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            value = "https://github.com/" + value["git@github.com:".Length..];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return null;
        value = value.TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];
        return value + "/tree/" + branch;
    }

    private static string ShortSha(string sha) => sha.Length > 8 ? sha[..8] : sha;
    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    internal static string SafeSegment(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray();
        var safe = new string(chars).Trim('-', '.');
        return safe.Length == 0 ? "task" : safe;
    }

    internal static string CachePathForProject(string workDir, string? projectId)
        => string.IsNullOrWhiteSpace(projectId)
            ? workDir
            : Path.Combine(workDir, SafeSegment(projectId));
}

public sealed record WorktreeTeardownResult(bool SecuredWork, string? Branch, string? CommitSha, string? BranchUrl)
{
    public static WorktreeTeardownResult NoWork { get; } = new(false, null, null, null);
}

public sealed class WorktreeSalvageException : Exception
{
    public WorktreeSalvageException(string worktreePath, string branch, Exception innerException)
        : base($"Could not secure worktree '{worktreePath}' on origin branch '{branch}'.", innerException)
    {
        WorktreePath = worktreePath;
        Branch = branch;
    }

    public string WorktreePath { get; }
    public string Branch { get; }
}
