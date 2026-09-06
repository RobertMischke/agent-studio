using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AgentStudio.Pipeline;

/// <summary>
/// Commits the job-folder evidence in the workspace repository at an
/// orchestrator run boundary. This is deliberately separate from
/// <see cref="GitService"/>: source-code commits happen in the watched project
/// repository, while these commits snapshot task artifacts in TaskRepository.
/// </summary>
public sealed class WorkspaceArtifactCommitService
{
    private const string CommitterName = "agent-orchestrator";
    private const string CommitterEmail = "agent-orchestrator@local";

    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkspaceArtifactCommitService> _logger;
    private readonly WorkspaceArtifactPushQueue? _pushQueue;
    private readonly int _indexLockRetryAttempts;
    private readonly int _indexLockRetryBackoffMs;
    private readonly long _maxStagedFileBytes;
    private readonly TimeSpan _maintenanceTimeout;
    private static readonly string[] RuntimeStateGlobs =
    {
        "logs/bus/**",
        ".metadata/attempt-authority*",
    };

    // Per-repo serialization gate. The punctual job-folder commits and the
    // debounced Transition-Committer's evidence batches both mutate the same
    // workspace repo's index; without a shared lock two `git commit` calls can
    // collide on `.git/index.lock`. Keyed by resolved git root so distinct
    // repos never contend. Static so the single DI singleton and any test
    // instance pointed at the same repo share the gate.
    private static readonly ConcurrentDictionary<string, object> RepoGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static object RepoGate(string gitRoot) =>
        RepoGates.GetOrAdd(gitRoot, _ => new object());

