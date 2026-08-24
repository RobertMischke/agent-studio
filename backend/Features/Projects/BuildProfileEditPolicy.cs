namespace AgentStudio.Projects;

/// <summary>
/// Pure decision for what a build-profile edit does to the onboarding status
/// (AGT-2677).
///
/// <para>
/// Chosen rule, in one sentence: <b>an edit never closes an open gate on its own -
/// it keeps the proven status and starts a bounded re-validation grace, and only
/// running out of that grace closes the gate.</b>
/// </para>
///
/// <para>
/// Before AGT-2677 every PUT of a build profile reset the status to
/// <see cref="BuildProfileStatuses.Declared"/>. During the 2026-08-18 Quality
/// Studio outage that turned a one-line review-spec fix into five days of
/// starvation: 25 ready cards stopped being claimable and nothing said why. The
/// reset was also disproportionate - most edits do not touch what the dry-run
/// actually proved.
/// </para>
///
/// <para>The matrix:</para>
/// <list type="bullet">
///   <item>No previous profile, or a previous profile whose gate was closed anyway
///   (declared / validating / validation-failed / grace used up): the edit lands as
///   <see cref="BuildProfileStatuses.Declared"/>. Nothing was proven, so nothing is
///   preserved.</item>
///   <item>Previous profile open and the edit leaves the dry-run material
///   (install command plus build commands) untouched: the status, validation
///   timestamps, and any running grace are carried over verbatim. The dry-run still
///   proves exactly what it proved before.</item>
///   <item>Previous profile open and the dry-run material changed: the proven
///   status and timestamps are carried over, but <see cref="BuildProfile.RevalidationPending"/>
///   is raised with <see cref="DefaultGraceRuns"/> grace pickups. The gate stays
///   open for those pickups; a green validation clears the flag, and exhausting the
///   grace closes the gate with a recorded reason.</item>
/// </list>
/// </summary>
public static class BuildProfileEditPolicy
{
    /// <summary>
    /// Pickups a proven profile keeps after its dry-run material changed. Small on
    /// purpose: enough that a real run can re-prove the profile through the build/test
    /// gate, short enough that a genuinely broken profile stops the project quickly.
    /// </summary>
    public const int DefaultGraceRuns = 3;

    /// <summary>
    /// Folds <paramref name="edited"/> onto <paramref name="previous"/> and returns
    /// the profile to persist. <paramref name="edited"/> carries the operator's
    /// fields; every status field on it is ignored - onboarding status is
    /// server-owned.
    /// </summary>
    public static BuildProfile Apply(BuildProfile? previous, BuildProfile edited, int graceRuns = DefaultGraceRuns)
    {
        ArgumentNullException.ThrowIfNull(edited);

        var declared = edited with
        {
            Status = BuildProfileStatuses.Declared,
            LastValidatedAt = null,
            LastValidationError = null,
            LastRemoteVerifiedAt = null,
            LastRemoteVerifiedBy = null,
            RevalidationPending = false,
            RevalidationRunsRemaining = 0,
        };

        if (previous is null || !AgentStudio.Runner.BuildProfileGate.AllowsAutoPickup(previous))
            return declared;

        var carried = declared with
        {
            Status = BuildProfileStatuses.Normalize(previous.Status),
            LastValidatedAt = previous.LastValidatedAt,
            LastRemoteVerifiedAt = previous.LastRemoteVerifiedAt,
            LastRemoteVerifiedBy = previous.LastRemoteVerifiedBy,
            RevalidationPending = previous.RevalidationPending,
            RevalidationRunsRemaining = previous.RevalidationRunsRemaining,
        };

        if (!DryRunMaterialChanged(previous, edited))
            return carried;

        return carried with
        {
            RevalidationPending = true,
            RevalidationRunsRemaining = Math.Max(1, graceRuns),
        };
    }

    /// <summary>
    /// True when the edit touched what the validation dry-run actually executes -
    /// the install command and the ordered build commands
    /// (<c>BuildProfileDryRunPlanner</c>). Test commands, lockfiles, preserve globs,
    /// pool size, and the informational stack label are deliberately excluded: none
    /// of them can invalidate a green install+build.
    /// </summary>
    public static bool DryRunMaterialChanged(BuildProfile previous, BuildProfile edited)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(edited);
        return !string.Equals(previous.InstallCmd, edited.InstallCmd, StringComparison.Ordinal)
               || !SameSequence(previous.BuildCmds, edited.BuildCmds);
    }

    private static bool SameSequence(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        var a = left ?? [];
        var b = right ?? [];
        return a.SequenceEqual(b, StringComparer.Ordinal);
    }
}
