namespace AgentStudio.Cli;

public enum CliRepairCooldownDecision
{
    Allowed,
    OnCooldown,
}

/// <summary>
/// Pure policy: bound npm-shim repair attempts (<see cref="NpmShimHealer.TryHealClaudeAsync"/>)
/// to at most one per <see cref="DefaultWindow"/>, so a persistently-broken
/// (or truly uninstalled) CLI cannot spend the postinstall's 2-minute budget
/// on every single job spawn attempted inside that window.
///
/// <para>
/// Deterministic function over plain inputs per the repo's "pure policy
/// first" convention (docs/quality/dotnet-backend.md) - no filesystem,
/// process, network, clock, or DI setup needed to test the decision matrix.
/// The stateful check-then-act around it lives in <see cref="CliRepairGate"/>.
/// </para>
/// </summary>
public static class CliRepairCooldownPolicy
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(1);

    public static CliRepairCooldownDecision Decide(DateTime? lastAttemptUtc, DateTime nowUtc, TimeSpan window)
        => lastAttemptUtc.HasValue && nowUtc - lastAttemptUtc.Value < window
            ? CliRepairCooldownDecision.OnCooldown
            : CliRepairCooldownDecision.Allowed;
}
