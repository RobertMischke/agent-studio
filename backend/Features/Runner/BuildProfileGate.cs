
namespace AgentStudio.Runner;

/// <summary>
/// Stable machine-readable outcomes of <see cref="BuildProfileGate"/>. The codes
/// travel into the dispatch rejection, the project banner, and the timeline, so
/// they are part of the wire contract and must not be renamed casually.
/// </summary>
public static class BuildProfileGateCodes
{
    /// <summary>No profile declared: legacy behaviour, the gate does not apply.</summary>
    public const string NoProfile = "no-profile";

    /// <summary>A green dry-run or a green remote verification opened the gate.</summary>
    public const string PipelineReady = "pipeline-ready";

    /// <summary>Profile edited after it was proven; grace pickups still open the gate.</summary>
    public const string RevalidationPending = "revalidation-pending";

    /// <summary>The grace pickups after a profile edit are used up.</summary>
    public const string RevalidationExhausted = "revalidation-exhausted";

    /// <summary>A validation dry-run is running right now.</summary>
    public const string Validating = "validating";

    /// <summary>The last validation dry-run was red.</summary>
    public const string ValidationFailed = "validation-failed";

    /// <summary>Declared but never proven: no green dry-run and no green remote run.</summary>
    public const string NotValidated = "not-validated";
}

/// <summary>
/// Pure onboarding gate (Slice P / ASS-1663). Decides whether the runner may
/// auto-pick a project given its <see cref="BuildProfile"/>. The rule is
/// serial-preserving: a project that has never declared a build profile
/// (<c>profile == null</c>) is always allowed (legacy behaviour, every existing
/// project), and a declared profile only opens the gate once a green validation
/// has flipped it to <see cref="BuildProfileStatuses.PipelineReady"/>.
///
/// <para>
/// AGT-2677 added two escapes learned from the five-day Quality Studio outage, in
/// which a profile edit reset the status to <c>declared</c> and silently made
/// every ready card unclaimable. First, an edit of an already proven profile keeps
/// the gate open for a bounded number of grace pickups
/// (<see cref="BuildProfile.RevalidationRunsRemaining"/>) instead of closing it at
/// once. Second, a profile proven green by a real run on the assigned runner
/// counts as validated, so the gate no longer depends on a local dry-run that
/// cannot go green in a workspace without the project sources.
/// </para>
///
/// <para>
/// Kept as a stateless static so the decision is trivially unit-testable and the
/// pickup loop reads one expression. "Ohne gruenen Dry-Run kein Auto-Pickup" still
/// holds; what changed is what counts as green and how loudly a closed gate is
/// reported.
/// </para>
/// </summary>
public static class BuildProfileGate
{
    /// <summary>
    /// Pickup decision, a stable <see cref="BuildProfileGateCodes"/> code, and a
    /// short human-readable reason for logs, rejections, banners, and the timeline.
    /// </summary>
    public readonly record struct Decision(bool AllowsPickup, string Code, string Reason);

    /// <summary>
    /// Evaluates the gate. A null profile is "no onboarding gate". A declared but
    /// un-validated / failed / in-progress profile blocks pickup; only
    /// <see cref="BuildProfileStatuses.PipelineReady"/> - or a still-funded
    /// revalidation grace - opens it.
    /// </summary>
    public static Decision Evaluate(BuildProfile? profile)
    {
        if (profile is null)
            return new Decision(true, BuildProfileGateCodes.NoProfile, "no build profile declared");

        // The grace window is checked before the status so an operator reading the
        // reason learns that the profile was edited, not merely that it is ready.
        if (profile.RevalidationPending)
            return profile.RevalidationRunsRemaining > 0
                ? new Decision(true, BuildProfileGateCodes.RevalidationPending,
                    $"build profile edited; re-validation pending, {profile.RevalidationRunsRemaining} grace pickup(s) left")
                : new Decision(false, BuildProfileGateCodes.RevalidationExhausted,
                    "build profile edited and the re-validation grace pickups are used up; re-validate the profile");

        var status = BuildProfileStatuses.Normalize(profile.Status);
        return status switch
        {
            BuildProfileStatuses.PipelineReady => new Decision(true, BuildProfileGateCodes.PipelineReady, "pipeline-ready"),
            BuildProfileStatuses.Validating => new Decision(false, BuildProfileGateCodes.Validating, "validation dry-run in progress"),
            BuildProfileStatuses.ValidationFailed => new Decision(false, BuildProfileGateCodes.ValidationFailed,
                "last validation dry-run failed" + (string.IsNullOrWhiteSpace(profile.LastValidationError) ? "" : $": {profile.LastValidationError}")),
            _ => new Decision(false, BuildProfileGateCodes.NotValidated,
                "build profile declared but not yet validated (no green dry-run and no green run on the assigned runner)"),
        };
    }

    /// <summary>Convenience boolean for the pickup loop.</summary>
    public static bool AllowsAutoPickup(BuildProfile? profile) => Evaluate(profile).AllowsPickup;
}
