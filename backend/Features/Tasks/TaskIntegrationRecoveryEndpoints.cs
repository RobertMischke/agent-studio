using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>
/// Operator action for an accepted delivery in a rebase-recoverable failure
/// state. It queues a focused steer round on the original remote-runner
/// delivery branch instead of asking the operator to reconstruct the branch,
/// target, and recovery prompt by hand.
/// </summary>
public static class TaskIntegrationRecoveryEndpoints
{
    public static void MapTaskIntegrationRecoveryEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{jobId}/integration/rebase", (
            string jobId,
            string? project,
            string? watchPath,
            TaskScannerService scanner,
            ProjectRegistry projects,
            ProjectSettingsService settings,
            TaskIntegrationStatusService integrationStatus,
            PipelineExecutionLog pipeline,
            TaskMutationService mutations,
            TaskStateMachine states,
            TimelineLog timeline) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var job = scanner.FindJob(jobId, watchPath);
            if (job is null) return Results.NotFound(new { error = "Task not found." });
            if (job.State is not (
                    TaskStates.HumanReview
                    or TaskStates.Completed
                    or TaskStates.Archive))
            {
                return Results.Conflict(new
                {
                    error = $"Integration recovery requires a task in {TaskStates.HumanReview}, {TaskStates.Completed}, or {TaskStates.Archive}.",
                });
            }

            var status = integrationStatus.BuildLookup([job]).GetValueOrDefault(job.TaskKey);
            if (status?.Status != IntegrationStatuses.ConflictSkipped
                || status.Failure?.RebaseRecoveryAvailable != true)
            {
                return Results.Conflict(new
                {
                    error = "Rebase recovery is not available for this integration failure.",
                    integrationStatus = status?.Status,
                    failureCode = status?.Failure?.Code,
                });
            }

            var mergeStep = pipeline.Read(job.FolderPath)?.Steps.LastOrDefault(
                step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
            var failure = mergeStep is null
                ? null
                : AcceptedIntegrationFailurePolicy.Classify(
                    mergeStep.Status,
                    mergeStep.Verdict,
                    mergeStep.Reason,
                    mergeStep.VerdictSummary,
                    mergeStep.FailureCode);
            if (failure?.RebaseRecoveryAvailable != true)
            {
                return Results.Conflict(new
                {
                    error = "Rebase recovery is not available for this integration failure.",
                    mergeVerdict = mergeStep?.Verdict,
                    failureCode = failure?.Code,
                });
            }

            var subject = ReviewSubjectStore.Read(job.FolderPath);
            if (subject is null || string.IsNullOrWhiteSpace(subject.ResultRef))
            {
                return Results.Conflict(new
                {
                    error = "The accepted task has no fenced remote delivery ref to recover.",
                });
            }

            var integrationBranch = status.IntegrationBranch;
            var prompt =
                $"Integration recovery for {job.Key ?? job.Id}. "
                + $"Resume the existing delivery branch '{subject.ResultRef}' at the fenced result {subject.ResultSha}. "
                + $"Fetch the latest '{integrationBranch}', rebase the delivery onto it, resolve every conflict without dropping the task's intended changes, "
                + "run the relevant tests, and finish with the normal task terminal sentinel. "
                + "Do not merge or push the integration branch yourself; publish only the updated delivery branch for a new delivery gate and review round.";

            var intent = mutations.SavePendingIntent(
                job.Id,
                ContinueModes.Steer,
                prompt,
                reason: failure.Code,
                activeJobId: null,
                watchPath: job.WatchPath);
            if (intent is null)
            {
                return Results.Conflict(new
                {
                    error = "The integration recovery steer intent could not be persisted.",
                });
            }

            mutations.AppendContinuationNote(job.Id, prompt, job.WatchPath);
            var supersession = mutations.SupersedeCurrentDeliveryOnFolder(
                job.FolderPath,
                TaskCommitSupersession.PendingAttempt);
            if (!supersession.Succeeded)
            {
                return Results.Json(
                    new { error = "The recovery intent was persisted, but the superseded delivery history could not be marked." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
            // An operator queues the recovery round, but the ledger cause is the
            // integration recovery it serves; the failure code is the qualifier.
            var position = states.PromoteToReadyTop(
                job.Id, job.WatchPath,
                transitionCause: LaneChangeCauses.IntegrationRecovery,
                transitionDetail: failure.Code);
            var queued = scanner.FindJob(job.Id, job.WatchPath);
            if (queued is null || queued.State != TaskStates.Ready)
            {
                return Results.Conflict(new
                {
                    error = "The recovery prompt was persisted, but the task could not be queued in Ready.",
                });
            }

            timeline.Append(
                queued.FolderPath,
                TimelineEventKinds.IntegrationRecoveryQueued,
                TimelineActors.System,
                $"Integration recovery queued: rebase {subject.ResultRef} onto {integrationBranch}.",
                details: new Dictionary<string, string>
                {
                    ["deliveryRef"] = subject.ResultRef,
                    ["resultSha"] = subject.ResultSha,
                    ["integrationBranch"] = integrationBranch,
                    ["mode"] = ContinueModes.Steer,
                    ["supersededCommits"] = supersession.MarkedCommits.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                });

            return Results.Accepted(value: new
            {
                status = "queued",
                mode = ContinueModes.Steer,
                targetState = TaskStates.Ready,
                position,
                deliveryRef = subject.ResultRef,
                resultSha = subject.ResultSha,
                integrationBranch,
            });
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
    }
}
