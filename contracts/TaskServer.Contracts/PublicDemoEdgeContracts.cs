namespace AgentStudio.TaskServer.Contracts;

public sealed record PublicDemoEdgeDecision(
    bool Allowed,
    string Code,
    string Message);

/// <summary>
/// Pure, shared policy for the public-demo browser edge (W34 §8 S4). This is
/// deliberately a *coarser* net than <see cref="ExecutionAdmissionPolicy"/>:
/// S2 denies a specific catalogued set of execution routes by identity, while
/// this policy denies every unsafe HTTP method outright and, for the safe
/// methods, accepts only an explicit read allowlist. A route S2 does not know
/// about (a future wiki-edit or decision endpoint that never got tagged
/// <c>ExecutionRouteMetadata</c>) still cannot mutate anything, because it
/// never reaches past this edge.
/// </summary>
public static class PublicDemoEdgePolicy
{
    public const string ReadOnlyDeniedCode = "public-demo-read-only";

    public static bool IsSafeMethod(string method) =>
        string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
        || string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase);

    /// <param name="pathAllowlisted">
    /// Whether the request path matches the explicit public-demo read
    /// allowlist. Ignored for unsafe methods, which are always denied.
    /// </param>
    public static PublicDemoEdgeDecision Decide(
        string? startupProfile,
        string method,
        bool pathAllowlisted)
    {
        if (!string.Equals(
                startupProfile?.Trim(),
                ExecutionAdmissionPolicy.PublicDemoProfile,
                StringComparison.OrdinalIgnoreCase))
        {
            return new PublicDemoEdgeDecision(true, "edge-not-applicable", "The public-demo edge only restricts the public-demo-readonly profile.");
        }

        if (!IsSafeMethod(method))
        {
            return new PublicDemoEdgeDecision(
                false,
                ReadOnlyDeniedCode,
                $"The public demo edge accepts only GET, HEAD, and OPTIONS ({method} rejected).");
        }

        if (!pathAllowlisted)
        {
            return new PublicDemoEdgeDecision(
                false,
                ReadOnlyDeniedCode,
                "This route is not on the public demo read allowlist.");
        }

        return new PublicDemoEdgeDecision(true, "edge-allowed", "Allowlisted public demo read.");
    }
}
