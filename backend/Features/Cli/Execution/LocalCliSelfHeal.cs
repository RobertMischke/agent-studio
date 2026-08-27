using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Shared;

namespace AgentStudio.Cli;

internal enum NpmCliInstallState
{
    MissingShimWithPackage,
    TrulyUninstalled,
    NonShimFailure,
}

internal sealed record NpmCliInstallInspection(
    NpmCliInstallState State,
    string Prefix,
    string PackageRoot,
    string? PackageVersion,
    DateTime? PackageChangedAt,
    IReadOnlyList<string> ExpectedShims,
    IReadOnlyList<string> ExistingShims,
    IReadOnlyList<string> NearbyNpmActivity);

internal sealed record CliRepairJournalEntry(
    DateTime ObservedAt,
    string CliType,
    string PackageName,
    string Classification,
    DateTime? AttemptedAt,
    string Outcome,
    string? CliVersionBefore,
    string? PackageVersionBefore,
    string? CliVersionAfter,
    string? PackageVersionAfter,
    int? NpmExitCode,
    string? Error,
    string Prefix,
    DateTime? PackageChangedAt,
    IReadOnlyList<string> ExpectedShims,
    IReadOnlyList<string> ExistingShims,
    IReadOnlyList<string> NearbyNpmActivity);

internal delegate Task<ProcessResult> NpmRepairLauncher(
    string fileName,
    IReadOnlyList<string> arguments,
    CancellationToken cancellationToken);

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Classifies a failed local Claude or Codex probe and repairs only the narrow
/// npm state where the global package still exists but its Windows command
/// shim has disappeared. Attempts are durable and rate-limited across backend
/// restarts.
/// </summary>
public sealed class LocalCliSelfHeal
{
    public static readonly TimeSpan RepairInterval = TimeSpan.FromHours(1);
    internal const string JournalFileName = "cli-self-heal.jsonl";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ILogger<LocalCliSelfHeal> _logger;
    private readonly Func<DateTime> _clock;
    private readonly NpmRepairLauncher _launcher;
    private readonly bool _isWindows;
    private readonly string _journalPath;
    private readonly string? _configuredPrefix;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusGate = new();
    private readonly Dictionary<string, CliRepairStatus> _latest =
        new(StringComparer.OrdinalIgnoreCase);

    public LocalCliSelfHeal(IConfiguration configuration, ILogger<LocalCliSelfHeal> logger)
        : this(
            logger,
            ResolveRuntimeDirectory(configuration),
            () => DateTime.UtcNow,
            RunNpmAsync,
            OperatingSystem.IsWindows(),
            configuration["CliSelfHeal:NpmPrefix"])
    {
    }

    internal LocalCliSelfHeal(
        ILogger<LocalCliSelfHeal> logger,
        string runtimeDirectory,
        Func<DateTime> clock,
        NpmRepairLauncher launcher,
        bool isWindows,
        string? configuredPrefix)
    {
        _logger = logger;
        _clock = clock;
        _launcher = launcher;
        _isWindows = isWindows;
        _configuredPrefix = configuredPrefix;
        Directory.CreateDirectory(runtimeDirectory);
        _journalPath = Path.Combine(runtimeDirectory, JournalFileName);
        LoadLatestStatuses();
    }

    public IReadOnlyList<CliRepairStatus> Snapshot()
    {
        lock (_statusGate)
            return _latest.Values.OrderBy(item => item.CliType, StringComparer.Ordinal).ToArray();
    }

