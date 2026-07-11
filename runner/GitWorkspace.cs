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
    private string? _workBranch;

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
            var branch = requested;
            if (!await BranchExistsOnOrigin(requested, ct))
                branch = await OriginDefaultBranch(ct) ?? _baseBranch;
            if (branch != requested)
                _log($"branch '{requested}' not found on origin; falling back to base branch '{branch}'");

            // Reclaim debris from a process crash before creating the new
            // linked checkout. No active slot can share this task key because
            // the server lease fences duplicate claims.
            await TryGit(["worktree", "remove", "--force", RepoPath], SharedRepoPath, ct);
            await TryGit(["worktree", "prune"], SharedRepoPath, ct);
            if (Directory.Exists(RepoPath)) Directory.Delete(RepoPath, recursive: true);

            _workBranch = $"runner/{SafeSegment(_options.RunnerId)}/{_safeTaskKey}";
            await TryGit(["branch", "-D", _workBranch], SharedRepoPath, ct);
            _log($"git worktree add {RepoPath} on {_workBranch} from origin/{branch}");
            await Git(["worktree", "add", "-B", _workBranch, RepoPath, $"origin/{branch}"], SharedRepoPath, ct);

            var head = (await Git(["rev-parse", "--short", "HEAD"], RepoPath, ct)).StdOut.Trim();
            _log($"task worktree ready on '{_workBranch}' at {head}");
            return branch;
        }
        finally
        {
            GitMetadataGate.Release();
        }
    }

    public async Task TeardownAsync(CancellationToken ct)
    {
        await GitMetadataGate.WaitAsync(ct);
        try
        {
            await TryGit(["worktree", "remove", "--force", RepoPath], SharedRepoPath, ct);
            await TryGit(["worktree", "prune"], SharedRepoPath, ct);
            if (_workBranch != null)
                await TryGit(["branch", "-D", _workBranch], SharedRepoPath, ct);
            _log($"task worktree removed: {RepoPath}");
        }
        finally
        {
            GitMetadataGate.Release();
        }
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
