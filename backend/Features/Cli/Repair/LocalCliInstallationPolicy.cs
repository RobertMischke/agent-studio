namespace AgentStudio.Cli;

/// <summary>
/// Pure classification for an unavailable local CLI. The package and callable
/// shim facts come from the npm boundary, which keeps this decision portable
/// and directly testable on non-Windows build agents.
/// </summary>
public static class LocalCliInstallationPolicy
{
    public static LocalCliInstallationState Classify(
        bool cliAvailable,
        bool packagePresent,
        bool callableShimPresent)
    {
        if (cliAvailable) return LocalCliInstallationState.Available;
        if (!packagePresent) return LocalCliInstallationState.Uninstalled;
        return callableShimPresent
            ? LocalCliInstallationState.BrokenInstall
            : LocalCliInstallationState.MissingShimWithPackagePresent;
    }

    public static bool MayAttemptRepair(DateTime? lastAttemptAt, DateTime now, TimeSpan cooldown)
        => !lastAttemptAt.HasValue || now - lastAttemptAt.Value >= cooldown;
}

public enum LocalCliInstallationState
{
    Available,
    MissingShimWithPackagePresent,
    Uninstalled,
    BrokenInstall,
}
