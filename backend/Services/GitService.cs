using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.AdHoc;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Tasks;

namespace OrchestratorApi.Services;

public record GitFileChange(string Status, string Path, int Added, int Removed);

public record GitStatusResult(
    bool IsRepo,
    string? Branch,
    int FilesChanged,
    int TotalAdded,
    int TotalRemoved,
    List<GitFileChange> Files,
    string? Error);

public record GitCommitResult(bool Success, string? Sha, string? Error);
public record GitPushResult(bool Success, string Sha, string Status, string? Error);
public record GitDiffLookupResult(bool Success, string Diff, string? Error);

/// <summary>
/// ADR-0052: result of a worktree / integration primitive
/// (<see cref="GitService.WorktreeAdd"/>, <see cref="GitService.WorktreeRemove"/>,
/// <see cref="GitService.RebaseOnto"/>, <see cref="GitService.ContinueRebase"/>,
/// <see cref="GitService.MergeFastForward"/>).
/// <paramref name="Path"/> is the worktree path for add; null otherwise.
/// </summary>
public record GitWorktreeResult(
    bool Success,
    string? Path,
    string? Error,
    IReadOnlyList<string>? ConflictedFiles = null);

/// <summary>
/// One commit in a per-run commit lookup. The numbers are derived from
/// <c>git log --shortstat</c> so we never have to re-run a diff to render
/// "12 files, +200 / -50". Returned by
/// <see cref="GitService.GetCommitsBetween"/>.
/// </summary>
public record GitCommitInfo(
    string Sha,
    string ShortSha,
    DateTime AuthorDateUtc,
    string Author,
    string Subject,
    int FilesChanged,
    int Added,
    int Removed);

public record GenerateMessageResult(string? Message, string? Error);

/// <summary>
/// Per-commit enrichment for the deterministic commit-attribution step: the
/// full commit body (<c>%B</c>, scanned for <c>Co-Authored-By:</c> trailers
/// so an agent co-author is detected even when the operator is the author)
/// and whether the commit is a merge (&gt;1 parent).
/// </summary>
public record CommitMeta(string Body, bool IsMerge);

public record GitProjectSummary(
    string ProjectName,
    string RootPath,
    string? RepositoryPath,
    bool IsRepo,
    string? Branch,
    int FilesChanged,
    int TotalAdded,
    int TotalRemoved);

/// <summary>
/// Repository hygiene snapshot used by the project header badge and the
/// review/completed job-detail strip. Answers: is the working tree dirty,
/// how many files are staged / unstaged / untracked, what is the current
/// branch and upstream, how far ahead/behind are we, and what was the last
/// recorded commit. When called with a job context, also reports whether
/// the job carries a platform-owned commit stamp and whether accepted task
/// work appears uncommitted ("dirty after accept").
/// <para>
/// Cached server-side per project for ~3 seconds: the fields are deliberately
/// cheap to recompute, but a polling UI should still avoid forking N git
/// processes per render.
/// </para>
/// </summary>
public record GitHygieneStatus
{
    public string ProjectName { get; init; } = "";
    public string? RepoRoot { get; init; }
    public bool IsRepo { get; init; }
    public string? Branch { get; init; }
    public string? Upstream { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public bool HasUpstream { get; init; }
    public bool IsDirty { get; init; }
    public int StagedCount { get; init; }
    public int UnstagedCount { get; init; }
    public int UntrackedCount { get; init; }
    public string? LastCommitSha { get; init; }
    public string? LastCommitShortSha { get; init; }
    public string? LastCommitSubject { get; init; }
    public DateTime? LastCommitAtUtc { get; init; }
    /// <summary>
    /// Job context, populated only when <see cref="GitService.GetJobHygiene"/>
    /// is the entry point. Null for project-only queries.
    /// </summary>
    public TaskHygieneContext? Job { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Per-job hygiene overlay. Scope is intentionally narrow: it answers
/// task-scoped questions only - did the platform stamp a commit on
/// this job (<see cref="TaskInfoCommitPresent"/>), and does this task
/// (only when active) look like its accepted work was left
/// uncommitted (<see cref="AcceptedTaskUncommitted"/>)? Repo-level
/// signals (ahead of upstream, push pending, branch behind, untracked
/// files in the repo root) live on the project-level
/// <see cref="GitHygieneStatus"/> fields and are surfaced on a
/// project-scoped UI surface, never on a per-task detail page.
/// </summary>
public record TaskHygieneContext(
    string JobId,
    string State,
    bool TaskInfoCommitPresent,
    string? StampedCommitSha,
    bool AcceptedTaskUncommitted);

/// <summary>
/// Thin wrapper around git CLI for the per-task Git view. Operates on the
/// project's configured repository path, not on the job folder.
/// </summary>
public class GitService
{
    private readonly ILogger<GitService> _logger;
    private readonly TaskScannerService _scanner;
    private readonly IConfiguration _config;
    private readonly RuntimePromptService _prompts;
    private readonly AdHocUsageRecorder? _usage;

    public GitService(
        ILogger<GitService> logger,
        TaskScannerService scanner,
        IConfiguration config,
        RuntimePromptService? prompts = null,
        AdHocUsageRecorder? usage = null)
    {
        _logger = logger;
        _scanner = scanner;
        _config = config;
        _prompts = prompts ?? new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        _usage = usage;
    }

    private readonly object _summaryLock = new();
    private DateTime _summaryAt = DateTime.MinValue;
    private List<GitProjectSummary> _summaryCache = [];

    // SHA-range answers (commits / file list / diff) are deterministic once
    // a run has captured HeadShaBefore..HeadShaAfter: git history is
    // content-addressed and the diff between two fixed SHAs never changes.
    // The protocol pane re-asks for these the moment the user clicks back
    // into a previously visible run, so a bounded LRU here keeps the
    // run-git viewer instant on re-open. Keys include the resolved
    // toplevel so two projects with overlapping SHAs (rare but possible
    // with shared subtrees) cannot collide.
    private const int ShaRangeCacheLimit = 512;
    private readonly object _shaRangeLock = new();
    private readonly LinkedList<string> _shaRangeOrder = new();
    private readonly Dictionary<string, (LinkedListNode<string> Node, object Value)> _shaRangeCache = new(StringComparer.Ordinal);

    private bool TryGetShaRangeCached<T>(string key, out T value) where T : class
    {
        lock (_shaRangeLock)
        {
            if (_shaRangeCache.TryGetValue(key, out var entry) && entry.Value is T typed)
            {
                _shaRangeOrder.Remove(entry.Node);
                _shaRangeOrder.AddLast(entry.Node);
                value = typed;
                return true;
            }
        }
        value = null!;
        return false;
    }

    private void StoreShaRangeCached(string key, object value)
    {
        lock (_shaRangeLock)
        {
            if (_shaRangeCache.TryGetValue(key, out var existing))
            {
                _shaRangeOrder.Remove(existing.Node);
                _shaRangeCache.Remove(key);
            }
            var node = _shaRangeOrder.AddLast(key);
            _shaRangeCache[key] = (node, value);
            while (_shaRangeCache.Count > ShaRangeCacheLimit && _shaRangeOrder.First is { } first)
            {
                _shaRangeOrder.RemoveFirst();
                _shaRangeCache.Remove(first.Value);
            }
        }
    }

    /// <summary>
    /// Per-project summary, cached for ~3 seconds so the board's tile-pills
    /// can ask freely without spawning N git processes per render.
    /// </summary>
    public List<GitProjectSummary> GetSummaries()
    {
        lock (_summaryLock)
        {
            if (DateTime.UtcNow - _summaryAt < TimeSpan.FromSeconds(3))
                return _summaryCache;
        }

        var list = new List<GitProjectSummary>();
        foreach (var entry in _scanner.GetWatchPaths())
        {
            var configured = ResolveConfiguredRepositoryPath(entry);
            if (string.IsNullOrWhiteSpace(configured))
            {
                list.Add(new GitProjectSummary(entry.Name, "", null, false, null, 0, 0, 0));
                continue;
            }
            var root = ResolveGitToplevel(configured);
            if (root == null)
            {
                list.Add(new GitProjectSummary(entry.Name, configured, configured, false, null, 0, 0, 0));
                continue;
            }

            var (statusOut, _, statusCode) = RunGit(root, "status --porcelain=v1");
            var fileCount = statusCode == 0
                ? statusOut.Split('\n').Count(l => !string.IsNullOrWhiteSpace(l))
                : 0;

            var (branchOut, _, _) = RunGit(root, "rev-parse --abbrev-ref HEAD");
            var (numOut, _, _) = RunGit(root, "diff --numstat HEAD");
            var added = 0; var removed = 0;
            foreach (var line in numOut.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                if (int.TryParse(parts[0], out var a)) added += a;
                if (int.TryParse(parts[1], out var r)) removed += r;
            }

            list.Add(new GitProjectSummary(
                entry.Name, root, root, true,
                string.IsNullOrWhiteSpace(branchOut) ? null : branchOut.Trim(),
                fileCount, added, removed));
        }

        lock (_summaryLock)
        {
            _summaryCache = list;
            _summaryAt = DateTime.UtcNow;
        }
        return list;
    }

    private readonly object _hygieneLock = new();
    private readonly Dictionary<string, (DateTime At, GitHygieneStatus Value)> _hygieneCache = new();
    private static readonly TimeSpan HygieneTtl = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Project-level hygiene snapshot. Cached for ~3 s so a polling UI can
    /// call freely without forking N git processes per render. Returns a
    /// shape with <see cref="GitHygieneStatus.IsRepo"/> false when the
    /// project is not a repo - callers can branch on that.
    /// </summary>
    public GitHygieneStatus GetProjectHygiene(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return new GitHygieneStatus { ProjectName = "", Error = "projectName is required" };

        lock (_hygieneLock)
        {
            if (_hygieneCache.TryGetValue(projectName, out var cached) &&
                DateTime.UtcNow - cached.At < HygieneTtl)
            {
                return cached.Value;
            }
        }

        var fresh = ComputeProjectHygiene(projectName);
        lock (_hygieneLock)
        {
            _hygieneCache[projectName] = (DateTime.UtcNow, fresh);
        }
        return fresh;
    }

    /// <summary>
    /// Job-scoped hygiene: project-level snapshot overlaid with whether the
    /// job carries a platform-owned commit stamp, whether the job's lane plus
    /// the live working tree imply accepted task work was left uncommitted,
    /// and whether the stamped commit is still ahead of the upstream
    /// (committed but unpushed). Used by the job-detail review/completed
    /// hygiene strip.
    ///
    /// <para>
    /// Worktree-isolation rule: <c>AcceptedTaskUncommitted</c> only fires
    /// when the task is the runner's currently-active job for its project
    /// (<paramref name="isActiveJob"/> = true). The working tree is shared
    /// across the whole repository, so a dirty tree on a non-active task
    /// is by definition not that task's concern - it belongs to whichever
    /// task the agent is currently editing. Surfacing the warning on the
    /// non-active task produces false alarms and trains operators to
    /// ignore hygiene warnings, which masks the real ones.
    /// </para>
    /// </summary>
    public GitHygieneStatus GetJobHygiene(string jobId, string? watchPath, bool isActiveJob = false)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null)
        {
            return new GitHygieneStatus { Error = $"Job not found: {jobId}" };
        }
        var project = GetProjectHygiene(info.ProjectName);
        var jobCommitPresent = info.Commit != null && !string.IsNullOrWhiteSpace(info.Commit.Sha);

        // "Accepted task work appears uncommitted" = the job has reached a
        // post-progress lane (auto-review, human-review, completed, archive)
        // and the working tree still has a dirty diff that wasn't recorded
        // on the job. We don't gate on the auto-commit setting - the user's
        // contract is that accepted task work must not silently sit dirty.
        // We DO gate on whether this task is the active one (see method
        // docstring): a non-active task can't be responsible for whatever
        // the agent is currently leaving in the worktree.
        var lane = info.State;
        var isPostProgress =
            lane == TaskStates.AutoReview ||
            lane == TaskStates.HumanReview ||
            lane == TaskStates.Escalated ||
            lane == TaskStates.Completed ||
            lane == TaskStates.Archive;
        var acceptedUncommitted = isActiveJob && isPostProgress && project.IsRepo && project.IsDirty;

        return project with
        {
            Job = new TaskHygieneContext(
                JobId: info.Id,
                State: info.State,
                TaskInfoCommitPresent: jobCommitPresent,
                StampedCommitSha: info.Commit?.Sha,
                AcceptedTaskUncommitted: acceptedUncommitted)
        };
    }

    private GitHygieneStatus ComputeProjectHygiene(string projectName)
    {
        var entry = _scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
        if (entry == null)
        {
            return new GitHygieneStatus { ProjectName = projectName, Error = "Unknown project" };
        }
        var configured = ResolveConfiguredRepositoryPath(entry);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return new GitHygieneStatus { ProjectName = projectName, IsRepo = false };
        }
        var root = ResolveGitToplevel(configured);
        if (root == null)
        {
            return new GitHygieneStatus { ProjectName = projectName, RepoRoot = configured, IsRepo = false };
        }

        var (statusOut, _, statusCode) = RunGit(root, "status --porcelain=v1");
        var staged = 0; var unstaged = 0; var untracked = 0;
        if (statusCode == 0)
        {
            foreach (var line in statusOut.Split('\n'))
            {
                if (string.IsNullOrEmpty(line) || line.Length < 2) continue;
                var x = line[0];
                var y = line.Length > 1 ? line[1] : ' ';
                if (x == '?' && y == '?') { untracked++; continue; }
                if (x != ' ' && x != '?') staged++;
                if (y != ' ' && y != '?') unstaged++;
            }
        }
        var dirty = (staged + unstaged + untracked) > 0;

        var (branchOut, _, _) = RunGit(root, "rev-parse --abbrev-ref HEAD");
        var branch = string.IsNullOrWhiteSpace(branchOut) ? null : branchOut.Trim();

        // Upstream + ahead/behind. `git rev-parse --abbrev-ref @{u}` returns
        // exit-code != 0 when no upstream is configured; treat that as
        // "no upstream" rather than an error. The numeric counts come from
        // rev-list --left-right --count.
        string? upstream = null;
        var hasUpstream = false;
        var ahead = 0; var behind = 0;
        var (upOut, _, upCode) = RunGit(root, "rev-parse --abbrev-ref --symbolic-full-name @{u}");
        if (upCode == 0 && !string.IsNullOrWhiteSpace(upOut))
        {
            upstream = upOut.Trim();
            hasUpstream = !string.IsNullOrEmpty(upstream);
            if (hasUpstream)
            {
                var (countOut, _, countCode) = RunGit(root, "rev-list --left-right --count HEAD..." + upstream);
                if (countCode == 0 && !string.IsNullOrWhiteSpace(countOut))
                {
                    var parts = countOut.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        int.TryParse(parts[0], out ahead);
                        int.TryParse(parts[1], out behind);
                    }
                }
            }
        }

