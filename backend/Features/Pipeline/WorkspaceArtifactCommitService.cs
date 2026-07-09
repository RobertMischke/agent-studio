using System.Diagnostics;
using System.Globalization;
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

    public WorkspaceArtifactCommitService(
        IConfiguration configuration,
        ILogger<WorkspaceArtifactCommitService> logger)
    {
        _configuration = configuration;
        _logger = logger;
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

            var addArgs = new List<string> { "add", "-A", "--" };
            addArgs.AddRange(pathspecs);
            var add = RunGit(gitRoot, addArgs);
            if (add.Code != 0)
                return WorkspaceArtifactCommitResult.Failed("git-add", add.ErrorText);

            var changed = RunGit(gitRoot, ["diff", "--cached", "--quiet", "--", .. pathspecs]);
            if (changed.Code == 0)
                return WorkspaceArtifactCommitResult.Skipped("nothing-to-commit");
            if (changed.Code != 1)
                return WorkspaceArtifactCommitResult.Failed("git-diff-cached", changed.ErrorText);

            var afterFolder = !string.IsNullOrWhiteSpace(afterMoveFolderPath)
                ? afterMoveFolderPath!
                : beforeMoveFolderPath ?? string.Empty;
            var plan = planMessage(gitRoot, pathspecs, afterFolder);

            var commitArgs = new List<string>
            {
                "-c", $"user.name={CommitterName}",
                "-c", $"user.email={CommitterEmail}",
                "commit", "-F", "-", "--"
            };
            commitArgs.AddRange(pathspecs);
            var commit = RunGit(gitRoot, commitArgs, plan.Message);
            if (commit.Code != 0)
            {
                if (commit.ErrorText.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                    return WorkspaceArtifactCommitResult.Skipped("nothing-to-commit");
                return WorkspaceArtifactCommitResult.Failed("git-commit", commit.ErrorText);
            }

            var sha = RunGit(gitRoot, ["rev-parse", "--short", "HEAD"]);
            var shortSha = sha.Code == 0 ? sha.Out.Trim() : null;
            _logger.LogInformation(
                "workspace-artifact-commit jobId={JobId} {LogLabel} runIndex={RunIndex} sha={Sha} paths={Paths}",
                jobId, logLabel, plan.RunIndex, shortSha ?? "", string.Join(",", pathspecs));
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
            PipelineStepStatus.Passed or PipelineStepStatus.Failed or PipelineStepStatus.Skipped => status.Value.ToString(),
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

    private sealed record GitProcessResult(string Out, string Err, int Code)
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
