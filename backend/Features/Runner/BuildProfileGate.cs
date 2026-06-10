
namespace AgentStudio.Runner;

/// <summary>
/// Pure onboarding gate (Slice P / ASS-1663). Decides whether the runner may
/// auto-pick a project given its <see cref="BuildProfile"/>. The rule is
/// serial-preserving: a project that has never declared a build profile
/// (<c>profile == null</c>) is always allowed (legacy behaviour, every existing
/// project), and a declared profile only opens the gate once a green validation
/// dry-run has flipped it to <see cref="BuildProfileStatuses.PipelineReady"/>.
///
/// <para>
/// Kept as a stateless static so the decision is trivially unit-testable and the
/// pickup loop reads one expression. "Ohne gruenen Dry-Run kein Auto-Pickup."
/// </para>
/// </summary>
public static class BuildProfileGate
{
    /// <summary>Pickup decision plus a short human-readable reason for logs / timeline.</summary>
    public readonly record struct Decision(bool AllowsPickup, string Reason);

    /// <summary>
    /// Evaluates the gate. A null profile is "no onboarding gate". A declared but
    /// un-validated / failed / in-progress profile blocks pickup; only
    /// <see cref="BuildProfileStatuses.PipelineReady"/> opens it.
    /// </summary>
    public static Decision Evaluate(BuildProfile? profile)
    {
        if (profile is null)
            return new Decision(true, "no build profile declared");

        var status = BuildProfileStatuses.Normalize(profile.Status);
        return status switch
        {
            BuildProfileStatuses.PipelineReady => new Decision(true, "pipeline-ready"),
            BuildProfileStatuses.Validating => new Decision(false, "validation dry-run in progress"),
            BuildProfileStatuses.ValidationFailed => new Decision(false,
                "last validation dry-run failed" + (string.IsNullOrWhiteSpace(profile.LastValidationError) ? "" : $": {profile.LastValidationError}")),
            _ => new Decision(false, "build profile declared but not yet validated (no green dry-run)"),
        };
    }

    /// <summary>Convenience boolean for the pickup loop.</summary>
    public static bool AllowsAutoPickup(BuildProfile? profile) => Evaluate(profile).AllowsPickup;
}
