using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Runner;

/// <summary>
/// Stable reason codes of a <see cref="BuildProfileGate.Decision"/>. They ride
/// along on the dispatch rejection and the project payload so a UI can branch on
/// the cause without parsing the human-readable sentence.
/// </summary>
public static class BuildProfileGateReasons
{
    public const string NoProfile = "no-build-profile";
    public const string PipelineReady = "pipeline-ready";
    public const string RemoteVerified = "remote-verified";
    public const string Validating = "validating";
    public const string ValidationFailed = "validation-failed";
    public const string RevalidationPending = "revalidation-pending";
    public const string RevalidationGraceExhausted = "revalidation-grace-exhausted";
    public const string NotValidated = "not-validated";
}

/// <summary>
/// Pure onboarding gate (Slice P / ASS-1663, hardened by AGT-2677). Decides
/// whether a project may be auto-picked given its <see cref="BuildProfile"/>.
/// The rule is serial-preserving: a project that has never declared a build
/// profile (<c>profile == null</c>) is always allowed (legacy behaviour, every
/// existing project).
///
/// <para>
/// A declared profile opens the gate on any of three pieces of evidence: a green
/// local validation dry-run (<see cref="BuildProfileStatuses.PipelineReady"/>), a
/// green build/test gate that ran this exact command set on the host that
/// executes the project (<see cref="BuildProfile.LastRemoteVerification"/>), or
/// the bounded revalidation grace granted when an already proven profile is
/// edited. Without evidence the gate stays closed - "ohne gruenen Dry-Run kein
/// Auto-Pickup" - but never silently: every caller turns a closed decision into
/// operator-visible state.
/// </para>
///
/// <para>
/// Kept as a stateless static so the decision is trivially unit-testable and
/// every pickup path reads one expression.
/// </para>
/// </summary>
public static class BuildProfileGate
{
    /// <summary>
    /// Dispatch-rejection code recorded on a Ready card that this gate refuses.
    /// One stable code for every gate cause; the cause itself is the reason text
    /// and <see cref="Decision.ReasonCode"/>.
    /// </summary>
    public const string RejectionCode = "build-profile-gate";

    /// <summary>Runs of auto-pickup granted after an already proven profile is edited.</summary>
    public const int DefaultRevalidationGraceRuns = 3;

    /// <summary>Pickup decision, a short human-readable reason, and a stable cause code.</summary>
    public readonly record struct Decision(bool AllowsPickup, string Reason, string ReasonCode);

    /// <summary>
    /// Evaluates the gate. A null profile is "no onboarding gate". Ordering is
    /// evidence-first: a green dry-run wins, then a remote verification that is
    /// not older than the last local attempt, then the revalidation grace. A
    /// local dry-run that is in progress or red only blocks while it is the most
    /// recent evidence about the declared commands.
    /// </summary>
    public static Decision Evaluate(BuildProfile? profile)
    {
        if (profile is null)
            return new Decision(true, "no build profile declared", BuildProfileGateReasons.NoProfile);

        var status = BuildProfileStatuses.Normalize(profile.Status);
        if (status == BuildProfileStatuses.PipelineReady)
            return new Decision(true, "pipeline-ready", BuildProfileGateReasons.PipelineReady);

        if (HasCurrentRemoteVerification(profile))
            return new Decision(
                true,
                $"proven by a green build/test gate on {profile.LastRemoteVerification!.VerifiedBy} " +
                $"at {profile.LastRemoteVerification.VerifiedAtUtc:u}",
                BuildProfileGateReasons.RemoteVerified);

        return status switch
        {
            BuildProfileStatuses.Validating => new Decision(
                false, "validation dry-run in progress", BuildProfileGateReasons.Validating),
            BuildProfileStatuses.ValidationFailed => new Decision(
                false,
                "last validation dry-run failed" + (string.IsNullOrWhiteSpace(profile.LastValidationError)
                    ? ""
                    : $": {profile.LastValidationError}"),
                BuildProfileGateReasons.ValidationFailed),
            BuildProfileStatuses.RevalidationPending when profile.RevalidationRunsRemaining is > 0 =>
                new Decision(
                    true,
                    "build profile edited after a green validation; revalidation pending " +
                    $"({profile.RevalidationRunsRemaining} run(s) of grace left)",
                    BuildProfileGateReasons.RevalidationPending),
            BuildProfileStatuses.RevalidationPending => new Decision(
                false,
                "build profile edited after a green validation and the revalidation grace is used up",
                BuildProfileGateReasons.RevalidationGraceExhausted),
            _ => new Decision(
                false,
                "build profile declared but not yet validated (no green dry-run)",
                BuildProfileGateReasons.NotValidated),
        };
    }

    /// <summary>Convenience boolean for the pickup loop.</summary>
    public static bool AllowsAutoPickup(BuildProfile? profile) => Evaluate(profile).AllowsPickup;

    /// <summary>
    /// True when the recorded remote verification still describes the declared
    /// commands and is not older than the last local dry-run attempt. Both terms
    /// matter: the fingerprint makes an edit invalidate the proof, and the
    /// timestamp makes a newer local red outrank it.
    /// </summary>
    public static bool HasCurrentRemoteVerification(BuildProfile? profile)
    {
        var verification = profile?.LastRemoteVerification;
        if (verification is null) return false;
        if (!string.Equals(
                verification.CommandFingerprint,
                BuildProfileCommandFingerprint.Create(profile),
                StringComparison.Ordinal))
            return false;
        return profile!.LastValidationAttemptAt is not { } attemptedAt
               || verification.VerifiedAtUtc.ToUniversalTime() >= attemptedAt.ToUniversalTime();
    }
}

/// <summary>
/// Stable fingerprint of the command set a validation proves: exactly the steps
/// <see cref="BuildProfileDryRunPlanner"/> would run (install, then the build
/// commands in order). Metadata that the dry-run cannot disprove - pool size,
/// preserve globs, lockfiles, test commands, stack label - is deliberately out
/// of scope so touching it never invalidates existing evidence.
/// </summary>
public static class BuildProfileCommandFingerprint
{
    public static string Create(BuildProfile? profile)
    {
        // Length-prefixed so no command text can forge a step boundary.
        var canonical = new StringBuilder();
        foreach (var step in BuildProfileDryRunPlanner.Plan(profile))
            canonical.Append(step.Kind).Append(':')
                .Append(step.Command.Length).Append(':')
                .Append(step.Command).Append('\n');

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
