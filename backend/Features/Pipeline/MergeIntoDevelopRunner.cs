

namespace AgentStudio.Pipeline;

/// <summary>
/// Runs the deferred, operator-triggered "Merge into Develop" post-step
/// (<see cref="PipelineCatalogue.MergeIntoDevelopStepId"/>). Unlike the automatic
/// in-run integration (<c>ProjectRunner.IntegrateWorktreeRunAsync</c>, ADR-0052),
/// this step does NOT run on its own: the catalogue marks it
/// <see cref="PipelineStep.Deferred"/> so it sits "pending" in the pipeline view
/// until the operator accepts a done-green task via the "Merge into Develop"
/// action (the <c>HumanReview -&gt; Completed</c> transition). That acceptance is
/// the trigger; <see cref="AgentStudio.Tasks.TaskTransitionService"/>
/// calls <see cref="Run"/> here.
///
/// <para>
/// It performs the real, scoped git merge <c>task/&lt;id&gt; -&gt; develop</c> via
/// <see cref="GitService.MergeBranchIntoIntegration"/> and records the outcome
/// into the job's <c>pipeline-execution.json</c> so the deferred step flips from
/// pending to passed / failed / skipped in place. A merge conflict is recorded
/// <see cref="PipelineStepStatus.Failed"/> with the conflicted files in the
/// verdict summary - made visible, never silently resolved - while the working
/// tree is left clean (the merge is aborted). Best-effort and fully guarded: it
/// runs after the lane move has already landed, so nothing it does can block the
/// transition.
/// </para>
/// </summary>
public sealed class MergeIntoDevelopRunner
{
    private readonly GitService _git;
    private readonly PipelineExecutionLog _pipelineLog;
    private readonly ILogger<MergeIntoDevelopRunner> _logger;

    public MergeIntoDevelopRunner(
        GitService git,
        PipelineExecutionLog pipelineLog,
        ILogger<MergeIntoDevelopRunner> logger)
    {
        _git = git;
        _pipelineLog = pipelineLog;
        _logger = logger;
    }

    /// <summary>
    /// Triggers the merge of the task branch into <paramref name="integrationBranch"/>
    /// and records the post-step outcome. Returns the underlying
    /// <see cref="MergeIntoIntegrationResult"/> for callers / tests; the lane
    /// transition does not depend on it. Never throws.
    /// </summary>
    public MergeIntoIntegrationResult Run(
        string project,
        string jobId,
        string jobFolderPath,
        string? watchPath,
        string integrationBranch)
    {
        var startedAt = DateTime.UtcNow;
        try
        {
            var repoRoot = _git.ResolveRepoRootForWatchPath(watchPath)
                ?? (string.IsNullOrWhiteSpace(watchPath) ? null : watchPath);
            if (string.IsNullOrWhiteSpace(repoRoot))
            {
                var unresolved = MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error, error: "Could not resolve repository root for the project.");
                Record(jobFolderPath, project, jobId, unresolved, startedAt);
                return unresolved;
            }

            var branch = _git.ResolveIntegrationBranch(repoRoot, integrationBranch);
            var taskBranch = WorktreeTaskLifecycle.BranchFor(jobId);

            var result = _git.MergeBranchIntoIntegration(repoRoot, taskBranch, branch);
            _logger.LogInformation(
                "merge-into-develop project={Project} job={JobId} task={Task} integration={Integration} outcome={Outcome}",
                project, jobId, taskBranch, branch, result.Outcome);
            Record(jobFolderPath, project, jobId, result, startedAt);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "merge-into-develop post-step failed for {JobId}", jobId);
            var errored = MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Error, error: ex.Message);
            try { Record(jobFolderPath, project, jobId, errored, startedAt); } catch (Exception __ex) { SilentCatch.Note(__ex, "MergeIntoDevelopRunner: recording is best-effort"); /* recording is best-effort */ }
            return errored;
        }
    }

    private void Record(
        string jobFolderPath,
        string project,
        string jobId,
        MergeIntoIntegrationResult result,
        DateTime startedAt)
    {
        // Record into the existing run when one is present (the deferred merge
        // step already sits in it as "pending"); only begin a fresh baseline when
        // none exists yet, so RecordStep is never a silent no-op. The merge step
        // only lives in the standard pipeline (the read-only variant drops every
        // git step), so the baseline uses the standard catalogue.
        if (_pipelineLog.Read(jobFolderPath) == null)
        {
            _pipelineLog.EnsureRun(jobFolderPath, PipelineCatalogue.Standard, project, jobId);
        }

        var completedAt = DateTime.UtcNow;
        var (status, verdict, reason, summary) = Project(result);

        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationMs = (long)(completedAt - startedAt).TotalMilliseconds,
            Verdict = verdict,
            VerdictSummary = summary,
            Reason = reason,
        });
    }

    private static (PipelineStepStatus Status, string? Verdict, string? Reason, string? Summary) Project(
        MergeIntoIntegrationResult result)
    {
        switch (result.Outcome)
        {
            case MergeIntoIntegrationOutcome.Merged:
                var sha = string.IsNullOrWhiteSpace(result.MergedSha)
                    ? string.Empty
                    : $" ({Short(result.MergedSha!)})";
                return (PipelineStepStatus.Passed, "merged", $"Merged into integration branch{sha}.", null);
            case MergeIntoIntegrationOutcome.AlreadyMerged:
                return (PipelineStepStatus.Passed, "already-merged", "Task branch already contained in the integration branch; no merge needed.", null);
            case MergeIntoIntegrationOutcome.NoTaskBranch:
                return (PipelineStepStatus.Skipped, "no-branch", result.Error ?? "No task branch to merge.", null);
            case MergeIntoIntegrationOutcome.Conflict:
                var files = result.ConflictedFiles is { Count: > 0 }
                    ? string.Join(", ", result.ConflictedFiles)
                    : "unknown files";
                return (
                    PipelineStepStatus.Failed,
                    "conflict",
                    $"Merge conflict in {result.ConflictedFiles?.Count ?? 0} file(s); merge aborted, working tree left clean.",
                    $"Conflicted: {files}");
            default:
                return (PipelineStepStatus.Failed, "error", result.Error ?? "Merge failed.", null);
        }
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
