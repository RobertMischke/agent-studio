namespace AgentStudio.Pipeline;

/// <summary>
/// Chooses the only permitted branch topology for immediate integration.
/// Repositories with both work and release lines must integrate into
/// <c>develop</c> first and may advance <c>main</c> only from that line.
/// </summary>
public static class ImmediateIntegrationLineagePolicy
{
    public static ImmediateIntegrationLineageDecision Decide(
        string targetBranch,
        bool developAvailable,
        bool mainIsAncestorOfDevelop)
    {
        if (!string.Equals(targetBranch, "main", StringComparison.OrdinalIgnoreCase)
            || !developAvailable)
        {
            return new(ImmediateIntegrationLineageMode.DirectToConfiguredTarget);
        }

        return mainIsAncestorOfDevelop
            ? new(ImmediateIntegrationLineageMode.DevelopThenMain)
            : new(
                ImmediateIntegrationLineageMode.Blocked,
                "Immediate integration cannot advance main because main is not an ancestor of develop. "
                + "Converge the existing branch history before retrying; no delivery ref was merged.");
    }

    /// <summary>
    /// Protects lower-level SHA pushes that predate the unified integration
    /// path. In a dual-line repository, such a push may advance <c>main</c>
    /// only to the exact commit already published as <c>develop</c>. Raw task
    /// and delivery commits must be published through their own refs and then
    /// integrated by the develop-then-main path.
    /// </summary>
    public static ImmediateMainAdvanceDecision DecideDirectMainAdvance(
        string targetBranch,
        bool developAvailable,
        bool candidateIsPublishedDevelopTip)
    {
        if (!string.Equals(targetBranch, "main", StringComparison.OrdinalIgnoreCase)
            || !developAvailable
            || candidateIsPublishedDevelopTip)
        {
            return new(ImmediateMainAdvanceMode.Allowed);
        }

        return new(
            ImmediateMainAdvanceMode.Blocked,
            "Direct main advance is blocked because the candidate is not the published develop tip. "
            + "Integrate the delivery into develop first, then fast-forward main to that exact commit.");
    }
}

public sealed record ImmediateIntegrationLineageDecision(
    ImmediateIntegrationLineageMode Mode,
    string? Reason = null);

public enum ImmediateIntegrationLineageMode
{
    DirectToConfiguredTarget,
    DevelopThenMain,
    Blocked,
}

public sealed record ImmediateMainAdvanceDecision(
    ImmediateMainAdvanceMode Mode,
    string? Reason = null);

public enum ImmediateMainAdvanceMode
{
    Allowed,
    Blocked,
}
