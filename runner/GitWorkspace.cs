namespace AgentRunner;

/// <summary>
/// Prepares a read-only working tree for the agent CLI from git origin. This is
/// the only code channel (parallel-task-execution.md §8.2C): the runner fetches,
/// it never pushes. Any change the CLI makes to the tree is captured as evidence
/// via the artifact-upload + external-completion endpoints, not committed here -
/// the platform owns git integration on the server side.
/// </summary>
public sealed class GitWorkspace
{
    private readonly RunnerOptions _options;
    private readonly Action<string> _log;

    public GitWorkspace(RunnerOptions options, Action<string> log)
    {
        _options = options;
        _log = log;
    }

    /// <summary>Absolute path of the checked-out repository once <see cref="PrepareAsync"/> runs.</summary>
    public string RepoPath => Path.Combine(_options.WorkDir, "repo");

    /// <summary>
    /// Clone (first run) or fetch (subsequent runs) origin, then check out the
    /// requested branch, falling back to the base branch when the task branch is
    /// absent. Returns the branch actually checked out.
    /// </summary>
    public async Task<string> PrepareAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.GitRemote))
            throw new InvalidOperationException("RUNNER_GIT_REMOTE is required: the runner reads code from git origin.");

        Directory.CreateDirectory(_options.WorkDir);

        if (!Directory.Exists(Path.Combine(RepoPath, ".git")))
        {
            _log($"git clone {_options.GitRemote} -> {RepoPath}");
            await Git(["clone", _options.GitRemote, RepoPath], workingDirectory: _options.WorkDir, ct);
        }
        else
        {
            _log("git fetch origin --prune");
            await Git(["fetch", "origin", "--prune"], ct);
        }

        var requested = string.IsNullOrWhiteSpace(_options.Branch) ? _options.BaseBranch : _options.Branch!;
        var branch = await BranchExistsOnOrigin(requested, ct) ? requested : _options.BaseBranch;
        if (branch != requested)
            _log($"branch '{requested}' not found on origin; falling back to base branch '{branch}'");

        _log($"git checkout {branch} (hard reset to origin)");
        await Git(["checkout", "-B", branch, $"origin/{branch}"], ct);
        await Git(["reset", "--hard", $"origin/{branch}"], ct);
        await Git(["clean", "-fdx"], ct);

        var head = (await Git(["rev-parse", "--short", "HEAD"], ct)).StdOut.Trim();
        _log($"working tree ready on '{branch}' at {head}");
        return branch;
    }

    private async Task<bool> BranchExistsOnOrigin(string branch, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            "git", ["ls-remote", "--heads", "origin", branch], workingDirectory: RepoPath, ct: ct);
        return result.Success && result.StdOut.Contains($"refs/heads/{branch}", StringComparison.Ordinal);
    }

    private async Task<ProcessResult> Git(IReadOnlyList<string> args, CancellationToken ct)
        => await Git(args, RepoPath, ct);

    private static async Task<ProcessResult> Git(IReadOnlyList<string> args, string workingDirectory, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("git", args, workingDirectory: workingDirectory, ct: ct);
        if (!result.Success)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.StdErr.Trim()}");
        return result;
    }
}
