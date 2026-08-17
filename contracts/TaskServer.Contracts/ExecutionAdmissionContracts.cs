namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Execution boundaries that must remain closed in the public demo. Keep this
/// list exhaustive: startup proof evaluates every value, and route inventories
/// bind each executable endpoint to one value.
/// </summary>
public enum ExecutionAdmissionPath
{
    Claim,
    Start,
    Continue,
    Review,
    Chat,
    Preview,
    PostStep,
}

public sealed record ExecutionAdmissionDecision(
    bool Allowed,
    string Code,
    string Message);

/// <summary>
/// Pure, shared execution-admission policy for Studio and the standalone Task
/// Server. The public-demo profile has no allow branch and cannot be changed at
/// runtime.
/// </summary>
public static class ExecutionAdmissionPolicy
{
    public const string PublicDemoProfile = "public-demo-readonly";
    public const string ExecutionDisabledCode = "execution-disabled";

    public static ExecutionAdmissionDecision Decide(
        string? startupProfile,
        ExecutionAdmissionPath path)
    {
        if (string.Equals(
                startupProfile?.Trim(),
                PublicDemoProfile,
                StringComparison.OrdinalIgnoreCase))
        {
            return new ExecutionAdmissionDecision(
                false,
                ExecutionDisabledCode,
                $"Execution is disabled by the startup-only public demo profile ({PathName(path)}).");
        }

        return new ExecutionAdmissionDecision(true, "execution-enabled", "Execution admission is enabled.");
    }

    public static IReadOnlyList<ExecutionAdmissionPath> AllPaths { get; } =
        Enum.GetValues<ExecutionAdmissionPath>();

    public static string PathName(ExecutionAdmissionPath path) => path switch
    {
        ExecutionAdmissionPath.PostStep => "post-step",
        _ => path.ToString().ToLowerInvariant(),
    };
}
