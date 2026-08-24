namespace AgentStudio.Runner;

/// <summary>
/// Pure rule for what a profile re-declaration does to the onboarding gate
/// (AGT-2677).
///
/// <para>
/// The incident this exists for: a card rewrote its own project's build profile,
/// the edit reset the status to <c>declared</c>, and every Ready card in that
/// project silently stopped being claimable for five days. An edit is a normal
/// part of maintaining a project and must not be able to close the gate without
/// anybody noticing.
/// </para>
///
/// <para>
/// The rule, in order:
/// </para>
/// <list type="number">
/// <item>No previous profile: the edit is the first declaration and lands in
/// <see cref="BuildProfileStatuses.Declared"/>, exactly as before.</item>
/// <item>The commands the dry-run would run are unchanged: the edit only touched
/// metadata the dry-run cannot disprove (pool size, preserve globs, lockfiles,
/// test commands, stack label), so every piece of evidence carries over
/// untouched and the gate does not move at all.</item>
/// <item>The commands changed while the previous profile was passing the gate:
/// the profile enters <see cref="BuildProfileStatuses.RevalidationPending"/> with
/// <see cref="BuildProfileGate.DefaultRevalidationGraceRuns"/> runs of grace.
/// Pickup continues while the project revalidates, and each granted run spends
/// one unit of grace, so an edit that really did break the build costs a bounded
/// number of runs instead of an unbounded silence.</item>
/// <item>The commands changed and the previous profile was not passing the gate:
/// there is no green evidence to carry over, so the edit lands in
/// <see cref="BuildProfileStatuses.Declared"/>.</item>
/// </list>
/// </summary>
public static class BuildProfileEditPolicy
{
    /// <summary>
    /// Applies the re-declaration rule. <paramref name="declared"/> is the
    /// freshly normalized profile (status <see cref="BuildProfileStatuses.Declared"/>,
    /// no evidence); <paramref name="previous"/> is what the project carried
    /// before the edit.
    /// </summary>
    public static BuildProfile Apply(BuildProfile? previous, BuildProfile declared)
    {
        ArgumentNullException.ThrowIfNull(declared);
        if (previous is null) return declared;

        var unchangedCommands = string.Equals(
            BuildProfileCommandFingerprint.Create(previous),
            BuildProfileCommandFingerprint.Create(declared),
            StringComparison.Ordinal);
        if (unchangedCommands)
            return declared with
            {
                Status = BuildProfileStatuses.Normalize(previous.Status),
                LastValidatedAt = previous.LastValidatedAt,
                LastValidationError = previous.LastValidationError,
                LastValidationAttemptAt = previous.LastValidationAttemptAt,
                RevalidationRunsRemaining = previous.RevalidationRunsRemaining,
                LastRemoteVerification = previous.LastRemoteVerification,
            };

        if (!BuildProfileGate.AllowsAutoPickup(previous)) return declared;

        // The stale remote verification rides along on purpose: its fingerprint
        // no longer matches, so the gate ignores it, but the UI can still say
        // when this project last built green.
        return declared with
        {
            Status = BuildProfileStatuses.RevalidationPending,
            RevalidationRunsRemaining = BuildProfileGate.DefaultRevalidationGraceRuns,
            LastValidatedAt = previous.LastValidatedAt,
            LastValidationAttemptAt = previous.LastValidationAttemptAt,
            LastRemoteVerification = previous.LastRemoteVerification,
        };
    }

    /// <summary>
    /// Spends one unit of revalidation grace. Returns the profile unchanged
    /// unless it is actually in <see cref="BuildProfileStatuses.RevalidationPending"/>
    /// with grace left, so callers can invoke it unconditionally on a granted
    /// pickup.
    /// </summary>
    public static BuildProfile? ConsumeRevalidationRun(BuildProfile? profile)
    {
        if (profile is null) return null;
        if (BuildProfileStatuses.Normalize(profile.Status) != BuildProfileStatuses.RevalidationPending)
            return profile;
        if (profile.RevalidationRunsRemaining is not > 0) return profile;
        return profile with { RevalidationRunsRemaining = profile.RevalidationRunsRemaining - 1 };
    }
}
