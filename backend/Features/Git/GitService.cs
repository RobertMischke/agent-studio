using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Git;

public record GitFileChange(string Status, string Path, int Added, int Removed);

public record GitStatusResult(
    bool IsRepo,
    string? Branch,
    int FilesChanged,
    int TotalAdded,
    int TotalRemoved,
    List<GitFileChange> Files,
    string? Error,
    bool IsWorktree = false);

public record GitCommitResult(bool Success, string? Sha, string? Error, CommitGateResult? Gate = null);
public record GitPushResult(bool Success, string Sha, string Status, string? Error);
public record GitDiffLookupResult(bool Success, string Diff, string? Error);
public record GitWorkerCommitCleanupResult(bool Success, string Status, string? Error);
public sealed record ReachableCommitLookupResult(
    bool Success,
    TaskCommitInfo? Commit,
    string? ErrorCode,
    string? Error);

/// <summary>
/// Result of a single-file content lookup that backs the git-pane's
/// rendered md/html preview (AGT-2008). <paramref name="Content"/> is the
/// UTF-8 text of the file at the requested ref (working tree or commit);
/// <paramref name="IsBinary"/> flags a NUL-containing blob so the UI can
/// decline to render it as text instead of splattering control bytes.
/// </summary>
public record GitFileContentResult(bool Success, string Content, bool IsBinary, string? Error);

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
/// Outcome of the deferred, operator-triggered merge of a task branch into the
/// integration branch (<see cref="GitService.MergeBranchIntoIntegration"/>),
/// behind the "Merge into Develop" post-step. Distinguishes the cases the
/// runner has to surface honestly in the pipeline view.
/// </summary>
public enum MergeIntoIntegrationOutcome
{
    /// <summary>A real merge commit was just created on the integration branch.</summary>
    Merged,
    /// <summary>The task branch was already contained in the integration branch; no-op.</summary>
    AlreadyMerged,
    /// <summary>No <c>task/&lt;id&gt;</c> branch exists (e.g. a sequential run); nothing to merge.</summary>
    NoTaskBranch,
    /// <summary>The configured pull-request strategy deliberately left the delivery ref for external review.</summary>
    PushedForReview,
    /// <summary>The merge hit a conflict. It was aborted (tree left clean) and the conflicted files are reported, not swallowed.</summary>
    Conflict,
    /// <summary>A precondition failed (dirty tree, missing branch, checkout failure) or git errored.</summary>
    Error,
}

/// <summary>
/// Result of <see cref="GitService.MergeBranchIntoIntegration"/>. On
/// <see cref="MergeIntoIntegrationOutcome.Conflict"/> the working tree is left
/// clean (the merge is aborted) and <see cref="ConflictedFiles"/> names the
/// files so the conflict is visible rather than silent.
/// </summary>
public record MergeIntoIntegrationResult(
    MergeIntoIntegrationOutcome Outcome,
    string? MergedSha,
    string? Error,
    IReadOnlyList<string> ConflictedFiles)
{
    public static MergeIntoIntegrationResult Of(MergeIntoIntegrationOutcome outcome, string? mergedSha = null, string? error = null)
        => new(outcome, mergedSha, error, Array.Empty<string>());

    public static MergeIntoIntegrationResult Conflicted(IReadOnlyList<string> conflictedFiles, string? error)
        => new(MergeIntoIntegrationOutcome.Conflict, null, error, conflictedFiles);
}

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

/// <summary>
/// A curated <c>merge(KEY)</c> / <c>merge-recut(KEY)</c> integration commit.
/// The committer timestamp lets evidence consumers reject a merge that predates
/// the card's current attributed commit.
/// </summary>
public record GitIntegrationMerge(string Sha, DateTime CommittedAtUtc);

public record GenerateMessageResult(string? Message, string? Error, string? SuspiciousReason = null);

/// <summary>
/// The most-recent commit that touched a single file under a directory walk.
/// Backs the wiki dashboard's "recent edits" list (page / author / when),
/// derived from git history rather than any app-internal edit log so the
/// author + timestamp are ground truth. Returned by
/// <see cref="GitService.GetRecentEditsUnderPath"/>, newest first, one entry
/// per distinct path.
/// </summary>
public record GitRecentFileEdit(
    string RepoRelPath,
    string Sha,
    string ShortSha,
    DateTime AuthorDateUtc,
    string Author,
    string Subject);

/// <summary>
/// The per-doc git provenance the wiki history panel renders, folded into one
/// record so a single HEAD-keyed cache entry covers both reads (see
/// <see cref="GitService.GetWikiDocGitInfoCached"/>): the file's commit history
/// (newest first) and the model name that last touched it (from the latest
/// commit's <c>Co-authored-by</c> trailer, or null for a hand-authored commit).
/// </summary>
public record WikiDocGitInfo(List<GitCommitInfo> Commits, string? Model);

/// <summary>A cached, read-only materialization of docs/ at one git ref.</summary>
public record WikiBranchSnapshot(string Ref, string Sha, string ShortSha, string RootPath, string? Error)
{
    public bool Success => Error == null;
}

/// <summary>
/// Per-commit enrichment for the deterministic commit-attribution step: the
/// full commit body (<c>%B</c>, scanned for <c>Co-Authored-By:</c> trailers
/// so an agent co-author is detected even when the operator is the author),
/// whether the commit is a merge (&gt;1 parent), and its author timestamp.
/// </summary>
public record CommitMeta(string Body, bool IsMerge, DateTime AuthorDateUtc);

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
/// One checkout of the project repository as reported by
/// <c>git worktree list --porcelain</c>. The primary checkout
/// (<see cref="IsPrimary"/>) is the repository root itself; additional
/// entries are the ADR-0052 per-task worktrees living on disk. Backs the
/// Project Hub Git View "where each checkout lives" surface, so the
/// concrete on-disk <see cref="Path"/> is always included.
/// </summary>
public record GitWorktreeEntry(
    string Path,
    string? Branch,
    string? HeadSha,
    string? HeadShortSha,
    bool IsPrimary,
    bool IsDetached,
    bool IsBare);

/// <summary>
/// One local branch in the Project Hub Git View inventory. <see cref="Category"/>
/// is a coarse classification (<c>main</c> / <c>develop</c> / <c>feature</c> /
/// <c>task</c> / <c>other</c>) the frontend groups the branch tree by, so the
/// operator can see at a glance what is integration, what is a feature branch,
/// and what is an open task branch. <see cref="WorktreePath"/> is non-null when
/// the branch is currently checked out in one of the <see cref="GitWorktreeEntry"/>
/// folders.
/// </summary>
public record GitBranchEntry(
    string Name,
    string Category,
    string? TipSha,
    string? TipShortSha,
    bool IsCurrent,
    string? Upstream,
    int Ahead,
    int Behind,
    string? LastCommitSubject,
    DateTime? LastCommitAtUtc,
    string? WorktreePath);

/// <summary>
/// One ref as emitted by <c>git for-each-ref</c>, used by the Git-Management
/// cleanup analysis (<see cref="GitCleanupService"/>) to enumerate local
/// <c>task/*</c> heads, <c>origin/task/*</c> remote-tracking refs, and the
/// operational <c>refs/backups/*</c> safety net. <see cref="FullName"/> is the
/// fully-qualified ref (e.g. <c>refs/backups/2026-07-09</c>) used for deletion
/// via <c>update-ref -d</c>; <see cref="ShortName"/> is git's abbreviated form
/// (e.g. <c>task/42</c>, <c>origin/task/42</c>) used for merge-base checks.
/// </summary>
public record GitRefLine(string FullName, string ShortName, string Sha, string ShortSha);

/// <summary>
/// A curated publisher commit found on the integration line. The task key is
/// parsed from the canonical <c>merge(KEY): ...</c> or
/// <c>merge-recut(KEY): ...</c> subject.
/// </summary>
public record GitIntegrationMergeCommit(
    string TaskKey,
    string Sha,
    string ShortSha,
    DateTime CommittedAtUtc,
    string Publisher,
    string Subject);

