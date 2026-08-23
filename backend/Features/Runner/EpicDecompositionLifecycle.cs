namespace AgentStudio.Runner;

/// <summary>
/// Shared post-CLI lifecycle for local and remote Epic planning runs. This is
/// the sole place that parses an authored plan, creates child coding cards,
/// records spawn evidence, and recovers an invalid plan to Backlog.
/// </summary>
public static class EpicDecompositionLifecycle
{
    public static EpicDecompositionFinalization Finalize(
        TaskInfo epic,
        IReadOnlyList<string>? outputLines,
        string? runId,
        ProjectSettingsService projectSettings,
        TaskMutationService mutations,
        TaskScannerService scanner,
        TaskStateMachine states,
        TimelineLog? timeline,
        OrchestratorChatLog? chatLog,
        ILogger? logger,
        string? invalidationReason = null)
    {
        // Callers hand in whatever TaskInfo snapshot they held when the run
        // ended, which may pre-date a lane move (the remote path finalizes
        // after moving the Epic into its planning-completion lane). Rebind
        // before writing evidence so the shared local/remote path never
        // recreates the stale lane folder as a planning-spawn ghost.
        epic = scanner.FindJob(epic.Id, epic.WatchPath) ?? epic;

        var result = string.IsNullOrWhiteSpace(invalidationReason)
            ? EpicDecompositionParser.Parse(outputLines)
            : new EpicDecompositionResult([], invalidationReason.Trim());
        var targetState = projectSettings.Get(epic.ProjectName).EpicSubTasksToReady == true
            ? TaskStates.Ready
            : TaskStates.Backlog;

        if (!result.HasSubTasks)
        {
            var reason = result.Error ?? "no plan found";
            logger?.LogWarning(
                "epic-decomposition-empty epic={EpicId} reason={Reason}", epic.Id, reason);
            chatLog?.Append(epic, OrchestratorMessageKind.Decision,
                $"[epic] Decomposition produced no sub-tasks ({reason}). The epic returns to Backlog so it cannot become a ghost completion. Clarify its goal before retrying.");
            timeline?.Append(
                epic.FolderPath,
                TimelineEventKinds.EpicDecomposed,
                TimelineActors.Orchestrator,
                summary: "Epic decomposition produced no sub-tasks",
                runId: runId,
                details: new()
                {
                    ["created"] = "0",
                    ["reason"] = reason,
                    ["recoveryState"] = TaskStates.Backlog,
                });

            var recovery = states.MoveJob(epic.Id, TaskStates.Backlog, epic.WatchPath,
                cause: "epic_decomposition_empty",
                transitionCause: LaneChangeCauses.RunnerRequeue,
                transitionDetail: "epic-decomposition-empty");
            if (recovery.Status != MoveJobStatus.Success)
                logger?.LogError(
                    "epic-decomposition-recovery-failed epic={EpicId} state={State} status={Status} error={Error}",
                    epic.Id, TaskStates.Backlog, recovery.Status, recovery.Message);
            return new EpicDecompositionFinalization(false, [], reason, TaskStates.Backlog);
        }

        var createdIds = EpicSubTaskFactory.CreateSubTasks(mutations, epic, result.SubTasks, targetState);
        foreach (var childId in createdIds)
        {
            var child = scanner.FindJob(childId, epic.WatchPath);
            SpawnedTaskLedger.Append(epic.FolderPath, new SpawnedTaskRecord
            {
                At = DateTime.UtcNow,
                SourceKey = epic.Key ?? epic.Id,
                TargetProject = child?.ProjectName ?? epic.ProjectName,
                TargetKey = child?.Key ?? childId,
                TargetJobId = childId,
                Reason = "Epic decomposition",
            }, logger);
        }

        logger?.LogInformation(
            "epic-decomposition-created epic={EpicId} count={Count} lane={Lane}",
            epic.Id, createdIds.Count, targetState);
        chatLog?.Append(epic, OrchestratorMessageKind.Decision,
            $"[epic] Decomposition created {createdIds.Count} sub-task(s) in {targetState}.");
        timeline?.Append(
            epic.FolderPath,
            TimelineEventKinds.EpicDecomposed,
            TimelineActors.Orchestrator,
            summary: $"Epic decomposition created {createdIds.Count} sub-task(s)",
            runId: runId,
            details: new()
            {
                ["created"] = createdIds.Count.ToString(),
                ["targetState"] = targetState,
                ["planningSpawn"] = "persisted",
            });
        return new EpicDecompositionFinalization(true, createdIds, null, targetState);
    }
}

public sealed record EpicDecompositionFinalization(
    bool Valid,
    IReadOnlyList<string> CreatedTaskIds,
    string? Error,
    string TargetState);
