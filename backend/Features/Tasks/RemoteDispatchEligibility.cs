namespace AgentStudio.Tasks;

/// <summary>
/// Verdict of the non-repository claim admission for one Ready card.
/// <para>
/// A refusal is either <em>none of this runner's business</em> (wrong project,
/// wrong agent, still waiting on a reference) - silent, because recording it
/// would rewrite every card in the workspace on every poll - or a <em>project
/// configuration problem</em> that keeps an otherwise claimable card from ever
/// being picked up. The second kind carries a rejection code and must be made
/// visible on the card (AGT-2677).
/// </para>
/// </summary>
public readonly record struct RemoteDispatchAdmission(
    bool Eligible,
    string? RejectionCode,
    string? RejectionReason)
{
    public static readonly RemoteDispatchAdmission Admitted = new(true, null, null);
    public static readonly RemoteDispatchAdmission SilentlyNotApplicable = new(false, null, null);
}

/// <summary>
/// Canonical non-repository eligibility rules shared by Remote claim
/// selection and the queue-depth watcher. Keeping the rules here prevents the
/// watcher from counting cards that the next claim poll must refuse.
/// Repository registration and cached host preflight remain claim-time checks.
/// </summary>
public static class RemoteDispatchEligibility
{
    /// <summary>
    /// Everything except the build-profile gate: routing, agent kind, intake
    /// phase, and reference blocks. Split out so both the claim endpoint and the
    /// starvation watcher can see a card that only the gate is holding back
    /// instead of dropping it from the queue picture entirely.
    /// </summary>
    public static bool IsAssignedAndRunnableExceptBuildProfile(
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
        TaskReferenceIndex references) =>
        IsAssignedAndRunnableExceptBuildProfile(task, project, runnerId, runnerName, references)
        && BuildProfileGate.AllowsAutoPickup(project.BuildProfile);

    /// <summary>
    /// Pure claim admission for one Ready card, with the cause of a refusal the
    /// caller has to make visible. Only the build-profile gate produces a
    /// rejection code today: it is the one refusal that is a standing project
    /// misconfiguration rather than a per-poll routing fact.
    /// </summary>
    public static RemoteDispatchAdmission Evaluate(
        TaskInfo task,
        ProjectSettings project,
        string runnerId,
        string? runnerName,
        TaskReferenceIndex references)
    {
        if (!IsAssignedAndRunnableExceptBuildProfile(task, project, runnerId, runnerName, references))
            return RemoteDispatchAdmission.SilentlyNotApplicable;

        var gate = BuildProfileGate.Evaluate(project.BuildProfile);
        return gate.AllowsPickup
            ? RemoteDispatchAdmission.Admitted
            : new RemoteDispatchAdmission(
                false,
                BuildProfileGate.RejectionCode,
                $"project build profile blocks auto-pickup: {gate.Reason}");
    }

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
