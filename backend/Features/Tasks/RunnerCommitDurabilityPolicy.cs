namespace AgentStudio.Tasks;

/// <summary>
/// Chooses the ref on which a runner-owned task commit is made durable.
///
/// <para>
/// Auto-commit produces a raw task commit in the project checkout. Making it
/// durable must never advance a shared release line: in a repository that has a
/// <c>develop</c> line, <c>main</c> may only ever move to the exact commit
/// already published as <c>develop</c>
/// (<see cref="AgentStudio.Pipeline.ImmediateIntegrationLineagePolicy.DecideDirectMainAdvance"/>).
/// Pushing the raw commit at the shared line is therefore refused by the
/// lineage guard on every single attempt, so the commit never reaches origin at
/// all and the periodic completed-push backstop retries the identical doomed
/// push forever.
/// </para>
///
/// <para>
/// The commit still has to become durable, so this policy redirects it to the
/// card's own task ref. That ref is private to the card, always fast-forwards,
/// and is exactly what <see cref="AgentStudio.Pipeline.DeliveryRefResolver"/>
/// later resolves as the delivery source. Shared lines keep advancing only
/// through the integration path.
/// </para>
/// </summary>
public static class RunnerCommitDurabilityPolicy
{
    public static RunnerCommitDurabilityDecision Decide(
        string targetBranch,
        bool developLineExists,
        bool candidateIsPublishedDevelopTip,
        string taskRef)
    {
        // Only a shared-line advance can trip the lineage guard. Anything else
        // (an explicit task/delivery ref target) is already safe.
        if (!string.Equals(targetBranch, "main", StringComparison.OrdinalIgnoreCase))
            return new(RunnerCommitDurabilityMode.SharedLine, targetBranch, null);

        // Single-line repository: main is the integration line, and a task
        // commit sitting on it fast-forwards normally.
        if (!developLineExists)
            return new(RunnerCommitDurabilityMode.SharedLine, targetBranch, null);

        // Dual-line repository, and the candidate already is the published
        // develop tip: this is the legitimate develop-then-main promotion.
        if (candidateIsPublishedDevelopTip)
            return new(RunnerCommitDurabilityMode.SharedLine, targetBranch, null);

        if (string.IsNullOrWhiteSpace(taskRef))
        {
            return new(
                RunnerCommitDurabilityMode.Blocked,
                targetBranch,
                "Cannot make the runner-owned commit durable: the repository has a develop line, "
                + "so the commit may not advance main, and no task ref is available to publish it on.");
        }

        return new(
            RunnerCommitDurabilityMode.TaskRef,
            taskRef,
            $"Repository has a develop line, so the raw task commit is published on '{taskRef}' "
            + "instead of advancing main. Shared lines advance only through integration.");
    }
}

public sealed record RunnerCommitDurabilityDecision(
    RunnerCommitDurabilityMode Mode,
    string TargetRef,
    string? Reason);

public enum RunnerCommitDurabilityMode
{
    /// <summary>Push at the configured shared line (single-line repo, or a legitimate promotion).</summary>
    SharedLine,

    /// <summary>Publish the raw commit on the card's own task ref instead.</summary>
    TaskRef,

    /// <summary>No durable ref is available; report instead of looping.</summary>
    Blocked,
}
