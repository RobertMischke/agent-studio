namespace AgentStudio.Tasks;

/// <summary>
/// Boot-time compatibility sweep for logs created before bounded writes were
/// introduced. It also keeps the one rotation file out of workspace evidence
/// commits by maintaining the central task repository's ignore rule.
/// </summary>
internal sealed class CliOutputLogMaintenanceService
{
    private readonly TaskScannerService _scanner;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CliOutputLogMaintenanceService> _logger;

    public CliOutputLogMaintenanceService(
        TaskScannerService scanner,
        IConfiguration configuration,
        ILogger<CliOutputLogMaintenanceService> logger)
    {
        _scanner = scanner;
        _configuration = configuration;
        _logger = logger;
    }

    internal CliOutputLogMaintenanceResult Run()
    {
        var migrated = 0;
        var failed = 0;
        var paths = _scanner.ScanAllJobs()
            .Select(job => TaskPaths.CliOutputLog(job.FolderPath))
            .Where(File.Exists)
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToList();

        foreach (var path in paths)
        {
            try
            {
                if (CliOutputLogFile.MigrateExisting(path)) migrated++;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "cli-output-log-migration-failed path={Path}", path);
            }
        }

        var ignoreUpdated = false;
        var taskRepository = _configuration["TaskRepository"];
        if (!string.IsNullOrWhiteSpace(taskRepository) && Directory.Exists(taskRepository))
        {
            try
            {
                ignoreUpdated = EnsureRotationIgnored(taskRepository);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "cli-output-log-ignore-migration-failed repository={Repository}", taskRepository);
            }
        }

        _logger.LogInformation(
            "cli-output-log-maintenance scanned={Scanned} migrated={Migrated} failed={Failed} ignoreUpdated={IgnoreUpdated}",
            paths.Count, migrated, failed, ignoreUpdated);
        return new CliOutputLogMaintenanceResult(paths.Count, migrated, failed, ignoreUpdated);
    }

    internal static bool EnsureRotationIgnored(string taskRepository)
    {
        var ignorePath = Path.Combine(Path.GetFullPath(taskRepository), ".gitignore");
        var existing = File.Exists(ignorePath) ? File.ReadAllText(ignorePath) : string.Empty;
        if (existing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => string.Equals(line, CliOutputLogFile.RotationIgnorePattern, StringComparison.Ordinal)))
        {
            return false;
        }

        var prefix = existing.Length == 0 || existing.EndsWith('\n') ? string.Empty : Environment.NewLine;
        var comment = existing.Contains("cli-output.log", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : "# Bounded CLI log rotation is runtime-only; cli-output.log remains the durable audit record."
              + Environment.NewLine;
        File.AppendAllText(
            ignorePath,
            prefix + comment + CliOutputLogFile.RotationIgnorePattern + Environment.NewLine);
        return true;
    }
}

internal sealed record CliOutputLogMaintenanceResult(
    int Scanned,
    int Migrated,
    int Failed,
    bool IgnoreUpdated);
