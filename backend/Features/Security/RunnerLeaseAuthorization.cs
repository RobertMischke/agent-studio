namespace AgentStudio.Security;

public static class RunnerLeaseAuthorization
{
    public static bool IsCurrent(
        HttpContext context,
        RunLeaseService leases,
        string taskKey,
        string? runnerId,
        string? leaseId,
        long fencingToken)
    {
        if (context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] is not RunnerPrincipal principal)
            return true;

        return string.Equals(principal.RunnerId, runnerId, StringComparison.Ordinal)
               && leases.IsCurrent(taskKey, leaseId ?? string.Empty, fencingToken, principal.RunnerId);
    }
}