        // Last commit. Single git invocation, machine-parseable.
        const char US = '\x1f';
        var (lastOut, _, lastCode) = RunGit(root, $"log -1 --pretty=format:%H{US}%h{US}%aI{US}%s");
        string? lastSha = null; string? lastShort = null; string? lastSubject = null; DateTime? lastAt = null;
        if (lastCode == 0 && !string.IsNullOrWhiteSpace(lastOut))
        {
            var parts = lastOut.Split(US);
            if (parts.Length >= 4)
            {
                lastSha = parts[0];
                lastShort = parts[1];
                if (DateTime.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var ts))
                {
                    lastAt = DateTime.SpecifyKind(ts, DateTimeKind.Utc);
                }
                lastSubject = parts[3];
            }
        }

        return new GitHygieneStatus
        {
            ProjectName = projectName,
            RepoRoot = root,
            IsRepo = true,
            Branch = branch,
            Upstream = upstream,
            HasUpstream = hasUpstream,
            Ahead = ahead,
            Behind = behind,
            IsDirty = dirty,
            StagedCount = staged,
            UnstagedCount = unstaged,
            UntrackedCount = untracked,
            LastCommitSha = lastSha,
            LastCommitShortSha = lastShort,
            LastCommitSubject = lastSubject,
            LastCommitAtUtc = lastAt
        };
    }

    /// <summary>
    /// Drops the in-memory hygiene cache. Tests use this to force a fresh
    /// observation after they have mutated a fixture repo; production code
    /// just lets the 3 s TTL roll over.
    /// </summary>
    public void InvalidateHygieneCache()
    {
        lock (_hygieneLock) _hygieneCache.Clear();
    }

    /// <summary>Resolve the repository root for a job.</summary>
    public string? ResolveRepoRoot(string jobId, string? watchPath)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return null;
        var entry = _scanner.GetWatchPaths().FirstOrDefault(e => e.Name == info.ProjectName);
        var configured = entry == null ? null : ResolveConfiguredRepositoryPath(entry);
        if (string.IsNullOrWhiteSpace(configured)) return null;
        return ResolveGitToplevel(configured) ?? configured;
    }

    /// <summary>
    /// Resolve the repository root for a project by name without needing a
    /// job context. Used by <see cref="OrchestratorApi.Services.Runner.CrashRecoveryService"/>
    /// at boot time to inspect the working tree before any job has been
    /// loaded into the runtime.
    /// </summary>
    public string? ResolveRepoRootForProject(string projectName)
    {
        var entry = _scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
        if (entry == null) return null;
        var configured = ResolveConfiguredRepositoryPath(entry);
        if (string.IsNullOrWhiteSpace(configured)) return null;
        return ResolveGitToplevel(configured) ?? configured;
    }

    public string? ResolveRepoRootForWatchPath(string? watchPath)
    {
        var entries = _scanner.GetWatchPaths();
        WatchPathEntry? entry = null;
        if (!string.IsNullOrWhiteSpace(watchPath))
        {
            entry = entries.FirstOrDefault(e =>
                string.Equals(e.Path, watchPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.RootPath, watchPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.RepositoryPath, watchPath, StringComparison.OrdinalIgnoreCase));
        }
        else if (entries.Count == 1)
        {
            entry = entries[0];
        }

        if (entry == null) return null;
        var configured = ResolveConfiguredRepositoryPath(entry);
        if (string.IsNullOrWhiteSpace(configured)) return null;
        return ResolveGitToplevel(configured) ?? configured;
    }

    /// <summary>
    /// True when the repo at <paramref name="repoRoot"/> has any uncommitted
    /// modifications (staged, unstaged, or untracked). Cheap-by-design helper
    /// for <see cref="OrchestratorApi.Services.Runner.CrashRecoveryService"/>.
    /// </summary>
    public bool RepoHasUncommittedChanges(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return false;
        var (output, _, code) = RunGit(repoRoot, "status --porcelain=v1");
        if (code != 0) return false;
        return output.Split('\n').Any(l => !string.IsNullOrWhiteSpace(l));
    }

    /// <summary>
    /// Stable porcelain status snapshot for containment checks. Returns null on
    /// git failure so callers can fail open for observability-only guards.
    /// </summary>
    public string? GetPorcelainStatus(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return null;
        var (output, _, code) = RunGit(repoRoot, "status --porcelain=v1");
        return code == 0 ? NormalizePorcelainStatus(output) : null;
    }

    private static string NormalizePorcelainStatus(string output)
        => string.Join("\n",
            (output ?? string.Empty)
                .Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .OrderBy(l => l, StringComparer.Ordinal));

    public GitStatusResult GetStatus(string jobId, string? watchPath)
    {
        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null)
            return new GitStatusResult(false, null, 0, 0, 0, [], "Job not found or project has no RootPath configured.");
        var root = ResolveGitToplevel(configured);
        if (root == null)
            return new GitStatusResult(false, null, 0, 0, 0, [], $"Not a git repository: {configured}");

        return ReadStatusAtRoot(root);
    }

    public GitStatusResult GetStatusForRepoRoot(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return new GitStatusResult(false, null, 0, 0, 0, [], $"Repo root missing: {repoRoot}");
        var root = ResolveGitToplevel(repoRoot);
        if (root == null)
            return new GitStatusResult(false, null, 0, 0, 0, [], $"Not a git repository: {repoRoot}");

        return ReadStatusAtRoot(root);
    }

    private GitStatusResult ReadStatusAtRoot(string root)
    {
        var (statusOut, statusErr, statusCode) = RunGit(root, "status --porcelain=v1");
        if (statusCode != 0)
            return new GitStatusResult(true, null, 0, 0, 0, [], statusErr.Trim());

        var (branchOut, _, _) = RunGit(root, "rev-parse --abbrev-ref HEAD");
        var branch = branchOut.Trim();

        var statusByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in statusOut.Split('\n'))
        {
            if (string.IsNullOrEmpty(line) || line.Length < 3) continue;
            var code = line[..2];
            var path = line[3..].TrimEnd('\r', '\n').Trim();
            if (path.StartsWith("\"") && path.EndsWith("\"") && path.Length >= 2)
                path = path[1..^1]; // strip git's quoting for non-ASCII
            statusByPath[path] = code;
        }

        // numstat - combine staged + unstaged so we get a useful per-file count
        // even for files only changed unstaged. Untracked files won't appear in
        // diff output; we add zero counts for those.
        var numstat = new Dictionary<string, (int Added, int Removed)>(StringComparer.Ordinal);
        foreach (var args in new[] { "diff --numstat HEAD", "diff --numstat" })
        {
            var (numOut, _, numCode) = RunGit(root, args);
            if (numCode != 0) continue;
            foreach (var line in numOut.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                var added   = int.TryParse(parts[0], out var a) ? a : 0;
                var removed = int.TryParse(parts[1], out var r) ? r : 0;
                var path    = parts[2].Trim();
                // Take the larger of the two diffs so a staged + unstaged
                // change isn't double-counted.
                if (numstat.TryGetValue(path, out var prev))
                    numstat[path] = (Math.Max(prev.Added, added), Math.Max(prev.Removed, removed));
                else
                    numstat[path] = (added, removed);
            }
        }

        var files = statusByPath
            .Select(kv =>
            {
                var (added, removed) = numstat.TryGetValue(kv.Key, out var n) ? n : (0, 0);
                return new GitFileChange(kv.Value, kv.Key, added, removed);
            })
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new GitStatusResult(
            true,
            string.IsNullOrWhiteSpace(branch) ? null : branch,
            files.Count,
            files.Sum(f => f.Added),
            files.Sum(f => f.Removed),
            files,
            null);
    }

    public string GetDiff(string jobId, string? watchPath, string? path)
    {
        var result = GetDiffResult(jobId, watchPath, path);
        return result.Success ? result.Diff : result.Error ?? "";
    }

    public GitDiffLookupResult GetDiffResult(string jobId, string? watchPath, string? path)
    {
        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null) return new GitDiffLookupResult(false, "", "Could not resolve repo root.");
        var root = ResolveGitToplevel(configured);
        if (root == null) return new GitDiffLookupResult(false, "", $"Not a git repository: {configured}");
        // HEAD diff catches both staged and unstaged. For untracked files we
        // fall back to showing the file body so the panel isn't empty.
        var (output, err, code) = string.IsNullOrWhiteSpace(path)
            ? RunGitArgs(root, "diff", "HEAD")
            : RunGitArgs(root, "diff", "HEAD", "--", path!);
        if (code != 0)
            return new GitDiffLookupResult(false, "", string.IsNullOrWhiteSpace(err) ? "git diff failed." : err.Trim());
        if (!string.IsNullOrWhiteSpace(output)) return new GitDiffLookupResult(true, output, null);

        if (!string.IsNullOrWhiteSpace(path))
        {
            var abs = Path.Combine(root, path);
            if (File.Exists(abs))
            {
                try { return new GitDiffLookupResult(true, File.ReadAllText(abs), null); } catch { /* best-effort */ }
            }
        }
        return new GitDiffLookupResult(true, output, null);
    }

    /// <summary>
    /// Commits the working tree with a fixed <c>crash-recovery</c> author tag.
    /// Used by <see cref="OrchestratorApi.Services.Runner.CrashRecoveryService"/>
    /// to rescue uncommitted work that survived a backend crash; the distinctive
    /// author makes the commit easy to find in <c>git log</c> later (ADR-0020).
    /// Returns a clean <c>"Nothing to commit"</c> result when the tree is empty;
    /// callers treat that as success-with-info.
    /// </summary>
    public GitCommitResult CrashRecoveryCommit(
        string projectName,
        string repoRoot,
        string message,
        IReadOnlyCollection<string>? pathspecs = null)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return new GitCommitResult(false, null, $"Repo root missing: {repoRoot}");

        if (pathspecs is { Count: > 0 })
        {
            var addArgs = new List<string> { "add", "-A", "--" };
            addArgs.AddRange(pathspecs);
            var (_, scopedAddErr, scopedAddCode) = RunGitArgs(repoRoot, addArgs.ToArray());
            if (scopedAddCode != 0)
                return new GitCommitResult(false, null, $"git add failed: {scopedAddErr.Trim()}");

            const string scopedAuthor = "Crash Recovery <crash-recovery@agent-taskboard>";
            var commitArgs = new List<string> { "commit", $"--author={scopedAuthor}", "-F", "-", "--" };
            commitArgs.AddRange(pathspecs);
            var (_, scopedCommitErr, scopedCommitCode) = RunGitArgs(repoRoot, commitArgs.ToArray(), stdin: message);
            if (scopedCommitCode != 0)
            {
                if (scopedCommitErr.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                    return new GitCommitResult(false, null, "Nothing to commit. Working tree is clean.");
                return new GitCommitResult(false, null, scopedCommitErr.Trim());
            }

            var (scopedSha, _, _) = RunGit(repoRoot, "rev-parse HEAD");
            return new GitCommitResult(true, scopedSha.Trim(), null);
        }

        var (_, addErr, addCode) = RunGit(repoRoot, "add -A");
        if (addCode != 0) return new GitCommitResult(false, null, $"git add failed: {addErr.Trim()}");

        var (stagedOut, _, _) = RunGit(repoRoot, "diff --cached --name-only");
        if (string.IsNullOrWhiteSpace(stagedOut))
            return new GitCommitResult(false, null, "Nothing to commit. Working tree is clean.");

        const string author = "Crash Recovery <crash-recovery@agent-taskboard>";
        var (_, commitErr, commitCode) = RunGit(
            repoRoot,
            $"commit --author=\"{author}\" -F -",
            stdin: message);
        if (commitCode != 0)
        {
            if (commitErr.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                return new GitCommitResult(false, null, "Nothing to commit. Working tree is clean.");
            return new GitCommitResult(false, null, commitErr.Trim());
        }
        var (sha, _, _) = RunGit(repoRoot, "rev-parse HEAD");
        return new GitCommitResult(true, sha.Trim(), null);
    }

    public GitCommitResult Commit(string jobId, string? watchPath, string message,
        IReadOnlyCollection<string>? pathspecs = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new GitCommitResult(false, null, "Commit message is required.");

        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null) return new GitCommitResult(false, null, "Could not resolve repo root.");
        var root = ResolveGitToplevel(configured);
        if (root == null) return new GitCommitResult(false, null, $"Not a git repository: {configured}");

        // Scoped commit: when the caller names the task's own paths, stage and
        // commit ONLY those. A sequential (maxParallelism==1) run shares the
        // main checkout, so unrelated dirty changes from operator edits or an
        // earlier task that never committed pile up there. A blanket `git add
        // -A` would sweep all of them into THIS task's commit (the mega-blob /
        // mis-attribution bug). Restricting the pathspec keeps the commit - and
        // its stamped SHA - to exactly the files this task touched, and leaves
        // the foreign changes dirty for their own owner to handle.
        if (pathspecs is { Count: > 0 })
        {
            var addArgs = new List<string> { "add", "-A", "--" };
            addArgs.AddRange(pathspecs);
            var (_, sAddErr, sAddCode) = RunGitArgs(root, addArgs.ToArray());
            if (sAddCode != 0) return new GitCommitResult(false, null, $"git add failed: {sAddErr.Trim()}");

            // `commit -- <pathspec>` performs a partial commit from those paths
            // only, so even a (policy-violating) pre-staged foreign change in
            // the index cannot ride along. Message via stdin (-F -).
            var commitArgs = new List<string> { "commit", "-F", "-", "--" };
            commitArgs.AddRange(pathspecs);
            var (_, sCommitErr, sCommitCode) = RunGitArgs(root, commitArgs.ToArray(), stdin: message);
            if (sCommitCode != 0)
            {
                if (sCommitErr.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                    return new GitCommitResult(false, null, "Nothing to commit. Working tree is clean.");
                return new GitCommitResult(false, null, sCommitErr.Trim());
            }

            var (scopedSha, _, _) = RunGit(root, "rev-parse --short HEAD");
            return new GitCommitResult(true, scopedSha.Trim(), null);
        }

        var (_, addErr, addCode) = RunGit(root, "add -A");
        if (addCode != 0) return new GitCommitResult(false, null, $"git add failed: {addErr.Trim()}");

        // Multi-line message via stdin avoids shell escaping landmines.
        var (_, commitErr, commitCode) = RunGit(root, "commit -F -", stdin: message);
        if (commitCode != 0)
        {
            // "nothing to commit" is a soft success-with-info, not an error.
            if (commitErr.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                return new GitCommitResult(false, null, "Nothing to commit. Working tree is clean.");
            return new GitCommitResult(false, null, commitErr.Trim());
        }

        var (sha, _, _) = RunGit(root, "rev-parse --short HEAD");
        return new GitCommitResult(true, sha.Trim(), null);
    }

    /// <summary>
    /// Sends the working-tree diff to Claude Haiku and asks for a Conventional
    /// Commit message. Only the subject + short body are returned; we strip
    /// leading code-fence noise.
    /// <para>
    /// Beyond the diff, the prompt is anchored on the task's stated intent so
    /// the resulting subject line reflects *why* the change is being recorded,
    /// not just *what* the diff touches: we pass the task title, the first
    /// paragraph of <c>prompt.md</c>, and the most recent extension prompt
    /// (<c>prompt-N.md</c>, written by Extend mode). When any of those are
    /// unavailable we pass an empty string and let the template fall through
    /// to a diff-only summary so legacy jobs without titles or extensions
    /// still produce a useful message.
    /// </para>
    /// </summary>
    public async Task<GenerateMessageResult> GenerateCommitMessageAsync(
        string jobId, string? watchPath, CancellationToken ct = default,
        IReadOnlyCollection<string>? pathspecs = null)
    {
        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null) return new GenerateMessageResult(null, "Could not resolve repo root.");
        var root = ResolveGitToplevel(configured);
        if (root == null) return new GenerateMessageResult(null, $"Not a git repository: {configured}");

        // Scope the diff to the task's own paths when the caller supplies them
        // (scoped auto-commit) so the generated subject describes only this
        // task's work, never foreign dirty changes sharing the checkout.
        var (diff, _, code) = pathspecs is { Count: > 0 }
            ? RunGitArgs(root, new[] { "diff", "HEAD", "--" }.Concat(pathspecs).ToArray())
            : RunGit(root, "diff HEAD");
        if (code != 0 || string.IsNullOrWhiteSpace(diff))
            return new GenerateMessageResult(null, "No diff against HEAD. Nothing to summarise.");

        // Bound the prompt size. Haiku handles plenty but huge diffs
        // just waste latency for a commit message.
        if (diff.Length > 60_000) diff = diff[..60_000] + "\n[truncated]";

        var intent = ReadTaskIntent(jobId, watchPath);
        var prompt = _prompts.Render(RuntimePromptService.CommitMessage,
            new Dictionary<string, string?>
            {
                ["diff"] = diff,
                ["task_title"] = intent.Title,
                ["task_prompt_first_paragraph"] = intent.PromptFirstParagraph,
                ["last_user_continue"] = intent.LastUserContinue
            });

        var claudePath = _config["ClaudeCli:Path"] ?? "claude";
        var model = _config["ClaudeCli:CommitMsgModel"] ?? ModelIds.ClaudeHaiku45;

        var psi = new ProcessStartInfo
        {
            FileName = CliExecutionServiceBase.ResolveExecutable(claudePath),
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = System.Text.Encoding.UTF8,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var arg in AdHocClaudeInvoker.BuildArgs(model)) psi.ArgumentList.Add(arg);

        var sw = Stopwatch.StartNew();
        try
        {
            using var p = Process.Start(psi)!;
            await p.StandardInput.WriteAsync(prompt.AsMemory(), ct);
            p.StandardInput.Close();
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            await p.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            sw.Stop();
            if (p.ExitCode != 0)
            {
                AdHocClaudeInvoker.Record(_usage, AdHocUsageSources.CommitMessage, model, null, sw.ElapsedMilliseconds, ok: false, jobId: jobId);
                return new GenerateMessageResult(null, $"claude exited {p.ExitCode}: {stderr.Trim()}");
            }

            var (text, callUsage) = AdHocClaudeInvoker.ParseOrFallback(stdout, model);
            AdHocClaudeInvoker.Record(_usage, AdHocUsageSources.CommitMessage, model, callUsage, sw.ElapsedMilliseconds, ok: true, jobId: jobId);

            var msg = SanitizeCommitMessage(text);
            if (string.IsNullOrWhiteSpace(msg))
                return new GenerateMessageResult(null, "claude returned an empty message.");
            return new GenerateMessageResult(msg, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to invoke claude for commit message");
            return new GenerateMessageResult(null, ex.Message);
        }
    }

    /// <summary>
    /// Returns the file list (status + path + numstat) for an already-recorded
    /// SHA via <c>git show --name-status</c> + <c>git show --numstat</c>. Used
    /// by the detail view to show what a past auto-commit touched without
    /// re-deriving from the live working tree.
    /// </summary>
    public List<GitFileChange> GetCommitFiles(string jobId, string? watchPath, string sha)
    {
        if (string.IsNullOrWhiteSpace(sha) || !IsLikelyShaOrRef(sha)) return [];
        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null) return [];
        var root = ResolveGitToplevel(configured);
        if (root == null) return [];

        var (statusOut, _, statusCode) = RunGitArgs(root, "show", "--name-status", "--pretty=format:", sha);
        if (statusCode != 0) return [];

        var statusByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in statusOut.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;
            // Renames look like "R100\told\tnew" - index by the new path.
            statusByPath[parts[^1].Trim()] = parts[0].Trim();
        }

        var numstat = new Dictionary<string, (int Added, int Removed)>(StringComparer.Ordinal);
        var (numOut, _, numCode) = RunGitArgs(root, "show", "--numstat", "--pretty=format:", sha);
        if (numCode == 0)
        {
            foreach (var line in numOut.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                var added = int.TryParse(parts[0], out var a) ? a : 0;
                var removed = int.TryParse(parts[1], out var r) ? r : 0;
                numstat[parts[^1].Trim()] = (added, removed);
            }
        }

        return statusByPath
            .Select(kv =>
            {
                var (added, removed) = numstat.TryGetValue(kv.Key, out var n) ? n : (0, 0);
                return new GitFileChange(kv.Value, kv.Key, added, removed);
            })
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Real change stat (files / +added / -removed) for a single
    /// already-recorded SHA via <c>git show --shortstat</c>. The persisted
    /// <see cref="OrchestratorApi.Models.TaskCommitInfo"/> chain only caches a
    /// file count, so an aspect-review diff summary built from that chain
    /// reports "+0/-0" even for a large commit; this re-derives the genuine
    /// line counts straight from git so the reviewer sees the real changeset.
    /// Returns (0,0,0) when the repo or SHA cannot be resolved - never throws.
    /// SHAs are validated through <see cref="IsLikelyShaOrRef"/> first so a
    /// crafted argument cannot smuggle a flag into the git invocation.
    /// </summary>
    public (int FilesChanged, int Added, int Removed) GetCommitStat(string jobId, string? watchPath, string sha)
    {
        if (string.IsNullOrWhiteSpace(sha) || !IsLikelyShaOrRef(sha)) return (0, 0, 0);
        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null) return (0, 0, 0);
        var root = ResolveGitToplevel(configured);
        if (root == null) return (0, 0, 0);

        // --pretty=format: drops the commit header so the only "changed" line
        // in the output is the shortstat summary we want to parse.
        var (output, _, code) = RunGit(root, $"show --shortstat --pretty=format: {sha}");
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return (0, 0, 0);
        var statLine = output.Replace("\r\n", "\n").Split('\n')
            .FirstOrDefault(l => l.Contains("changed", StringComparison.OrdinalIgnoreCase));
        return ParseShortstat(statLine);
    }

    /// <summary>
    /// Returns the unified diff for an already-recorded commit, optionally
    /// scoped to a single path. Used by the detail view so a long-completed
    /// task still surfaces "what changed in this commit" even after the
    /// working tree has moved on.
    /// </summary>
    public string GetCommitDiff(string jobId, string? watchPath, string sha, string? path)
    {
        var result = GetCommitDiffResult(jobId, watchPath, sha, path);
        return result.Success ? result.Diff : result.Error ?? "";
    }

    public GitDiffLookupResult GetCommitDiffResult(string jobId, string? watchPath, string sha, string? path)
    {
        if (string.IsNullOrWhiteSpace(sha) || !IsLikelyShaOrRef(sha))
            return new GitDiffLookupResult(false, "", "Invalid commit SHA.");
        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null) return new GitDiffLookupResult(false, "", "Could not resolve repo root.");
        var root = ResolveGitToplevel(configured);
        if (root == null) return new GitDiffLookupResult(false, "", $"Not a git repository: {configured}");
        var (output, err, code) = string.IsNullOrWhiteSpace(path)
            ? RunGitArgs(root, "show", "--pretty=format:", sha)
            : RunGitArgs(root, "show", "--pretty=format:", sha, "--", path!);
        if (code != 0)
            return new GitDiffLookupResult(false, "", string.IsNullOrWhiteSpace(err) ? "git show failed." : err.Trim());
        return new GitDiffLookupResult(true, output, null);
    }

    /// <summary>
    /// Aggregated file list for a curated set of task commits. Unlike a broad
    /// <c>first^..last</c> range, this includes only the SHAs the task actually
    /// owns, so manual attribution/exclusion cannot pull unrelated history into
    /// the review pane.
    /// </summary>
    public List<GitFileChange> GetAggregateCommitFiles(string jobId, string? watchPath, IEnumerable<string> shas)
    {
        var filesByPath = new Dictionary<string, (string Status, int Added, int Removed)>(StringComparer.Ordinal);

        foreach (var sha in NormalizeCommitShas(shas))
        {
            foreach (var file in GetCommitFiles(jobId, watchPath, sha))
            {
                var status = SimplifyAggregatedStatus(file.Status);
                if (filesByPath.TryGetValue(file.Path, out var existing))
                {
                    filesByPath[file.Path] = (
                        CombineAggregatedStatus(existing.Status, status),
                        existing.Added + file.Added,
                        existing.Removed + file.Removed);
                }
                else
                {
                    filesByPath[file.Path] = (status, file.Added, file.Removed);
                }
            }
        }

        return filesByPath
            .Select(kv => new GitFileChange(kv.Value.Status, kv.Key, kv.Value.Added, kv.Value.Removed))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Aggregated patch text for a curated set of task commits. Patches are
    /// concatenated commit-by-commit rather than computed as a broad range, so
    /// the result remains exact even when the task's attributed commits are not
    /// contiguous in repository history.
    /// </summary>
    public string GetAggregateCommitDiff(string jobId, string? watchPath, IEnumerable<string> shas, string? path)
    {
        var result = GetAggregateCommitDiffResult(jobId, watchPath, shas, path);
        return result.Success ? result.Diff : result.Error ?? "";
    }

    public GitDiffLookupResult GetAggregateCommitDiffResult(string jobId, string? watchPath, IEnumerable<string> shas, string? path)
    {
        var parts = new StringBuilder();
        foreach (var sha in NormalizeCommitShas(shas))
        {
            var result = GetCommitDiffResult(jobId, watchPath, sha, path);
            if (!result.Success) return result;
            var diff = result.Diff;
            if (string.IsNullOrWhiteSpace(diff)) continue;
            if (parts.Length > 0) parts.AppendLine();
            parts.Append(diff.TrimEnd());
            parts.AppendLine();
        }
        return new GitDiffLookupResult(true, parts.ToString(), null);
    }

    private static IEnumerable<string> NormalizeCommitShas(IEnumerable<string> shas) =>
        (shas ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s) && IsLikelyShaOrRef(s))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string SimplifyAggregatedStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return "?";
        var first = status[0];
        return first is 'A' or 'D' or 'M' ? first.ToString() : "M";
    }

    private static string CombineAggregatedStatus(string existing, string next)
    {
        if (existing == next) return existing;
        if ((existing == "A" && next == "D") || (existing == "D" && next == "A")) return "M";
        return "M";
    }

    /// <summary>
    /// Convenience used by the auto-commit hook on the progress→review move:
    /// generates a Conventional Commit message via Haiku and commits in one
    /// go. Returns the commit result and the message that was used so the
    /// caller can persist it on the job.
    /// </summary>
    public async Task<(GitCommitResult Result, string Message)> AutoCommitAsync(
        string jobId, string? watchPath, CancellationToken ct = default,
        IReadOnlyCollection<string>? pathspecs = null)
    {
        var statusBefore = GetStatus(jobId, watchPath);
        if (!statusBefore.IsRepo)
            return (new GitCommitResult(false, null, statusBefore.Error ?? "Not a git repo"), "");
        if (statusBefore.FilesChanged == 0)
            return (new GitCommitResult(false, null, "Nothing to commit. Working tree is clean."), "");

        // When scoped, the deterministic-fallback count must reflect the task's
        // own paths, not the (possibly larger) whole-tree dirty count.
        var fileCount = pathspecs is { Count: > 0 } ? pathspecs.Count : statusBefore.FilesChanged;

        var msg = await GenerateCommitMessageAsync(jobId, watchPath, ct, pathspecs);
        var message = msg.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            // Fall back to a deterministic message so an LLM hiccup does not block the auto-commit.
            message = $"chore: snapshot for review ({fileCount} file{(fileCount == 1 ? "" : "s")} changed)";
        }
        var result = Commit(jobId, watchPath, message, pathspecs);
        return (result, message);
    }

    public Task<GitPushResult> PushShaAsync(string sha, string? watchPath, CancellationToken ct = default, string targetBranch = "main")
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(new GitPushResult(false, sha, "cancelled", "Push cancelled."));

        if (!IsLikelyShaOrRef(sha))
            return Task.FromResult(new GitPushResult(false, sha, "invalid-sha", "Invalid SHA."));

        if (!IsLikelyBranchName(targetBranch))
            return Task.FromResult(new GitPushResult(false, sha, "invalid-branch", $"Invalid target branch '{targetBranch}'."));

        var root = ResolveRepoRootForWatchPath(watchPath);
        if (root == null)
            return Task.FromResult(new GitPushResult(false, sha, "repo-missing", "Could not resolve repo root."));

        var (_, existsErr, existsCode) = RunGit(root, $"cat-file -e {sha}^{{commit}}");
        if (existsCode != 0)
            return Task.FromResult(new GitPushResult(false, sha, "missing-sha", existsErr.Trim()));

        RunGitArgs(root, "fetch", "origin", targetBranch);

        var (_, remoteErr, remoteCode) = RunGitArgs(root, "rev-parse", "--verify", $"origin/{targetBranch}");
        if (remoteCode == 0)
        {
            var (_, ancestorErr, ancestorCode) = RunGitArgs(root, "merge-base", "--is-ancestor", sha, $"origin/{targetBranch}");
            if (ancestorCode == 0)
                return Task.FromResult(new GitPushResult(true, sha, "already-remote", null));
            if (ancestorCode != 1)
                _logger.LogInformation("Auto-push ancestor check for {Sha} against {Branch} returned {Code}: {Error}", sha, targetBranch, ancestorCode, ancestorErr.Trim());
        }
        else
        {
            _logger.LogInformation("Auto-push did not find origin/{Branch} before pushing {Sha}: {Error}", targetBranch, sha, remoteErr.Trim());
        }

        var (pushOut, pushErr, pushCode) = RunGitArgs(root, "push", "origin", $"{sha}:refs/heads/{targetBranch}");
        if (pushCode == 0)
            return Task.FromResult(new GitPushResult(true, sha, "pushed", null));

        var err = string.IsNullOrWhiteSpace(pushErr) ? pushOut.Trim() : pushErr.Trim();
        var status = err.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
            || err.Contains("fetch first", StringComparison.OrdinalIgnoreCase)
            || err.Contains("rejected", StringComparison.OrdinalIgnoreCase)
                ? "remote-rejected"
                : "failed";
        return Task.FromResult(new GitPushResult(false, sha, status, err));
    }

    public async Task<GitPushResult> PushShaWithRetryAsync(
        string sha,
        string? watchPath,
        CancellationToken ct = default,
        string targetBranch = "main",
        int attempts = 3,
        TimeSpan? retryDelay = null)
    {
        attempts = Math.Max(1, attempts);
        var delay = retryDelay ?? TimeSpan.FromSeconds(1);
        GitPushResult? last = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (ct.IsCancellationRequested)
                return new GitPushResult(false, sha, "cancelled", "Push cancelled.");

            last = await PushShaAsync(sha, watchPath, ct, targetBranch);
            if (last.Success || !IsRetryablePushStatus(last.Status) || attempt == attempts)
                return last;

            _logger.LogWarning(
                "Push of {Sha} to origin/{Branch} failed with {Status}; retrying attempt {Attempt}/{Attempts}: {Error}",
                sha,
                targetBranch,
                last.Status,
                attempt + 1,
                attempts,
                last.Error ?? "<no error>");
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, ct); }
                catch (TaskCanceledException)
                {
                    return new GitPushResult(false, sha, "cancelled", "Push cancelled.");
                }
            }
        }

        return last ?? new GitPushResult(false, sha, "failed", "Push did not run.");
    }

    public GitWorktreeResult DeleteRemoteBranch(string repoRoot, string branch, string remote = "origin")
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return new GitWorktreeResult(false, null, "Repo root does not exist.");
        if (!IsLikelyBranchName(branch))
            return new GitWorktreeResult(false, null, $"Invalid branch name '{branch}'.");
        if (string.IsNullOrWhiteSpace(remote) || !IsLikelyBranchName(remote))
            return new GitWorktreeResult(false, null, $"Invalid remote name '{remote}'.");

        if (!HasRemote(repoRoot, remote))
            return new GitWorktreeResult(true, repoRoot, null);

        var (lsOut, lsErr, lsCode) = RunGitArgs(repoRoot, "ls-remote", "--exit-code", "--heads", remote, branch);
        if (lsCode == 2)
            return new GitWorktreeResult(true, repoRoot, null);
        if (lsCode != 0)
        {
            var lsError = string.IsNullOrWhiteSpace(lsErr) ? $"Could not inspect {remote}/{branch}." : lsErr.Trim();
            _logger.LogWarning("Remote branch lookup failed for {Remote}/{Branch} at {Path}: {Error}", remote, branch, repoRoot, lsError);
            return new GitWorktreeResult(false, repoRoot, lsError);
        }
        if (string.IsNullOrWhiteSpace(lsOut))
            return new GitWorktreeResult(true, repoRoot, null);

        var (pushOut, pushErr, pushCode) = RunGitArgs(repoRoot, "push", remote, "--delete", branch);
        if (pushCode == 0)
        {
            _logger.LogInformation("Deleted remote branch {Remote}/{Branch} at {Path}", remote, branch, repoRoot);
            return new GitWorktreeResult(true, repoRoot, null);
        }

        var err = string.IsNullOrWhiteSpace(pushErr) ? pushOut.Trim() : pushErr.Trim();
        if (err.Contains("remote ref does not exist", StringComparison.OrdinalIgnoreCase)
            || err.Contains("unable to delete", StringComparison.OrdinalIgnoreCase) && err.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return new GitWorktreeResult(true, repoRoot, null);

        _logger.LogWarning("Remote branch delete failed for {Remote}/{Branch} at {Path}: {Error}", remote, branch, repoRoot, err);
        return new GitWorktreeResult(false, repoRoot, err);
    }

    // ADR-0052 worktree + integration primitives. These are low-level git
    // plumbing for the parallel-task model (worktree-per-task on task/<id>
    // branches off the integration branch). They take an explicit repo or
    // worktree root so the orchestrator can drive them directly and so they
    // are unit-testable against a temp repo. None of them run while
    // maxParallelism == 1, so the sequential runner is unaffected.

    /// <summary>
    /// Creates a new worktree at <paramref name="worktreePath"/> with a fresh
    /// branch <paramref name="branch"/> based on <paramref name="fromRef"/>
    /// (<c>git worktree add -b &lt;branch&gt; &lt;path&gt; &lt;fromRef&gt;</c>).
    /// The shared <c>.git</c> is reused, so this is cheap compared to a clone.
    /// Fails if the branch already exists or the path is occupied.
    /// </summary>
    public GitWorktreeResult WorktreeAdd(string repoRoot, string worktreePath, string branch, string fromRef)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return new GitWorktreeResult(false, null, "Repo root does not exist.");
        if (string.IsNullOrWhiteSpace(worktreePath))
            return new GitWorktreeResult(false, null, "Worktree path is required.");
        if (!IsLikelyBranchName(branch))
            return new GitWorktreeResult(false, null, $"Invalid branch name '{branch}'.");
        if (!IsLikelyBranchName(fromRef))
            return new GitWorktreeResult(false, null, $"Invalid base ref '{fromRef}'.");

        var (_, err, code) = RunGitArgs(repoRoot, "worktree", "add", "-b", branch, worktreePath, fromRef);
        if (code != 0)
        {
            _logger.LogWarning("Worktree add failed for branch {Branch} at {Path}: {Error}", branch, worktreePath, err.Trim());
            return new GitWorktreeResult(false, null, err.Trim());
        }
        _logger.LogInformation("Worktree added: branch {Branch} from {FromRef} at {Path}", branch, fromRef, worktreePath);
        return new GitWorktreeResult(true, worktreePath, null);
    }

    /// <summary>
    /// Removes the worktree at <paramref name="worktreePath"/>
    /// (<c>git worktree remove --force</c>). Force is used so a worktree with
    /// a dirty or locked working tree is still torn down at the end of a task;
    /// the task branch ref survives the removal and is integrated separately.
    /// </summary>
    public GitWorktreeResult WorktreeRemove(string repoRoot, string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return new GitWorktreeResult(false, null, "Repo root does not exist.");
        if (string.IsNullOrWhiteSpace(worktreePath))
            return new GitWorktreeResult(false, null, "Worktree path is required.");

        var (_, err, code) = RunGitArgs(repoRoot, "worktree", "remove", "--force", worktreePath);
        if (code != 0)
        {
            _logger.LogWarning("Worktree remove failed for {Path}: {Error}", worktreePath, err.Trim());
            return new GitWorktreeResult(false, worktreePath, err.Trim());
        }
        _logger.LogInformation("Worktree removed: {Path}", worktreePath);
        return new GitWorktreeResult(true, worktreePath, null);
    }

    /// <summary>
    /// Rebases the branch currently checked out at <paramref name="worktreePath"/>
    /// onto <paramref name="ontoRef"/> (<c>git rebase &lt;ontoRef&gt;</c>),
    /// replaying the task commits on top of the latest integration tip. By
    /// default, conflicts are aborted so the worktree is left clean. The
    /// parallel integration pipeline can pass <paramref name="abortOnConflict"/>
    /// as <c>false</c> so a managed conflict-resolution step gets the actual
    /// conflicted index and conflict markers to resolve.
    /// </summary>
    public GitWorktreeResult RebaseOnto(string worktreePath, string ontoRef, bool abortOnConflict = true)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
            return new GitWorktreeResult(false, null, "Worktree path does not exist.");
        if (!IsLikelyBranchName(ontoRef))
            return new GitWorktreeResult(false, null, $"Invalid onto ref '{ontoRef}'.");

        var (_, err, code) = RunGitArgs(worktreePath, "rebase", ontoRef);
        if (code != 0)
        {
            var conflictedFiles = ListUnmergedFiles(worktreePath);
            if (abortOnConflict)
            {
                RunGitArgs(worktreePath, "rebase", "--abort");
                _logger.LogWarning("Rebase onto {OntoRef} failed at {Path}, aborted: {Error}", ontoRef, worktreePath, err.Trim());
            }
            else
            {
                _logger.LogWarning("Rebase onto {OntoRef} failed at {Path}, preserving conflict state: {Error}", ontoRef, worktreePath, err.Trim());
            }
            return new GitWorktreeResult(false, worktreePath, err.Trim(), conflictedFiles);
        }
        _logger.LogInformation("Rebased worktree {Path} onto {OntoRef}", worktreePath, ontoRef);
        return new GitWorktreeResult(true, worktreePath, null);
    }

    /// <summary>
    /// Returns true when a rebase is active in <paramref name="worktreePath"/>.
    /// Used by the conflict-resolution pipeline step after the resolver has
    /// edited conflict files but before the harness attempts the final
    /// fast-forward merge.
    /// </summary>
    public bool IsRebaseInProgress(string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
            return false;
        var (gitDirRaw, _, code) = RunGitArgs(worktreePath, "rev-parse", "--git-dir");
        if (code != 0 || string.IsNullOrWhiteSpace(gitDirRaw))
            return false;

        var gitDir = gitDirRaw.Trim();
        if (!Path.IsPathRooted(gitDir))
            gitDir = Path.GetFullPath(Path.Combine(worktreePath, gitDir));

        return Directory.Exists(Path.Combine(gitDir, "rebase-merge"))
            || Directory.Exists(Path.Combine(gitDir, "rebase-apply"));
    }

    /// <summary>
    /// Continues an active rebase after conflict files have been resolved and
    /// staged. The editor is disabled so git reuses the original commit message
    /// in headless runner contexts.
    /// </summary>
    public GitWorktreeResult ContinueRebase(string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
            return new GitWorktreeResult(false, null, "Worktree path does not exist.");

        var conflictedFiles = ListUnmergedFiles(worktreePath);
        if (conflictedFiles.Count > 0)
            return new GitWorktreeResult(false, worktreePath, "Unmerged files remain; cannot continue rebase.", conflictedFiles);

        var (_, err, code) = RunGitArgs(worktreePath, "-c", "core.editor=true", "rebase", "--continue");
        if (code != 0)
        {
            var stillConflicted = ListUnmergedFiles(worktreePath);
            _logger.LogWarning("Rebase --continue failed at {Path}: {Error}", worktreePath, err.Trim());
            return new GitWorktreeResult(false, worktreePath, err.Trim(), stillConflicted);
        }
        _logger.LogInformation("Continued rebase at {Path}", worktreePath);
        return new GitWorktreeResult(true, worktreePath, null);
    }

    /// <summary>
    /// Lists currently unmerged files in a worktree (<c>git diff --name-only --diff-filter=U</c>).
    /// Callers that abort a rebase must read this before the abort clears the index.
    /// </summary>
    public IReadOnlyList<string> ListUnmergedFiles(string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
            return Array.Empty<string>();

        var (output, _, code) = RunGitArgs(worktreePath, "diff", "--name-only", "--diff-filter=U");
        if (code != 0 || string.IsNullOrWhiteSpace(output))
            return Array.Empty<string>();

        return output.Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Fast-forwards the branch checked out at <paramref name="repoRoot"/> to
    /// <paramref name="sourceRef"/> (<c>git merge --ff-only &lt;sourceRef&gt;</c>).
    /// After a successful <see cref="RebaseOnto"/> the task branch is a linear
    /// descendant of the integration branch, so this folds it back in with no
    /// merge commit. Fails (without creating a merge commit) when the source
    /// is not a fast-forward, which is the signal to route through the
    /// merge-queue instead.
    /// </summary>
    public GitWorktreeResult MergeFastForward(string repoRoot, string sourceRef)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return new GitWorktreeResult(false, null, "Repo root does not exist.");
        if (!IsLikelyBranchName(sourceRef))
            return new GitWorktreeResult(false, null, $"Invalid source ref '{sourceRef}'.");

        var (_, err, code) = RunGitArgs(repoRoot, "merge", "--ff-only", sourceRef);
        if (code != 0)
        {
            _logger.LogWarning("Fast-forward merge of {SourceRef} failed at {Path}: {Error}", sourceRef, repoRoot, err.Trim());
            return new GitWorktreeResult(false, repoRoot, err.Trim());
        }
        _logger.LogInformation("Fast-forwarded {Path} to {SourceRef}", repoRoot, sourceRef);
        return new GitWorktreeResult(true, repoRoot, null);
    }

    /// <summary>
    /// Deletes the local branch <paramref name="branch"/> from
    /// <paramref name="repoRoot"/>. ADR-0052 worktree teardown: after a task
    /// branch has been folded back into the integration branch its ref is dead
    /// weight, so the cleanup post-step drops it. <paramref name="force"/>
    /// chooses <c>git branch -D</c> (drop even when not fully merged - used when
    /// the work was abandoned) over the default safe <c>git branch -d</c> (which
    /// refuses to delete an unmerged branch, so a successful merge can be
    /// asserted by the delete succeeding). A branch that is still checked out in
    /// a live worktree cannot be deleted; remove the worktree first.
    /// </summary>
    public GitWorktreeResult DeleteBranch(string repoRoot, string branch, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return new GitWorktreeResult(false, null, "Repo root does not exist.");
        if (!IsLikelyBranchName(branch))
            return new GitWorktreeResult(false, null, $"Invalid branch name '{branch}'.");

        var (_, err, code) = RunGitArgs(repoRoot, "branch", force ? "-D" : "-d", branch);
        if (code != 0)
        {
            _logger.LogWarning("Delete branch {Branch} failed at {Path}: {Error}", branch, repoRoot, err.Trim());
            return new GitWorktreeResult(false, null, err.Trim());
        }
        _logger.LogInformation("Deleted branch {Branch} at {Path}", branch, repoRoot);
        return new GitWorktreeResult(true, null, null);
    }

    /// <summary>
    /// True when the local branch <paramref name="branch"/> exists in
    /// <paramref name="repoRoot"/>. ADR-0052: the parallel runner uses this to
    /// decide whether a task already has a <c>task/&lt;id&gt;</c> branch from an
    /// earlier run (which must be REUSED on resume/reissue, not re-cut).
    /// </summary>
    public bool BranchExists(string repoRoot, string branch)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return false;
        if (!IsLikelyBranchName(branch)) return false;
        var (_, _, code) = RunGitArgs(repoRoot, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
        return code == 0;
    }

    /// <summary>
    /// Attaches an <b>existing</b> local branch into a fresh worktree
    /// (<c>git worktree add &lt;path&gt; &lt;branch&gt;</c>, no <c>-b</c>). The
    /// reuse counterpart to <see cref="WorktreeAdd"/>: a task that already owns a
    /// <c>task/&lt;id&gt;</c> branch from a prior run gets that branch (with all
    /// its commits) checked back out so resume/reissue continues where it left
    /// off, instead of failing with "branch already exists".
    /// </summary>
    public GitWorktreeResult WorktreeAddExisting(string repoRoot, string worktreePath, string branch)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return new GitWorktreeResult(false, null, "Repo root does not exist.");
        if (string.IsNullOrWhiteSpace(worktreePath))
            return new GitWorktreeResult(false, null, "Worktree path is required.");
        if (!IsLikelyBranchName(branch))
            return new GitWorktreeResult(false, null, $"Invalid branch name '{branch}'.");

        var (_, err, code) = RunGitArgs(repoRoot, "worktree", "add", worktreePath, branch);
        if (code != 0)
        {
            _logger.LogWarning("Worktree attach failed for existing branch {Branch} at {Path}: {Error}", branch, worktreePath, err.Trim());
            return new GitWorktreeResult(false, null, err.Trim());
        }
        _logger.LogInformation("Worktree attached: existing branch {Branch} at {Path}", branch, worktreePath);
        return new GitWorktreeResult(true, worktreePath, null);
    }

    /// <summary>
    /// The filesystem path of the worktree that currently has
    /// <paramref name="branch"/> checked out, or null when the branch is not
    /// checked out in any registered worktree. Parses
    /// <c>git worktree list --porcelain</c>. Used by the parallel runner to find
    /// (and reuse) a live task worktree on resume.
    /// </summary>
    public string? WorktreePathForBranch(string repoRoot, string branch)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return null;
        if (!IsLikelyBranchName(branch)) return null;
        var (output, _, code) = RunGitArgs(repoRoot, "worktree", "list", "--porcelain");
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return null;

        var wanted = $"refs/heads/{branch}";
        string? currentPath = null;
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
                currentPath = line.Substring("worktree ".Length).Trim();
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var refName = line.Substring("branch ".Length).Trim();
                if (string.Equals(refName, wanted, StringComparison.Ordinal) && currentPath != null)
                {
                    // git emits forward-slash paths even on Windows; normalize to
                    // the platform separator so callers can compare/round-trip it.
                    try { return Path.GetFullPath(currentPath); } catch { return currentPath; }
                }
            }
            else if (line.Length == 0)
                currentPath = null;
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="ancestor"/> is an ancestor of
    /// <paramref name="descendant"/> (<c>git merge-base --is-ancestor</c>) - i.e.
    /// the descendant already contains the ancestor's commit. ADR-0052 terminal
    /// teardown uses this to confirm a task branch is fully folded into the work
    /// branch before dropping it, so unmerged conflict work is never discarded.
    /// </summary>
    public bool IsAncestor(string repoRoot, string ancestor, string descendant)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return false;
        if (!IsLikelyBranchName(ancestor) || !IsLikelyBranchName(descendant)) return false;
        var (_, _, code) = RunGitArgs(repoRoot, "merge-base", "--is-ancestor", ancestor, descendant);
        return code == 0;
    }

    /// <summary>
    /// Prunes stale worktree administrative entries (<c>git worktree prune</c>)
    /// whose working directory was removed out-of-band. Best-effort: lets a
    /// reuse/attach succeed at a deterministic path after a crash left a dead
    /// registration behind.
    /// </summary>
    public void WorktreePrune(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return;
        RunGitArgs(repoRoot, "worktree", "prune");
    }

    /// <summary>
    /// Creates a fresh pooled worktree at <paramref name="worktreePath"/> on a
    /// DETACHED HEAD at <paramref name="atRef"/> (<c>git worktree add --detach</c>).
    /// The recycling pool (Slice A / ASS-1664) keeps idle worktrees on a detached
    /// HEAD rather than on a branch so that any <c>task/&lt;id&gt;</c> branch can be
    /// checked out into the slot later without colliding with git's "branch already
    /// checked out in another worktree" rule. The shared <c>.git</c> is reused.
    /// </summary>
    public GitWorktreeResult WorktreeAddDetached(string repoRoot, string worktreePath, string atRef)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return new GitWorktreeResult(false, null, "Repo root does not exist.");
        if (string.IsNullOrWhiteSpace(worktreePath))
            return new GitWorktreeResult(false, null, "Worktree path is required.");
        if (!IsLikelyBranchName(atRef))
            return new GitWorktreeResult(false, null, $"Invalid base ref '{atRef}'.");

        var (_, err, code) = RunGitArgs(repoRoot, "worktree", "add", "--detach", worktreePath, atRef);
        if (code != 0)
        {
            _logger.LogWarning("Detached worktree add failed at {Path} ({AtRef}): {Error}", worktreePath, atRef, err.Trim());
            return new GitWorktreeResult(false, null, err.Trim());
        }
        _logger.LogInformation("Pooled worktree added (detached): {Path} at {AtRef}", worktreePath, atRef);
        return new GitWorktreeResult(true, worktreePath, null);
    }

    /// <summary>
    /// Best-effort <c>git fetch</c> for the worktree-checkout pre-step. A
    /// single-machine repo with no remote (or an offline run) must not fail the
    /// pre-step, so a missing-remote / network error is swallowed and reported as
    /// success-with-no-op. Pass an explicit <paramref name="remote"/> (default
    /// <c>origin</c>); when the remote is absent the call is skipped.
    /// </summary>
    public GitWorktreeResult Fetch(string root, string remote = "origin")
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new GitWorktreeResult(false, null, "Path does not exist.");

        var (remotesOut, _, remotesCode) = RunGitArgs(root, "remote");
        var hasRemote = remotesCode == 0 && remotesOut
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(remote, StringComparer.Ordinal);
        if (!hasRemote)
            return new GitWorktreeResult(true, root, null); // no remote -> nothing to fetch

        var (_, err, code) = RunGitArgs(root, "fetch", "--prune", remote);
        if (code != 0)
        {
            // Offline / transient: do not wedge the run, but surface the reason.
            _logger.LogWarning("Fetch from {Remote} at {Path} failed (continuing offline): {Error}", remote, root, err.Trim());
            return new GitWorktreeResult(true, root, err.Trim());
        }
        return new GitWorktreeResult(true, root, null);
    }

    /// <summary>
    /// Checks the <paramref name="branch"/> out into the worktree at
    /// <paramref name="worktreePath"/>, creating it off <paramref name="baseRef"/>
    /// when it does not exist yet. <c>--force</c> is used so a pooled worktree that
    /// still carries the previous occupant's (already committed / integrated) HEAD
    /// switches cleanly. This is the "checkout" half of the recycling-pool pre-step
    /// (Slice A): an existing <c>task/&lt;id&gt;</c> branch is reused with its
    /// commits (resume), a new one is cut fresh off the integration branch.
    /// </summary>
    public GitWorktreeResult CheckoutTaskBranch(string worktreePath, string branch, string baseRef)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
            return new GitWorktreeResult(false, null, "Worktree path does not exist.");
        if (!IsLikelyBranchName(branch))
            return new GitWorktreeResult(false, null, $"Invalid branch name '{branch}'.");
        if (!IsLikelyBranchName(baseRef))
            return new GitWorktreeResult(false, null, $"Invalid base ref '{baseRef}'.");

        var exists = RunGitArgs(worktreePath, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").Code == 0;
        var (_, err, code) = exists
            ? RunGitArgs(worktreePath, "checkout", "--force", branch)
            : RunGitArgs(worktreePath, "checkout", "--force", "-b", branch, baseRef);
        if (code != 0)
        {
            _logger.LogWarning("Checkout of {Branch} (base {Base}) at {Path} failed: {Error}", branch, baseRef, worktreePath, err.Trim());
            return new GitWorktreeResult(false, worktreePath, err.Trim());
        }
        return new GitWorktreeResult(true, worktreePath, null);
    }

    /// <summary>
    /// Detaches HEAD in the worktree at <paramref name="worktreePath"/>
    /// (<c>git checkout --detach</c>), freeing whatever branch it had checked out
    /// so another pooled worktree can claim that branch. Used by the recycling
    /// pool to release a <c>task/&lt;id&gt;</c> branch left checked out in an idle
    /// slot before checking it out into the acquired slot (git forbids the same
    /// branch in two live worktrees).
    /// </summary>
    public GitWorktreeResult DetachHead(string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
            return new GitWorktreeResult(false, null, "Worktree path does not exist.");

        var (_, err, code) = RunGitArgs(worktreePath, "checkout", "--detach");
        if (code != 0)
        {
            _logger.LogWarning("Detach HEAD at {Path} failed: {Error}", worktreePath, err.Trim());
            return new GitWorktreeResult(false, worktreePath, err.Trim());
        }
        return new GitWorktreeResult(true, worktreePath, null);
    }

    /// <summary>
    /// Hard-resets the worktree at <paramref name="worktreePath"/> to its current
    /// branch tip (or to <paramref name="toRef"/> when given), dropping any
    /// uncommitted index / working-tree changes left by a previous occupant. The
    /// reset half of the recycling-pool pre-step.
    /// </summary>
    public GitWorktreeResult ResetHard(string worktreePath, string? toRef = null)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
            return new GitWorktreeResult(false, null, "Worktree path does not exist.");
        if (toRef != null && !IsLikelyBranchName(toRef))
            return new GitWorktreeResult(false, null, $"Invalid reset ref '{toRef}'.");

        var (_, err, code) = toRef is null
            ? RunGitArgs(worktreePath, "reset", "--hard")
            : RunGitArgs(worktreePath, "reset", "--hard", toRef);
        if (code != 0)
        {
            _logger.LogWarning("Reset --hard at {Path} failed: {Error}", worktreePath, err.Trim());
            return new GitWorktreeResult(false, worktreePath, err.Trim());
        }
        return new GitWorktreeResult(true, worktreePath, null);
    }

    /// <summary>
    /// Removes untracked files and directories from the worktree while PRESERVING
    /// <c>node_modules</c> (<c>git clean -fd -e node_modules</c>). Slice A
    /// invariant: <b>NEVER</b> <c>-x</c> - ignored build artefacts AND the
    /// dependency cache must survive recycling so <c>deps-ensure</c> can skip the
    /// install when the lockfiles are unchanged. This is the leak-proof
    /// replacement for the old <c>git add -A</c> cross-sweep: a recycled worktree
    /// starts each task with only tracked files + node_modules, so a commit can
    /// never sweep in a sibling task's stray output.
    /// </summary>
    public GitWorktreeResult CleanPreservingNodeModules(string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
            return new GitWorktreeResult(false, null, "Worktree path does not exist.");

        var (_, err, code) = RunGitArgs(worktreePath, "clean", "-fd", "-e", "node_modules");
        if (code != 0)
        {
            _logger.LogWarning("Clean (preserve node_modules) at {Path} failed: {Error}", worktreePath, err.Trim());
            return new GitWorktreeResult(false, worktreePath, err.Trim());
        }
        return new GitWorktreeResult(true, worktreePath, null);
    }

    /// <summary>
    /// Returns the working tree's current HEAD SHA, or null when the path
    /// is not a git repository or git is unavailable. Used by the run
    /// timeline to capture the deterministic "before / after" SHAs that
    /// give us a precise rev-list range for "commits made during this
    /// run" - the wall-clock window in <see cref="GetCommitsBetween"/>
    /// is a best-effort fallback for older runs / projects without a
    /// repo configured.
    /// </summary>
    public string? GetHeadSha(string jobId, string? watchPath)
    {
        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null) return null;
        var root = ResolveGitToplevel(configured);
        if (root == null) return null;
        var (output, _, code) = RunGit(root, "rev-parse HEAD");
        if (code != 0) return null;
        var sha = output.Trim();
        return string.IsNullOrWhiteSpace(sha) ? null : sha;
    }

    /// <summary>
    /// Reads HEAD at an explicit repo or worktree root with no config-based
    /// resolution. ADR-0052 integration steps drive git against paths the
    /// orchestrator already holds (the main checkout, a task worktree), so they
    /// need a direct HEAD read rather than the jobId/watchPath lookup
    /// <see cref="GetHeadSha"/> performs. Returns null when the path is missing
    /// or not a git repo.
    /// </summary>
    public string? ReadHeadShaAt(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        var (output, _, code) = RunGit(root, "rev-parse HEAD");
        if (code != 0) return null;
        var sha = output.Trim();
        return string.IsNullOrWhiteSpace(sha) ? null : sha;
    }

    /// <summary>
    /// Lists commits in the SHA range <c>before..after</c> (exclusive of
    /// <paramref name="beforeSha"/>, inclusive of <paramref name="afterSha"/>).
    /// This is the *deterministic* commit-attribution path: the run
    /// timeline captures HEAD before/after each CLI run, so this
    /// method returns exactly the commits that landed during the run -
    /// no wall-clock heuristic, no boundary slack, no double-attribution
    /// when two runs share the same minute.
    ///
    /// Returns an empty list when either SHA is missing, equal, or the
    /// repo is not a git repo. Callers should fall back to
    /// <see cref="GetCommitsBetween"/> in those cases.
    /// </summary>
    public List<GitCommitInfo> GetCommitsInShaRange(string jobId, string? watchPath, string? beforeSha, string? afterSha)
    {
        if (string.IsNullOrWhiteSpace(beforeSha) || string.IsNullOrWhiteSpace(afterSha)) return [];
        if (string.Equals(beforeSha, afterSha, StringComparison.OrdinalIgnoreCase)) return [];

        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null) return [];
        var root = ResolveGitToplevel(configured);
        if (root == null) return [];

        // Defensive: reject anything that looks like a flag or path so
        // git can't be coaxed into running an unrelated command via a
        // crafted SHA argument.
        if (!IsLikelyShaOrRef(beforeSha!) || !IsLikelyShaOrRef(afterSha!)) return [];

        var cacheKey = $"commits|{root}|{beforeSha}|{afterSha}";
        if (TryGetShaRangeCached<List<GitCommitInfo>>(cacheKey, out var cached)) return cached;

        const char US = '';
        var fmt = "%H%x1f%h%x1f%aI%x1f%aN%x1f%s";
        var args = $"log --no-merges --shortstat --pretty=format:\"{fmt}\" {beforeSha}..{afterSha}";
        var (output, _, code) = RunGit(root, args);
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return [];

        var list = new List<GitCommitInfo>();
        var raw = output.Replace("\r\n", "\n");
        foreach (var block in raw.Split("\n\n", StringSplitOptions.None))
        {
            if (string.IsNullOrWhiteSpace(block)) continue;
            string? recordLine = null;
            string? shortstatLine = null;
            foreach (var l in block.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                if (recordLine == null) recordLine = l;
                else { shortstatLine = l.Trim(); break; }
            }
            if (recordLine == null) continue;
            var parts = recordLine.Split(US);
            if (parts.Length < 5) continue;
            if (!DateTime.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var ts))
                continue;
            var (files, added, removed) = ParseShortstat(shortstatLine);
            list.Add(new GitCommitInfo(
                Sha: parts[0],
                ShortSha: parts[1],
                AuthorDateUtc: DateTime.SpecifyKind(ts, DateTimeKind.Utc),
                Author: parts[3],
                Subject: parts[4],
                FilesChanged: files,
                Added: added,
                Removed: removed));
        }
        StoreShaRangeCached(cacheKey, list);
        return list;
    }

    /// <summary>
    /// Single source of truth for the durable per-run worktree-commit body
    /// trailer. Both the writer (<c>ProjectRunner.IntegrateWorktreeRunAsync</c>
    /// crash-recovery commit) and the reader (<see cref="GetTaskRunCommits"/>
    /// grep) MUST use this so reconstruction stays exact. <paramref name="jobId"/>
    /// is the run's job id (= the <c>task/&lt;slug&gt;</c> branch suffix).
    /// </summary>
    public static string WorktreeRunCommitTrailer(string jobId)
        => $"[parallel-slot worktree run; jobId={jobId}]";

    /// <summary>
    /// Reconstructs the full per-task commit history for a per-task-worktree
    /// job by grepping every ref for the durable run trailer
    /// (<see cref="WorktreeRunCommitTrailer"/>). This is the recoverable
    /// source the collapsed per-run SHA ranges and the (empty-while-in-progress)
    /// attribution chain cannot provide: <c>direct-merge</c> integration rebases
    /// <c>task/&lt;id&gt;</c> onto develop and fast-forwards, so the branch never
    /// retains more than its un-integrated tip - but the trailer survives the
    /// rebase+FF and uniquely identifies each run's commit on the mainline.
    /// Deduped by SHA, newest-first via the caller's sort. Empty when the repo
    /// can't be resolved or no run commits exist (e.g. shared-checkout tasks).
    /// </summary>
    public List<GitCommitInfo> GetTaskRunCommits(string jobId, string? watchPath)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return [];

        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null) return [];
        var root = ResolveGitToplevel(configured);
        if (root == null) return [];

        var marker = WorktreeRunCommitTrailer(jobId);
        var cacheKey = $"taskruncommits|{root}|{marker}";
        if (TryGetShaRangeCached<List<GitCommitInfo>>(cacheKey, out var cached)) return cached;

        var fmt = "%H%x1f%h%x1f%aI%x1f%aN%x1f%s";
        // RunGitArgs passes args via ArgumentList (no shell re-split), so the
        // marker's spaces/brackets reach git verbatim. --fixed-strings makes the
        // bracketed trailer a literal, not a regex.
        var (output, _, code) = RunGitArgs(root,
            "log", "--all", "--no-merges", "--shortstat", "--fixed-strings",
            "--grep=" + marker, "--pretty=format:" + fmt);
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return [];

        var list = ParseCommitLogBlocks(output);
        StoreShaRangeCached(cacheKey, list);
        return list;
    }

    /// <summary>
    /// Parses the <c>%H%x1f%h%x1f%aI%x1f%aN%x1f%s</c> + <c>--shortstat</c> block
    /// stream git emits, into <see cref="GitCommitInfo"/> rows, deduped by SHA.
    /// Mirrors the inline parse in <see cref="GetCommitsInShaRange"/>; kept
    /// separate so the range query stays untouched.
    /// </summary>
    private static List<GitCommitInfo> ParseCommitLogBlocks(string output)
    {
        const char US = '\x1f';
        var list = new List<GitCommitInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var raw = output.Replace("\r\n", "\n");
        foreach (var block in raw.Split("\n\n", StringSplitOptions.None))
        {
            if (string.IsNullOrWhiteSpace(block)) continue;
            string? recordLine = null;
            string? shortstatLine = null;
            foreach (var l in block.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                if (recordLine == null) recordLine = l;
                else { shortstatLine = l.Trim(); break; }
            }
            if (recordLine == null) continue;
            var parts = recordLine.Split(US);
            if (parts.Length < 5) continue;
            if (string.IsNullOrWhiteSpace(parts[0]) || !seen.Add(parts[0])) continue;
            if (!DateTime.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var ts))
                continue;
            var (files, added, removed) = ParseShortstat(shortstatLine);
            list.Add(new GitCommitInfo(
                Sha: parts[0],
                ShortSha: parts[1],
                AuthorDateUtc: DateTime.SpecifyKind(ts, DateTimeKind.Utc),
                Author: parts[3],
                Subject: parts[4],
                FilesChanged: files,
                Added: added,
                Removed: removed));
        }
        return list;
    }

    /// <summary>
    /// Per-SHA enrichment for the commit-attribution step. Returns the full
    /// commit message body and merge flag for each requested SHA in a single
    /// <c>git show -s</c> call. Unknown SHAs are simply absent from the
    /// result; an unavailable repo yields an empty dictionary. SHAs are
    /// validated through <see cref="IsLikelyShaOrRef"/> first so a crafted
    /// argument cannot smuggle a flag into the git invocation.
    /// </summary>
    public Dictionary<string, CommitMeta> GetCommitMeta(string jobId, string? watchPath, IEnumerable<string> shas)
    {
        var result = new Dictionary<string, CommitMeta>(StringComparer.OrdinalIgnoreCase);
        var list = (shas ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s) && IsLikelyShaOrRef(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count == 0) return result;

        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null) return result;
        var root = ResolveGitToplevel(configured);
        if (root == null) return result;

        const char US = '';
        const char RS = '';
        var args = "show -s --no-patch --pretty=format:\"%H%x1f%P%x1f%B%x1e\" " + string.Join(' ', list);
        var (output, _, code) = RunGit(root, args);
        if (code != 0 || string.IsNullOrEmpty(output)) return result;

        foreach (var rec in output.Split(RS))
        {
            var block = rec.Trim('\n', '\r', ' ');
            if (string.IsNullOrWhiteSpace(block)) continue;
            var firstUs = block.IndexOf(US);
            if (firstUs < 0) continue;
            var sha = block[..firstUs];
            var rest = block[(firstUs + 1)..];
            var secondUs = rest.IndexOf(US);
            if (secondUs < 0) continue;
            var parents = rest[..secondUs];
            var body = rest[(secondUs + 1)..];
            var parentCount = parents.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            result[sha] = new CommitMeta(body, parentCount > 1);
        }
        return result;
    }

    /// <summary>
    /// Aggregated file list for the SHA range <c>before..after</c>.
    /// One row per path that any commit in the range touched, with the
    /// combined +/- counts (the largest insertion / deletion seen, not
    /// summed - a file rewritten twice in the same run shows the net
    /// diff, not double-counted). Drives the file-tree side of the
    /// run's git viewer modal.
    /// </summary>
    public List<GitFileChange> GetFilesChangedInShaRange(string jobId, string? watchPath, string? beforeSha, string? afterSha)
    {
        if (string.IsNullOrWhiteSpace(beforeSha) || string.IsNullOrWhiteSpace(afterSha)) return [];
        if (!IsLikelyShaOrRef(beforeSha!) || !IsLikelyShaOrRef(afterSha!)) return [];
        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null) return [];
        var root = ResolveGitToplevel(configured);
        if (root == null) return [];

        var cacheKey = $"files|{root}|{beforeSha}|{afterSha}";
        if (TryGetShaRangeCached<List<GitFileChange>>(cacheKey, out var cached)) return cached;

        // git diff --name-status + --numstat over the range: one git
        // call each, merged by path. --diff-filter avoids type-change
        // noise; we keep adds, deletes, modifies, renames, and copies.
        var statusByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        var (statusOut, _, statusCode) = RunGit(root, $"diff --name-status --diff-filter=ACDMR {beforeSha}..{afterSha}");
        if (statusCode == 0)
        {
            foreach (var line in statusOut.Replace("\r\n", "\n").Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;
                statusByPath[parts[^1].Trim()] = parts[0].Trim();
            }
        }

        var numstat = new Dictionary<string, (int Added, int Removed)>(StringComparer.Ordinal);
        var (numOut, _, numCode) = RunGit(root, $"diff --numstat {beforeSha}..{afterSha}");
        if (numCode == 0)
        {
            foreach (var line in numOut.Replace("\r\n", "\n").Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                var added = int.TryParse(parts[0], out var a) ? a : 0;
                var removed = int.TryParse(parts[1], out var r) ? r : 0;
                numstat[parts[^1].Trim()] = (added, removed);
            }
        }

        var result = statusByPath
            .Select(kv =>
            {
                var (added, removed) = numstat.TryGetValue(kv.Key, out var n) ? n : (0, 0);
                return new GitFileChange(kv.Value, kv.Key, added, removed);
            })
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        StoreShaRangeCached(cacheKey, result);
        return result;
    }

    /// <summary>
    /// Unified diff for the SHA range, optionally scoped to one path.
    /// Used by the run's git viewer when a file is selected. Empty
    /// string is a valid result (no changes for that path in the
    /// range) and should not be treated as an error.
    /// </summary>
    public string GetDiffInShaRange(string jobId, string? watchPath, string? beforeSha, string? afterSha, string? path)
    {
        if (string.IsNullOrWhiteSpace(beforeSha) || string.IsNullOrWhiteSpace(afterSha)) return "";
        if (!IsLikelyShaOrRef(beforeSha!) || !IsLikelyShaOrRef(afterSha!)) return "";
        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null) return "";
        var root = ResolveGitToplevel(configured);
        if (root == null) return "";

        var cacheKey = $"diff|{root}|{beforeSha}|{afterSha}|{path ?? ""}";
        if (TryGetShaRangeCached<string>(cacheKey, out var cachedDiff)) return cachedDiff;

        var pathArg = string.IsNullOrWhiteSpace(path)
            ? ""
            : $" -- \"{path!.Replace("\"", "\\\"")}\"";
        var (output, err, code) = RunGit(root, $"diff {beforeSha}..{afterSha}{pathArg}");
        var diff = code == 0 ? output : err;
        // Cache successful diffs only; an err payload is usually a transient
        // git error (lock contention, repo busy) we don't want to pin.
        if (code == 0 && !string.IsNullOrEmpty(output))
        {
            StoreShaRangeCached(cacheKey, diff);
        }
        return diff;
    }

    private static bool IsLikelyShaOrRef(string s)
    {
        // Accept hex SHAs (full or short) and a small set of git refs.
        // We never accept anything containing whitespace, slashes, or
        // shell metacharacters - the SHA flows into a git argument list,
        // and we want defence in depth even though RunGit doesn't shell.
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '^')) return false;
        }
        return true;
    }

    private static bool IsRetryablePushStatus(string status)
        => !string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(status, "invalid-sha", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(status, "invalid-branch", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(status, "missing-sha", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(status, "repo-missing", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(status, "already-remote", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="s"/> is a safe local branch name. Like
    /// <see cref="IsLikelyShaOrRef"/> but additionally allows the slash that
    /// namespaced branches need (e.g. <c>task/42</c>, <c>develop</c>). Still
    /// rejects whitespace and shell metacharacters, a leading dash (so the
    /// name can't be read as a flag), and git's invalid sequences
    /// (<c>..</c>, leading/trailing slash, trailing <c>.lock</c>). The value
    /// flows into a git argument list, not a shell, so this is defence in
    /// depth rather than the only guard.
    /// </summary>
    private static bool IsLikelyBranchName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (s[0] == '-' || s[0] == '/' || s[^1] == '/') return false;
        if (s.Contains("..", StringComparison.Ordinal)) return false;
        if (s.EndsWith(".lock", StringComparison.Ordinal)) return false;
        foreach (var c in s)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == '/')) return false;
        }
        return true;
    }

    private static bool HasRemote(string repoRoot, string remote)
    {
        var (remotesOut, _, remotesCode) = RunGitArgs(repoRoot, "remote");
        if (remotesCode != 0 || string.IsNullOrWhiteSpace(remotesOut)) return false;
        return remotesOut
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(remote, StringComparer.Ordinal);
    }

    /// <summary>
    /// Lists commits whose author date falls in the half-open interval
    /// <c>[fromUtc, toUtc)</c>. Used by the per-run commit lookup so the
    /// protocol-pane run timeline can show the software-side change set
    /// for each run. Returns an empty list when the path is not a repo
    /// or git is missing - callers should treat empty as "no commits in
    /// this window", not a hard failure.
    ///
    /// <para>
    /// The matching window is the run's wall-clock duration, not its
    /// commit count: the agent may have authored a commit a few seconds
    /// before <c>StartedAt</c> if the runner spawned the CLI before the
    /// session-event line landed. We add a small buffer on each side
    /// (<paramref name="paddingSeconds"/>) so a normal sequential run
    /// captures its commits even when the wall-clock boundary slipped.
    /// </para>
    /// </summary>
    public List<GitCommitInfo> GetCommitsBetween(
        string jobId,
        string? watchPath,
        DateTime fromUtc,
        DateTime toUtc,
        int paddingSeconds = 5)
    {
        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null) return [];
        var root = ResolveGitToplevel(configured);
        if (root == null) return [];

        if (toUtc < fromUtc) (fromUtc, toUtc) = (toUtc, fromUtc);
        var fromIso = fromUtc.AddSeconds(-paddingSeconds).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var toIso   = toUtc.AddSeconds(paddingSeconds).ToString("yyyy-MM-ddTHH:mm:ssZ");

        // Single git invocation, machine-parseable. Records are separated
        // by ASCII Record Separator (0x1E); fields by Unit Separator (0x1F).
        // --shortstat is appended on its own line after the format string.
        const char US = '';
        var fmt = "%H%x1f%h%x1f%aI%x1f%aN%x1f%s";
        var args = $"log --no-merges --since=\"{fromIso}\" --until=\"{toIso}\" --shortstat --pretty=format:\"{fmt}\"";
        var (output, _, code) = RunGit(root, args);
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return [];

        var list = new List<GitCommitInfo>();
        // Records emitted by --pretty=format: are separated by newlines;
        // when --shortstat is on, each record ends with a blank line then
        // " N files changed, +X insertions(+), -Y deletions(-)".
        var raw = output.Replace("\r\n", "\n");
        var blocks = raw.Split("\n\n", StringSplitOptions.None);
        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block)) continue;
            var blockLines = block.Split('\n');
            // First non-empty line is the format record; subsequent lines
            // (if any) are the shortstat. Some commits with no file
            // changes (rare with --no-merges) won't have a shortstat line.
            string? recordLine = null;
            string? shortstatLine = null;
            foreach (var l in blockLines)
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                if (recordLine == null) recordLine = l;
                else { shortstatLine = l.Trim(); break; }
            }
            if (recordLine == null) continue;
            var parts = recordLine.Split(US);
            if (parts.Length < 5) continue;
            if (!DateTime.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var ts))
                continue;
            var (files, added, removed) = ParseShortstat(shortstatLine);
            list.Add(new GitCommitInfo(
                Sha: parts[0],
                ShortSha: parts[1],
                AuthorDateUtc: DateTime.SpecifyKind(ts, DateTimeKind.Utc),
                Author: parts[3],
                Subject: parts[4],
                FilesChanged: files,
                Added: added,
                Removed: removed));
        }
        return list;
    }

    /// <summary>
    /// Per-file commit history (newest first) for a path relative to the
    /// repository root. Backs the wiki "history / provenance" panel: which
    /// commit (and thus which agent run) touched a document, when, and the
    /// commit subject as the "why". <c>--follow</c> keeps a renamed document's
    /// lineage intact. Returns an empty list when the repo or path can't be
    /// resolved, mirroring the other read-only git lookups on this service.
    /// </summary>
    public List<GitCommitInfo> GetFileHistory(string repoRoot, string repoRelPath, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(repoRelPath)) return [];
        var root = ResolveGitToplevel(repoRoot) ?? repoRoot;
        if (!Directory.Exists(root)) return [];
        if (limit <= 0) limit = 50;

        // Fields separated by Unit Separator (0x1F) so a commit subject with
        // any printable char round-trips. --follow needs exactly one pathspec
        // after `--`; args are passed verbatim so a path with spaces survives.
        const string fmt = "%H%x1f%h%x1f%aI%x1f%aN%x1f%s";
        var (output, _, code) = RunGitArgs(root,
            "log", "--no-merges", $"--max-count={limit}", "--follow",
            "--shortstat", $"--pretty=format:{fmt}", "--", repoRelPath.Replace('\\', '/'));
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return [];
        return ParseLogBlocks(output);
    }

    /// <summary>
    /// Parses <c>git log --shortstat --pretty=format:%H&lt;US&gt;...</c> output
    /// into <see cref="GitCommitInfo"/> records. Records are newline-separated;
    /// when --shortstat is on each record is followed by a blank line then the
    /// " N files changed, +X / -Y" summary. Fields inside a record use the
    /// Unit Separator (0x1F).
    /// </summary>
    private static List<GitCommitInfo> ParseLogBlocks(string output)
    {
        const char US = '';
        var list = new List<GitCommitInfo>();
        var raw = output.Replace("\r\n", "\n");
        var blocks = raw.Split("\n\n", StringSplitOptions.None);
        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block)) continue;
            var blockLines = block.Split('\n');
            string? recordLine = null;
            string? shortstatLine = null;
            foreach (var l in blockLines)
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                if (recordLine == null) recordLine = l;
                else { shortstatLine = l.Trim(); break; }
            }
            if (recordLine == null) continue;
            var parts = recordLine.Split(US);
            if (parts.Length < 5) continue;
            if (!DateTime.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var ts))
                continue;
            var (files, added, removed) = ParseShortstat(shortstatLine);
            list.Add(new GitCommitInfo(
                Sha: parts[0],
                ShortSha: parts[1],
                AuthorDateUtc: DateTime.SpecifyKind(ts, DateTimeKind.Utc),
                Author: parts[3],
                Subject: parts[4],
                FilesChanged: files,
                Added: added,
                Removed: removed));
        }
        return list;
    }

    /// <summary>
    /// Best-effort model/agent attribution for the most recent commit that
    /// touched <paramref name="repoRelPath"/>, read from the commit's
    /// <c>Co-authored-by</c> trailer (managed runs stamp the model there). The
    /// address is stripped so the wiki provenance line shows just the model
    /// name. Returns null for a hand-authored commit with no such trailer.
    /// </summary>
    public string? GetLatestModelForPath(string repoRoot, string repoRelPath)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(repoRelPath)) return null;
        var root = ResolveGitToplevel(repoRoot) ?? repoRoot;
        if (!Directory.Exists(root)) return null;

        var (output, _, code) = RunGitArgs(root,
            "log", "--no-merges", "--max-count=1",
            "--pretty=format:%(trailers:key=Co-authored-by,valueonly,separator=%x1f)",
            "--", repoRelPath.Replace('\\', '/'));
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return null;
        return ParseModelTrailer(output);
    }

    /// <summary>
    /// Extracts a display model name from a <c>Co-authored-by</c> trailer
    /// value-list ("Name &lt;email&gt;", possibly several, US-separated). Takes
    /// the first non-empty entry and strips the address. Split out for tests.
    /// </summary>
    internal static string? ParseModelTrailer(string trailerOutput)
    {
        if (string.IsNullOrWhiteSpace(trailerOutput)) return null;
        var first = trailerOutput
            .Replace("\r", "")
            .Split('\n', '\x1f')
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        if (string.IsNullOrWhiteSpace(first)) return null;
        var lt = first.IndexOf('<');
        var name = (lt > 0 ? first[..lt] : first).Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Counts commits in the SHA range without deserialising any
    /// metadata. Cheap enough for the per-job kanban aggregate path
    /// (one process per non-trivial range). Returns 0 when the range is
    /// empty, the SHAs are missing, or the repo is unavailable.
    /// </summary>
    public int CountCommitsInShaRange(string jobId, string? watchPath, string? beforeSha, string? afterSha)
    {
        if (string.IsNullOrWhiteSpace(beforeSha) || string.IsNullOrWhiteSpace(afterSha)) return 0;
        if (string.Equals(beforeSha, afterSha, StringComparison.OrdinalIgnoreCase)) return 0;
        if (!IsLikelyShaOrRef(beforeSha!) || !IsLikelyShaOrRef(afterSha!)) return 0;
        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null) return 0;
        var root = ResolveGitToplevel(configured);
        if (root == null) return 0;

        var (output, _, code) = RunGit(root, $"rev-list --no-merges --count {beforeSha}..{afterSha}");
        if (code != 0) return 0;
        return int.TryParse(output.Trim(), out var n) ? n : 0;
    }

    private static (int Files, int Added, int Removed) ParseShortstat(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return (0, 0, 0);
        // Examples:
        //   1 file changed, 4 insertions(+)
        //   2 files changed, 12 insertions(+), 3 deletions(-)
        //   1 file changed, 1 deletion(-)
        int files = 0, added = 0, removed = 0;
        var m1 = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)\s+files?\s+changed");
        if (m1.Success) int.TryParse(m1.Groups[1].Value, out files);
        var m2 = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)\s+insertions?\(\+\)");
        if (m2.Success) int.TryParse(m2.Groups[1].Value, out added);
        var m3 = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)\s+deletions?\(-\)");
        if (m3.Success) int.TryParse(m3.Groups[1].Value, out removed);
        return (files, added, removed);
    }

    public bool OpenInVsCode(string jobId, string? watchPath, out string? error)
    {
        error = null;
        var root = ResolveRepoRoot(jobId, watchPath);
        if (root == null) { error = "Could not resolve repo root."; return false; }
        if (!Directory.Exists(root)) { error = $"Path does not exist: {root}"; return false; }

        var codePath = _config["VsCode:Path"] ?? "code";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = CliExecutionServiceBase.ResolveExecutable(codePath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = root
            };
            // -r reuses the existing window if one is open for this folder; if
            // no instance exists yet, a new one starts. That's the closest
            // we can get without an unreliable PID/window-title scan.
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add(root);
            using var p = Process.Start(psi);
            return p != null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string SanitizeCommitMessage(string raw)
    {
        var lines = raw.Replace("\r\n", "\n").Split('\n')
            .SkipWhile(l => l.StartsWith("```"))
            .Reverse().SkipWhile(l => l.StartsWith("```") || string.IsNullOrWhiteSpace(l)).Reverse();
        return string.Join("\n", lines).Trim();
    }

    private readonly record struct TaskIntent(string Title, string PromptFirstParagraph, string LastUserContinue);

    private TaskIntent ReadTaskIntent(string jobId, string? watchPath)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return new TaskIntent("", "", "");

        var title = info.Title ?? "";
        var firstParagraph = ReadFirstParagraph(Path.Combine(info.FolderPath, "prompt.md"));
        var lastContinue = ReadLastExtensionPrompt(info.FolderPath);
        return new TaskIntent(title, firstParagraph, lastContinue);
    }

    private static string ReadFirstParagraph(string path)
    {
        if (!File.Exists(path)) return "";
        string body;
        try { body = File.ReadAllText(path); }
        catch { return ""; }
        return ExtractFirstParagraph(body);
    }

    internal static string ExtractFirstParagraph(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var normalized = body.Replace("\r\n", "\n").TrimStart('﻿', ' ', '\t', '\n');
        var blank = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        var paragraph = blank >= 0 ? normalized[..blank] : normalized;
        paragraph = paragraph.Trim();
        // Bound length so a wall-of-text prompt does not dominate the LLM call.
        const int max = 1500;
        if (paragraph.Length > max) paragraph = paragraph[..max].TrimEnd() + "...";
        return paragraph;
    }

    private static string ReadLastExtensionPrompt(string jobFolder)
    {
        if (!Directory.Exists(jobFolder)) return "";
        int bestIndex = -1;
        string? bestPath = null;
        foreach (var path in Directory.EnumerateFiles(jobFolder, "prompt-*.md"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var dash = name.IndexOf('-');
            if (dash < 0 || dash >= name.Length - 1) continue;
            if (!int.TryParse(name[(dash + 1)..], out var index)) continue;
            if (index > bestIndex)
            {
                bestIndex = index;
                bestPath = path;
            }
        }
        if (bestPath == null) return "";
        string body;
        try { body = File.ReadAllText(bestPath); }
        catch { return ""; }
        var trimmed = body.Replace("\r\n", "\n").Trim();
        const int max = 1500;
        if (trimmed.Length > max) trimmed = trimmed[..max].TrimEnd() + "...";
        return trimmed;
    }

    /// <summary>
    /// Resolves a configured project path to its git work-tree toplevel via
    /// <c>git rev-parse --show-toplevel</c>. Returns the toplevel (which may
    /// equal the input or be a parent directory) or null if the path is not
    /// inside a git repository.
    /// </summary>
    private static string? ResolveGitToplevel(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;
        var (output, _, code) = RunGit(path, "rev-parse --show-toplevel");
        if (code != 0) return null;
        var toplevel = output.Trim();
        if (string.IsNullOrEmpty(toplevel)) return null;
        return toplevel.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string? ResolveConfiguredRepositoryPath(WatchPathEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.RepositoryPath)) return entry.RepositoryPath;
        if (!string.IsNullOrWhiteSpace(entry.RootPath)) return entry.RootPath;
        return null;
    }

    private static (string Out, string Err, int Code) RunGit(string cwd, string args, string? stdin = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardInput = stdin != null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        return RunGitProcess(psi, stdin);
    }

    /// <summary>
    /// Like <see cref="RunGit"/> but passes each argument verbatim via
    /// <see cref="ProcessStartInfo.ArgumentList"/> instead of a single
    /// pre-joined string. Use this whenever an argument can contain a space
    /// or other character the string form would re-split (worktree paths,
    /// branch names) - the OS receives the array unchanged, so there is no
    /// quoting to get wrong and no shell to inject into.
    /// </summary>
    private static (string Out, string Err, int Code) RunGitArgs(string cwd, params string[] args)
        => RunGitArgs(cwd, args, stdin: null);

    private static (string Out, string Err, int Code) RunGitArgs(string cwd, string[] args, string? stdin)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardInput = stdin != null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        return RunGitProcess(psi, stdin);
    }

    private static (string Out, string Err, int Code) RunGitProcess(ProcessStartInfo psi, string? stdin)
    {
        try
        {
            using var p = Process.Start(psi)!;
            if (stdin != null)
            {
                p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(true); } catch { }
                return ("", "git timed out", -1);
            }
            return (so, se, p.ExitCode);
        }
        catch (Exception ex)
        {
            return ("", ex.Message, -1);
        }
    }

}
