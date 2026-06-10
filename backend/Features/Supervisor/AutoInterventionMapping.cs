namespace AgentStudio.Supervisor;

/// <summary>
/// Pure rule that maps a <see cref="SupervisorAdvisory"/> to an automatic
/// emergency-primitive call, or to none. Used by
/// <see cref="AutoInterventionHostedService"/> when the per-project policy
/// is enabled. Off by default; this rule is consulted only when the user
/// has explicitly turned auto-intervention on for the project.
/// </summary>
public static class AutoInterventionMapping
{
    public sealed record Action(SupervisorInterventionKind Kind, string Reason);

    public static Action? MapAdvisory(SupervisorAdvisory a, SupervisorSeverity threshold)
    {
        // Feedback-loop guard. Auto-intervention never acts on the supervisor's
        // own writes; only HardCheck and SoftReasoning advisories qualify.
        if (a.Source == SupervisorSource.AutoIntervention) return null;
        if (a.Source == SupervisorSource.User) return null;
        if (CompareSeverity(a.Severity, threshold) < 0) return null;

        return a.Topic switch
        {
            "quota-critical" => new Action(
                SupervisorInterventionKind.PausePickup,
                $"auto: quota-critical -> pause pickup (advisory: {a.Message})"),
            "no-progress" => new Action(
                SupervisorInterventionKind.CancelRun,
                $"auto: no-progress -> cancel run (advisory: {a.Message})"),
            "error-burst" => new Action(
                SupervisorInterventionKind.PausePickup,
                $"auto: error-burst -> pause pickup (advisory: {a.Message})"),
            "tool-call-repeat" => new Action(
                SupervisorInterventionKind.CancelRun,
                $"auto: tool-call-repeat -> cancel run (advisory: {a.Message})"),
            _ => null,
        };
    }

    private static int CompareSeverity(SupervisorSeverity a, SupervisorSeverity b) =>
        Rank(a) - Rank(b);

    private static int Rank(SupervisorSeverity s) => s switch
    {
        SupervisorSeverity.Info => 0,
        SupervisorSeverity.Warn => 1,
        SupervisorSeverity.High => 2,
        _ => 0,
    };
}
