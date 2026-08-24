namespace AgentStudio.Tasks;

/// <summary>
/// Canonical non-repository eligibility rules shared by Remote claim
/// selection and the queue-depth watcher. Keeping the rules here prevents the
/// watcher from counting cards that the next claim poll must refuse.
/// Repository registration and cached host preflight remain claim-time checks.
///
/// <para>
/// The build-profile gate is deliberately a separate predicate (AGT-2677). Folding
/// it into the same boolean made a gate-blocked card indistinguishable from a card
/// that is simply not this runner's business: it disappeared from the claim loop
/// before any rejection could be recorded and from the starvation watcher before
/// any alarm could fire. Callers now ask the two questions separately so a closed
/// gate can be reported instead of silently filtering.
/// </para>
/// </summary>
public static class RemoteDispatchEligibility
{
    /// <summary>
    /// Everything that makes a task this runner's business, ignoring the
    /// build-profile gate. A task that satisfies this but fails
    /// <see cref="BuildProfileGate.AllowsAutoPickup"/> is exactly the population the
    /// gate is starving, and is what the claim loop records a rejection for.
    /// </summary>
    public static bool IsAssignedAndRunnableApartFromBuildProfile(
        TaskInfo task,
        ProjectSettings project,
        string runnerId,
        string? runnerName,
        TaskReferenceIndex references)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(references);

        return ProjectExecutionPolicy.AllowsAutomaticPickup(project)
               && ProjectExecutionPolicy.IsAssignedRemote(project, runnerId, runnerName)
               && AgentTypes.IsAutoPickupEligible(task.Agent)
               && !TaskSlugs.IsHumanDecisionNeeded(task.Id)
               && (!project.IntakeEnabled.GetValueOrDefault()
                   || task.Phase == LifecyclePhases.IntakePassed)
               && !references.EvaluateWaitsOn(task).Blocked;
    }

    public static bool IsAssignedAndRunnable(
        TaskInfo task,
        ProjectSettings project,
        string runnerId,
        string? runnerName,
        TaskReferenceIndex references)
        => IsAssignedAndRunnableApartFromBuildProfile(task, project, runnerId, runnerName, references)
           && BuildProfileGate.AllowsAutoPickup(project.BuildProfile);

    public static bool IsReadOnlyRefused(TaskInfo task, bool runnerReadOnly) =>
        runnerReadOnly && !TaskKinds.IsEpic(task.Kind);

    public static bool IsClaimableReady(
        TaskInfo task,
        ProjectSettings project,
        string runnerId,
        string? runnerName,
        bool runnerReadOnly,
        TaskReferenceIndex references) =>
        task.State == TaskStates.Ready
        && IsAssignedAndRunnable(task, project, runnerId, runnerName, references)
        && !IsReadOnlyRefused(task, runnerReadOnly);
}