    internal async Task<bool> TryRepairAsync(
        string cliType,
        string resolvedCliPath,
        string? previousCliVersion,
        Func<(bool Available, string? Version, string Path)> verify,
        CancellationToken cancellationToken)
    {
        if (!_isWindows || !TryDefinition(cliType, out var definition)) return false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _clock().ToUniversalTime();
            var inspection = Inspect(definition, PrefixCandidates(resolvedCliPath));
            if (inspection.State != NpmCliInstallState.MissingShimWithPackage)
            {
                var classification = inspection.State == NpmCliInstallState.TrulyUninstalled
                    ? "truly-uninstalled"
                    : "non-shim-failure";
                var detail = inspection.State == NpmCliInstallState.TrulyUninstalled
                    ? $"{definition.DisplayName} is not installed; no automatic install was attempted."
                    : $"{definition.DisplayName} failed its version probe, but its npm shims are present; no shim repair was attempted.";
                SetStatus(new CliRepairStatus(
                    cliType,
                    classification,
                    now,
                    detail,
                    previousCliVersion,
                    null,
                    null));
                _logger.LogInformation(
                    "Local CLI self-heal classified cli={Cli} state={State} package={Package} prefix={Prefix}",
                    cliType, classification, definition.PackageName, inspection.Prefix);
                return false;
            }

            var lastAttempt = LastAttempt(cliType);
            if (lastAttempt is not null && now - lastAttempt.Value < RepairInterval)
            {
                var next = lastAttempt.Value.Add(RepairInterval);
                var detail = $"{definition.DisplayName} npm shims are missing; repair is limited to one attempt per hour and may retry after {next:o}.";
                Append(new CliRepairJournalEntry(
                    now,
                    cliType,
                    definition.PackageName,
                    "missing-shim-with-package",
                    null,
                    "throttled",
                    previousCliVersion,
                    inspection.PackageVersion,
                    null,
                    null,
                    null,
                    null,
                    inspection.Prefix,
                    inspection.PackageChangedAt,
                    inspection.ExpectedShims,
                    inspection.ExistingShims,
                    inspection.NearbyNpmActivity));
                _logger.LogInformation(
                    "Local CLI repair throttled cli={Cli} nextAttemptAt={NextAttemptAt}",
                    cliType, next);
                return false;
            }

            ProcessResult? npmResult = null;
            string? launchError = null;
            Append(new CliRepairJournalEntry(
                now,
                cliType,
                definition.PackageName,
                "missing-shim-with-package",
                now,
                "attempt-started",
                previousCliVersion,
                inspection.PackageVersion,
                null,
                null,
                null,
                null,
                inspection.Prefix,
                inspection.PackageChangedAt,
                inspection.ExpectedShims,
                inspection.ExistingShims,
                inspection.NearbyNpmActivity));
            try
            {
                var npm = GenericCliExecutionService.ResolveExecutable("npm");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                npmResult = await _launcher(
                    npm,
                    ["install", "-g", definition.PackageName],
                    timeout.Token);
            }
            catch (Exception exception)
            {
                launchError = exception is OperationCanceledException
                    ? "npm repair timed out after five minutes"
                    : exception.Message;
            }

            var afterProbe = npmResult?.Success == true
                ? verify()
                : (Available: false, Version: (string?)null, Path: resolvedCliPath);
            var afterInspection = Inspect(definition, PrefixCandidates(resolvedCliPath));
            var repaired = npmResult?.Success == true && afterProbe.Available;
            var error = repaired
                ? null
                : launchError
                  ?? (npmResult is { Success: false }
                      ? $"npm install exited with code {npmResult.ExitCode}"
                      : $"{definition.Executable} --version still failed after npm install");
            var outcome = repaired ? "repaired" : "repair-failed";
            var message = repaired
                ? $"CLI repaired at {now:o}"
                : $"CLI repair failed at {now:o}: {error}";
            SetStatus(new CliRepairStatus(
                cliType,
                outcome,
                now,
                message,
                previousCliVersion,
                afterProbe.Version,
                repaired ? null : now.Add(RepairInterval)));
            Append(new CliRepairJournalEntry(
                now,
                cliType,
                definition.PackageName,
                "missing-shim-with-package",
                now,
                outcome,
                previousCliVersion,
                inspection.PackageVersion,
                afterProbe.Version,
                afterInspection.PackageVersion,
                npmResult?.ExitCode,
                error,
                inspection.Prefix,
                inspection.PackageChangedAt,
                inspection.ExpectedShims,
                inspection.ExistingShims,
                inspection.NearbyNpmActivity));

            if (repaired)
            {
                _logger.LogInformation(
                    "Local CLI repaired cli={Cli} at={RepairedAt} package={Package} cliVersionBefore={BeforeVersion} cliVersionAfter={AfterVersion} packageVersionBefore={PackageBefore} packageVersionAfter={PackageAfter} npmActivity={NpmActivity}",
                    cliType,
                    now,
                    definition.PackageName,
                    previousCliVersion ?? "unknown",
                    afterProbe.Version ?? "unknown",
                    inspection.PackageVersion ?? "unknown",
                    afterInspection.PackageVersion ?? "unknown",
                    string.Join("; ", inspection.NearbyNpmActivity));
            }
            else
            {
                _logger.LogError(
                    "Local CLI repair failed cli={Cli} at={AttemptedAt} package={Package} error={Error} nextAttemptAt={NextAttemptAt}",
                    cliType, now, definition.PackageName, error, now.Add(RepairInterval));
            }
            return repaired;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static NpmCliInstallInspection Inspect(
        NpmCliDefinition definition,
        IEnumerable<string> prefixes)
    {
        var candidates = prefixes
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var prefix in candidates)
        {
            var packageRoot = Path.Combine(
                prefix,
                "node_modules",
                definition.PackageName.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(packageRoot)) continue;
            var expectedShims = ExpectedShims(prefix, definition.Executable);
            var existingShims = expectedShims.Where(File.Exists).ToArray();
            var hasLaunchableWindowsShim = new[]
            {
                Path.Combine(prefix, $"{definition.Executable}.cmd"),
                Path.Combine(prefix, $"{definition.Executable}.exe")
            }.Any(File.Exists);
            return new NpmCliInstallInspection(
                !hasLaunchableWindowsShim
                    ? NpmCliInstallState.MissingShimWithPackage
                    : NpmCliInstallState.NonShimFailure,
                prefix,
                packageRoot,
                ReadPackageVersion(packageRoot),
                SafeLastWriteTime(Path.Combine(packageRoot, "package.json")),
                expectedShims,
                existingShims,
                NearbyNpmActivity(prefix, definition.Executable, definition.PackageName));
        }

        var fallbackPrefix = candidates.FirstOrDefault() ?? string.Empty;
        return new NpmCliInstallInspection(
            NpmCliInstallState.TrulyUninstalled,
            fallbackPrefix,
            Path.Combine(
                fallbackPrefix,
                "node_modules",
                definition.PackageName.Replace('/', Path.DirectorySeparatorChar)),
            null,
            null,
            ExpectedShims(fallbackPrefix, definition.Executable),
            [],
            NearbyNpmActivity(fallbackPrefix, definition.Executable, definition.PackageName));
    }

