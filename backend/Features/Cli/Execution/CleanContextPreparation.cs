using AgentStudio.CliHosting;

namespace AgentStudio.Cli;

/// <summary>
/// Studio projection of a host-owned, task-stable clean-context lease. The
/// shared store owns path composition, seeding, restart adoption, and retention
/// for both the local backend and the standalone Agent Host.
/// </summary>
public sealed class CleanContextPreparation : IDisposable
{
    private readonly TaskCleanContextLease _lease;

    internal CleanContextPreparation(
        TaskCleanContextLease lease,
        IReadOnlyList<CliContextSource> sources)
    {
        _lease = lease;
        Sources = sources;
    }

    public string CliType => _lease.CliType;

    /// <summary>
    /// Absolute path of the persistent per-task config home. The historical
    /// property name remains wire-compatible with existing execution-context UI.
    /// </summary>
    public string TempHome => _lease.HomePath;

    public IReadOnlyDictionary<string, string> EnvOverrides => _lease.Environment;
    public IReadOnlyList<CliContextSource> Sources { get; }
    public bool Reused => _lease.Reused;

    /// <summary>Delete a newly created home after a pre-adoption start failure.</summary>
    internal bool Delete() => _lease.Delete();

    /// <summary>
    /// Release the handle without deleting task state. The retention sweep owns
    /// deletion after the bounded continuation window.
    /// </summary>
    public void Dispose() => _lease.Dispose();
}

/// <summary>
/// Backend adapter over <see cref="TaskCleanContextStore"/>. This type only maps
/// shared host facts into Studio's execution-context observability model; it
/// does not compose a second path or seed policy.
/// </summary>
public static class CleanContextPreparer
{
    public static CleanContextPreparation? PrepareClaude(
        string? userHome,
        string taskIdentity,
        ILogger? logger = null,
        string? rootOverride = null)
        => Prepare(CliTypes.Claude, userHome, taskIdentity, logger, rootOverride);

    public static CleanContextPreparation? PrepareCodex(
        string? userHome,
        string taskIdentity,
        ILogger? logger = null,
        string? rootOverride = null)
        => Prepare(CliTypes.Codex, userHome, taskIdentity, logger, rootOverride);

    public static bool TryGetExistingHome(
        string cliType,
        string? userHome,
        string taskIdentity,
        out string? home,
        string? rootOverride = null)
        => TaskCleanContextStore.TryGetExistingHome(
            cliType,
            taskIdentity,
            out home,
            userHome,
            rootOverride);

    private static CleanContextPreparation? Prepare(
        string cliType,
        string? userHome,
        string taskIdentity,
        ILogger? logger,
        string? rootOverride)
    {
        try
        {
            var lease = TaskCleanContextStore.Acquire(
                cliType,
                taskIdentity,
                userHome,
                rootOverride);
            foreach (var seed in lease.SeededFiles.Where(seed =>
                         seed.SharedCredential && seed.Method == CleanContextSeedMethod.Copy))
            {
                logger?.LogWarning(
                    "Clean-context {Cli}: {File} could only be copied from {Source}; concurrent OAuth refreshes may drift",
                    cliType,
                    seed.RelativePath,
                    seed.SourcePath);
            }

            logger?.LogInformation(
                "Clean-context {Cli}: {Mode} task home {Home}",
                cliType,
                lease.Reused ? "reused" : "seeded",
                lease.HomePath);
            return new CleanContextPreparation(lease, BuildSources(lease));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "Could not acquire stable clean-context home for {Cli}; falling back to shared",
                cliType);
            return null;
        }
    }

    private static IReadOnlyList<CliContextSource> BuildSources(TaskCleanContextLease lease)
    {
        var sources = new List<CliContextSource>
        {
            new()
            {
                Kind = CliContextSourceKinds.Env,
                Label = lease.EnvironmentVariable,
                Path = lease.HomePath,
                Exists = Directory.Exists(lease.HomePath),
                Detail = lease.Reused
                    ? "task-stable clean-context home reused for session continuation"
                    : "task-stable clean-context home seeded outside the OS temporary directory",
            },
        };

        sources.AddRange(lease.SeededFiles.Select(seed => new CliContextSource
        {
            Kind = CliContextSourceKinds.GlobalConfig,
            Label = seed.SharedCredential ? $"Linked {seed.RelativePath}" : $"Seeded {seed.RelativePath}",
            Path = seed.DestinationPath,
            Exists = File.Exists(seed.DestinationPath),
            Detail = seed.Method switch
            {
                CleanContextSeedMethod.HardLink => $"hard-linked from {seed.SourcePath}",
                CleanContextSeedMethod.SymbolicLink => $"symbolically linked from {seed.SourcePath}",
                CleanContextSeedMethod.Copy when seed.SharedCredential =>
                    $"copied from {seed.SourcePath}; link unavailable, so concurrent refreshes may drift",
                CleanContextSeedMethod.Copy => $"copied from {seed.SourcePath}",
                _ => $"retained from the task's existing clean home; source is {seed.SourcePath}",
            },
        }));
        return sources;
    }
}