/// <summary>
/// Read-only branch + worktree + recent-history inventory for a single
/// project, returned by <see cref="GitService.GetProjectInventory"/> and
/// surfaced on the Project Hub Git View. Deliberately project-scoped: it
/// answers "what branches and checkouts does THIS project's repository have,
/// and what changed recently", never a global git-client view. Carries an
/// <see cref="Error"/> (with <see cref="IsRepo"/> false) when the project has
/// no configured repository or the folder is not a git working tree, so the
/// frontend can render a clean empty/error state.
/// </summary>
public record GitProjectInventory(
    string ProjectName,
    string? RepositoryPath,
    bool IsRepo,
    string? CurrentBranch,
    List<GitWorktreeEntry> Worktrees,
    List<GitBranchEntry> Branches,
    List<GitCommitInfo> RecentCommits,
    string? Error);

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
    private readonly ProjectRegistry _registry;
    private readonly CommitCandidateGate _commitGate;

    public GitService(
        ILogger<GitService> logger,
        TaskScannerService scanner,
        IConfiguration config,
        RuntimePromptService? prompts = null,
        AdHocUsageRecorder? usage = null,
        ProjectRegistry? registry = null,
        IEnumerable<ICommitCandidateScanner>? commitCandidateScanners = null)
    {
        _logger = logger;
        _scanner = scanner;
        _config = config;
        _prompts = prompts ?? new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        _usage = usage;
        _registry = registry ?? new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        _commitGate = new CommitCandidateGate(logger, commitCandidateScanners);
        // Give the ambient git-spawn telemetry a logger for out-of-scope
        // slow-spawn warnings (the per-request rollup uses its own logger).
        GitProcessTelemetry.Logger ??= logger;
    }

    // The git toplevel of a directory is immutable for the lifetime of the
    // process - a checkout's work-tree root does not move - so it is safe to
    // memoize unconditionally. ResolveGitToplevel sits on the hot path of
    // essentially every git-info endpoint (status, diff, hygiene, provenance,
    // inventory, commits...); caching it removes one ~70ms `rev-parse
    // --show-toplevel` spawn (Windows) from every one of those requests once
    // warm. Only successful resolutions are cached, so a path that is not yet a
    // repository keeps being probed until it becomes one.
    private static readonly ConcurrentDictionary<string, string> _toplevelCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Drops the memoized <c>rev-parse --show-toplevel</c> results. Tests that
    /// recreate a repository at a path they previously probed call this so a
    /// stale toplevel can't leak across fixtures; production never needs it.
    /// </summary>
    internal static void InvalidateToplevelCache() => _toplevelCache.Clear();

    // Task-detail status is polled and is commonly requested again while the
    // user switches panes. A one-second cache removes every git spawn from that
    // burst. Unlike commit history, working-tree status cannot be keyed only by
    // HEAD because unstaged files do not move HEAD, so expiry is deliberately
    // short and is the correctness boundary for external filesystem changes.
    private readonly object _statusCacheLock = new();
    private readonly Dictionary<string, (DateTime At, GitStatusResult Value)> _statusCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan StatusTtl = TimeSpan.FromSeconds(1);

    internal void InvalidateStatusCache()
    {
        lock (_statusCacheLock) _statusCache.Clear();
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

    // ---- HEAD-keyed git memoization (AGT-2013, shared primitive with AGT-2007) ----
    //
    // Git history under a branch is immutable while HEAD does not move: the
    // recent-edits directory walk and the per-file history for a given path
    // return the identical answer until a new commit lands. So those answers are
    // memoized keyed by a logical key and validated by the repo's HEAD sha - HEAD
    // unchanged => cache hit, no `git log` spawn - paying only a single cheap
    // `rev-parse HEAD` (itself briefly TTL-cached below) to decide hit vs miss.
    // This is the CACHE half of AGT-2013's wiki-history work and is deliberately
    // a general primitive so the task-detail git-info surface (AGT-2007) can key
    // its own history reads off HEAD the same way rather than duplicating a cache.

    private readonly ConcurrentDictionary<string, (DateTime At, string? Sha)> _headShaCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime At, string? Sha)> _refShaCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan HeadShaTtl = TimeSpan.FromSeconds(2);

    private readonly object _wikiSnapshotLock = new();
    private readonly Dictionary<string, WikiBranchSnapshot> _wikiSnapshots = new(StringComparer.Ordinal);

    private const int HeadKeyedCacheLimit = 256;
    private readonly object _headKeyedLock = new();
    private readonly LinkedList<string> _headKeyedOrder = new();
    private readonly Dictionary<string, (LinkedListNode<string> Node, string Head, object Value)> _headKeyedCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// HEAD sha at <paramref name="root"/>, cached for ~2s so a burst of wiki
    /// requests (tree + recent + history when a page is opened) shares one
    /// <c>rev-parse HEAD</c> spawn instead of one per call. Returns null when the
    /// path is not a git repository. The spawn, when it does happen, still records
    /// into the ambient <see cref="GitProcessTelemetry"/> scope.
    /// </summary>
    public string? GetHeadShaCached(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        if (_headShaCache.TryGetValue(root, out var cached) && DateTime.UtcNow - cached.At < HeadShaTtl)
            return cached.Sha;
        var sha = ReadHeadShaAt(root);
        _headShaCache[root] = (DateTime.UtcNow, sha);
        return sha;
    }

    /// <summary>Resolves a branch or remote-tracking ref with the shared short TTL.</summary>
    public string? GetRefShaCached(string root, string gitRef)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(gitRef)
            || !IsLikelyBranchName(gitRef)) return null;
        var key = root + CacheKeySep + gitRef;
        if (_refShaCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < HeadShaTtl)
            return cached.Sha;
        var (output, _, code) = RunGitArgs(root, "rev-parse", "--verify", "--quiet", gitRef + "^{commit}");
        var sha = code == 0 ? output.Trim() : null;
        if (string.IsNullOrWhiteSpace(sha)) sha = null;
        _refShaCache[key] = (DateTime.UtcNow, sha);
        return sha;
    }

    /// <summary>
    /// Materializes docs/ from a configured ref without changing any checkout.
    /// The immutable SHA is the cache key, so a warm wiki request performs no
    /// archive work and a moved branch creates exactly one new snapshot.
    /// </summary>
    public WikiBranchSnapshot GetWikiBranchSnapshotCached(string repoRoot, string gitRef)
    {
        var root = ResolveGitToplevel(repoRoot) ?? repoRoot;
        if (!Directory.Exists(root))
            return new(gitRef, "", "", "", "Repository not found.");
        var sha = GetRefShaCached(root, gitRef);
        if (sha == null)
            return new(gitRef, "", "", "", $"Git ref '{gitRef}' was not found. Fetch it or choose another wiki source.");

        var key = root + CacheKeySep + sha;
        lock (_wikiSnapshotLock)
        {
            if (_wikiSnapshots.TryGetValue(key, out var hit) && Directory.Exists(hit.RootPath))
                return hit;

            var rootHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..12];
            var snapshotRoot = Path.Combine(Path.GetTempPath(), "agent-studio", "wiki-snapshots", rootHash, sha);
            var docsDir = Path.Combine(snapshotRoot, "docs");
            if (!Directory.Exists(docsDir))
            {
                Directory.CreateDirectory(snapshotRoot);
                var archive = Path.Combine(snapshotRoot, "docs.tar");
                var (_, error, code) = RunGitArgs(root, "archive", "--format=tar", $"--output={archive}", sha, "--", "docs");
                if (code != 0)
                    return new(gitRef, sha, sha[..Math.Min(8, sha.Length)], snapshotRoot,
                        string.IsNullOrWhiteSpace(error) ? "The selected ref has no readable docs/ tree." : error.Trim());
                try
                {
                    TarFile.ExtractToDirectory(archive, snapshotRoot, overwriteFiles: true);
                    File.Delete(archive);
                }
                catch (Exception ex)
                {
                    return new(gitRef, sha, sha[..Math.Min(8, sha.Length)], snapshotRoot,
                        $"Could not materialize the wiki source: {ex.Message}");
                }
            }

            var snapshot = new WikiBranchSnapshot(gitRef, sha, sha[..Math.Min(8, sha.Length)], snapshotRoot, null);
            _wikiSnapshots[key] = snapshot;
            return snapshot;
        }
    }

    /// <summary>
    /// Serves <paramref name="compute"/>'s result from cache when the repo HEAD at
    /// <paramref name="root"/> is unchanged since it was last computed for
    /// <paramref name="logicalKey"/>, else recomputes and stores it. A new commit
    /// moves HEAD and transparently invalidates every dependent entry. Bounded LRU.
    /// When HEAD cannot be read (the path is not a repo) it falls back to a live
    /// compute with no caching, so behaviour degrades to "always fresh", never to
    /// a wrong cached hit. <paramref name="compute"/> must return a non-null
    /// reference (a null result is not cached and is recomputed each call). The
    /// compute runs outside the lock; a rare concurrent double-miss just computes
    /// twice, which is harmless for these idempotent read-only lookups.
    /// </summary>
    public T MemoizeByHead<T>(string root, string logicalKey, Func<T> compute) where T : class
    {
        var head = GetHeadShaCached(root);
        if (head == null) return compute();

        lock (_headKeyedLock)
        {
            if (_headKeyedCache.TryGetValue(logicalKey, out var e) && e.Head == head && e.Value is T hit)
            {
                _headKeyedOrder.Remove(e.Node);
                _headKeyedOrder.AddLast(e.Node);
                return hit;
            }
        }

        var value = compute();
        if (value == null) return value;

        lock (_headKeyedLock)
        {
            if (_headKeyedCache.TryGetValue(logicalKey, out var existing))
            {
                _headKeyedOrder.Remove(existing.Node);
                _headKeyedCache.Remove(logicalKey);
            }
            var node = _headKeyedOrder.AddLast(logicalKey);
            _headKeyedCache[logicalKey] = (node, head, value);
            while (_headKeyedCache.Count > HeadKeyedCacheLimit && _headKeyedOrder.First is { } first)
            {
                _headKeyedOrder.RemoveFirst();
                _headKeyedCache.Remove(first.Value);
            }
        }
        return value;
    }

    /// <summary>
    /// Drops the HEAD-sha and HEAD-keyed memo caches. Tests that commit into a
    /// fixture repo and then re-query within the 2s HEAD TTL call this so the new
    /// commit is observed immediately; production just lets HEAD roll over.
    /// </summary>
    internal void InvalidateHeadKeyedCaches()
    {
        _headShaCache.Clear();
        lock (_headKeyedLock)
        {
            _headKeyedCache.Clear();
            _headKeyedOrder.Clear();
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
        using var _t = GitProcessTelemetry.BeginRequest("tasks/git/hygiene", _logger);
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

    // ----- Project Hub Git View: branch + worktree + history inventory -----

    private readonly object _inventoryLock = new();
    private readonly Dictionary<string, (DateTime At, GitProjectInventory Value)> _inventoryCache = new();
    private static readonly TimeSpan InventoryTtl = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Branch / worktree / recent-history inventory for one project, backing the
    /// Project Hub Git View. Read-only: it forks a handful of cheap plumbing
    /// commands (<c>worktree list</c>, <c>for-each-ref</c>, <c>log</c>) and never
    /// mutates the repository. Cached per project for ~3 s so a polling UI can
    /// call freely without forking N git processes per render. Returns a shape
    /// with <see cref="GitProjectInventory.IsRepo"/> false and a populated
    /// <see cref="GitProjectInventory.Error"/> when the project is unknown, has
    /// no configured repository, or the folder is not a git working tree - the
    /// frontend branches on that for its empty/error state.
    /// </summary>
    public GitProjectInventory GetProjectInventory(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            return EmptyInventory("", null, "projectName is required");

        lock (_inventoryLock)
        {
            if (_inventoryCache.TryGetValue(projectName, out var cached) &&
                DateTime.UtcNow - cached.At < InventoryTtl)
            {
                return cached.Value;
            }
        }

        using var _t = GitProcessTelemetry.BeginRequest("git/inventory", _logger);
        var fresh = ComputeProjectInventory(projectName);
        lock (_inventoryLock)
        {
            _inventoryCache[projectName] = (DateTime.UtcNow, fresh);
        }
        return fresh;
    }

    private static GitProjectInventory EmptyInventory(string projectName, string? repoPath, string? error)
        => new(projectName, repoPath, false, null, [], [], [], error);

    private GitProjectInventory ComputeProjectInventory(string projectName)
    {
        var entry = _scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
        if (entry == null)
            return EmptyInventory(projectName, null, "Unknown project");

        var configured = ResolveConfiguredRepositoryPath(entry);
        if (string.IsNullOrWhiteSpace(configured))
            return EmptyInventory(projectName, null, "Project has no configured repository path.");

        var root = ResolveGitToplevel(configured);
        if (root == null)
            return EmptyInventory(projectName, configured, $"Not a git repository: {configured}");

        var (branchOut, _, _) = RunGit(root, "rev-parse --abbrev-ref HEAD");
        var currentBranch = string.IsNullOrWhiteSpace(branchOut) ? null : branchOut.Trim();

        var (wtOut, _, wtCode) = RunGitArgs(root, "worktree", "list", "--porcelain");
        var worktrees = wtCode == 0 ? ParseWorktreePorcelain(wtOut) : [];
        var worktreeByBranch = worktrees
            .Where(w => !string.IsNullOrEmpty(w.Branch))
            .GroupBy(w => w.Branch!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Path, StringComparer.Ordinal);

        var branches = ReadBranchInventory(root, currentBranch, worktreeByBranch);

        // Recent history on the current HEAD; the tree browser reads this as the
        // "browse recent commits" list and hands a selected SHA to the reused
        // diff renderer. Bounded so a long-lived repo can't stall the render.
        var (logOut, _, logCode) = RunGitArgs(root,
            "log", "--no-merges", "--max-count=40", "--shortstat",
            "--pretty=format:%H%x1f%h%x1f%aI%x1f%aN%x1f%s");
        var recent = logCode == 0 ? ParseLogBlocks(logOut) : [];

        return new GitProjectInventory(
            projectName, root, true, currentBranch, worktrees, branches, recent, null);
    }

    /// <summary>
    /// Parses <c>git worktree list --porcelain</c> into typed entries. The first
    /// block git emits is always the primary working tree (the repo root); it is
    /// flagged <see cref="GitWorktreeEntry.IsPrimary"/>. Split out for unit
    /// testing of the parse without a live repo.
    /// </summary>
    internal static List<GitWorktreeEntry> ParseWorktreePorcelain(string output)
    {
        var list = new List<GitWorktreeEntry>();
        if (string.IsNullOrWhiteSpace(output)) return list;

        string? path = null, head = null, branch = null;
        var detached = false; var bare = false;
        var first = true;

        void Flush()
        {
            if (path == null) return;
            string? normalized;
            try { normalized = Path.GetFullPath(path); } catch { normalized = path; }
            list.Add(new GitWorktreeEntry(
                normalized,
                branch,
                head,
                head is { Length: > 7 } ? head[..7] : head,
                first,
                detached,
                bare));
            first = false;
            path = null; head = null; branch = null; detached = false; bare = false;
        }

        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Flush();
                path = line.Substring("worktree ".Length).Trim();
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
                head = line.Substring("HEAD ".Length).Trim();
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var refName = line.Substring("branch ".Length).Trim();
                branch = refName.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? refName.Substring("refs/heads/".Length)
                    : refName;
            }
            else if (line == "detached") detached = true;
            else if (line == "bare") bare = true;
        }
        Flush();
        return list;
    }

    private List<GitBranchEntry> ReadBranchInventory(
        string root, string? currentBranch, IReadOnlyDictionary<string, string> worktreeByBranch)
    {
        // One for-each-ref pass gives tip SHA, upstream, ahead/behind track, the
        // last-commit date and subject per branch. The explicit refs/heads
        // pattern is intentional: refs/backups/* is an operational safety net,
        // not user-visible branch inventory, and large backup namespaces must
        // not add scan or response cost. Fields are separated by the Unit
        // Separator (0x1f) so a commit subject with spaces round-trips.
        const char US = '';
        var fmt = string.Join(US.ToString(), new[]
        {
            "%(refname:short)", "%(objectname)", "%(objectname:short)",
            "%(upstream:short)", "%(upstream:track)",
            "%(committerdate:iso-strict)", "%(contents:subject)"
        });
        var (output, _, code) = RunGitArgs(root,
            "for-each-ref", "--sort=-committerdate", "--count=200",
            $"--format={fmt}", "refs/heads");
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return [];

        var list = new List<GitBranchEntry>();
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var parts = raw.Split(US);
            if (parts.Length < 7) continue;
            var name = parts[0].Trim();
            if (name.Length == 0) continue;

            var (ahead, behind) = ParseAheadBehind(parts[4]);
            DateTime? lastAt = null;
            if (DateTime.TryParse(parts[5], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var ts))
                lastAt = DateTime.SpecifyKind(ts, DateTimeKind.Utc);

            list.Add(new GitBranchEntry(
                Name: name,
                Category: CategorizeBranch(name),
                TipSha: EmptyToNull(parts[1]),
                TipShortSha: EmptyToNull(parts[2]),
                IsCurrent: string.Equals(name, currentBranch, StringComparison.Ordinal),
                Upstream: EmptyToNull(parts[3]),
                Ahead: ahead,
                Behind: behind,
                LastCommitSubject: EmptyToNull(parts[6]),
                LastCommitAtUtc: lastAt,
                WorktreePath: worktreeByBranch.TryGetValue(name, out var wt) ? wt : null));
        }
        return list;
    }

    private static string? EmptyToNull(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// Coarse branch classification for the Git View tree grouping. Mirrors the
    /// project's branch conventions: <c>main</c>/<c>master</c> and
    /// <c>develop</c>/<c>dev</c> are integration lines, <c>task/*</c> are ADR-0052
    /// task branches, <c>feature/*</c> (and <c>feat/*</c>) are feature branches,
    /// everything else is <c>other</c>.
    /// </summary>
    internal static string CategorizeBranch(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "other";
        if (name is "main" or "master") return "main";
        if (name is "develop" or "dev") return "develop";
        if (name.StartsWith("task/", StringComparison.Ordinal)) return "task";
        if (name.StartsWith("feature/", StringComparison.Ordinal) ||
            name.StartsWith("feat/", StringComparison.Ordinal)) return "feature";
        return "other";
    }

    /// <summary>
    /// Parses git's <c>%(upstream:track)</c> string (e.g. <c>[ahead 2, behind 1]</c>,
    /// <c>[ahead 3]</c>, <c>[behind 4]</c>, <c>[gone]</c>, or empty) into an
    /// ahead/behind pair. Returns (0,0) when there is no upstream or nothing to
    /// report. Internal for unit testing.
    /// </summary>
    internal static (int Ahead, int Behind) ParseAheadBehind(string? track)
    {
        if (string.IsNullOrWhiteSpace(track)) return (0, 0);
        var ahead = 0; var behind = 0;
        foreach (Match m in Regex.Matches(track, @"(ahead|behind)\s+(\d+)"))
        {
            if (!int.TryParse(m.Groups[2].Value, out var n)) continue;
            if (m.Groups[1].Value == "ahead") ahead = n; else behind = n;
        }
        return (ahead, behind);
    }

    /// <summary>
    /// Project-scoped file list for an already-recorded commit, resolved through
    /// the project's configured repository (no job context). Backs the Project
    /// Hub Git View so a selected history commit's changed files can be shown and
    /// handed to the reused diff renderer. The SHA is validated through
    /// <see cref="IsLikelyShaOrRef"/> first so a crafted argument cannot smuggle a
    /// flag into the git invocation. Returns an empty list when the project or
    /// SHA can't be resolved - never throws.
    /// </summary>
    public List<GitFileChange> GetProjectCommitFiles(string projectName, string sha)
    {
        if (string.IsNullOrWhiteSpace(sha) || !IsLikelyShaOrRef(sha)) return [];
        var root = ResolveProjectRoot(projectName);
        if (root == null) return [];
        return GetCommitFilesAtRoot(root, sha);
    }

    /// <summary>
    /// Project-scoped unified diff for an already-recorded commit, optionally
    /// scoped to one path. Mirrors <see cref="GetCommitDiffResult"/> but resolves
    /// the repository from the project name instead of a job, so the Project Hub
    /// Git View can drive the shared diff renderer with a browsed commit.
    /// </summary>
    public GitDiffLookupResult GetProjectCommitDiffResult(string projectName, string sha, string? path)
    {
        if (string.IsNullOrWhiteSpace(sha) || !IsLikelyShaOrRef(sha))
            return new GitDiffLookupResult(false, "", "Invalid commit SHA.");
        var root = ResolveProjectRoot(projectName);
        if (root == null)
            return new GitDiffLookupResult(false, "", "Could not resolve repo root for project.");
        return GetCommitDiffResultAtRoot(root, sha, path);
    }

    /// <summary>
    /// Resolves a project's git toplevel the same way <see cref="GetProjectInventory"/>
    /// does (scanner entry -> configured repository path -> <c>rev-parse --show-toplevel</c>),
    /// so an inventory SHA and its diff are always read from the same checkout.
    /// </summary>
    private string? ResolveProjectRoot(string projectName)
    {
        var entry = _scanner.GetWatchPaths().FirstOrDefault(e => e.Name == projectName);
        if (entry == null) return null;
        var configured = ResolveConfiguredRepositoryPath(entry);
        if (string.IsNullOrWhiteSpace(configured)) return null;
        return ResolveGitToplevel(configured);
    }

    /// <summary>
    /// Public counterpart to <see cref="ResolveProjectRoot"/>: the git toplevel a
    /// project's Git-Management surfaces read, resolved the same way
    /// <see cref="GetProjectInventory"/> does (scanner entry -> configured
    /// repository path -> <c>rev-parse --show-toplevel</c>). The cleanup service
    /// (<see cref="GitCleanupService"/>) uses this so the branches it analyses and
    /// prunes always come from the same checkout the Git View shows. Returns null
    /// when the project is unknown, has no configured repository, or the folder is
    /// not a git working tree.
    /// </summary>
    public string? ResolveProjectRepoRoot(string projectName) => ResolveProjectRoot(projectName);

    /// <summary>
    /// Resolves one runner entry to its authoritative Git checkout root.
    /// <c>RepositoryPath</c> wins over <c>RootPath</c>; the latter may be a
    /// monorepo subfolder, while task storage in <c>Path</c> is never treated as
    /// source. Returns null instead of handing a non-Git local folder to a
    /// mutating run.
    /// </summary>
    public string? ResolveRepositoryRoot(WatchPathEntry entry)
    {
        var configured = ResolveConfiguredRepositoryPath(entry);
        return string.IsNullOrWhiteSpace(configured) ? null : ResolveGitToplevel(configured);
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
    /// The resolved location a task's LIVE Git view should read: the task's own
    /// worktree when it is running in one (parallel model, ADR-0052), otherwise
    /// the project's main checkout. <see cref="Root"/> is the directory git is
    /// invoked in; <see cref="IsWorktree"/> tells the view which it is so the
    /// header can label it (<c>task/&lt;id&gt; (Worktree)</c> vs
    /// <c>develop (Haupt-Checkout)</c>).
    /// </summary>
    private sealed record RunLocation(string Configured, string? Root, string? Branch, bool IsWorktree);

    /// <summary>
    /// Resolves the run-location for a task's live status/diff view. A task that
    /// runs in its own <c>task/&lt;id&gt;</c> worktree must show THAT worktree's
    /// branch + dirty tree, never the shared main checkout's - otherwise the view
    /// cross-attributes a sibling run's uncommitted files to this task (the bug
    /// this method exists to fix). Reuses the existing
    /// <see cref="WorktreePathForBranch"/> lookup rather than re-deriving the
    /// path. Falls back to the project's main checkout (toplevel) when the task
    /// has no live worktree - a sequential run, or after the worktree was torn
    /// down (the provenance/landed-state view then owns "where the work went").
    /// Returns null only when the job or its configured repository can't be
    /// resolved at all, mirroring <see cref="ResolveRepoRoot"/>'s null contract.
    /// </summary>
    private RunLocation? ResolveRunLocation(string jobId, string? watchPath)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return null;
        var entry = _scanner.GetWatchPaths().FirstOrDefault(e => e.Name == info.ProjectName);
        var configured = entry == null ? null : ResolveConfiguredRepositoryPath(entry);
        if (string.IsNullOrWhiteSpace(configured)) return null;

        var mainRoot = ResolveGitToplevel(configured);
        if (mainRoot == null) return new RunLocation(configured, null, null, false);

        var branch = WorktreeTaskLifecycle.BranchFor(info.Id);
        var worktree = WorktreePathForBranch(mainRoot, branch);
        if (!string.IsNullOrEmpty(worktree) && Directory.Exists(worktree))
            return new RunLocation(configured, worktree, branch, true);

        return new RunLocation(configured, mainRoot, null, false);
    }

    /// <summary>
    /// Resolve the repository root for a project by name without needing a
    /// job context. Used by <see cref="AgentStudio.Runner.CrashRecoveryService"/>
    /// at boot time to inspect the working tree before any job has been
    /// loaded into the runtime.
    /// </summary>
    public string? ResolveRepoRootForProject(string projectName)
    {
        // Same resolution chain as the docs surface (ProjectDocsService), so
        // the file-write target and the git commit root can never diverge.
        var repo = ProjectRepoResolver.ResolveForProject(projectName, _scanner, _registry);
        if (string.IsNullOrWhiteSpace(repo)) return null;
        return ResolveGitToplevel(repo) ?? repo;
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
    /// True when <paramref name="path"/> lives inside a git working tree.
    /// Diagnostic helper only: coding isolation must resolve the authoritative
    /// repository through <see cref="ResolveRepositoryRoot"/> and fail closed
    /// when it is absent. Cheap: a single <c>rev-parse --show-toplevel</c>.
    /// </summary>
    public bool IsGitRepo(string? path) => ResolveGitToplevel(path ?? string.Empty) != null;

    /// <summary>
    /// True when the repo at <paramref name="repoRoot"/> has any uncommitted
    /// modifications (staged, unstaged, or untracked). Cheap-by-design helper
    /// for <see cref="AgentStudio.Runner.CrashRecoveryService"/>.
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

    /// <summary>
    /// Live working-tree status for a task. With <paramref name="preferRunLocation"/>
    /// the view reads the task's own worktree when it has one (see
    /// <see cref="ResolveRunLocation"/>) so a parallel run never shows a sibling
    /// run's dirty files; the per-task Git pane passes true. Internal callers that
    /// pair the status with the main-checkout root (auto-commit scoping,
    /// read-only containment) leave it false and keep reading the main checkout.
    /// </summary>
    public GitStatusResult GetStatus(string jobId, string? watchPath, bool preferRunLocation = false)
    {
        if (preferRunLocation)
        {
            // Measure the endpoint-driven live status path (AGT-2007). Internal
            // callers pass preferRunLocation=false and stay unmeasured to keep
            // the rollup logs scoped to user-facing git-info requests.
            using var _t = GitProcessTelemetry.BeginRequest("tasks/git/status", _logger);
            var cacheKey = $"{watchPath}\n{jobId}";
            lock (_statusCacheLock)
            {
                if (_statusCache.TryGetValue(cacheKey, out var cached) &&
                    DateTime.UtcNow - cached.At < StatusTtl)
                {
                    return cached.Value;
                }
            }
            var loc = ResolveRunLocation(jobId, watchPath);
            if (loc == null)
                return new GitStatusResult(false, null, 0, 0, 0, [], "Job not found or project has no RootPath configured.");
            if (loc.Root == null)
                return new GitStatusResult(false, null, 0, 0, 0, [], $"Not a git repository: {loc.Configured}");
            var fresh = ReadStatusAtRoot(loc.Root, loc.IsWorktree);
            lock (_statusCacheLock) _statusCache[cacheKey] = (DateTime.UtcNow, fresh);
            return fresh;
        }

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

    private GitStatusResult ReadStatusAtRoot(string root, bool isWorktree = false)
    {
        // These four reads are independent: the porcelain status, the current
        // branch, and the two numstat diffs share no state. Each is its own git
        // process (~70-160ms of Windows spawn cost), so running them
        // concurrently turns a serial ~500ms into roughly one spawn's wall-time
        // (AGT-2007). Parsing below is unchanged - only the fetch is parallel.
        var reads = RunGitParallel(
            () => RunGitReadonly(root, "status --porcelain=v1"),
            () => RunGitReadonly(root, "rev-parse --abbrev-ref HEAD"),
            () => RunGitReadonly(root, "diff --numstat HEAD"),
            () => RunGitReadonly(root, "diff --numstat"));

        var (statusOut, statusErr, statusCode) = reads[0];
        if (statusCode != 0)
            return new GitStatusResult(true, null, 0, 0, 0, [], statusErr.Trim(), isWorktree);

        var (branchOut, _, _) = reads[1];
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
        foreach (var (numOut, _, numCode) in new[] { reads[2], reads[3] })
        {
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
            null,
            isWorktree);
    }

    public string GetDiff(string jobId, string? watchPath, string? path, bool preferRunLocation = false)
    {
        var result = GetDiffResult(jobId, watchPath, path, preferRunLocation);
        return result.Success ? result.Diff : result.Error ?? "";
    }

    /// <summary>
    /// Live working-tree diff for a task. With <paramref name="preferRunLocation"/>
    /// the diff is read from the task's own worktree when it has one, matching
    /// <see cref="GetStatus"/> so the per-task Git pane's file list and diffs come
    /// from the same checkout. Defaults to the main checkout for internal callers.
    /// </summary>
    public GitDiffLookupResult GetDiffResult(string jobId, string? watchPath, string? path, bool preferRunLocation = false)
    {
        string? root;
        using var _t = preferRunLocation
            ? GitProcessTelemetry.BeginRequest("tasks/git/diff", _logger)
            : null;
        if (preferRunLocation)
        {
            var loc = ResolveRunLocation(jobId, watchPath);
            if (loc == null) return new GitDiffLookupResult(false, "", "Could not resolve repo root.");
            if (loc.Root == null) return new GitDiffLookupResult(false, "", $"Not a git repository: {loc.Configured}");
            root = loc.Root;
        }
        else
        {
            var configured = ResolveRepoRoot(jobId, watchPath);
            if (configured == null) return new GitDiffLookupResult(false, "", "Could not resolve repo root.");
            root = ResolveGitToplevel(configured);
            if (root == null) return new GitDiffLookupResult(false, "", $"Not a git repository: {configured}");
        }
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
                try { return new GitDiffLookupResult(true, File.ReadAllText(abs), null); } catch (Exception __ex) { SilentCatch.Note(__ex, "GitService: best-effort"); /* best-effort */ }
            }
        }
        return new GitDiffLookupResult(true, output, null);
    }

    /// <summary>
    /// Full text of a single tracked file, for the git-pane's rendered
    /// md/html preview (AGT-2008). With a non-empty <paramref name="sha"/> the
    /// content is read from that commit (<c>git show &lt;sha&gt;:&lt;path&gt;</c>)
    /// so a historical / commit-mode preview matches the diff; otherwise the
    /// live working-tree copy is read. <paramref name="preferRunLocation"/>
    /// mirrors <see cref="GetDiffResult"/> so a per-task worktree run previews
    /// its own file, never a sibling checkout's. A NUL-containing blob is
    /// reported as binary (Success=true, IsBinary=true) so the caller can show
    /// a "not previewable" note rather than raw bytes.
    /// </summary>
    public GitFileContentResult GetFileContentResult(
        string jobId, string? watchPath, string? path, string? sha, bool preferRunLocation = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new GitFileContentResult(false, "", false, "No file path given.");

        var normalizedPath = path.Replace('\\', '/').TrimStart('/');
        if (normalizedPath.Length == 0 || normalizedPath.Split('/').Contains(".."))
            return new GitFileContentResult(false, "", false, "Invalid file path.");

        // Historical preview: read the blob at a specific commit. Validated the
        // same way as the diff endpoints so an arbitrary ref can't be shown.
        if (!string.IsNullOrWhiteSpace(sha))
        {
            if (!IsLikelyShaOrRef(sha))
                return new GitFileContentResult(false, "", false, "Invalid commit SHA.");
            var commitRoot = ResolveRepoRoot(jobId, watchPath);
            if (commitRoot == null) return new GitFileContentResult(false, "", false, "Could not resolve repo root.");
            var (blob, blobErr, blobCode) = RunGitArgs(commitRoot, "show", $"{sha}:{normalizedPath}");
            if (blobCode != 0)
                return new GitFileContentResult(false, "", false,
                    string.IsNullOrWhiteSpace(blobErr) ? "File not found in that commit." : blobErr.Trim());
            return ClassifyContent(blob);
        }

        // Live preview: read the working-tree file at the run location.
        string? root;
        if (preferRunLocation)
        {
            var loc = ResolveRunLocation(jobId, watchPath);
            if (loc?.Root == null) return new GitFileContentResult(false, "", false, "Could not resolve repo root.");
            root = loc.Root;
        }
        else
        {
            var configured = ResolveRepoRoot(jobId, watchPath);
            root = configured == null ? null : ResolveGitToplevel(configured) ?? configured;
            if (root == null) return new GitFileContentResult(false, "", false, "Could not resolve repo root.");
        }

        var full = Path.GetFullPath(Path.Combine(root, normalizedPath));
        var rootFull = Path.GetFullPath(root);
        // Containment guard: never read outside the repo root even if the
        // normalized path somehow re-escapes (belt-and-braces with the `..`
        // reject above).
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            return new GitFileContentResult(false, "", false, "Invalid file path.");
        if (!File.Exists(full))
            return new GitFileContentResult(false, "", false, "File is not present in the working tree.");
        try
        {
            var bytes = File.ReadAllBytes(full);
            return ClassifyContent(Encoding.UTF8.GetString(bytes), bytes);
        }
        catch (Exception ex)
        {
            return new GitFileContentResult(false, "", false, ex.Message);
        }
    }

    /// <summary>
    /// Classify a fetched blob as text or binary. A NUL byte in the first
    /// slice is git's own heuristic for "binary"; we mirror it so the preview
    /// declines control-byte content instead of rendering garbage.
    /// </summary>
    private static GitFileContentResult ClassifyContent(string content, byte[]? rawBytes = null)
    {
        var probe = rawBytes is { Length: > 0 }
            ? rawBytes.AsSpan(0, Math.Min(rawBytes.Length, 8000)).IndexOf((byte)0) >= 0
            : content.AsSpan(0, Math.Min(content.Length, 8000)).IndexOf('\0') >= 0;
        return probe
            ? new GitFileContentResult(true, "", true, null)
            : new GitFileContentResult(true, content, false, null);
    }

    /// <summary>
    /// Commits the working tree with a fixed <c>crash-recovery</c> author tag.
    /// Used by <see cref="AgentStudio.Runner.CrashRecoveryService"/>
    /// to rescue uncommitted work that survived a backend crash; the distinctive
    /// author makes the commit easy to find in <c>git log</c> later (ADR-0020).
    /// Returns a clean <c>"Nothing to commit"</c> result when the tree is empty;
    /// callers treat that as success-with-info.
    /// </summary>
    public GitCommitResult CrashRecoveryCommit(
        string projectName,
        string repoRoot,
        string message,
        IReadOnlyCollection<string>? pathspecs = null,
        string? taskId = null,
        string? runnerId = null)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return new GitCommitResult(false, null, $"Repo root missing: {repoRoot}");
        var gate = InspectCommitCandidates(
            "crash-recovery", projectName, repoRoot, taskId, runnerId, pathspecs,
            requireTaskWorktree: false, expectedBranch: null, explicitlyReviewed: true,
            requireExplicitPaths: pathspecs is not { Count: > 0 });
        return CommitBoundManifest(repoRoot, message, gate,
            author: "Crash Recovery <crash-recovery@agent-taskboard>");
    }

    /// <summary>
    /// Platform-owned landing commit for a per-task worktree run. Same add-all +
    /// fixed-body mechanics as <see cref="CrashRecoveryCommit"/>, but it does NOT
    /// stamp the <c>Crash Recovery</c> author: the regular completion path must
    /// land under the configured git identity so a normal landing is
    /// distinguishable from a genuine boot-time orphan rescue.
    ///
    /// <para>
    /// Reusing <see cref="CrashRecoveryCommit"/> here was the root cause of every
    /// landing showing <c>author='Crash Recovery'</c> once Always-Worktree routed
    /// all runs through <c>ProjectRunner.IntegrateWorktreeRunAsync</c>: the
    /// recovery author is the exception net's marker and must never appear on a
    /// regular landing. The <see cref="WorktreeRunCommitTrailer"/> still lives in
    /// the body, so the per-task history reconstruction (ASS-1712) is unaffected.
    /// </para>
    /// Returns a clean <c>"Nothing to commit"</c> result when the tree is empty;
    /// callers treat that as success-with-info.
    /// </summary>
    public GitCommitResult WorktreeRunCommit(
        string projectName,
        string repoRoot,
        string message,
        string? taskId = null,
        string? runnerId = null,
        string? expectedBranch = null)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return new GitCommitResult(false, null, $"Repo root missing: {repoRoot}");
        var gate = InspectCommitCandidates(
            "worktree-run", projectName, repoRoot, taskId ?? projectName, runnerId, null,
            requireTaskWorktree: true, expectedBranch, explicitlyReviewed: false);
        return CommitBoundManifest(repoRoot, message, gate);
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

        var task = _scanner.FindJob(jobId, watchPath);
        var gate = InspectCommitCandidates(
            "manual-or-auto", task?.ProjectName ?? jobId, root, jobId,
            task?.Runner?.RunnerId, pathspecs, requireTaskWorktree: false,
            expectedBranch: null, explicitlyReviewed: false,
            requireExplicitPaths: pathspecs is not { Count: > 0 });
        return CommitBoundManifest(root, message, gate);
    }

    private CommitGateResult InspectCommitCandidates(
        string operation,
        string projectId,
        string repoRoot,
        string? taskId,
        string? runnerId,
        IReadOnlyCollection<string>? expectedPaths,
        bool requireTaskWorktree,
        string? expectedBranch,
        bool explicitlyReviewed,
        bool requireExplicitPaths = false)
    {
        string? evidenceDirectory = null;
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            try
            {
                var task = _scanner.FindJob(taskId);
                if (!string.IsNullOrWhiteSpace(task?.FolderPath))
                    evidenceDirectory = Path.Combine(task.FolderPath, "results");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Commit gate could not resolve evidence folder for {TaskId}", taskId);
            }
        }

        var gate = _commitGate.Inspect(new CommitGateRequest(
            operation, projectId, repoRoot, taskId, runnerId, expectedPaths,
            requireTaskWorktree, expectedBranch, explicitlyReviewed, evidenceDirectory,
            requireExplicitPaths));
        _logger.LogInformation(
            "Commit candidate gate {Decision} for {Operation} project={Project} task={TaskId} runner={RunnerId} candidates={Candidates} included={Included} findings={Findings} evidence={Evidence}",
            gate.Decision, operation, projectId, taskId ?? "<none>", runnerId ?? "<none>",
            gate.Candidates.Count, gate.IncludedPaths.Count, gate.Findings.Count,
            gate.EvidencePath ?? "<unavailable>");
        return gate;
    }

    private GitCommitResult CommitBoundManifest(
        string repoRoot,
        string message,
        CommitGateResult gate,
        string? author = null)
    {
        if (gate.Candidates.Count == 0)
            return new GitCommitResult(false, null, "Nothing to commit. Working tree is clean.", gate);
        if (!gate.CanCommit)
        {
            var codes = string.Join(", ", gate.Findings.Select(f => f.Code).Distinct(StringComparer.Ordinal));
            return new GitCommitResult(false, null,
                $"Commit candidate gate {gate.Decision}: {codes}.", gate);
        }
        if (!_commitGate.TryPrepareBoundIndex(gate, out var boundIndex, out var stageError)
            || boundIndex == null)
            return new GitCommitResult(false, null, stageError, gate);

        using (boundIndex)
        {
            var environment = new Dictionary<string, string?>
            {
                ["GIT_INDEX_FILE"] = boundIndex.FilePath
            };
            if (!string.IsNullOrWhiteSpace(author))
            {
                var match = Regex.Match(author, @"^(?<name>.+?)\s*<(?<email>[^<>]+)>$");
                if (!match.Success)
                    return new GitCommitResult(false, null, "Invalid platform commit author.", gate);
                environment["GIT_AUTHOR_NAME"] = match.Groups["name"].Value.Trim();
                environment["GIT_AUTHOR_EMAIL"] = match.Groups["email"].Value.Trim();
            }

            var (parent, parentErr, parentCode) = RunGitArgs(repoRoot, "rev-parse", "HEAD");
            if (parentCode != 0)
                return new GitCommitResult(false, null, parentErr.Trim(), gate);

            var (tree, treeErr, treeCode) = RunGitArgs(
                repoRoot, ["write-tree"], stdin: null, environment: environment);
            if (treeCode != 0)
                return new GitCommitResult(false, null, treeErr.Trim(), gate);

            var boundMessage = AppendCommitGateProvenance(message, gate);
            var (sha, commitErr, commitCode) = RunGitArgs(
                repoRoot, ["commit-tree", tree.Trim(), "-p", parent.Trim(), "-F", "-"],
                stdin: boundMessage, environment: environment);
            if (commitCode != 0)
                return new GitCommitResult(false, null, commitErr.Trim(), gate);

            // Compare-and-swap the branch tip so a concurrent commit cannot be
            // overwritten after candidate inspection.
            var (_, updateErr, updateCode) = RunGitArgs(
                repoRoot, "update-ref", "HEAD", sha.Trim(), parent.Trim());
            if (updateCode != 0)
                return new GitCommitResult(false, null,
                    $"Branch changed after candidate inspection: {updateErr.Trim()}", gate);

            // Keep the user's real index coherent with the new HEAD for only
            // the committed paths. Concurrent working-tree edits remain dirty,
            // and unrelated staged entries remain untouched.
            var resetArgs = new List<string> { "reset", "-q", "HEAD", "--" };
            resetArgs.AddRange(gate.IncludedPaths);
            RunGitArgs(repoRoot, resetArgs.ToArray());

            return new GitCommitResult(true, sha.Trim(), null, gate);
        }
    }

    private static string AppendCommitGateProvenance(string message, CommitGateResult gate)
    {
        var sb = new StringBuilder(message.TrimEnd());
        sb.AppendLine().AppendLine();
        if (!string.IsNullOrWhiteSpace(gate.Provenance.TaskId))
            sb.Append("Task-Id: ").AppendLine(gate.Provenance.TaskId);
        if (!string.IsNullOrWhiteSpace(gate.Provenance.RunnerId))
            sb.Append("Runner-Id: ").AppendLine(gate.Provenance.RunnerId);
        sb.Append("Commit-Gate: ").Append(gate.Decision)
            .Append(" (candidates=").Append(gate.Candidates.Count)
            .Append(", included=").Append(gate.IncludedPaths.Count).AppendLine(")");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Sends the inspected candidate manifest and working-tree diff summary to
    /// Codex gpt-5.4-mini for additive semantic review and commit-message text.
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
        IReadOnlyCollection<string>? pathspecs = null,
        CommitGateResult? gate = null)
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

        // Bound the prompt size. Codex handles plenty but huge diffs
        // just waste latency for a commit message.
        if (diff.Length > 60_000) diff = diff[..60_000] + "\n[truncated]";

        var intent = ReadTaskIntent(jobId, watchPath);
        var prompt = _prompts.Render(RuntimePromptService.CommitMessage,
            new Dictionary<string, string?>
            {
                ["diff"] = diff,
                ["diff_summary"] = BuildDiffSummary(root, pathspecs),
                ["candidate_manifest"] = gate == null
                    ? "Candidate manifest unavailable. Deterministic enforcement still runs before staging."
                    : JsonSerializer.Serialize(gate.Candidates.Select(c => new
                    {
                        c.Path, c.Status, c.Size, c.Sha256, c.Binary, c.Included, c.ExclusionReason
                    })),
                ["task_title"] = intent.Title,
                ["task_prompt_first_paragraph"] = intent.PromptFirstParagraph,
                ["last_user_continue"] = intent.LastUserContinue
            });

        var codexPath = _config["CodexCli:Path"] ?? "codex";
        var model = ModelIds.Gpt54Mini;

        var psi = new ProcessStartInfo
        {
            FileName = GenericCliExecutionService.ResolveExecutable(codexPath),
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
        foreach (var arg in new[] { "exec", "--experimental-json", "--sandbox", "read-only", "-m", model, "-" })
            psi.ArgumentList.Add(arg);

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
                return new GenerateMessageResult(null, $"codex exited {p.ExitCode}: {stderr.Trim()}");
            }

            var text = ParseCodexAgentMessage(stdout);
            AdHocClaudeInvoker.Record(_usage, AdHocUsageSources.CommitMessage, model, null, sw.ElapsedMilliseconds, ok: true, jobId: jobId);

            var (msg, suspicious) = ParseCommitReview(text);
            if (!string.IsNullOrWhiteSpace(suspicious))
                return new GenerateMessageResult(null, "Codex semantic reviewer reported a suspicious or unrelated candidate.", suspicious);
            if (string.IsNullOrWhiteSpace(msg))
                return new GenerateMessageResult(null, "codex returned an empty message or omitted the required sentinel.");
            return new GenerateMessageResult(msg, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to invoke Codex for commit message review");
            return new GenerateMessageResult(null, ex.Message);
        }
    }

    private string BuildDiffSummary(string root, IReadOnlyCollection<string>? pathspecs)
    {
        var args = new List<string> { "diff", "HEAD", "--stat", "--summary", "--" };
        if (pathspecs is { Count: > 0 }) args.AddRange(pathspecs);
        var (summary, _, code) = RunGitArgs(root, args.ToArray());
        return code == 0 ? summary.Trim() : "Diff summary unavailable.";
    }

    internal static string ParseCodexAgentMessage(string stdout)
    {
        string? finalReply = null;
        foreach (var line in stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("type", out var type) && type.GetString() == "item.completed"
                    && root.TryGetProperty("item", out var item)
                    && item.TryGetProperty("type", out var itemType) && itemType.GetString() == "agent_message"
                    && item.TryGetProperty("text", out var text) && !string.IsNullOrWhiteSpace(text.GetString()))
                    finalReply = text.GetString();
            }
            catch (JsonException ex)
            {
                // Non-JSON diagnostics are ignored; exit status remains authoritative.
                SilentCatch.Note(ex, "Commit-message Codex output contained a non-JSON diagnostic line.");
            }
        }
        // Codex may emit progress agent_message items before its final answer.
        // Only the final agent message owns the strict COMMIT_REVIEW sentinel;
        // concatenating progress text in front of it makes a valid review look
        // malformed and silently drops the additive semantic gate.
        return finalReply ?? "";
    }

    private static (string? Message, string? SuspiciousReason) ParseCommitReview(string raw)
    {
        var clean = SanitizeCommitMessage(raw);
        var lines = clean.Split('\n');
        if (lines.Length == 0) return (null, null);
        if (string.Equals(lines[0].Trim(), "COMMIT_REVIEW: ALLOW", StringComparison.Ordinal))
            return (string.Join("\n", lines.Skip(1)).Trim(), null);
        const string suspiciousPrefix = "COMMIT_REVIEW: SUSPICIOUS ";
        if (lines[0].StartsWith(suspiciousPrefix, StringComparison.Ordinal))
            return (null, lines[0][suspiciousPrefix.Length..].Trim());
        return (null, null);
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
        return GetCommitFilesAtRoot(root, sha);
    }

    /// <summary>
    /// Validates an operator-supplied full SHA against the task's canonical
    /// repository and returns durable task-commit metadata. A loose commit
    /// object is not sufficient: at least one repository ref must contain it,
    /// which prevents an unreachable/pruned branch tip from being attributed.
    /// </summary>
    public ReachableCommitLookupResult GetReachableCommit(
        string jobId,
        string? watchPath,
        string? suppliedSha)
    {
        var sha = suppliedSha?.Trim() ?? "";
        if (!Regex.IsMatch(sha, "^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant))
            return new(false, null, "invalid-sha", "Each commit must be a full 40-character hexadecimal SHA.");

        var configured = ResolveRepoRoot(jobId, watchPath);
        if (configured == null)
            return new(false, null, "repository-unavailable", "The task's repository could not be resolved.");
        var root = ResolveGitToplevel(configured);
        if (root == null)
            return new(false, null, "repository-unavailable", $"Not a git repository: {configured}");

        var (canonicalOut, _, canonicalCode) = RunGitArgs(
            root, "rev-parse", "--verify", "--quiet", sha + "^{commit}");
        var canonicalSha = canonicalOut.Trim();
        if (canonicalCode != 0
            || !Regex.IsMatch(canonicalSha, "^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant))
            return new(false, null, "unknown-sha", $"Commit '{sha}' does not exist in the task's repository.");

        var (refsOut, _, refsCode) = RunGitArgs(
            root, "for-each-ref", "--contains=" + canonicalSha, "--format=%(refname)");
        if (refsCode != 0 || string.IsNullOrWhiteSpace(refsOut))
            return new(false, null, "unreachable-sha", $"Commit '{canonicalSha}' is not reachable from any repository ref.");

        const char US = '\x1f';
        var (metaOut, _, metaCode) = RunGitArgs(
            root, "show", "-s", "--no-patch",
            "--format=%H%x1f%h%x1f%aI%x1f%s", canonicalSha);
        var parts = metaOut.TrimEnd('\r', '\n').Split(US);
        if (metaCode != 0 || parts.Length < 4
            || !DateTime.TryParse(
                parts[2],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var authoredAt))
            return new(false, null, "metadata-unavailable", $"Commit metadata could not be read for '{canonicalSha}'.");

        var files = GetCommitFilesAtRoot(root, canonicalSha);
        return new(true, new TaskCommitInfo
        {
            Sha = parts[0],
            ShortSha = parts[1],
            Message = parts[3],
            FilesChanged = files.Count,
            Files = files.Select(file => file.Path).ToList(),
            At = DateTime.SpecifyKind(authoredAt, DateTimeKind.Utc),
            Attribution = CommitAttributionKinds.Operator,
            Confidence = null,
        }, null, null);
    }

    /// <summary>
    /// Root-scoped core of <see cref="GetCommitFiles"/>: the <c>git show
    /// --name-status</c> + <c>--numstat</c> parse against an already-resolved
    /// git toplevel. Shared by the job-scoped and project-scoped
    /// (<see cref="GetProjectCommitFiles"/>) entry points so both derive an
    /// identical file list. Assumes <paramref name="sha"/> is already validated.
    /// </summary>
    private List<GitFileChange> GetCommitFilesAtRoot(string root, string sha)
    {
        if (string.IsNullOrWhiteSpace(sha) || !IsLikelyShaOrRef(sha)) return [];
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
    /// <see cref="AgentStudio.Shared.TaskCommitInfo"/> chain only caches a
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
        return GetCommitDiffResultAtRoot(root, sha, path);
    }

    /// <summary>
    /// Root-scoped core of <see cref="GetCommitDiffResult"/>: the <c>git show</c>
    /// unified-diff lookup against an already-resolved git toplevel, optionally
    /// path-scoped. Shared by the job-scoped and project-scoped
    /// (<see cref="GetProjectCommitDiffResult"/>) entry points. Assumes
    /// <paramref name="sha"/> is already validated.
    /// </summary>
    private GitDiffLookupResult GetCommitDiffResultAtRoot(string root, string sha, string? path)
    {
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

        var root = ResolveRepoRootForWatchPath(watchPath);
        if (root == null)
            return (new GitCommitResult(false, null, "Could not resolve repo root."), "");
        var task = _scanner.FindJob(jobId, watchPath);
        var gate = InspectCommitCandidates(
            "auto-commit", task?.ProjectName ?? jobId, root, jobId,
            task?.Runner?.RunnerId, pathspecs, requireTaskWorktree: false,
            expectedBranch: null, explicitlyReviewed: false,
            requireExplicitPaths: pathspecs is not { Count: > 0 });
        if (!gate.CanCommit)
            return (new GitCommitResult(false, null,
                $"Commit candidate gate {gate.Decision}: {string.Join(", ", gate.Findings.Select(f => f.Code).Distinct())}.", gate), "");

        // The deterministic fallback count reflects the manifest that can
        // actually commit, never the larger dirty tree.
        var fileCount = gate.IncludedPaths.Count;

        var msg = await GenerateCommitMessageAsync(jobId, watchPath, ct, gate.IncludedPaths, gate);
        if (!string.IsNullOrWhiteSpace(msg.SuspiciousReason))
        {
            gate = AddSemanticGateFinding(gate, msg.SuspiciousReason);
            return (new GitCommitResult(false, null,
                "Commit semantic review reported a suspicious or unrelated candidate; review the gate evidence.", gate), "");
        }
        var message = msg.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            // Fall back to a deterministic message so an LLM hiccup does not block the auto-commit.
            message = $"chore: snapshot for review ({fileCount} file{(fileCount == 1 ? "" : "s")} changed)";
        }
        var result = CommitBoundManifest(root, message, gate);
        return (result, message);
    }

    private static CommitGateResult AddSemanticGateFinding(CommitGateResult gate, string reason)
    {
        // The reason is model-authored metadata only. The prompt forbids secret
        // bodies and the deterministic gate never supplies detected values.
        var reasonBytes = Encoding.UTF8.GetBytes(reason);
        var containsSecret = new BuiltInCommitCandidateScanner()
            .Scan("", ".", reasonBytes, binary: false).Count > 0;
        var safeReason = containsSecret
            ? "Codex reported a possible secret; the model explanation was redacted."
            : reason.Length > 500 ? reason[..500] : reason;
        var findings = gate.Findings.Concat([
            new CommitGateFinding("semantic-suspicious", CommitGateSeverities.Warning, ".",
                safeReason, "codex-gpt-5.4-mini")
        ]).ToArray();
        var updated = gate with { Decision = CommitGateDecisions.Warn, CanCommit = false, Findings = findings };
        if (!string.IsNullOrWhiteSpace(updated.EvidencePath))
        {
            try
            {
                File.WriteAllText(updated.EvidencePath,
                    JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
                var evidenceDirectory = Path.GetDirectoryName(updated.EvidencePath)!;
                var operation = Regex.Replace(updated.Provenance.Operation, "[^A-Za-z0-9_.-]", "-");
                var historyPath = Path.Combine(evidenceDirectory,
                    $"commit-candidate-gate-{updated.Provenance.InspectedAtUtc:yyyyMMddTHHmmssfffZ}-{operation}.json");
                File.WriteAllText(historyPath,
                    JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                // Commit remains blocked even when best-effort evidence refresh fails.
                SilentCatch.Note(ex, "Commit semantic-review evidence refresh failed.");
            }
        }
        return updated;
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

    /// <summary>
    /// Pushes the integration branch itself (e.g. <c>develop</c>) to
    /// <c>origin</c> so an accepted merge lands on the remote, not only in the
    /// local checkout (AGT-1999). Companion to <see cref="PushShaAsync"/> - that
    /// one pushes a completed task's commit SHA to a target branch; this one
    /// pushes the local integration branch ref by name after
    /// <see cref="MergeBranchIntoIntegration"/> has folded the task branch into
    /// it. Never force-pushes: a diverged remote is reported
    /// <c>remote-rejected</c> (the caller surfaces it, git leaves the remote
    /// untouched). Status values mirror <see cref="PushShaAsync"/>:
    /// <c>pushed</c> / <c>already-remote</c> / <c>remote-rejected</c> /
    /// <c>failed</c>, plus <c>no-remote</c> (no <c>origin</c> configured -
    /// a local-only project, treated as a benign skip, not a failure) and
    /// <c>missing-branch</c> (the integration branch does not exist locally).
    /// </summary>
    public Task<GitPushResult> PushIntegrationBranchAsync(string repoRoot, string branch, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return Task.FromResult(new GitPushResult(false, string.Empty, "cancelled", "Push cancelled."));

        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return Task.FromResult(new GitPushResult(false, string.Empty, "repo-missing", "Could not resolve repo root."));

        if (!IsLikelyBranchName(branch))
            return Task.FromResult(new GitPushResult(false, string.Empty, "invalid-branch", $"Invalid integration branch '{branch}'."));

        // Resolve the local branch tip for reporting / the ancestor short-circuit.
        var (headRaw, headErr, headCode) = RunGitArgs(repoRoot, "rev-parse", "--verify", $"refs/heads/{branch}");
        if (headCode != 0)
            return Task.FromResult(new GitPushResult(false, string.Empty, "missing-branch", headErr.Trim()));
        var sha = headRaw.Trim();

        // A project without an origin remote is a local-only checkout; there is
        // nothing to push. Treat as a benign skip so the step is not flagged as
        // a failure.
        if (!HasRemote(repoRoot, "origin"))
            return Task.FromResult(new GitPushResult(true, sha, "no-remote", null));

        RunGitArgs(repoRoot, "fetch", "origin", branch);

        var (_, remoteErr, remoteCode) = RunGitArgs(repoRoot, "rev-parse", "--verify", $"origin/{branch}");
        if (remoteCode == 0)
        {
            var (_, ancestorErr, ancestorCode) = RunGitArgs(repoRoot, "merge-base", "--is-ancestor", sha, $"origin/{branch}");
            if (ancestorCode == 0)
                return Task.FromResult(new GitPushResult(true, sha, "already-remote", null));
            if (ancestorCode != 1)
                _logger.LogInformation("Integration-branch push ancestor check for {Branch} returned {Code}: {Error}", branch, ancestorCode, ancestorErr.Trim());
        }
        else
        {
            _logger.LogInformation("Integration-branch push did not find origin/{Branch} before pushing: {Error}", branch, remoteErr.Trim());
        }

        // Non-force push of the branch ref. A non-fast-forward (diverged remote)
        // is reported, never overwritten.
        var (pushOut, pushErr, pushCode) = RunGitArgs(repoRoot, "push", "origin", $"refs/heads/{branch}:refs/heads/{branch}");
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

    // ADR-0052/ADR-0057 worktree + integration primitives. These are low-level
    // git plumbing for the worktree-per-coding-task model on task/<id> branches
    // off the integration branch. They take an explicit repo or worktree root
    // so the orchestrator can drive them directly and so they are unit-testable
    // against a temp repo. Coding runs use them at every slot count;
    // maxParallelism controls admission capacity only.

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
    /// Advances an explicit target branch to an exact, already-tested source
    /// revision using a fast-forward only. The expected SHAs close the gap
    /// between a pre-main test run and the ref mutation: if either branch moved
    /// while the suite was running, no merge is attempted.
    /// </summary>
    public MergeIntoIntegrationResult MergeBranchFastForward(
        string repoRoot,
        string sourceBranch,
        string targetBranch,
        string expectedSourceSha,
        string expectedTargetSha)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.Error, error: "Repo root does not exist.");
        if (!IsLikelyBranchName(sourceBranch) || !IsLikelyBranchName(targetBranch))
            return MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.Error, error: "Invalid source or target branch.");

        var sourceSha = GetBranchTip(repoRoot, sourceBranch);
        var targetSha = GetBranchTip(repoRoot, targetBranch);
        if (!string.Equals(sourceSha, expectedSourceSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(targetSha, expectedTargetSha, StringComparison.OrdinalIgnoreCase))
        {
            return MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.Error,
                error: "Source or target branch moved after the pre-main test run; release merge was not attempted.");
        }

        if (IsAncestor(repoRoot, sourceBranch, targetBranch))
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.AlreadyMerged);
        if (!IsAncestor(repoRoot, targetBranch, sourceBranch))
        {
            return MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.Error,
                error: $"Release source '{sourceBranch}' is not a fast-forward of '{targetBranch}'.");
        }
        if (RepoHasUncommittedChanges(repoRoot))
        {
            return MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.Error,
                error: "Integration working tree has uncommitted changes; refusing to merge.");
        }

        var (currentRaw, _, headCode) = RunGit(repoRoot, "rev-parse --abbrev-ref HEAD");
        var current = headCode == 0 ? currentRaw.Trim() : null;
        if (!string.Equals(current, targetBranch, StringComparison.Ordinal))
        {
            var (_, checkoutError, checkoutCode) = RunGitArgs(repoRoot, "checkout", targetBranch);
            if (checkoutCode != 0)
            {
                return MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: $"Could not check out '{targetBranch}': {checkoutError.Trim()}");
            }
        }

        // Merge the immutable revision that passed the suite, not the movable
        // branch name. The branch-tip checks above reject movement already
        // observed after the gate; this also closes the smaller race between
        // those checks and the ref mutation itself.
        var (_, mergeError, mergeCode) = RunGitArgs(
            repoRoot, "merge", "--ff-only", expectedSourceSha);
        if (mergeCode != 0)
        {
            return MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.Error,
                error: $"Fast-forward release merge failed: {mergeError.Trim()}");
        }

        var mergedSha = ReadHeadShaAt(repoRoot);
        if (!string.Equals(mergedSha, expectedSourceSha, StringComparison.OrdinalIgnoreCase))
        {
            return MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.Error,
                error: "Fast-forward release merge did not land on the tested source SHA.");
        }

        _logger.LogInformation(
            "Fast-forwarded release target {TargetBranch} to tested source {SourceBranch} at {Sha}",
            targetBranch, sourceBranch, mergedSha);
        return MergeIntoIntegrationResult.Of(
            MergeIntoIntegrationOutcome.Merged, mergedSha: mergedSha);
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
    /// Resolves the branch that task worktrees should branch from and merge back
    /// into. The configured project branch wins when it exists; otherwise the
    /// repository's default branch is used so main-only repositories do not fail
    /// with "invalid reference: develop".
    /// </summary>
    public string ResolveIntegrationBranch(string repoRoot, string? configuredBranch)
    {
        var configured = string.IsNullOrWhiteSpace(configuredBranch)
            ? new ProjectSettings().IntegrationBranch
            : configuredBranch.Trim();

        if (BranchExists(repoRoot, configured))
            return configured;

        var fallback = ResolveRepositoryDefaultBranch(repoRoot);
        if (!string.IsNullOrWhiteSpace(fallback) && BranchExists(repoRoot, fallback))
        {
            _logger.LogInformation(
                "Integration branch {ConfiguredBranch} does not exist at {RepoRoot}; using repository default branch {FallbackBranch}.",
                configured,
                repoRoot,
                fallback);
            return fallback;
        }

        return configured;
    }

    /// <summary>
    /// Resolves the integration revision for read-only projections. Unlike
    /// worktree mutation paths, these readers can consume a remote-tracking ref
    /// when a clone has no corresponding local branch yet.
    /// </summary>
    internal string ResolveIntegrationReadRef(string repoRoot, string? configuredBranch)
    {
        var configured = string.IsNullOrWhiteSpace(configuredBranch)
            ? new ProjectSettings().IntegrationBranch
            : configuredBranch.Trim();

        if (BranchExists(repoRoot, configured)) return configured;
        if (RemoteBranchExists(repoRoot, configured)) return "origin/" + configured;

        var fallback = ResolveRepositoryDefaultBranch(repoRoot);
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            if (BranchExists(repoRoot, fallback)) return fallback;
            if (RemoteBranchExists(repoRoot, fallback)) return "origin/" + fallback;
        }

        return configured;
    }

    /// <summary>
    /// Resolves the remote-tracking ref for an integration/release branch.
    /// Queue and promotion projections deliberately do not fall back to a local
    /// branch: a local integration commit is still waiting until the publisher
    /// has made it visible on <c>origin</c>.
    /// </summary>
    public string ResolveOriginReadRef(string branch)
    {
        var candidate = string.IsNullOrWhiteSpace(branch) ? "develop" : branch.Trim();
        return candidate.StartsWith("origin/", StringComparison.Ordinal)
            ? candidate
            : "origin/" + candidate;
    }

    private bool RemoteBranchExists(string repoRoot, string branch)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return false;
        if (!IsLikelyBranchName(branch)) return false;
        var (_, _, code) = RunGitArgs(
            repoRoot,
            "rev-parse",
            "--verify",
            "--quiet",
            $"refs/remotes/origin/{branch}");
        return code == 0;
    }

    private string? ResolveRepositoryDefaultBranch(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return null;

        var (remoteHead, _, remoteHeadCode) = RunGitArgs(repoRoot, "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD");
        if (remoteHeadCode == 0 && !string.IsNullOrWhiteSpace(remoteHead))
        {
            var branch = remoteHead.Trim();
            const string originPrefix = "origin/";
            if (branch.StartsWith(originPrefix, StringComparison.Ordinal))
                branch = branch[originPrefix.Length..];
            if (IsLikelyBranchName(branch))
                return branch;
        }

        var (head, _, headCode) = RunGitArgs(repoRoot, "symbolic-ref", "--quiet", "--short", "HEAD");
        if (headCode == 0)
        {
            var branch = head.Trim();
            if (IsLikelyBranchName(branch))
                return branch;
        }

        return null;
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
    /// The merge-base (fork point) SHA of two refs, or null when either ref is
    /// missing / they share no history. ASS-1724 uses this to capture a task
    /// branch's <c>base</c> - the commit <c>task/&lt;id&gt;</c> was cut from off the
    /// integration branch - so the graph merge-set (<c>base..branch</c>) is exact.
    /// </summary>
    public string? GetMergeBase(string repoRoot, string refA, string refB)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return null;
        if (!IsLikelyBranchName(refA) || !IsLikelyBranchName(refB)) return null;
        var (output, _, code) = RunGitArgs(repoRoot, "merge-base", refA, refB);
        if (code != 0) return null;
        var sha = output.Trim();
        return string.IsNullOrWhiteSpace(sha) ? null : sha;
    }

    /// <summary>
    /// Tip SHA of a local or remote-tracking ref (<c>rev-parse --verify</c>), or
    /// null when the ref does not exist. ASS-1724 reads the <c>task/&lt;id&gt;</c>
    /// tip and the develop/main heads to anchor each lane transition and to build
    /// the landed ladder's "HEAD now" rungs.
    /// </summary>
    public string? GetBranchTip(string repoRoot, string branch)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return null;
        if (!IsLikelyBranchName(branch)) return null;
        var (output, _, code) = RunGitArgs(repoRoot, "rev-parse", "--verify", "--quiet", branch);
        if (code != 0) return null;
        var sha = output.Trim();
        return string.IsNullOrWhiteSpace(sha) ? null : sha;
    }

    /// <summary>
    /// Commits reachable from <paramref name="toRef"/> but not
    /// <paramref name="fromRef"/> (<c>git log fromRef..toRef --no-merges</c>),
    /// newest first. This is the graph merge-set ASS-1724 displays: with
    /// <c>fromRef = base</c> and <c>toRef = task/&lt;id&gt;</c> it is exactly the
    /// commits the task branch is ahead of its fork point - no wall-clock window,
    /// no boundary slack. Returns an empty list when either ref is missing.
    /// </summary>
    public List<GitCommitInfo> GetCommitsInRangeAtRoot(string repoRoot, string fromRef, string toRef)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return [];
        if (!IsLikelyBranchName(fromRef) || !IsLikelyBranchName(toRef)) return [];
        var root = ResolveGitToplevel(repoRoot) ?? repoRoot;

        const char US = '\x1f';
        var fmt = "%H%x1f%h%x1f%aI%x1f%aN%x1f%s";
        var args = $"log --no-merges --shortstat --pretty=format:\"{fmt}\" {fromRef}..{toRef}";
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
        return list;
    }

    /// <summary>
    /// Merges <paramref name="taskBranch"/> (e.g. <c>task/&lt;id&gt;</c>) into
    /// <paramref name="integrationBranch"/> (e.g. <c>develop</c>) with an explicit
    /// merge commit (<c>git merge --no-ff --no-edit</c>) so an accepted task lands
    /// on the integration branch as a single, revertable delivery. This is the
    /// engine behind the deferred, operator-triggered "Merge into Develop"
    /// post-step (<c>PipelineCatalogue.MergeIntoDevelopStepId</c>); it is NOT the
    /// automatic in-run integration (<see cref="RebaseOnto"/> +
    /// <see cref="MergeFastForward"/>) used to keep parallel worktrees in sync.
    ///
    /// <para>Contract:
    /// <list type="bullet">
    /// <item>No task branch -&gt; <see cref="MergeIntoIntegrationOutcome.NoTaskBranch"/> (benign skip, e.g. a sequential run with no worktree branch).</item>
    /// <item>Already contained -&gt; <see cref="MergeIntoIntegrationOutcome.AlreadyMerged"/> (idempotent no-op, so a re-trigger is safe).</item>
    /// <item>Dirty integration tree / missing integration branch / checkout failure -&gt; <see cref="MergeIntoIntegrationOutcome.Error"/> (never merge into a dirty tree).</item>
    /// <item>Conflict -&gt; the merge is aborted so the tree is left clean and the conflicted files are returned (<see cref="MergeIntoIntegrationOutcome.Conflict"/>); the conflict is surfaced, never silently resolved or left half-applied.</item>
    /// <item>Otherwise -&gt; <see cref="MergeIntoIntegrationOutcome.Merged"/> with the new integration HEAD sha.</item>
    /// </list>
    /// The merge runs in the working tree at <paramref name="repoRoot"/>; the
    /// integration branch is checked out there first when it is not already HEAD
    /// (only when the tree is clean).</para>
    /// </summary>
    public MergeIntoIntegrationResult MergeBranchIntoIntegration(string repoRoot, string taskBranch, string integrationBranch)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Error, error: "Repo root does not exist.");
        if (!IsLikelyBranchName(taskBranch))
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Error, error: $"Invalid task branch '{taskBranch}'.");
        if (!IsLikelyBranchName(integrationBranch))
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Error, error: $"Invalid integration branch '{integrationBranch}'.");

        if (!BranchExists(repoRoot, taskBranch))
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.NoTaskBranch, error: $"Task branch '{taskBranch}' does not exist.");
        return MergeRefIntoIntegration(repoRoot, taskBranch, integrationBranch);
    }

    /// <summary>
    /// Fetches and verifies the exact fenced result produced by a remote runner,
    /// then merges that immutable commit into the configured integration branch.
    /// The delivery branch name and SHA come from <c>review-subject.json</c>.
    /// Acceptance must never guess <c>task/&lt;slug&gt;</c> for a remote run or
    /// silently merge a branch head that advanced after review.
    /// </summary>
    public MergeIntoIntegrationResult MergeRemoteDeliveryIntoIntegration(
        string repoRoot,
        string deliveryBranch,
        string expectedResultSha,
        string integrationBranch)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Error, error: "Repo root does not exist.");
        if (!IsLikelyBranchName(deliveryBranch))
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Error, error: $"Invalid delivery branch '{deliveryBranch}'.");
        if (!ReviewSubjectStore.IsValidResultSha(expectedResultSha))
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Error, error: "Remote delivery has no valid fenced result SHA.");
        if (!IsLikelyBranchName(integrationBranch))
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Error, error: $"Invalid integration branch '{integrationBranch}'.");
        if (!HasRemote(repoRoot, "origin"))
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Error, error: "Remote delivery cannot be fetched because origin is not configured.");

        var remoteRef = $"refs/remotes/origin/{deliveryBranch}";
        var remoteIntegrationRef = $"refs/remotes/origin/{integrationBranch}";
        var integrationFetchSource = $"refs/heads/{integrationBranch}";
        var integrationFetchTarget = $"+{integrationFetchSource}:{remoteIntegrationRef}";
        var (_, integrationFetchError, integrationFetchCode) = RunGitArgs(
            repoRoot, "fetch", "--no-tags", "origin", integrationFetchTarget);
        if (integrationFetchCode != 0)
        {
            return MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.Error,
                error: $"Integration branch '{integrationBranch}' could not be fetched from origin: {integrationFetchError.Trim()}");
        }

        var fetchSource = $"refs/heads/{deliveryBranch}";
        var fetchTarget = $"+{fetchSource}:{remoteRef}";
        var (_, fetchError, fetchCode) = RunGitArgs(repoRoot, "fetch", "--no-tags", "origin", fetchTarget);
        if (fetchCode != 0)
        {
            return MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.NoTaskBranch,
                error: $"Delivery branch '{deliveryBranch}' could not be fetched from origin: {fetchError.Trim()}");
        }

        var (fetchedSha, verifyError, verifyCode) = RunGitArgs(
            repoRoot, "rev-parse", "--verify", $"{remoteRef}^{{commit}}");
        if (verifyCode != 0)
        {
            return MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.Error,
                error: $"Fetched delivery branch '{deliveryBranch}' is not a commit: {verifyError.Trim()}");
        }

        var actualResultSha = fetchedSha.Trim();
        if (!string.Equals(actualResultSha, expectedResultSha, StringComparison.OrdinalIgnoreCase))
        {
            return MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.Error,
                error: $"Fenced delivery mismatch for '{deliveryBranch}': review expects {AbbreviateSha(expectedResultSha)}, origin has {AbbreviateSha(actualResultSha)}.");
        }

        if (!BranchExists(repoRoot, integrationBranch))
        {
            var (_, createError, createCode) = RunGitArgs(
                repoRoot, "branch", integrationBranch, remoteIntegrationRef);
            if (createCode != 0)
            {
                return MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: $"Could not create local integration branch '{integrationBranch}' from origin: {createError.Trim()}");
            }
        }

        return MergeRefIntoIntegration(
            repoRoot,
            expectedResultSha,
            integrationBranch,
            remoteIntegrationRef);
    }

    private static string AbbreviateSha(string sha)
        => sha[..Math.Min(8, sha.Length)];

    private MergeIntoIntegrationResult MergeRefIntoIntegration(
        string repoRoot,
        string sourceRef,
        string integrationBranch,
        string? synchronizeFromRemoteRef = null)
    {
        if (!BranchExists(repoRoot, integrationBranch))
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Error, error: $"Integration branch '{integrationBranch}' does not exist.");

        // Never merge into a dirty tree - that would entangle the operator's
        // in-flight edits with the delivery merge.
        if (RepoHasUncommittedChanges(repoRoot))
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Error, error: "Integration working tree has uncommitted changes; refusing to merge.");

        var (currentRaw, _, headCode) = RunGit(repoRoot, "rev-parse --abbrev-ref HEAD");
        var current = headCode == 0 ? currentRaw.Trim() : null;
        if (!string.Equals(current, integrationBranch, StringComparison.Ordinal))
        {
            var (_, coErr, coCode) = RunGitArgs(repoRoot, "checkout", integrationBranch);
            if (coCode != 0)
            {
                _logger.LogWarning("Merge-into-develop: checkout of {Integration} at {Path} failed: {Error}", integrationBranch, repoRoot, coErr.Trim());
                return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Error, error: $"Could not check out '{integrationBranch}': {coErr.Trim()}");
            }
        }

        if (!string.IsNullOrWhiteSpace(synchronizeFromRemoteRef))
        {
            var (_, syncError, syncCode) = RunGitArgs(
                repoRoot, "merge", "--ff-only", synchronizeFromRemoteRef);
            if (syncCode != 0)
            {
                return MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: $"Local integration branch '{integrationBranch}' diverged from origin; refusing to merge into a stale target: {syncError.Trim()}");
            }
        }

        // Idempotent: a re-trigger after a successful merge is a clean no-op.
        if (IsAncestor(repoRoot, sourceRef, integrationBranch))
            return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.AlreadyMerged);

        var (_, mergeErr, mergeCode) = RunGitArgs(repoRoot, "merge", "--no-ff", "--no-edit", sourceRef);
        if (mergeCode != 0)
        {
            var conflicted = ListUnmergedFiles(repoRoot);
            // Abort so the tree is left clean; the conflict is reported, not silently resolved.
            RunGitArgs(repoRoot, "merge", "--abort");
            _logger.LogWarning(
                "Merge-into-develop: merging {Task} into {Integration} at {Path} conflicted ({Count} files), aborted: {Error}",
                sourceRef, integrationBranch, repoRoot, conflicted.Count, mergeErr.Trim());
            return MergeIntoIntegrationResult.Conflicted(conflicted, mergeErr.Trim());
        }

        var mergedSha = ReadHeadShaAt(repoRoot);
        _logger.LogInformation("Merge-into-develop: merged {Task} into {Integration} at {Path} ({Sha})", sourceRef, integrationBranch, repoRoot, mergedSha);
        return MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged, mergedSha: mergedSha);
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
    /// Enumerates the refs under a fully-qualified namespace pattern (e.g.
    /// <c>refs/heads/task</c>, <c>refs/remotes/origin/task</c>,
    /// <c>refs/backups</c>) via <c>git for-each-ref</c>. Read-only: it backs the
    /// Git-Management cleanup analysis, which needs the tip SHA of every candidate
    /// ref to check whether it is already contained in the integration branch. The
    /// pattern must start with <c>refs/</c> and is validated against the same
    /// safe-name rules the mutating primitives use, so a crafted argument cannot
    /// smuggle a flag into the git invocation. Returns an empty list when the
    /// pattern is unsafe, the repo is missing, or nothing matches.
    /// </summary>
    public IReadOnlyList<GitRefLine> ListRefs(string repoRoot, string pattern)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return [];
        if (!IsLikelyRefPattern(pattern)) return [];

        const char US = '\x1f';
        var fmt = string.Join(US.ToString(), new[]
        {
            "%(refname)", "%(refname:short)", "%(objectname)", "%(objectname:short)"
        });
        var (output, _, code) = RunGitArgs(repoRoot, "for-each-ref", $"--format={fmt}", pattern);
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return [];

        var list = new List<GitRefLine>();
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var parts = raw.Split(US);
            if (parts.Length < 4) continue;
            var full = parts[0].Trim();
            if (full.Length == 0) continue;
            list.Add(new GitRefLine(full, parts[1].Trim(), parts[2].Trim(), parts[3].Trim()));
        }
        return list;
    }

    /// <summary>
    /// Typed <c>git worktree list --porcelain</c> for a repo root, reusing
    /// <see cref="ParseWorktreePorcelain"/>. Read-only helper for the cleanup
    /// analysis, which cross-references the registered worktrees against on-disk
    /// folders to spot stale (orphaned) registrations and to keep branches that
    /// are still checked out out of the delete set.
    /// </summary>
    public IReadOnlyList<GitWorktreeEntry> ListWorktrees(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return [];
        var (output, _, code) = RunGitArgs(repoRoot, "worktree", "list", "--porcelain");
        return code == 0 ? ParseWorktreePorcelain(output) : [];
    }

    /// <summary>
    /// Deletes a single fully-qualified ref via <c>git update-ref -d</c>. The
    /// Git-Management cleanup uses this to drop <c>refs/backups/*</c> entries whose
    /// commit is already contained in the integration branch (the operational
    /// safety net has served its purpose). Guarded: the ref must start with
    /// <c>refs/</c> and pass the safe-name check, so it can never be coaxed into
    /// deleting a branch head or an arbitrary path. Deleting a missing ref is
    /// treated as success (idempotent teardown).
    /// </summary>
    public GitWorktreeResult DeleteRef(string repoRoot, string fullRef)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
            return new GitWorktreeResult(false, null, "Repo root does not exist.");
        if (!IsLikelyRefPattern(fullRef) || !fullRef.StartsWith("refs/", StringComparison.Ordinal))
            return new GitWorktreeResult(false, null, $"Invalid ref '{fullRef}'.");

        var (_, err, code) = RunGitArgs(repoRoot, "update-ref", "-d", fullRef);
        if (code == 0)
        {
            _logger.LogInformation("Deleted ref {Ref} at {Path}", fullRef, repoRoot);
            return new GitWorktreeResult(true, null, null);
        }

        var error = err.Trim();
        if (error.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || error.Contains("cannot lock ref", StringComparison.OrdinalIgnoreCase) && error.Contains("unable to resolve", StringComparison.OrdinalIgnoreCase))
            return new GitWorktreeResult(true, null, null);

        _logger.LogWarning("Delete ref {Ref} failed at {Path}: {Error}", fullRef, repoRoot, error);
        return new GitWorktreeResult(false, null, error);
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
    /// Folds a worker-created linear commit range back into the working tree so
    /// the normal platform commit step can record it. This is deliberately a
    /// soft reset: file content and index state are preserved. The operation
    /// refuses stale observations and non-linear history, so it cannot discard
    /// a concurrent commit or rewrite work that predates the run.
    /// </summary>
    public GitWorkerCommitCleanupResult FoldWorkerCommitsIntoPlatformCommit(
        string worktreePath,
        string? headBefore,
        string? observedHeadAfter)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
            return new GitWorkerCommitCleanupResult(false, "needs-cleanup", "Worktree path does not exist.");
        if (string.IsNullOrWhiteSpace(headBefore) || !IsLikelyShaOrRef(headBefore))
            return new GitWorkerCommitCleanupResult(false, "needs-cleanup", "Run-start HEAD is missing or invalid.");
        if (string.IsNullOrWhiteSpace(observedHeadAfter) || !IsLikelyShaOrRef(observedHeadAfter))
            return new GitWorkerCommitCleanupResult(false, "needs-cleanup", "Run-end HEAD is missing or invalid.");

        var current = ReadHeadShaAt(worktreePath);
        if (!string.Equals(current, observedHeadAfter, StringComparison.OrdinalIgnoreCase))
        {
            return new GitWorkerCommitCleanupResult(
                false,
                "needs-cleanup",
                "HEAD changed again after the worker finished; automatic cleanup was skipped.");
        }

        if (!IsAncestor(worktreePath, headBefore, observedHeadAfter))
        {
            return new GitWorkerCommitCleanupResult(
                false,
                "unsafe-history",
                "The worker HEAD is not a linear descendant of the run-start HEAD.");
        }

        var (_, error, code) = RunGitArgs(worktreePath, "reset", "--soft", headBefore);
        if (code != 0)
        {
            return new GitWorkerCommitCleanupResult(
                false,
                "needs-cleanup",
                string.IsNullOrWhiteSpace(error) ? "git reset --soft failed." : error.Trim());
        }

        var resetHead = ReadHeadShaAt(worktreePath);
        if (!string.Equals(resetHead, headBefore, StringComparison.OrdinalIgnoreCase))
        {
            return new GitWorkerCommitCleanupResult(
                false,
                "needs-cleanup",
                "Soft reset returned success but HEAD did not return to the run-start commit.");
        }

        return new GitWorkerCommitCleanupResult(true, "platform-commit-ready", null);
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
    /// Reads the checked-out branch at an explicit repository or worktree
    /// root. A detached checkout returns <c>HEAD</c>; null means the path is
    /// not a readable git repository.
    /// </summary>
    public string? ReadBranchAt(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        var (output, _, code) = RunGit(root, "rev-parse --abbrev-ref HEAD");
        if (code != 0) return null;
        var branch = output.Trim();
        return string.IsNullOrWhiteSpace(branch) ? null : branch;
    }

    /// <summary>
    /// Reads the local branch checked out at an explicit repo or worktree root.
    /// Returns null for detached HEAD or when the path is not a repository.
    /// </summary>
    public string? ReadCurrentBranchAt(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        var (output, _, code) = RunGitArgs(root, "symbolic-ref", "--quiet", "--short", "HEAD");
        if (code != 0) return null;
        var branch = output.Trim();
        return string.IsNullOrWhiteSpace(branch) || !IsLikelyBranchName(branch) ? null : branch;
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
        var args = "show -s --no-patch --pretty=format:\"%H%x1f%P%x1f%aI%x1f%B%x1e\" " + string.Join(' ', list);
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
            rest = rest[(secondUs + 1)..];
            var thirdUs = rest.IndexOf(US);
            if (thirdUs < 0) continue;
            if (!DateTime.TryParse(
                    rest[..thirdUs],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var authoredAt))
                continue;
            var body = rest[(thirdUs + 1)..];
            var parentCount = parents.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            result[sha] = new CommitMeta(
                body,
                parentCount > 1,
                DateTime.SpecifyKind(authoredAt, DateTimeKind.Utc));
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

        return GetFilesChangedInRangeAtRoot(root, beforeSha!, afterSha!);
    }

    /// <summary>
    /// Project/root-scoped aggregate file stat for a ref range. This is the
    /// promotion counterpart to the per-task SHA-range reader.
    /// </summary>
    public List<GitFileChange> GetFilesChangedInRangeAtRoot(string root, string beforeSha, string afterSha)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return [];
        if ((!IsLikelyShaOrRef(beforeSha) && !IsLikelyBranchName(beforeSha))
            || (!IsLikelyShaOrRef(afterSha) && !IsLikelyBranchName(afterSha))) return [];

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

    /// <summary>
    /// True when <paramref name="s"/> is a safe fully-qualified ref namespace or
    /// ref (e.g. <c>refs/heads/task</c>, <c>refs/remotes/origin/task</c>,
    /// <c>refs/backups/2026-07-09</c>). Like <see cref="IsLikelyBranchName"/> it
    /// allows the slashes a ref path needs while rejecting whitespace, shell
    /// metacharacters, a leading dash (so the value can't be read as a flag), and
    /// git's invalid <c>..</c> / <c>.lock</c> sequences. Defence in depth: the
    /// value flows into a git argument list, not a shell.
    /// </summary>
    private static bool IsLikelyRefPattern(string s)
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
    public List<GitCommitInfo> GetFileHistory(string repoRoot, string repoRelPath, int limit = 50, string? atRef = null)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(repoRelPath)) return [];
        var root = ResolveGitToplevel(repoRoot) ?? repoRoot;
        if (!Directory.Exists(root)) return [];
        if (limit <= 0) limit = 50;

        // Fields separated by Unit Separator (0x1F) so a commit subject with
        // any printable char round-trips. --follow needs exactly one pathspec
        // after `--`; args are passed verbatim so a path with spaces survives.
        const string fmt = "%H%x1f%h%x1f%aI%x1f%aN%x1f%s";
        var args = new List<string> {
            "log", "--no-merges", $"--max-count={limit}", "--follow",
            "--shortstat", $"--pretty=format:{fmt}"
        };
        if (!string.IsNullOrWhiteSpace(atRef)) args.Add(atRef);
        args.Add("--");
        args.Add(repoRelPath.Replace('\\', '/'));
        var (output, _, code) = RunGitArgs(root, args.ToArray());
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return [];
        return ParseLogBlocks(output);
    }

    /// <summary>
    /// Walks the commit history under <paramref name="repoRelDir"/> and returns
    /// the most-recent commit that touched each distinct file, newest first.
    /// Backs the wiki dashboard's "recent edits" surface: which page changed
    /// last, by whom (git author), and when. One entry per path; a file that
    /// appears in several commits is reported only at its newest one. The walk
    /// is bounded by <paramref name="commitScan"/> (how many commits to read)
    /// and the result by <paramref name="limit"/> (how many distinct files to
    /// return). Returns an empty list when the repo or directory can't be
    /// resolved, mirroring the other read-only git lookups on this service.
    /// </summary>
    public List<GitRecentFileEdit> GetRecentEditsUnderPath(
        string repoRoot, string repoRelDir, int limit = 20, int commitScan = 200, string? atRef = null)
    {
        if (string.IsNullOrWhiteSpace(repoRoot)) return [];
        var root = ResolveGitToplevel(repoRoot) ?? repoRoot;
        if (!Directory.Exists(root)) return [];
        if (limit <= 0) limit = 20;
        if (commitScan <= 0) commitScan = 200;
        var fixedKey = !string.IsNullOrWhiteSpace(atRef) && LooksLikeImmutableSha(atRef)
            ? string.Join(CacheKeySep, "wiki-recent-ref", root, atRef, repoRelDir, limit, commitScan)
            : null;
        if (fixedKey != null && TryGetShaRangeCached<List<GitRecentFileEdit>>(fixedKey, out var fixedHit))
            return fixedHit;

        // Records are separated by Record Separator (0x1E); fields inside the
        // header line by Unit Separator (0x1F). --name-only lists each changed
        // path on its own line after the header. Because git log is newest
        // first, the first time we see a path is its most-recent commit.
        const string fmt = "%x1e%H%x1f%h%x1f%aI%x1f%aN%x1f%s";
        var pathspec = string.IsNullOrWhiteSpace(repoRelDir) ? "." : repoRelDir.Replace('\\', '/');
        var args = new List<string> {
            "log", "--no-merges", $"--max-count={commitScan}",
            "--name-only", $"--pretty=format:{fmt}"
        };
        if (!string.IsNullOrWhiteSpace(atRef)) args.Add(atRef);
        args.Add("--");
        args.Add(pathspec);
        var (output, _, code) = RunGitArgs(root, args.ToArray());
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return [];
        var result = ParseRecentEdits(output, limit);
        if (fixedKey != null) StoreShaRangeCached(fixedKey, result);
        return result;
    }

    /// <summary>
    /// Most-recent commit metadata for every tracked file below
    /// <paramref name="repoRelDir"/>, cached by repository HEAD. This is the
    /// complete variant of <see cref="GetRecentEditsUnderPath"/> used by wiki
    /// folder listings: one batch <c>git log --name-only</c> walk supplies all
    /// page dates, and subsequent folder requests reuse the same result while
    /// HEAD is unchanged. It deliberately has no commit-count cap because an
    /// old page may have last changed before the dashboard feed's bounded
    /// recent-history window.
    /// </summary>
    public List<GitRecentFileEdit> GetLatestFileEditsUnderPathCached(
        string repoRoot, string repoRelDir)
    {
        if (string.IsNullOrWhiteSpace(repoRoot)) return [];
        var root = ResolveGitToplevel(repoRoot) ?? repoRoot;
        var pathspec = string.IsNullOrWhiteSpace(repoRelDir)
            ? "."
            : repoRelDir.Replace('\\', '/');
        var key = string.Join(CacheKeySep, "wiki-file-dates", root, pathspec);
        return MemoizeByHead(root, key,
            () => GetRecentEditsUnderPath(root, pathspec, int.MaxValue, int.MaxValue));
    }

    /// <summary>
    /// Author dates (UTC, newest first) of up to <paramref name="maxCommits"/>
    /// non-merge commits that touched any of <paramref name="repoRelPaths"/>.
    /// Backs the wiki Pulse drift-grading heuristic (PULSE-1): how many code
    /// commits have landed under the code roots since a knowledge page was last
    /// refreshed. A single <c>git log</c> spawn that reads only the author date
    /// (no diff / name walk) so it stays cheap even with the cap. Returns an
    /// empty list when the repo or paths can't be resolved, mirroring the other
    /// read-only git lookups on this service.
    /// </summary>
    public List<DateTime> GetCommitAuthorDatesUnderPaths(
        string repoRoot, IReadOnlyCollection<string> repoRelPaths, int maxCommits = 500)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || repoRelPaths == null || repoRelPaths.Count == 0) return [];
        var root = ResolveGitToplevel(repoRoot) ?? repoRoot;
        if (!Directory.Exists(root)) return [];
        if (maxCommits <= 0) maxCommits = 500;

        var args = new List<string> { "log", "--no-merges", $"--max-count={maxCommits}", "--pretty=format:%aI", "--" };
        foreach (var p in repoRelPaths)
            if (!string.IsNullOrWhiteSpace(p)) args.Add(p.Replace('\\', '/'));
        if (args[^1] == "--") return []; // no usable pathspec survived

        var (output, _, code) = RunGitArgs(root, args.ToArray());
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return [];

        var list = new List<DateTime>();
        foreach (var line in output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (DateTime.TryParse(line.Trim(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var ts))
                list.Add(DateTime.SpecifyKind(ts, DateTimeKind.Utc));
        }
        return list;
    }

    /// <summary>
    /// Parses <c>git log --name-only --pretty=format:&lt;RS&gt;%H&lt;US&gt;...</c>
    /// output (see <see cref="GetRecentEditsUnderPath"/>) into per-file
    /// most-recent-commit records. Split out for unit testing.
    /// </summary>
    internal static List<GitRecentFileEdit> ParseRecentEdits(string output, int limit)
    {
        const char RS = '\x1e';
        const char US = '\x1f';
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<GitRecentFileEdit>();
        var raw = output.Replace("\r\n", "\n");
        foreach (var record in raw.Split(RS, StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = record.Split('\n');
            if (lines.Length == 0) continue;
            var header = lines[0];
            var parts = header.Split(US);
            if (parts.Length < 5) continue;
            if (!DateTime.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var ts))
                continue;
            var when = DateTime.SpecifyKind(ts, DateTimeKind.Utc);
            for (var i = 1; i < lines.Length; i++)
            {
                var path = lines[i].Trim();
                if (string.IsNullOrEmpty(path)) continue;
                if (!seen.Add(path)) continue;
                list.Add(new GitRecentFileEdit(
                    RepoRelPath: path,
                    Sha: parts[0],
                    ShortSha: parts[1],
                    AuthorDateUtc: when,
                    Author: parts[3],
                    Subject: parts[4]));
                if (list.Count >= limit) return list;
            }
        }
        return list;
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
    public string? GetLatestModelForPath(string repoRoot, string repoRelPath, string? atRef = null)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(repoRelPath)) return null;
        var root = ResolveGitToplevel(repoRoot) ?? repoRoot;
        if (!Directory.Exists(root)) return null;

        var args = new List<string> {
            "log", "--no-merges", "--max-count=1",
            "--pretty=format:%(trailers:key=Co-authored-by,valueonly,separator=%x1f)"
        };
        if (!string.IsNullOrWhiteSpace(atRef)) args.Add(atRef);
        args.Add("--");
        args.Add(repoRelPath.Replace('\\', '/'));
        var (output, _, code) = RunGitArgs(root, args.ToArray());
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

    // -------- Wiki file operations (content at a revision + commit-backed CRUD) --------

    /// <summary>
    /// Reads a file's content as it existed at <paramref name="sha"/> via
    /// <c>git show &lt;sha&gt;:&lt;path&gt;</c>. Backs the wiki "view old revision"
    /// action. The SHA is validated through <see cref="IsLikelyShaOrRef"/> first
    /// so a crafted ref can't smuggle shell-meaningful input; the path is
    /// forward-slashed for git's object syntax. Returns null when the repo, sha,
    /// or path can't be resolved.
    /// </summary>
    public string? GetFileAtCommit(string repoRoot, string sha, string repoRelPath)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || string.IsNullOrWhiteSpace(repoRelPath)) return null;
        if (string.IsNullOrWhiteSpace(sha) || !IsLikelyShaOrRef(sha)) return null;
        var root = ResolveGitToplevel(repoRoot) ?? repoRoot;
        if (!Directory.Exists(root)) return null;

        var (output, _, code) = RunGitArgs(root, "show", $"{sha}:{repoRelPath.Replace('\\', '/')}");
        return code == 0 ? output : null;
    }

    // ---- HEAD-cached wiki git reads (AGT-2013) ----
    //
    // The wiki dashboard + per-doc panels re-ask for the same recent-edits walk
    // and file history on every navigation. These wrap the raw reads above in the
    // HEAD-keyed memo so a warm (HEAD-unchanged) request serves them from memory
    // with at most one `rev-parse HEAD` spawn instead of a fresh multi-hundred-ms
    // `git log`. See <see cref="MemoizeByHead{T}"/>.

    private const char CacheKeySep = '';

    /// <summary>
    /// The per-doc git provenance the wiki history panel needs: the file's commit
    /// history (newest first) and the model that last touched it. Both raw reads
    /// are folded into one HEAD-keyed memo entry so a warm history open costs zero
    /// git spawns beyond the shared HEAD probe, instead of the two `git log`
    /// spawns (history + trailer) it used to make on every open.
    /// </summary>
    public WikiDocGitInfo GetWikiDocGitInfoCached(string repoRoot, string repoRelPath, int limit = 50, string? atCommit = null)
    {
        var root = ResolveGitToplevel(repoRoot) ?? repoRoot;
        var key = string.Join(CacheKeySep, "wiki-hist", root, repoRelPath, limit, atCommit ?? "HEAD");
        if (!string.IsNullOrWhiteSpace(atCommit) && LooksLikeImmutableSha(atCommit))
        {
            if (TryGetShaRangeCached<WikiDocGitInfo>(key, out var fixedHit)) return fixedHit;
            var fixedValue = new WikiDocGitInfo(
                GetFileHistory(root, repoRelPath, limit, atCommit),
                GetLatestModelForPath(root, repoRelPath, atCommit));
            StoreShaRangeCached(key, fixedValue);
            return fixedValue;
        }
        return MemoizeByHead(root, key, () => new WikiDocGitInfo(
            GetFileHistory(root, repoRelPath, limit),
            GetLatestModelForPath(root, repoRelPath)));
    }

    /// <summary>
    /// HEAD-independent cached <see cref="GetFileAtCommit"/>: a file's bytes at a
    /// concrete commit are content-addressed and never change, so the result is
    /// cached permanently (bounded LRU, reusing the SHA-range cache) keyed by
    /// (root, sha, path). Only caches when <paramref name="sha"/> is an immutable
    /// object name (hex), never a symbolic ref like a branch that could move.
    /// </summary>
    public string? GetFileAtCommitCached(string repoRoot, string sha, string repoRelPath)
    {
        if (string.IsNullOrWhiteSpace(sha) || !IsLikelyShaOrRef(sha)) return null;
        var root = ResolveGitToplevel(repoRoot) ?? repoRoot;
        if (!LooksLikeImmutableSha(sha))
            return GetFileAtCommit(root, sha, repoRelPath);

        var key = string.Join(CacheKeySep, "wiki-rev", root, sha, repoRelPath.Replace('\\', '/'));
        if (TryGetShaRangeCached<string>(key, out var cached)) return cached;
        var content = GetFileAtCommit(root, sha, repoRelPath);
        if (content != null) StoreShaRangeCached(key, content);
        return content;
    }

    /// <summary>
    /// True when <paramref name="sha"/> is a plain hex object name (a concrete,
    /// immutable commit id), as opposed to a symbolic ref such as a branch or tag
    /// whose target can move. Used to gate permanent caching of content-addressed
    /// reads: an immutable sha is safe to cache forever; a movable ref is not.
    /// </summary>
    private static bool LooksLikeImmutableSha(string sha)
    {
        if (sha.Length is < 7 or > 40) return false;
        foreach (var c in sha)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex) return false;
        }
        return true;
    }

    /// <summary>
    /// Stages then commits the given repo-relative paths into the project repo.
    /// Used by the wiki create-page/create-folder operations: the file already
    /// exists on disk, this records it. A clean tree (nothing to commit) is a
    /// soft failure, not an exception.
    /// </summary>
    public GitCommitResult CommitPaths(string repoRoot, string message, IReadOnlyCollection<string> repoRelPaths)
    {
        var root = ResolveGitToplevel(repoRoot);
        if (root == null) return new GitCommitResult(false, null, $"Not a git repository: {repoRoot}");
        var paths = NormalizePaths(repoRelPaths);
        if (paths.Count == 0) return new GitCommitResult(false, null, "No paths to commit.");

        var addArgs = new List<string> { "add", "-A", "--" };
        addArgs.AddRange(paths);
        var (_, addErr, addCode) = RunGitArgs(root, addArgs.ToArray());
        if (addCode != 0) return new GitCommitResult(false, null, $"git add failed: {addErr.Trim()}");

        return CommitPathspecs(root, message, paths);
    }

    /// <summary>
    /// Restores a bounded set of repository-relative paths to HEAD and removes
    /// only untracked files below those exact pathspecs. This is the rollback
    /// half of the managed on-demand artifact boundary: its caller first proves
    /// the checkout was clean, so every supplied path belongs to the failed
    /// platform write rather than to an operator or another task.
    /// </summary>
    public GitWorktreeResult RestorePathsToHead(
        string repoRoot,
        IReadOnlyCollection<string> repoRelPaths)
    {
        var root = ResolveGitToplevel(repoRoot);
        if (root == null) return new GitWorktreeResult(false, null, $"Not a git repository: {repoRoot}");
        var paths = NormalizePaths(repoRelPaths);
        if (paths.Count == 0) return new GitWorktreeResult(true, null, null);

        var failures = new List<string>();
        foreach (var path in paths)
        {
            var (_, _, trackedCode) = RunGitArgs(root, "ls-files", "--error-unmatch", "--", path);
            if (trackedCode == 0)
            {
                var (_, restoreError, restoreCode) = RunGitArgs(
                    root, "restore", "--source=HEAD", "--staged", "--worktree", "--", path);
                if (restoreCode != 0) failures.Add($"{path}: {restoreError.Trim()}");
                continue;
            }

            var (_, cleanError, cleanCode) = RunGitArgs(root, "clean", "-fd", "--", path);
            if (cleanCode != 0) failures.Add($"{path}: {cleanError.Trim()}");
        }

        return failures.Count == 0
            ? new GitWorktreeResult(true, null, null)
            : new GitWorktreeResult(false, null, string.Join("; ", failures));
    }

    /// <summary>
    /// Moves/renames a wiki node via <c>git mv</c> and commits it. Tracked files
    /// move through git; an untracked source falls back to a filesystem move plus
    /// <c>git add</c> so a never-committed page can still be renamed. Refuses to
    /// overwrite an existing destination.
    /// </summary>
    public GitCommitResult MoveAndCommit(string repoRoot, string fromRel, string toRel, string message)
    {
        var root = ResolveGitToplevel(repoRoot);
        if (root == null) return new GitCommitResult(false, null, $"Not a git repository: {repoRoot}");

        var from = NormalizeRel(fromRel);
        var to = NormalizeRel(toRel);
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            return new GitCommitResult(false, null, "Source and destination are required.");

        var fromAbs = Path.GetFullPath(Path.Combine(root, from));
        var toAbs = Path.GetFullPath(Path.Combine(root, to));
        if (!File.Exists(fromAbs) && !Directory.Exists(fromAbs))
            return new GitCommitResult(false, null, "Source does not exist.");
        if (File.Exists(toAbs) || Directory.Exists(toAbs))
            return new GitCommitResult(false, null, "Destination already exists.");

        var toDir = Path.GetDirectoryName(toAbs);
        if (!string.IsNullOrEmpty(toDir)) Directory.CreateDirectory(toDir);

        var (_, mvErr, mvCode) = RunGitArgs(root, "mv", from, to);
        if (mvCode != 0)
        {
            // Untracked source: git mv refuses. Fall back to a plain move + stage.
            try
            {
                if (Directory.Exists(fromAbs)) Directory.Move(fromAbs, toAbs);
                else File.Move(fromAbs, toAbs);
            }
            catch (Exception ex)
            {
                return new GitCommitResult(false, null, $"Move failed: {mvErr.Trim()} / {ex.Message}");
            }
            var addArgs = new List<string> { "add", "-A", "--", from, to };
            RunGitArgs(root, addArgs.ToArray());
        }

        return CommitPathspecs(root, message, new[] { from, to });
    }

    /// <summary>
    /// Deletes a wiki node via <c>git rm -r</c> and commits it. An untracked
    /// target falls back to a filesystem delete so an uncommitted page can still
    /// be removed.
    /// </summary>
    public GitCommitResult RemoveAndCommit(string repoRoot, string repoRel, string message)
    {
        var root = ResolveGitToplevel(repoRoot);
        if (root == null) return new GitCommitResult(false, null, $"Not a git repository: {repoRoot}");

        var rel = NormalizeRel(repoRel);
        if (string.IsNullOrEmpty(rel)) return new GitCommitResult(false, null, "Path is required.");

        var abs = Path.GetFullPath(Path.Combine(root, rel));
        if (!File.Exists(abs) && !Directory.Exists(abs))
            return new GitCommitResult(false, null, "Path does not exist.");

        var (_, rmErr, rmCode) = RunGitArgs(root, "rm", "-r", "--", rel);
        if (rmCode != 0)
        {
            // Untracked target: git rm refuses. Fall back to a filesystem delete.
            try
            {
                if (Directory.Exists(abs)) Directory.Delete(abs, recursive: true);
                else File.Delete(abs);
            }
            catch (Exception ex)
            {
                return new GitCommitResult(false, null, $"Delete failed: {rmErr.Trim()} / {ex.Message}");
            }
            RunGitArgs(root, "add", "-A", "--", rel);
        }

        return CommitPathspecs(root, message, new[] { rel });
    }

    /// <summary>
    /// Commits the staged changes limited to <paramref name="pathspecs"/>,
    /// passing the message on stdin so it survives newlines and quoting. A clean
    /// tree degrades to a soft failure rather than an error.
    /// </summary>
    private static GitCommitResult CommitPathspecs(string root, string message, IReadOnlyCollection<string> pathspecs)
    {
        if (string.IsNullOrWhiteSpace(message)) return new GitCommitResult(false, null, "Commit message is required.");
        var commitArgs = new List<string> { "commit", "-F", "-", "--" };
        commitArgs.AddRange(pathspecs);
        var (_, commitErr, commitCode) = RunGitArgs(root, commitArgs.ToArray(), stdin: message);
        if (commitCode != 0)
        {
            if (commitErr.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                return new GitCommitResult(false, null, "Nothing to commit. Working tree is clean.");
            return new GitCommitResult(false, null, commitErr.Trim());
        }

        var (sha, _, _) = RunGitArgs(root, "rev-parse", "--short", "HEAD");
        return new GitCommitResult(true, sha.Trim(), null);
    }

    private static List<string> NormalizePaths(IEnumerable<string> paths) =>
        paths
            .Select(NormalizeRel)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()!;

    private static string NormalizeRel(string? rel) =>
        string.IsNullOrWhiteSpace(rel) ? "" : rel.Replace('\\', '/').Trim().TrimStart('/');

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
                FileName = GenericCliExecutionService.ResolveExecutable(codePath),
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
        if (_toplevelCache.TryGetValue(path, out var cachedRoot)) return cachedRoot;
        var (output, _, code) = RunGit(path, "rev-parse --show-toplevel");
        if (code != 0) return null;
        var toplevel = output.Trim();
        if (string.IsNullOrEmpty(toplevel)) return null;
        var normalized = toplevel.Replace('/', Path.DirectorySeparatorChar);
        _toplevelCache[path] = normalized;
        return normalized;
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
    /// Like <see cref="RunGit"/> but with <c>GIT_OPTIONAL_LOCKS=0</c>, so the
    /// spawn never takes the optional <c>index.lock</c> or writes the refreshed
    /// index back. Used for the read-only status/diff reads the live-status view
    /// now fans out in parallel (AGT-2007): it guarantees a pure read - no
    /// working-tree mutation even while a run agent is actively editing the same
    /// checkout - and removes any index.lock contention between the concurrent
    /// reads. Git treats the status index-refresh lock as optional, so this only
    /// skips a stat-cache write; the porcelain/numstat output is unchanged.
    /// </summary>
    private static (string Out, string Err, int Code) RunGitReadonly(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";
        return RunGitProcess(psi, null);
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
        => RunGitArgs(cwd, args, stdin, environment: null);

    private static (string Out, string Err, int Code) RunGitArgs(
        string cwd,
        string[] args,
        string? stdin,
        IReadOnlyDictionary<string, string?>? environment)
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
        if (environment != null)
        {
            foreach (var (key, value) in environment)
            {
                if (value == null) psi.Environment.Remove(key);
                else psi.Environment[key] = value;
            }
        }

        return RunGitProcess(psi, stdin);
    }

    private static (string Out, string Err, int Code) RunGitProcess(ProcessStartInfo psi, string? stdin)
    {
        // Time every spawn and record it against the ambient git-info request
        // scope (if any). This is the per-subprocess half of the AGT-2007
        // instrumentation; the scope rollup turns it into "N spawns, X ms".
        var command = CommandLabel(psi);
        var sw = Stopwatch.StartNew();
        var result = RunGitProcessCore(psi, stdin);
        sw.Stop();
        GitProcessTelemetry.Record(command, sw.ElapsedMilliseconds, result.Code);
        return result;
    }

    private static (string Out, string Err, int Code) RunGitProcessCore(ProcessStartInfo psi, string? stdin)
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
                try { p.Kill(true); } catch (Exception __ex) { SilentCatch.Note(__ex, "GitService:2736"); }
                return ("", "git timed out", -1);
            }
            return (so, se, p.ExitCode);
        }
        catch (Exception ex)
        {
            return ("", ex.Message, -1);
        }
    }

    /// <summary>
    /// A short, low-cardinality label for one git spawn - the git subcommand
    /// (e.g. <c>status</c>, <c>merge-base</c>, <c>rev-parse</c>). Deliberately
    /// the first token only: it groups spawns cleanly in the telemetry rollup
    /// and never leaks a branch name, path, or commit message into the logs.
    /// </summary>
    private static string CommandLabel(ProcessStartInfo psi)
    {
        if (psi.ArgumentList.Count > 0)
            return psi.ArgumentList[0];
        var args = psi.Arguments;
        if (string.IsNullOrWhiteSpace(args)) return "git";
        var space = args.IndexOf(' ');
        return space < 0 ? args : args[..space];
    }

    /// <summary>
    /// Runs several independent git invocations concurrently and returns their
    /// results in input order. Each git call is its own OS process, so on
    /// Windows - where a bare spawn already costs ~70-100ms - fanning the
    /// independent reads of a status/provenance request out across the thread
    /// pool collapses a serial sum into a single max. The ambient
    /// <see cref="GitProcessTelemetry"/> scope flows into each task (captured
    /// ExecutionContext), so per-request spawn accounting still adds up. The
    /// callbacks must not throw (the git helpers already swallow their own
    /// failures); a throwing callback surfaces via <see cref="Task.WaitAll"/>.
    /// </summary>
    private static T[] RunGitParallel<T>(params Func<T>[] work)
    {
        if (work.Length == 0) return [];
        if (work.Length == 1) return [work[0]()];
        var tasks = new Task<T>[work.Length];
        for (var i = 0; i < work.Length; i++)
        {
            var w = work[i];
            tasks[i] = Task.Run(w);
        }
        Task.WaitAll(tasks);
        var results = new T[work.Length];
        for (var i = 0; i < work.Length; i++) results[i] = tasks[i].Result;
        return results;
    }

    /// <summary>
    /// The set of commit SHAs reachable from <paramref name="tipRef"/> but not
    /// from <paramref name="baseSha"/> (<c>git rev-list base..tip</c>), as full
    /// SHAs. ONE git call regardless of how many commits are in the range - the
    /// batch replacement for calling <c>merge-base --is-ancestor</c> once per
    /// commit when classifying a task branch's merge-set (AGT-2007). Empty on
    /// any failure or when the ref does not exist, matching the conservative
    /// "unknown -> not contained" behaviour of the per-commit checks it
    /// replaces.
    /// </summary>
    public HashSet<string> GetReachableShaSet(string repoRoot, string baseSha, string tipRef)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return set;
        if (!IsLikelyShaOrRef(baseSha) || !IsLikelyBranchName(tipRef)) return set;
        var (output, _, code) = RunGitArgs(repoRoot, "rev-list", $"{baseSha}..{tipRef}");
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return set;
        foreach (var line in output.Split('\n'))
        {
            var sha = line.Trim();
            if (sha.Length > 0) set.Add(sha);
        }
        return set;
    }

    /// <summary>
    /// The full set of commit SHAs reachable from <paramref name="tipRef"/>
    /// (<c>git rev-list &lt;tip&gt;</c>) - i.e. every ancestor of the ref, tip
    /// included. ONE git call regardless of history length. Membership in this
    /// set is exactly "<c>sha</c> is an ancestor of (or equal to)
    /// <paramref name="tipRef"/>", so the board can answer "is this task's anchor
    /// in develop / main?" for MANY cards with an in-memory lookup instead of a
    /// <c>merge-base --is-ancestor</c> spawn per card - the O(repos) batch that
    /// keeps the board merge signal off the per-card spawn path (AGT-2046, same
    /// spirit as <see cref="GetReachableShaSet"/> / AGT-2007). Empty on any
    /// failure or when the ref does not exist, matching the conservative
    /// "unknown -&gt; not contained" behaviour elsewhere.
    /// </summary>
    public HashSet<string> GetAncestorShaSet(string repoRoot, string tipRef)
        => GetAncestorShaSet(repoRoot, (IReadOnlyCollection<string>)[tipRef]);

    /// <summary>
    /// Union of the commits reachable from several refs in one
    /// <c>git rev-list --ignore-missing</c> process. Missing local/origin mirrors
    /// do not discard the refs that do exist.
    /// </summary>
    public HashSet<string> GetAncestorShaSet(
        string repoRoot,
        IReadOnlyCollection<string> tipRefs)
    {
        TryGetAncestorShaSet(repoRoot, tipRefs, out var set);
        return set;
    }

    internal bool TryGetAncestorShaSet(
        string repoRoot,
        IReadOnlyCollection<string> tipRefs,
        out HashSet<string> set)
    {
        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return false;
        var refs = tipRefs
            .Where(IsLikelyBranchName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (refs.Length == 0) return false;

        var args = new List<string> { "rev-list", "--ignore-missing" };
        args.AddRange(refs);
        var (output, _, code) = RunGitArgs(repoRoot, args.ToArray());
        if (code != 0) return false;
        if (string.IsNullOrWhiteSpace(output)) return true;
        foreach (var line in output.Split('\n'))
        {
            var sha = line.Trim();
            if (sha.Length > 0) set.Add(sha);
        }
        return true;
    }

    /// <summary>
    /// Commit parent graph reachable from all supplied refs in one git process.
    /// Consumers can derive ancestry and edge distance for many ref pairs in
    /// memory instead of spawning merge-base and rev-list once per card.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetCommitParentGraph(
        string repoRoot,
        IReadOnlyCollection<string> tipRefs)
    {
        var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return graph;
        var refs = tipRefs.Where(IsLikelyBranchName).Distinct(StringComparer.Ordinal).ToArray();
        if (refs.Length == 0) return graph;
        var args = new List<string> { "rev-list", "--parents", "--ignore-missing" };
        args.AddRange(refs);
        var (output, _, code) = RunGitArgs(repoRoot, args.ToArray());
        if (code != 0) return graph;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) graph[parts[0]] = parts.Skip(1).ToArray();
        }
        return graph;
    }

    /// <summary>
    /// AGT-2202 - the curated-merge map for an integration ref: task KEY -&gt; the
    /// SHA of the <c>merge(&lt;KEY&gt;)</c> / <c>merge-recut(&lt;KEY&gt;)</c> commit that
    /// folded that task into develop. ONE bounded <c>git log --grep</c> spawn per
    /// repo (the grep pre-filters to only the curated integrator merges, so the
    /// output is O(accepted tasks), not O(history)). This is the authoritative
    /// integration signal that anchor-ancestry cannot see: the async curated
    /// integrator rewrites commits, so a task's own SHAs are frequently NOT
    /// ancestors of develop even though its work landed under a curated merge
    /// commit. Read-only; empty on any failure. When several merges reference the
    /// same key the newest (first in <c>git log</c> order) wins.
    /// </summary>
    public Dictionary<string, string> GetIntegrationMergeShaByKey(string repoRoot, string integrationRef)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return map;
        if (!IsLikelyBranchName(integrationRef)) return map;

        // Extended-regexp grep: match a subject that starts with merge( or
        // merge-recut( followed by a task key. --grep is ORed across the two
        // patterns; -E makes the alternation / groups work.
        var (output, _, code) = RunGitArgs(
            repoRoot,
            "log",
            "--no-color",
            "-E",
            "--grep=^merge(-recut)?\\(",
            "--format=%H%x1f%s",
            integrationRef);
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return map;

        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var sep = line.IndexOf('\x1f');
            if (sep <= 0) continue;
            var sha = line[..sep].Trim();
            var subject = line[(sep + 1)..];
            var key = ParseIntegrationMergeKey(subject);
            if (sha.Length == 0 || key == null) continue;
            // git log is newest-first; keep the first (newest) merge per key.
            map.TryAdd(key, sha);
        }
        return map;
    }

    /// <summary>
    /// All curated integration commits reachable from the supplied test-run
    /// revisions, grouped by task key. The committer timestamp is part of the
    /// result because a key alone is not revision identity: consumers must
    /// reject integrations older than the card's current attributed commit.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<GitIntegrationMerge>> GetIntegrationMergesByKey(
        string repoRoot,
        IReadOnlyCollection<string> tipRefs)
    {
        var map = new Dictionary<string, List<GitIntegrationMerge>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return
            new Dictionary<string, IReadOnlyList<GitIntegrationMerge>>(StringComparer.OrdinalIgnoreCase);
        var refs = tipRefs.Where(IsLikelyBranchName).Distinct(StringComparer.Ordinal).ToArray();
        if (refs.Length == 0) return
            new Dictionary<string, IReadOnlyList<GitIntegrationMerge>>(StringComparer.OrdinalIgnoreCase);

        var args = new List<string>
        {
            "log",
            "--no-color",
            "-E",
            "--grep=^merge(-recut)?\\(",
            "--format=%H%x1f%cI%x1f%s",
        };
        args.AddRange(refs);
        var (output, _, code) = RunGitArgs(repoRoot, args.ToArray());
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return
            new Dictionary<string, IReadOnlyList<GitIntegrationMerge>>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\x1f', 3);
            if (parts.Length != 3) continue;
            var sha = parts[0].Trim();
            if (sha.Length == 0
                || !DateTime.TryParse(
                    parts[1],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                        | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var committedAtUtc)
                || ParseIntegrationMergeKey(parts[2]) is not { } key)
            {
                continue;
            }
            if (!map.TryGetValue(key, out var commits))
            {
                commits = [];
                map[key] = commits;
            }
            if (!commits.Any(commit => string.Equals(commit.Sha, sha, StringComparison.OrdinalIgnoreCase)))
                commits.Add(new GitIntegrationMerge(sha, committedAtUtc));
        }

        return map.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<GitIntegrationMerge>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Curated publisher history on one integration ref, newest first. Unlike
    /// the key-to-SHA lookup this retains subject, committer identity, and
    /// commit timestamp for the operator-facing integration feed.
    /// </summary>
    public List<GitIntegrationMergeCommit> GetIntegrationMergeCommits(string repoRoot, string integrationRef)
    {
        var result = new List<GitIntegrationMergeCommit>();
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return result;
        if (!IsLikelyBranchName(integrationRef)) return result;

        const char US = '\x1f';
        var (output, _, code) = RunGitArgs(
            repoRoot,
            "log",
            "--no-color",
            "-E",
            "--grep=^merge(-recut)?\\(",
            "--format=%H%x1f%h%x1f%cI%x1f%cN%x1f%s",
            integrationRef);
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return result;

        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(US);
            if (parts.Length < 5) continue;
            var key = ParseIntegrationMergeKey(parts[4]);
            if (key == null) continue;
            if (!DateTime.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var at)) continue;
            result.Add(new GitIntegrationMergeCommit(
                key, parts[0], parts[1], DateTime.SpecifyKind(at, DateTimeKind.Utc), parts[3], parts[4]));
        }
        return result;
    }

    /// <summary>
    /// Extracts the task KEY from a curated integrator merge subject:
    /// <c>merge(AGT-2202): ...</c> or <c>merge-recut(AGT-2202): ...</c> -&gt;
    /// <c>AGT-2202</c>. Returns null when the subject is not a curated merge.
    /// Case-insensitive on the <c>merge</c> prefix; the key is upper-cased so it
    /// matches <see cref="AgentStudio.Shared.TaskInfo.Key"/>.
    /// </summary>
    internal static string? ParseIntegrationMergeKey(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var m = IntegrationMergeSubjectRegex.Match(subject.Trim());
        return m.Success ? m.Groups["key"].Value.ToUpperInvariant() : null;
    }

    private static readonly System.Text.RegularExpressions.Regex IntegrationMergeSubjectRegex =
        new(@"^merge(?:-recut)?\((?<key>[A-Za-z][A-Za-z0-9]*-\d+)\)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled);

    // ----- PUB-1: publish-target derivation primitives (read-only) -----

    /// <summary>
    /// The newest version tag (<c>v*</c>) in the repository, by descending
    /// semantic-version order, or null when the package has never been tagged
    /// (the "first publish pending" case). One <c>git tag</c> spawn. Returns the
    /// raw tag name including the leading <c>v</c> (e.g. <c>v0.3.1</c>); callers
    /// strip the prefix for display. Read-only; empty/failure yields null.
    /// </summary>
    public string? GetLatestVersionTag(string repoRoot)
    {
        TryGetLatestVersionTag(repoRoot, out var tag);
        return tag;
    }

    internal bool TryGetLatestVersionTag(string repoRoot, out string? tag)
    {
        tag = null;
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return false;
        var (output, _, code) = RunGitArgs(repoRoot, "tag", "--list", "v[0-9]*", "--sort=-v:refname");
        if (code != 0) return false;
        if (string.IsNullOrWhiteSpace(output)) return true;
        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            var candidate = line.Trim();
            if (candidate.Length > 0)
            {
                tag = candidate;
                return true;
            }
        }
        return true;
    }

    /// <summary>
    /// UTC author date of a ref's tip commit (e.g. a Pages deploy branch tip),
    /// or null when the ref does not resolve. PUB-1 treats a <c>gh-pages</c> tip
    /// date as the "last website deploy" instant when no release tag anchors the
    /// website target. Read-only; one spawn.
    /// </summary>
    public DateTime? GetTipCommitDateUtc(string repoRoot, string tipRef)
    {
        TryGetTipCommitDateUtc(repoRoot, tipRef, out var value);
        return value;
    }

    internal bool TryGetTipCommitDateUtc(
        string repoRoot,
        string tipRef,
        out DateTime? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return false;
        if (!IsLikelyBranchName(tipRef)) return false;
        var (output, _, code) = RunGitArgs(
            repoRoot,
            "log",
            "--ignore-missing",
            "-1",
            "--pretty=format:%aI",
            tipRef);
        if (code != 0) return false;
        if (string.IsNullOrWhiteSpace(output)) return true;
        if (DateTime.TryParse(output.Trim(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var ts))
        {
            value = DateTime.SpecifyKind(ts, DateTimeKind.Utc);
            return true;
        }
        return false;
    }

    /// <summary>
    /// First-parent (mainline) commits on <paramref name="branch"/> that touch a
    /// publish target's path scope, newest first. Optionally bounded below by
    /// <paramref name="sinceRef"/> (a tag or SHA: <c>sinceRef..branch</c>) and/or
    /// <paramref name="sinceDateIso"/> (git <c>--since</c>). <c>--first-parent</c>
    /// collapses each merged task branch to its single mainline commit, so the
    /// result is "how many integrations touched this target since the reference",
    /// not raw per-commit churn - the merge commit's SHA is exactly the anchor the
    /// board records for that task, so a caller can answer "is task X publishable
    /// to this target?" by set-membership without any per-task git spawn. Scope is
    /// expressed as git pathspecs: <paramref name="includePrefixes"/> (empty =
    /// whole tree) and <paramref name="excludePrefixes"/> (git <c>:(exclude)</c>
    /// magic). Ref and path arguments are validated so a crafted value yields an
    /// empty list rather than a smuggled flag. Read-only; one spawn.
    /// </summary>
    public List<GitCommitInfo> GetMainlineCommitsForScope(
        string repoRoot,
        string branch,
        IReadOnlyList<string> includePrefixes,
        IReadOnlyList<string> excludePrefixes,
        string? sinceRef = null,
        string? sinceDateIso = null)
    {
        TryGetMainlineCommitsForScope(
            repoRoot,
            branch,
            includePrefixes,
            excludePrefixes,
            out var commits,
            sinceRef,
            sinceDateIso);
        return commits;
    }

    internal bool TryGetMainlineCommitsForScope(
        string repoRoot,
        string branch,
        IReadOnlyList<string> includePrefixes,
        IReadOnlyList<string> excludePrefixes,
        out List<GitCommitInfo> commits,
        string? sinceRef = null,
        string? sinceDateIso = null)
    {
        commits = [];
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return false;
        if (!IsLikelyBranchName(branch)) return false;
        if (!string.IsNullOrWhiteSpace(sinceRef) && !IsLikelyShaOrRef(sinceRef!)) return false;

        const string fmt = "%H%x1f%h%x1f%aI%x1f%aN%x1f%s";
        var args = new List<string> { "log", "--first-parent", "--shortstat", $"--pretty=format:{fmt}" };
        if (!string.IsNullOrWhiteSpace(sinceDateIso)) args.Add($"--since={sinceDateIso}");
        args.Add(string.IsNullOrWhiteSpace(sinceRef) ? branch : $"{sinceRef}..{branch}");

        args.Add("--");
        var addedInclude = false;
        foreach (var inc in includePrefixes)
        {
            var p = NormalizePathspec(inc);
            if (p == null) continue;
            args.Add(p);
            addedInclude = true;
        }
        if (!addedInclude) args.Add("."); // whole tree
        foreach (var exc in excludePrefixes)
        {
            var p = NormalizePathspec(exc);
            if (p == null) continue;
            args.Add($":(exclude){p}");
        }

        var (output, _, code) = RunGitArgs(repoRoot, args.ToArray());
        if (code != 0) return false;
        if (string.IsNullOrWhiteSpace(output)) return true;
        commits = ParseLogBlocks(output);
        return true;
    }

    /// <summary>
    /// Validates and normalises a repo-relative path prefix used as a git
    /// pathspec: forward slashes, no leading slash, no <c>..</c> traversal, no
    /// pathspec-magic leader (<c>:</c>). Returns null for an unusable value so it
    /// is dropped rather than passed to git. Internal for unit testing.
    /// </summary>
    internal static string? NormalizePathspec(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return null;
        var p = prefix.Replace('\\', '/').Trim();
        if (p.Length == 0) return null;
        if (p[0] == '/' || p[0] == ':') return null;
        if (p.Contains("..", StringComparison.Ordinal)) return null;
        return p;
    }

}