    private IEnumerable<string> PrefixCandidates(string resolvedCliPath)
    {
        if (!string.IsNullOrWhiteSpace(_configuredPrefix)) yield return _configuredPrefix;
        if (Path.IsPathRooted(resolvedCliPath))
        {
            var directory = Path.GetDirectoryName(resolvedCliPath);
            if (!string.IsNullOrWhiteSpace(directory)) yield return directory;
        }
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrWhiteSpace(appData)) yield return Path.Combine(appData, "npm");
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return directory;
        }
    }

    private DateTime? LastAttempt(string cliType)
    {
        if (!File.Exists(_journalPath)) return null;
        DateTime? latest = null;
        try
        {
            foreach (var line in File.ReadLines(_journalPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var entry = JsonSerializer.Deserialize<CliRepairJournalEntry>(line, Json);
                if (entry?.AttemptedAt is not { } attempted
                    || !string.Equals(entry.CliType, cliType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (latest is null || attempted > latest) latest = attempted;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not read CLI repair journal at {Path}", _journalPath);
        }
        return latest;
    }

    private void Append(CliRepairJournalEntry entry)
    {
        try
        {
            File.AppendAllText(_journalPath, JsonSerializer.Serialize(entry, Json) + Environment.NewLine);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not append CLI repair journal at {Path}", _journalPath);
        }
    }

    private void LoadLatestStatuses()
    {
        if (!File.Exists(_journalPath)) return;
        try
        {
            foreach (var line in File.ReadLines(_journalPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var entry = JsonSerializer.Deserialize<CliRepairJournalEntry>(line, Json);
                if (entry is null) continue;
                var message = entry.Outcome switch
                {
                    "repaired" => $"CLI repaired at {entry.ObservedAt:o}",
                    "repair-failed" => $"CLI repair failed at {entry.ObservedAt:o}: {entry.Error}",
                    _ => null,
                };
                if (message is null) continue;
                SetStatus(new CliRepairStatus(
                    entry.CliType,
                    entry.Outcome,
                    entry.ObservedAt,
                    message,
                    entry.CliVersionBefore,
                    entry.CliVersionAfter,
                    entry.Outcome == "repair-failed"
                        ? entry.ObservedAt.Add(RepairInterval)
                        : null));
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not restore CLI repair status from {Path}", _journalPath);
        }
    }

    private void SetStatus(CliRepairStatus status)
    {
        lock (_statusGate) _latest[status.CliType] = status;
    }

    private static string ResolveRuntimeDirectory(IConfiguration configuration)
    {
        var taskRepository = configuration["TaskRepository"];
        return !string.IsNullOrWhiteSpace(taskRepository)
            ? Path.Combine(taskRepository, ".runtime")
            : Path.Combine(AppContext.BaseDirectory, "runtime");
    }

    private static async Task<ProcessResult> RunNpmAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception exception)
            {
                SilentCatch.Note(exception, "LocalCliSelfHeal: npm process-tree stop is best-effort");
            }
            throw;
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static IReadOnlyList<string> ExpectedShims(string prefix, string executable)
        => [
            Path.Combine(prefix, executable),
            Path.Combine(prefix, executable + ".cmd"),
            Path.Combine(prefix, executable + ".ps1"),
        ];

    private static string? ReadPackageVersion(string packageRoot)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageRoot, "package.json")));
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? SafeLastWriteTime(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null; }
        catch { return null; }
    }

    private static IReadOnlyList<string> NearbyNpmActivity(
        string prefix,
        string executable,
        string packageName)
    {
        var activity = new List<string>();
        try
        {
            if (Directory.Exists(prefix))
            {
                activity.AddRange(Directory.GetFiles(prefix, $".{executable}*")
                    .Select(path => $"shim-orphan:{Path.GetFileName(path)}@{File.GetLastWriteTimeUtc(path):o}"));
            }
        }
        catch (Exception exception)
        {
            SilentCatch.Note(exception, "LocalCliSelfHeal: orphan evidence is best-effort");
            // Evidence is best-effort and must never block repair.
        }

        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData)) return activity;
        var npmLogs = Path.Combine(localAppData, "npm-cache", "_logs");
        try
        {
            if (Directory.Exists(npmLogs))
            {
                activity.AddRange(Directory.GetFiles(npmLogs, "*-debug-0.log")
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Take(10)
                    .Select(file =>
                    {
                        var summary = SummarizeNpmLog(file.FullName, packageName);
                        return $"npm-log:{file.Name}@{file.LastWriteTimeUtc:o}"
                               + (summary is null ? string.Empty : $":{summary}");
                    }));
            }
        }
        catch (Exception exception)
        {
            SilentCatch.Note(exception, "LocalCliSelfHeal: npm log evidence is best-effort");
            // Evidence is best-effort and must never block repair.
        }
        return activity;
    }

    internal static string? SummarizeNpmLog(string path, string packageName)
    {
        try
        {
            var relevant = File.ReadLines(path)
                .Take(250)
                .Where(line => line.Contains(packageName, StringComparison.OrdinalIgnoreCase)
                               || line.Contains("verbose title", StringComparison.OrdinalIgnoreCase)
                               || line.Contains("verbose argv", StringComparison.OrdinalIgnoreCase))
                .Select(line => Regex.Replace(
                    line.Trim(),
                    @"(?i)(token|_auth|password)([=:]\s*)\S+",
                    "$1$2[redacted]"))
                .Take(3)
                .ToArray();
            return relevant.Length == 0 ? null : string.Join(" | ", relevant);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryDefinition(string cliType, out NpmCliDefinition definition)
    {
        definition = cliType.Trim().ToLowerInvariant() switch
        {
            CliTypes.Claude => new NpmCliDefinition(
                CliTypes.Claude,
                "Claude CLI",
                "claude",
                "@anthropic-ai/claude-code"),
            CliTypes.Codex => new NpmCliDefinition(
                CliTypes.Codex,
                "Codex CLI",
                "codex",
                "@openai/codex"),
            _ => null!,
        };
        return definition is not null;
    }
}

internal sealed record NpmCliDefinition(
    string CliType,
    string DisplayName,
    string Executable,
    string PackageName);