    public WorkspaceArtifactCommitService(
        IConfiguration configuration,
        ILogger<WorkspaceArtifactCommitService> logger,
        WorkspaceArtifactPushQueue? pushQueue = null)
    {
        _configuration = configuration;
        _logger = logger;
        _pushQueue = pushQueue;
        _indexLockRetryAttempts = Math.Clamp(
            configuration.GetValue<int?>("WorkspaceEvidence:IndexLockRetryAttempts") ?? 5, 1, 50);
        _indexLockRetryBackoffMs = Math.Clamp(
            configuration.GetValue<int?>("WorkspaceEvidence:IndexLockRetryBackoffMs") ?? 100, 0, 5000);
        _maxStagedFileBytes = Math.Clamp(
            configuration.GetValue<long?>("WorkspaceEvidence:MaxStagedFileBytes") ?? 50L * 1024 * 1024,
            1L * 1024 * 1024,
            95L * 1024 * 1024);
        _maintenanceTimeout = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue<int?>("WorkspaceRepositoryMaintenance:CommandTimeoutSeconds") ?? 600,
            30, 3600));
    }

    public WorkspaceArtifactCommitResult TryCommitRunBoundary(
        string? workspaceRoot,
        string jobId,
        string? beforeMoveFolderPath,
        string? afterMoveFolderPath,
        ReviewDecisionKind verdict)
        => TryCommitJobFolder(
            workspaceRoot,
            jobId,
            beforeMoveFolderPath,
            afterMoveFolderPath,
            logLabel: $"verdict={NormalizeVerdict(verdict)}",
            failLabel: verdict.ToString(),
            planMessage: (gitRoot, pathspecs, afterFolder) =>
            {
                var runIndex = ResolveRunIndex(gitRoot, pathspecs, afterFolder);
                var steps = ResolveStepsTrailer(afterFolder);
                return new ArtifactCommitPlan(BuildCommitMessage(jobId, runIndex, verdict, steps), runIndex, steps);
            });

    /// <summary>
    /// Commits the job-folder evidence at an out-of-band completion boundary
    /// (<c>docs/concepts/out-of-band-task-completion.md</c> §3). Shares the
    /// add/diff/commit plumbing with <see cref="TryCommitRunBoundary"/>; only
    /// the commit message differs, recording the external source instead of an
    /// orchestrator verdict + run index.
    /// </summary>
    public WorkspaceArtifactCommitResult TryCommitExternalCompletion(
        string? workspaceRoot,
        string jobId,
        string? beforeMoveFolderPath,
        string? afterMoveFolderPath,
        string source)
    {
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "external" : source.Trim();
        return TryCommitJobFolder(
            workspaceRoot,
            jobId,
            beforeMoveFolderPath,
            afterMoveFolderPath,
            logLabel: $"external-completion source={normalizedSource}",
            failLabel: "external-completion",
            planMessage: (_, _, _) =>
                new ArtifactCommitPlan(BuildExternalCompletionMessage(jobId, normalizedSource), 0, null));
    }

    public WorkspaceArtifactCommitResult TryCommitArtifactUpload(
        string? workspaceRoot,
        string jobId,
        string jobFolderPath,
        IReadOnlyList<string> files)
        => TryCommitJobFolder(
            workspaceRoot,
            jobId,
            beforeMoveFolderPath: null,
            afterMoveFolderPath: jobFolderPath,
            logLabel: $"artifact-upload files={files.Count}",
            failLabel: "artifact-upload",
            planMessage: (_, _, _) =>
                new ArtifactCommitPlan(BuildArtifactUploadMessage(jobId, files), 0, null));

    /// <summary>
    /// Shared add/diff/commit core for the workspace evidence commits. The
    /// caller supplies the commit message (and the run-index/steps it wants
    /// echoed in the result) via <paramref name="planMessage"/>, which runs only
    /// after the tree is confirmed dirty, so the message builders never pay for
    /// a no-op commit.
    /// </summary>
    private WorkspaceArtifactCommitResult TryCommitJobFolder(
        string? workspaceRoot,
        string jobId,
        string? beforeMoveFolderPath,
        string? afterMoveFolderPath,
        string logLabel,
        string failLabel,
        Func<string, IReadOnlyList<string>, string, ArtifactCommitPlan> planMessage)
    {
        try
        {
            workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
                ? _configuration["TaskRepository"]
                : workspaceRoot;
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
                return WorkspaceArtifactCommitResult.Skipped("workspace-missing");

            var gitRoot = ResolveGitRoot(workspaceRoot);
            if (gitRoot == null)
                return WorkspaceArtifactCommitResult.Skipped("not-a-git-repo");

            var pathspecs = BuildPathspecs(gitRoot, beforeMoveFolderPath, afterMoveFolderPath);
            if (pathspecs.Count == 0)
                return WorkspaceArtifactCommitResult.Skipped("job-folder-outside-workspace");

            string? shortSha;
            ArtifactCommitPlan plan;
            // Serialize with the Transition-Committer's evidence batches (and
            // any concurrent job-folder commit) on this repo, with an
            // index.lock retry so a lost race with an external git process is
            // recovered rather than surfaced as a failure.
            lock (RepoGate(gitRoot))
            {
                var candidates = FindEligibleChanges(gitRoot, pathspecs, [], trackedOnly: false);
                if (candidates.Count == 0)
                    return WorkspaceArtifactCommitResult.Skipped("nothing-to-commit");
                var add = AddCandidates(gitRoot, candidates);
                if (add.Code != 0)
                    return WorkspaceArtifactCommitResult.Failed("git-add", add.ErrorText);

                var changed = RunGit(gitRoot, ["diff", "--cached", "--quiet", "--", .. candidates]);
                if (changed.Code == 0)
                    return WorkspaceArtifactCommitResult.Skipped("nothing-to-commit");
                if (changed.Code != 1)
                    return WorkspaceArtifactCommitResult.Failed("git-diff-cached", changed.ErrorText);

                var afterFolder = !string.IsNullOrWhiteSpace(afterMoveFolderPath)
                    ? afterMoveFolderPath!
                    : beforeMoveFolderPath ?? string.Empty;
                plan = planMessage(gitRoot, pathspecs, afterFolder);

                var commitArgs = new List<string>
                {
                    "-c", $"user.name={CommitterName}",
                    "-c", $"user.email={CommitterEmail}",
                    "commit", "-F", "-", "--"
                };
                commitArgs.AddRange(candidates);
                var commit = RunGitRetryingIndexLock(gitRoot, commitArgs, plan.Message);
                if (commit.Code != 0)
                {
                    if (commit.ErrorText.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                        return WorkspaceArtifactCommitResult.Skipped("nothing-to-commit");
                    return WorkspaceArtifactCommitResult.Failed("git-commit", commit.ErrorText);
                }

                var sha = RunGit(gitRoot, ["rev-parse", "--short", "HEAD"]);
                shortSha = sha.Code == 0 ? sha.Out.Trim() : null;
            }
            _logger.LogInformation(
                "workspace-artifact-commit jobId={JobId} {LogLabel} runIndex={RunIndex} sha={Sha} paths={Paths}",
                jobId, logLabel, plan.RunIndex, shortSha ?? "", string.Join(",", pathspecs));
            if (WorkspaceAutoPushEnabled() && _pushQueue != null)
            {
                var enqueued = _pushQueue.Enqueue(new WorkspaceArtifactPushRequest(gitRoot, jobId));
                if (!enqueued)
                    _logger.LogWarning("workspace-artifact-push enqueue failed jobId={JobId} repo={Repo}", jobId, gitRoot);
            }
            return WorkspaceArtifactCommitResult.Committed(shortSha, plan.RunIndex, plan.Steps ?? "none");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "workspace-artifact-commit failed for {JobId} ({Label})",
                jobId, failLabel);
            return WorkspaceArtifactCommitResult.Failed("exception", ex.Message);
        }
    }

    internal static string BuildExternalCompletionMessage(string jobId, string source)
    {
        var normalizedJob = string.IsNullOrWhiteSpace(jobId) ? "job" : jobId.Trim();
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "external" : source.Trim();
        return
            $"chore(workspace): record external completion for {normalizedJob}\n\n" +
            $"Completed-Externally-By: {normalizedSource}\n";
    }

    private bool WorkspaceAutoPushEnabled() =>
        _configuration.GetValue<bool?>("WorkspaceArtifacts:AutoPushEnabled") ?? true;

    internal static string BuildArtifactUploadMessage(string jobId, IReadOnlyList<string> files)
    {
        var normalizedJob = string.IsNullOrWhiteSpace(jobId) ? "job" : jobId.Trim();
        var normalizedFiles = files.Count == 0
            ? "none"
            : string.Join(",", files.Select(f => string.IsNullOrWhiteSpace(f) ? "unknown" : f.Trim()));
        return
            $"chore(workspace): record uploaded artifacts for {normalizedJob}\n\n" +
            $"Artifact-Upload-Files: {normalizedFiles}\n";
    }

    /// <summary>Message plan produced once the tree is known dirty: the commit body plus the run-index/steps echoed in the result.</summary>
    private sealed record ArtifactCommitPlan(string Message, int RunIndex, string? Steps);

    internal static string BuildCommitMessage(
        string jobId,
        int runIndex,
        ReviewDecisionKind verdict,
        string steps)
    {
        var normalizedJob = string.IsNullOrWhiteSpace(jobId) ? "job" : jobId.Trim();
        var normalizedSteps = string.IsNullOrWhiteSpace(steps) ? "none" : steps.Trim();
        return
            $"chore(workspace): record run artifacts for {normalizedJob}\n\n" +
            $"Run-Index: {runIndex.ToString(CultureInfo.InvariantCulture)}\n" +
            $"Verdict: {NormalizeVerdict(verdict)}\n" +
            $"Steps: {normalizedSteps}\n";
    }

    internal static string ResolveStepsTrailer(string? jobFolderPath)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath)) return "none";
        var path = Path.Combine(jobFolderPath, "pipeline-execution.json");
        if (!File.Exists(path)) return "none";

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("steps", out var stepsEl)
                || stepsEl.ValueKind != JsonValueKind.Array)
            {
                return "none";
            }

            var steps = new List<string>();
            foreach (var step in stepsEl.EnumerateArray())
            {
                var id = GetString(step, "stepId");
                if (string.IsNullOrWhiteSpace(id)) continue;

                var verdict = GetString(step, "verdict");
                var status = ResolveTerminalStatusToken(step);
                var value = string.IsNullOrWhiteSpace(verdict) ? status : verdict;
                if (string.IsNullOrWhiteSpace(value)) continue;
                steps.Add($"{id}={NormalizeToken(value)}");
            }

            return steps.Count == 0 ? "none" : string.Join(",", steps);
        }
        catch
        {
            return "unreadable";
        }
    }

    internal static int ResolveRunIndexFromSessionEvents(string? jobFolderPath)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath)) return 0;
        var path = Path.Combine(jobFolderPath, "logs", "session-events.jsonl");
        if (!File.Exists(path)) return 0;

        var count = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object) count++;
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "WorkspaceArtifactCommitService: Tolerate torn JSONL lines like TaskSessionLog does.");
                // Tolerate torn JSONL lines like TaskSessionLog does.
            }
        }
        return count;
    }

    internal static int ResolveRunIndexFromPipelineAttempt(string? jobFolderPath)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath)) return 0;
        var path = Path.Combine(jobFolderPath, "pipeline-execution.json");
        if (!File.Exists(path)) return 0;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("attempt", out var attemptEl)
                && attemptEl.ValueKind == JsonValueKind.Number
                && attemptEl.TryGetInt32(out var attempt)
                && attempt > 0)
            {
                return attempt;
            }

            if (doc.RootElement.TryGetProperty("previousAttempts", out var previousEl)
                && previousEl.ValueKind == JsonValueKind.Array)
            {
                return previousEl.GetArrayLength() + 1;
            }
        }
        catch
        {
            return 0;
        }

        return 0;
    }

    private int ResolveRunIndex(string gitRoot, IReadOnlyList<string> pathspecs, string afterFolder)
    {
        var fromPipeline = ResolveRunIndexFromPipelineAttempt(afterFolder);
        if (fromPipeline > 0) return fromPipeline;

        var fromEvents = ResolveRunIndexFromSessionEvents(afterFolder);
        if (fromEvents > 0) return fromEvents;

        var logArgs = new List<string> { "log", "--format=%B%x00", "--" };
        logArgs.AddRange(pathspecs);
        var log = RunGit(gitRoot, logArgs);
        if (log.Code != 0 || string.IsNullOrWhiteSpace(log.Out)) return 1;

        var max = 0;
        foreach (var raw in log.Out.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!line.StartsWith("Run-Index:", StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(line["Run-Index:".Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    max = Math.Max(max, n);
            }
        }
        return max + 1;
    }

    /// <summary>
    /// Resolves the git top-level for a workspace/watch path, falling back to
    /// the configured <c>TaskRepository</c> when none is supplied. Used by the
    /// Transition-Committer to bucket enqueued transitions by repo without a
    /// second copy of the git-root plumbing.
    /// </summary>
    public string? ResolveWorkspaceGitRoot(string? workspaceRoot)
    {
        var root = string.IsNullOrWhiteSpace(workspaceRoot)
            ? _configuration["TaskRepository"]
            : workspaceRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;
        return ResolveGitRoot(root);
    }

    /// <summary>
    /// Debounced evidence commit for the Transition-Committer: stages the data
    /// paths of the workspace repo touched by a batch of lane transitions
    /// (<paramref name="watchPaths"/>, each scoped to its relative folder under
    /// the git root) minus <paramref name="excludeGlobs"/>, and commits them
    /// with <paramref name="message"/>. Shares the committer identity, git
    /// plumbing, per-repo lock and index.lock retry with the punctual
    /// job-folder commits above; there is no parallel implementation. Push is left to
    /// the caller (the batcher enqueues onto the existing push queue when the
    /// <c>WorkspaceEvidence:Push</c> switch is on), so this method never
    /// touches the network. Never throws: every failure is returned as a
    /// <see cref="WorkspaceArtifactCommitResult.Failed"/> so the caller and
    /// ultimately the transition that produced the evidence are never broken.
    /// </summary>
    public WorkspaceArtifactCommitResult TryCommitEvidence(
        string gitRoot,
        IReadOnlyList<string> watchPaths,
        IReadOnlyList<string> excludeGlobs,
        string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gitRoot) || !Directory.Exists(gitRoot))
                return WorkspaceArtifactCommitResult.Skipped("workspace-missing");

            var pathspecs = new List<string>();
            foreach (var wp in watchPaths)
                AddPathspec(pathspecs, gitRoot, wp);
            pathspecs = pathspecs
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (pathspecs.Count == 0)
                return WorkspaceArtifactCommitResult.Skipped("no-data-paths");

            // Enumerate exact candidate files before `git add`. The friendly
            // workspace globs are matched in-process because Git's no-slash
            // exclude pathspec rules can unexpectedly exclude the whole tree.
            string? shortSha;
            lock (RepoGate(gitRoot))
            {
                var candidates = FindEligibleChanges(
                    gitRoot, pathspecs, excludeGlobs, trackedOnly: false);
                if (candidates.Count == 0)
                    return WorkspaceArtifactCommitResult.Skipped("nothing-to-commit");
                var add = AddCandidates(gitRoot, candidates);
                if (add.Code != 0)
                    return WorkspaceArtifactCommitResult.Failed("git-add", add.ErrorText);

                var diffArgs = new List<string> { "diff", "--cached", "--quiet", "--" };
                diffArgs.AddRange(candidates);
                var changed = RunGit(gitRoot, diffArgs);
                if (changed.Code == 0)
                    return WorkspaceArtifactCommitResult.Skipped("nothing-to-commit");
                if (changed.Code != 1)
                    return WorkspaceArtifactCommitResult.Failed("git-diff-cached", changed.ErrorText);

                var commitArgs = new List<string>
                {
                    "-c", $"user.name={CommitterName}",
                    "-c", $"user.email={CommitterEmail}",
                    "commit", "-F", "-", "--"
                };
                commitArgs.AddRange(candidates);
                var commit = RunGitRetryingIndexLock(gitRoot, commitArgs, message);
                if (commit.Code != 0)
                {
                    if (commit.ErrorText.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                        return WorkspaceArtifactCommitResult.Skipped("nothing-to-commit");
                    return WorkspaceArtifactCommitResult.Failed("git-commit", commit.ErrorText);
                }

                var sha = RunGit(gitRoot, ["rev-parse", "--short", "HEAD"]);
                shortSha = sha.Code == 0 ? sha.Out.Trim() : null;
            }

            _logger.LogInformation(
                "workspace-evidence-commit gitRoot={Root} sha={Sha} paths={Paths}",
                gitRoot, shortSha ?? "", string.Join(",", pathspecs));
            return WorkspaceArtifactCommitResult.Committed(shortSha, 0, "evidence");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "workspace-evidence-commit failed for {Root}", gitRoot);
            return WorkspaceArtifactCommitResult.Failed("exception", ex.Message);
        }
    }

    /// <summary>
    /// Commits tracked workspace files left dirty by writers that do not have a
    /// punctual commit boundary. Runtime-only state and oversized files are
    /// excluded. The lifecycle worker invokes this at most once per hour.
    /// </summary>
    public WorkspaceArtifactCommitResult TryCommitTrackedSweep(string? workspaceRoot)
    {
        try
        {
            var gitRoot = ResolveWorkspaceGitRoot(workspaceRoot);
            if (gitRoot == null)
                return WorkspaceArtifactCommitResult.Skipped("not-a-git-repo");

            string? shortSha;
            lock (RepoGate(gitRoot))
            {
                var candidates = FindEligibleChanges(
                    gitRoot, ["."], RuntimeStateGlobs, trackedOnly: true);
                if (candidates.Count == 0)
                    return WorkspaceArtifactCommitResult.Skipped("nothing-to-commit");

                var add = AddCandidates(gitRoot, candidates);
                if (add.Code != 0)
                    return WorkspaceArtifactCommitResult.Failed("git-add", add.ErrorText);

                var commitArgs = new List<string>
                {
                    "-c", $"user.name={CommitterName}",
                    "-c", $"user.email={CommitterEmail}",
                    "commit", "-m", "chore(workspace): sweep tracked workspace drift", "--"
                };
                commitArgs.AddRange(candidates);
                var commit = RunGitRetryingIndexLock(gitRoot, commitArgs);
                if (commit.Code != 0)
                    return WorkspaceArtifactCommitResult.Failed("git-commit", commit.ErrorText);

                var sha = RunGit(gitRoot, ["rev-parse", "--short", "HEAD"]);
                shortSha = sha.Code == 0 ? sha.Out.Trim() : null;
            }

            EnqueueSweepPush(gitRoot);
            _logger.LogInformation(
                "workspace-artifact-sweep-commit repo={Repo} sha={Sha}", gitRoot, shortSha ?? "");
            return WorkspaceArtifactCommitResult.Committed(shortSha, 0, "tracked-sweep");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "workspace-artifact-sweep failed");
            return WorkspaceArtifactCommitResult.Failed("exception", ex.Message);
        }
    }

    /// <summary>
    /// Persists the runtime-state classification in the workspace repository:
    /// bus logs and all attempt-authority snapshots remain on disk but are
    /// ignored and removed from Git tracking.
    /// </summary>
    public WorkspaceArtifactCommitResult TryApplyRuntimeStatePolicy(string? workspaceRoot)
    {
        try
        {
            var gitRoot = ResolveWorkspaceGitRoot(workspaceRoot);
            if (gitRoot == null)
                return WorkspaceArtifactCommitResult.Skipped("not-a-git-repo");

            string? shortSha;
            lock (RepoGate(gitRoot))
            {
                var ignorePath = Path.Combine(gitRoot, ".gitignore");
                var existing = File.Exists(ignorePath) ? File.ReadAllText(ignorePath) : string.Empty;
                var required = new[] { "/logs/bus/", "/.metadata/attempt-authority*" };
                var missing = required.Where(rule => !existing.Split('\n')
                    .Any(line => string.Equals(line.Trim(), rule, StringComparison.Ordinal))).ToList();
                if (missing.Count > 0)
                {
                    var separator = existing.Length == 0 || existing.EndsWith('\n') ? string.Empty : Environment.NewLine;
                    File.AppendAllText(ignorePath, separator + string.Join(Environment.NewLine, missing) + Environment.NewLine);
                }

                // The task repository is platform-owned. Clearing its index is
                // safe under the shared gate and prevents unrelated staged
                // paths from leaking into this policy commit.
                var reset = RunGitRetryingIndexLock(gitRoot, ["reset", "-q"]);
                if (reset.Code != 0)
                    return WorkspaceArtifactCommitResult.Failed("git-reset", reset.ErrorText);
                var addIgnore = RunGitRetryingIndexLock(gitRoot, ["add", "--", ".gitignore"]);
                if (addIgnore.Code != 0)
                    return WorkspaceArtifactCommitResult.Failed("git-add-ignore", addIgnore.ErrorText);
                var untrack = RunGitRetryingIndexLock(gitRoot,
                    ["rm", "-q", "-r", "--cached", "--ignore-unmatch", "--", "logs/bus", ".metadata/attempt-authority*"]);
                if (untrack.Code != 0)
                    return WorkspaceArtifactCommitResult.Failed("git-untrack-runtime", untrack.ErrorText);

                var changed = RunGit(gitRoot, ["diff", "--cached", "--quiet"]);
                if (changed.Code == 0)
                    return WorkspaceArtifactCommitResult.Skipped("policy-current");
                if (changed.Code != 1)
                    return WorkspaceArtifactCommitResult.Failed("git-diff-cached", changed.ErrorText);

                var commit = RunGitRetryingIndexLock(gitRoot,
                    ["-c", $"user.name={CommitterName}", "-c", $"user.email={CommitterEmail}",
                     "commit", "-m", "chore(workspace): keep runtime state out of git"]);
                if (commit.Code != 0)
                    return WorkspaceArtifactCommitResult.Failed("git-commit", commit.ErrorText);
                var sha = RunGit(gitRoot, ["rev-parse", "--short", "HEAD"]);
                shortSha = sha.Code == 0 ? sha.Out.Trim() : null;
            }

            EnqueueSweepPush(gitRoot);
            _logger.LogInformation(
                "workspace-runtime-state-policy-committed repo={Repo} sha={Sha}", gitRoot, shortSha ?? "");
            return WorkspaceArtifactCommitResult.Committed(shortSha, 0, "runtime-policy");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "workspace-runtime-state-policy failed");
            return WorkspaceArtifactCommitResult.Failed("exception", ex.Message);
        }
    }

    internal WorkspaceArtifactCommitResult TryRunMaintenance(
        string? workspaceRoot,
        Action<string, IReadOnlyList<string>>? observedCommand = null)
    {
        try
        {
            var gitRoot = ResolveWorkspaceGitRoot(workspaceRoot);
            if (gitRoot == null)
                return WorkspaceArtifactCommitResult.Skipped("not-a-git-repo");
            lock (RepoGate(gitRoot))
            {
                IReadOnlyList<IReadOnlyList<string>> commands =
                [
                    ["config", "--local", "gc.auto", "2000"],
                    ["config", "--local", "maintenance.strategy", "incremental"],
                    ["config", "--local", "core.fsmonitor", OperatingSystem.IsWindows() ? "true" : "false"],
                    ["repack", "-d", "-l"],
                    ["maintenance", "run", "--task=commit-graph", "--task=incremental-repack"],
                    ["prune-packed"],
                ];
                foreach (var command in commands)
                {
                    observedCommand?.Invoke(gitRoot, command);
                    var result = RunGitBounded(gitRoot, command, _maintenanceTimeout);
                    if (result.Code != 0)
                        return WorkspaceArtifactCommitResult.Failed("git-maintenance", result.ErrorText);
                }
            }
            return WorkspaceArtifactCommitResult.Skipped("maintenance-complete");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "workspace-repository-maintenance failed");
            return WorkspaceArtifactCommitResult.Failed("exception", ex.Message);
        }
    }

    private void EnqueueSweepPush(string gitRoot)
    {
        if (!WorkspaceAutoPushEnabled() || _pushQueue == null) return;
        if (!_pushQueue.Enqueue(new WorkspaceArtifactPushRequest(gitRoot, "workspace-lifecycle")))
            _logger.LogWarning("workspace-artifact-push enqueue failed jobId=workspace-lifecycle repo={Repo}", gitRoot);
    }

    private List<string> FindEligibleChanges(
        string gitRoot,
        IReadOnlyList<string> includePathspecs,
        IReadOnlyList<string> excludeGlobs,
        bool trackedOnly)
    {
        var trackedArgs = new List<string> { "ls-files", "-m", "-d", "-z", "--" };
        trackedArgs.AddRange(includePathspecs);
        var tracked = RunGit(gitRoot, trackedArgs);
        if (tracked.Code != 0) return [];

        var listedOutput = tracked.Out;
        if (!trackedOnly)
        {
            var untrackedArgs = new List<string> { "ls-files", "-o", "--exclude-standard", "-z", "--" };
            untrackedArgs.AddRange(includePathspecs);
            var untracked = RunGit(gitRoot, untrackedArgs);
            if (untracked.Code != 0) return [];
            listedOutput += untracked.Out;
        }

        var accepted = new List<string>();
        foreach (var raw in listedOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var relative = raw.Replace('\\', '/');
            if (excludeGlobs.Any(pattern => MatchesWorkspaceGlob(relative, pattern)))
                continue;
            var full = Path.Combine(gitRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                var bytes = new FileInfo(full).Length;
                if (bytes > _maxStagedFileBytes)
                {
                    _logger.LogWarning(
                        "workspace-artifact-size-guard-refused repo={Repo} path={Path} bytes={Bytes} limitBytes={LimitBytes}",
                        gitRoot, relative, bytes, _maxStagedFileBytes);
                    continue;
                }
            }
            accepted.Add(relative);
        }
        return accepted.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    private GitProcessResult AddCandidates(string gitRoot, IReadOnlyList<string> candidates)
    {
        GitProcessResult result = new(string.Empty, string.Empty, 0);
        foreach (var batch in candidates.Chunk(200))
        {
            var args = new List<string> { "add", "-A", "--" };
            args.AddRange(batch);
            result = RunGitRetryingIndexLock(gitRoot, args);
            if (result.Code != 0) return result;
        }
        return result;
    }

    private static bool MatchesWorkspaceGlob(string relativePath, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var normalized = pattern.Trim().Replace('\\', '/').Replace("**", "*");
        return FileSystemName.MatchesSimpleExpression(
            normalized,
            relativePath,
            ignoreCase: OperatingSystem.IsWindows());
    }

    private GitProcessResult RunGitRetryingIndexLock(string gitRoot, IReadOnlyList<string> args, string? stdin = null)
        => RunWithIndexLockRetry(
            () => RunGit(gitRoot, args, stdin),
            _indexLockRetryAttempts,
            attempt => Thread.Sleep(_indexLockRetryBackoffMs * attempt));

    /// <summary>
    /// Retry a git invocation while it fails on <c>.git/index.lock</c>
    /// contention. Pure over an injected <paramref name="run"/> so the retry
    /// contract is unit-testable without real git or real time (the debounce
    /// path uses virtual time; this one uses a no-op backoff).
    /// </summary>
    internal static GitProcessResult RunWithIndexLockRetry(
        Func<GitProcessResult> run,
        int attempts,
        Action<int>? backoff)
    {
        var result = run();
        var max = Math.Max(1, attempts);
        for (var attempt = 1; attempt < max && IsIndexLockContention(result.Code, result.ErrorText); attempt++)
        {
            backoff?.Invoke(attempt);
            result = run();
        }
        return result;
    }

    internal static bool IsIndexLockContention(int code, string errorText)
    {
        if (code == 0 || string.IsNullOrEmpty(errorText)) return false;
        return errorText.Contains("index.lock", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("Another git process", StringComparison.OrdinalIgnoreCase)
            || (errorText.Contains("Unable to create", StringComparison.OrdinalIgnoreCase)
                && errorText.Contains(".lock", StringComparison.OrdinalIgnoreCase));
    }

    private string? ResolveGitRoot(string workspaceRoot)
    {
        var result = RunGit(workspaceRoot, ["rev-parse", "--show-toplevel"]);
        return result.Code == 0 && !string.IsNullOrWhiteSpace(result.Out)
            ? Path.GetFullPath(result.Out.Trim())
            : null;
    }

    private static List<string> BuildPathspecs(
        string gitRoot,
        string? beforeMoveFolderPath,
        string? afterMoveFolderPath)
    {
        var result = new List<string>();
        AddPathspec(result, gitRoot, beforeMoveFolderPath);
        AddPathspec(result, gitRoot, afterMoveFolderPath);
        return result
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddPathspec(List<string> result, string gitRoot, string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        var full = Path.GetFullPath(folderPath);
        var rel = Path.GetRelativePath(gitRoot, full);
        if (string.IsNullOrWhiteSpace(rel)
            || rel == "."
            || rel.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(rel))
        {
            return;
        }
        result.Add(rel.Replace('\\', '/'));
    }

    private static string NormalizeVerdict(ReviewDecisionKind verdict) => verdict switch
    {
        ReviewDecisionKind.AcceptAsDone => "accept",
        ReviewDecisionKind.Reissue => "reissue",
        ReviewDecisionKind.Escalate => "escalate",
        ReviewDecisionKind.Skipped => "skipped",
        _ => verdict.ToString().ToLowerInvariant(),
    };

    private static string NormalizeToken(string value) =>
        value.Trim().Replace(' ', '-').ToLowerInvariant();

    private static string? ResolveTerminalStatusToken(JsonElement step)
    {
        if (!step.TryGetProperty("status", out var el)) return null;

        PipelineStepStatus? status = null;
        string? raw = null;
        if (el.ValueKind == JsonValueKind.String)
        {
            raw = el.GetString();
            if (!string.IsNullOrWhiteSpace(raw)
                && Enum.TryParse<PipelineStepStatus>(raw, ignoreCase: true, out var parsed))
            {
                status = parsed;
            }
        }
        else if (el.ValueKind == JsonValueKind.Number
                 && el.TryGetInt32(out var numeric)
                 && Enum.IsDefined(typeof(PipelineStepStatus), numeric))
        {
            status = (PipelineStepStatus)numeric;
        }

        return status switch
        {
            PipelineStepStatus.Passed or PipelineStepStatus.Failed or PipelineStepStatus.Skipped
                or PipelineStepStatus.NotApplicable => status.Value.ToString(),
            null when !string.IsNullOrWhiteSpace(raw) => raw,
            _ => null,
        };
    }

    private static string? GetString(JsonElement obj, string property)
        => obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static GitProcessResult RunGit(string cwd, IReadOnlyList<string> args, string? stdin = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi)!;
        if (stdin != null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return new GitProcessResult(stdout, stderr, p.ExitCode);
    }

    private static GitProcessResult RunGitBounded(
        string cwd, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        var result = GitNetworkProcessRunner.Run(psi, timeout: timeout);
        return new GitProcessResult(result.StandardOutput, result.StandardError, result.ExitCode);
    }

    internal sealed record GitProcessResult(string Out, string Err, int Code)
    {
        public string ErrorText => string.IsNullOrWhiteSpace(Err) ? Out.Trim() : Err.Trim();
    }
}

public sealed record WorkspaceArtifactCommitResult(
    bool Success,
    bool DidCommit,
    string? Sha,
    int? RunIndex,
    string? Steps,
    string? Error)
{
    public static WorkspaceArtifactCommitResult Committed(string? sha, int runIndex, string steps) =>
        new(true, true, sha, runIndex, steps, null);

    public static WorkspaceArtifactCommitResult Skipped(string reason) =>
        new(true, false, null, null, null, reason);

    public static WorkspaceArtifactCommitResult Failed(string phase, string error) =>
        new(false, false, null, null, null, $"{phase}: {error}");
}
