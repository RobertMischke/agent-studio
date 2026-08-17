namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Deployment profiles that change the server's execution authority. The public
/// demo value is deliberately shared by the Studio backend and the standalone
/// Task Server so the two authority boundaries cannot drift onto different
/// spellings.
/// </summary>
public static class DeploymentProfiles
{
    public const string PublicDemoReadonly = "public-demo-readonly";
}

public enum ExecutionAdmissionPath
{
    Claim,
    Start,
    Continue,
    Review,
    Chat,
    Preview,
    PostStep,
    Mutation,
    RepositoryTool,
}

public sealed record ExecutionAdmissionDecision(
    bool Allowed,
    string? Code = null,
    string? Message = null)
{
    public const string DisabledCode = "execution-disabled";
    public const string DisabledMessage =
        "Execution is disabled by the public-demo-readonly deployment profile.";

    public static ExecutionAdmissionDecision Allow { get; } = new(true);
    public static ExecutionAdmissionDecision Deny { get; } =
        new(false, DisabledCode, DisabledMessage);
}

/// <summary>
/// Pure execution-admission policy. Profile identity is captured once during
/// startup; no project setting, browser request, or management route can change
/// this decision for the lifetime of the process.
/// </summary>
public sealed class ExecutionAdmissionPolicy
{
    public ExecutionAdmissionPolicy(string? deploymentProfile)
    {
        DeploymentProfile = string.IsNullOrWhiteSpace(deploymentProfile)
            ? "local"
            : deploymentProfile.Trim().ToLowerInvariant();
    }

    public string DeploymentProfile { get; }

    public bool IsPublicDemoLocked => string.Equals(
        DeploymentProfile,
        DeploymentProfiles.PublicDemoReadonly,
        StringComparison.Ordinal);

    public ExecutionAdmissionDecision Decide(ExecutionAdmissionPath _)
        => IsPublicDemoLocked
            ? ExecutionAdmissionDecision.Deny
            : ExecutionAdmissionDecision.Allow;
}
