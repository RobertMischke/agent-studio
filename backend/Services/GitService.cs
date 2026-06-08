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

/// <summary>
/// ADR-0052: result of a worktree / integration primitive
/// (<see cref="GitService.WorktreeAdd"/>, <see cref="GitService.WorktreeRemove"/>,
/// <see cref="GitService.RebaseOnto"/>, <see cref="GitService.MergeFastForward"/>).
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
        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null) return "";
        var root = ResolveGitToplevel(configured);
        if (root == null) return "";
        // HEAD diff catches both staged and unstaged. For untracked files we
        // fall back to showing the file body so the panel isn't empty.
        var args = string.IsNullOrWhiteSpace(path)
            ? "diff HEAD"
            : $"diff HEAD -- \"{path.Replace("\"", "\\\"")}\"";
        var (output, err, code) = RunGit(root, args);
        if (code == 0 && !string.IsNullOrWhiteSpace(output)) return output;

        if (!string.IsNullOrWhiteSpace(path))
        {
            var abs = Path.Combine(root, path);
            if (File.Exists(abs))
            {
                try { return File.ReadAllText(abs); } catch { /* best-effort */ }
            }
        }
        return string.IsNullOrWhiteSpace(output) ? err : output;
    }

    /// <summary>
    /// Commits the working tree with a fixed <c>crash-recovery</c> author tag.
    /// Used by <see cref="OrchestratorApi.Services.Runner.CrashRecoveryService"/>
    /// to rescue uncommitted work that survived a backend crash; the distinctive
    /// author makes the commit easy to find in <c>git log</c> later (ADR-0020).
    /// Returns a clean <c>"Nothing to commit"</c> result when the tree is empty;
    /// callers treat that as success-with-info.
    /// </summary>
    public GitCommitResult CrashRecoveryCommit(string projectName, string repoRoot, string message)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return new GitCommitResult(false, null, $"Repo root missing: {repoRoot}");

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

    public GitCommitResult Commit(string jobId, string? watchPath, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new GitCommitResult(false, null, "Commit message is required.");

        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null) return new GitCommitResult(false, null, "Could not resolve repo root.");
        var root = ResolveGitToplevel(configured);
        if (root == null) return new GitCommitResult(false, null, $"Not a git repository: {configured}");

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
        string jobId, string? watchPath, CancellationToken ct = default)
    {
        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null) return new GenerateMessageResult(null, "Could not resolve repo root.");
        var root = ResolveGitToplevel(configured);
        if (root == null) return new GenerateMessageResult(null, $"Not a git repository: {configured}");

        var (diff, _, code) = RunGit(root, "diff HEAD");
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
        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null || string.IsNullOrWhiteSpace(sha)) return [];
        var root = ResolveGitToplevel(configured);
        if (root == null) return [];

        var (statusOut, _, statusCode) = RunGit(root, $"show --name-status --pretty=format: {sha}");
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
        var (numOut, _, numCode) = RunGit(root, $"show --numstat --pretty=format: {sha}");
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
        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null || string.IsNullOrWhiteSpace(sha)) return "";
        var root = ResolveGitToplevel(configured);
        if (root == null) return "";
        var args = string.IsNullOrWhiteSpace(path)
            ? $"show --pretty=format: {sha}"
            : $"show --pretty=format: {sha} -- \"{path.Replace("\"", "\\\"")}\"";
        var (output, err, code) = RunGit(root, args);
        return code == 0 ? output : err;
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
        var parts = new StringBuilder();
        foreach (var sha in NormalizeCommitShas(shas))
        {
            var diff = GetCommitDiff(jobId, watchPath, sha, path);
            if (string.IsNullOrWhiteSpace(diff)) continue;
            if (parts.Length > 0) parts.AppendLine();
            parts.Append(diff.TrimEnd());
            parts.AppendLine();
        }
        return parts.ToString();
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
        string jobId, string? watchPath, CancellationToken ct = default)
    {
        var statusBefore = GetStatus(jobId, watchPath);
        if (!statusBefore.IsRepo)
            return (new GitCommitResult(false, null, statusBefore.Error ?? "Not a git repo"), "");
        if (statusBefore.FilesChanged == 0)
            return (new GitCommitResult(false, null, "Nothing to commit. Working tree is clean."), "");

        var msg = await GenerateCommitMessageAsync(jobId, watchPath, ct);
        var message = msg.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            // Fall back to a deterministic message so an LLM hiccup does not block the auto-commit.
            message = $"chore: snapshot for review ({statusBefore.FilesChanged} file{(statusBefore.FilesChanged == 1 ? "" : "s")} changed)";
        }
        var result = Commit(jobId, watchPath, message);
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
    /// replaying the task commits on top of the latest integration tip. On
    /// conflict the rebase is aborted so the worktree is left clean and the
    /// caller can fall back to the merge-queue / pull-request path.
    /// </summary>
    public GitWorktreeResult RebaseOnto(string worktreePath, string ontoRef)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
            return new GitWorktreeResult(false, null, "Worktree path does not exist.");
        if (!IsLikelyBranchName(ontoRef))
            return new GitWorktreeResult(false, null, $"Invalid onto ref '{ontoRef}'.");

        var (_, err, code) = RunGitArgs(worktreePath, "rebase", ontoRef);
        if (code != 0)
        {
            var conflictedFiles = ListUnmergedFiles(worktreePath);
            RunGitArgs(worktreePath, "rebase", "--abort");
            _logger.LogWarning("Rebase onto {OntoRef} failed at {Path}, aborted: {Error}", ontoRef, worktreePath, err.Trim());
            return new GitWorktreeResult(false, worktreePath, err.Trim(), conflictedFiles);
        }
        _logger.LogInformation("Rebased worktree {Path} onto {OntoRef}", worktreePath, ontoRef);
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
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        return RunGitProcess(psi, null);
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
