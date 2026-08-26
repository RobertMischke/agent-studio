using System.Diagnostics;
using System.Text.Json;
using AgentStudio.Persistence;

namespace AgentStudio.Cli;

public sealed record LocalCliRepairStatus(
    string CliType,
    string Event,
    DateTimeOffset OccurredAt,
    string? CliVersionBefore,
    string? PackageVersionBefore,
    string? CliVersionAfter,
    string Detail,
    string JournalPath);

public sealed record LocalCliRepairJournalEntry(
    DateTimeOffset OccurredAt,
    string CliType,
    string PackageName,
    string Event,
    string? CliVersionBefore,
    string? PackageVersionBefore,
    string? CliVersionAfter,
    int? NpmExitCode,
    string Detail,
    NpmCliInstallSnapshot Before,
    NpmCliInstallSnapshot? After,
    string? NpmStdoutTail,
    string? NpmStderrTail);

/// <summary>
/// Coordinates local Windows CLI capability repair. A package must still be
/// installed while its executable npm shim is absent; genuinely uninstalled
/// CLIs and broken binaries with an existing shim are observation-only states.
/// </summary>
public sealed class LocalCliSelfHeal
{
    public static readonly TimeSpan AttemptWindow = TimeSpan.FromHours(1);

    private readonly IConfiguration _configuration;
    private readonly CliRouter _router;
    private readonly IJsonlAppender _appender;
    private readonly ILogger<LocalCliSelfHeal> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastHealthyVersions = new(StringComparer.OrdinalIgnoreCase);
    private LocalCliRepairStatus? _latest;
    private bool _journalLoaded;

    public LocalCliSelfHeal(
        IConfiguration configuration,
        CliRouter router,
        IJsonlAppender appender,
        ILogger<LocalCliSelfHeal> logger,
        TimeProvider? time = null)
    {
        _configuration = configuration;
        _router = router;
        _appender = appender;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public string JournalPath
    {
        get
        {
            var root = _configuration["TaskRepository"];
            if (!string.IsNullOrWhiteSpace(root))
                return Path.Combine(root, "logs", "cli-self-heal.jsonl");
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();
            return Path.Combine(local, "agent-taskboard", "logs", "cli-self-heal.jsonl");
        }
    }

    public LocalCliRepairStatus? Latest()
    {
        EnsureJournalLoaded();
        lock (_stateGate) return _latest;
    }

    public static bool AttemptAllowed(DateTimeOffset now, DateTimeOffset? lastAttempt)
        => lastAttempt is null || now - lastAttempt.Value >= AttemptWindow;

    public async Task ProbeAndRepairAllAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var cli in _router.All.Where(item =>
                     item.CliType is CliTypes.Claude or CliTypes.Codex))
        {
            await ProbeAndRepairAsync(cli, ct).ConfigureAwait(false);
        }
    }

    private async Task ProbeAndRepairAsync(ICliExecutionService cli, CancellationToken ct)
    {
        var probe = cli.TestCliPath();
        if (probe.Available)
        {
            RememberHealthyVersion(cli.CliType, probe.Version);
            return;
        }
        if (!IsDefaultNpmCommand(cli.CliType, cli.GetCliPath())) return;
        var npmBin = ResolveNpmBin();
        if (string.IsNullOrWhiteSpace(npmBin)) return;

        var now = _time.GetUtcNow();
        var before = NpmCliShimDetection.Inspect(
            cli.CliType,
            npmBin,
            ResolveNpmCache(),
            cliAvailable: false,
            now,
            ResolveProviderActivity(cli.CliType));
        if (before.State != NpmCliInstallState.MissingShimWithPackagePresent) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureJournalLoaded();
            now = _time.GetUtcNow();
            if (_lastAttempts.TryGetValue(cli.CliType, out var last)
                && !AttemptAllowed(now, last))
            {
                return;
            }
            // Re-read under the gate so two simultaneous capability requests
            // cannot reinstall after the first one already restored the shim.
            probe = cli.TestCliPath();
            before = NpmCliShimDetection.Inspect(
                cli.CliType,
                npmBin,
                ResolveNpmCache(),
                probe.Available,
                now,
                ResolveProviderActivity(cli.CliType));
            if (before.State != NpmCliInstallState.MissingShimWithPackagePresent) return;
            _lastAttempts[cli.CliType] = now;
            var cliVersionBefore = LastHealthyVersion(cli.CliType) ?? before.PackageVersion;

            var attempted = new LocalCliRepairJournalEntry(
                now,
                cli.CliType,
                before.PackageName,
                "repair-attempted",
                cliVersionBefore,
                before.PackageVersion,
                null,
                null,
                $"{cli.CliType} package is present but its npm command shim is missing; starting bounded global reinstall.",
                before,
                null,
                null,
                null);
            try
            {
                await _appender.AppendAsync(JournalPath, attempted, ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "cli-self-heal-journal-failed cli={Cli} event=repair-attempted path={Path}",
                    cli.CliType, JournalPath);
            }

            var command = await RunNpmInstallAsync(before.PackageName, ct).ConfigureAwait(false);
            var verify = cli.TestCliPath();
            var after = NpmCliShimDetection.Inspect(
                cli.CliType,
                npmBin,
                ResolveNpmCache(),
                verify.Available,
                _time.GetUtcNow(),
                ResolveProviderActivity(cli.CliType));
            var succeeded = command.ExitCode == 0 && verify.Available;
            if (succeeded) RememberHealthyVersion(cli.CliType, verify.Version ?? after.PackageVersion);
            var occurredAt = _time.GetUtcNow();
            var detail = succeeded
                ? $"{cli.CliType} npm shim restored; {cliVersionBefore ?? "unknown"} -> {verify.Version ?? after.PackageVersion ?? "unknown"}."
                : $"{cli.CliType} npm shim repair failed (npm exit {command.ExitCode?.ToString() ?? "timeout"}); CLI remains unavailable.";
            var eventName = succeeded ? "repair-succeeded" : "repair-failed";
            var entry = new LocalCliRepairJournalEntry(
                occurredAt,
                cli.CliType,
                before.PackageName,
                eventName,
                cliVersionBefore,
                before.PackageVersion,
                verify.Version ?? after.PackageVersion,
                command.ExitCode,
                detail,
                before,
                after,
                Tail(command.Stdout),
                Tail(command.Stderr));

            try
            {
                await _appender.AppendAsync(JournalPath, entry, ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "cli-self-heal-journal-failed cli={Cli} path={Path}", cli.CliType, JournalPath);
            }

            lock (_stateGate)
            {
                _latest = new LocalCliRepairStatus(
                    cli.CliType,
                    eventName,
                    occurredAt,
                    cliVersionBefore,
                    before.PackageVersion,
                    verify.Version ?? after.PackageVersion,
                    detail,
                    JournalPath);
            }

            if (succeeded)
            {
                _logger.LogInformation(
                    "cli-self-heal-repaired cli={Cli} occurredAt={OccurredAt} before={Before} after={After} journal={Journal}",
                    cli.CliType, occurredAt, cliVersionBefore, verify.Version ?? after.PackageVersion, JournalPath);
            }
            else
            {
                _logger.LogError(
                    "cli-self-heal-failed cli={Cli} occurredAt={OccurredAt} npmExit={ExitCode} before={Before} journal={Journal} stderr={Stderr}",
                    cli.CliType, occurredAt, command.ExitCode, before.PackageVersion, JournalPath, Tail(command.Stderr));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureJournalLoaded()
    {
        lock (_stateGate)
        {
            if (_journalLoaded) return;
            _journalLoaded = true;
            if (!File.Exists(JournalPath)) return;
            try
            {
                foreach (var line in File.ReadLines(JournalPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    LocalCliRepairJournalEntry? entry;
                    try { entry = JsonSerializer.Deserialize<LocalCliRepairJournalEntry>(line, JsonOptions); }
                    catch { continue; }
                    if (entry is null) continue;
                    if (!_lastAttempts.TryGetValue(entry.CliType, out var last) || entry.OccurredAt > last)
                        _lastAttempts[entry.CliType] = entry.OccurredAt;
                    if (entry.Event is "repair-succeeded" or "repair-failed"
                        && (_latest is null || entry.OccurredAt > _latest.OccurredAt))
                    {
                        _latest = new LocalCliRepairStatus(
                            entry.CliType,
                            entry.Event,
                            entry.OccurredAt,
                            entry.CliVersionBefore,
                            entry.PackageVersionBefore,
                            entry.CliVersionAfter,
                            entry.Detail,
                            JournalPath);
                        if (entry.Event == "repair-succeeded"
                            && !string.IsNullOrWhiteSpace(entry.CliVersionAfter))
                        {
                            _lastHealthyVersions[entry.CliType] = entry.CliVersionAfter;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "cli-self-heal-journal-read-failed path={Path}", JournalPath);
            }
        }
    }

    private string? ResolveNpmBin()
    {
        var configured = _configuration["CliSelfHeal:NpmBinPath"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        return string.IsNullOrWhiteSpace(appData) ? null : Path.Combine(appData, "npm");
    }

    private string? LastHealthyVersion(string cliType)
    {
        lock (_stateGate)
            return _lastHealthyVersions.GetValueOrDefault(cliType);
    }

    private void RememberHealthyVersion(string cliType, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return;
        lock (_stateGate)
            _lastHealthyVersions[cliType] = version;
    }

    private string? ResolveNpmCache()
    {
        var configured = _configuration["CliSelfHeal:NpmCachePath"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData) ? null : Path.Combine(localAppData, "npm-cache");
    }

    private string? ResolveProviderActivity(string cliType)
    {
        var configured = _configuration[$"CliSelfHeal:{cliType}:ActivityPath"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile)) return null;
        return cliType == CliTypes.Claude
            ? Path.Combine(profile, ".claude", "debug")
            : Path.Combine(profile, ".codex", "log");
    }

    private async Task<NpmCommandResult> RunNpmInstallAsync(string packageName, CancellationToken ct)
    {
        var executable = _configuration["CliSelfHeal:NpmExecutable"] ?? "npm";
        var resolved = GenericCliExecutionService.ResolveExecutable(executable);
        var commandInterpreter = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = commandInterpreter,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        process.StartInfo.ArgumentList.Add("/d");
        process.StartInfo.ArgumentList.Add("/s");
        process.StartInfo.ArgumentList.Add("/c");
        process.StartInfo.ArgumentList.Add($"\"{resolved.Replace("\"", "\"\"")}\" install --global \"{packageName}\"");

        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, "LocalCliSelfHeal: npm timeout kill"); }
                return new NpmCommandResult(null, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            }
            return new NpmCommandResult(
                process.ExitCode,
                await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return new NpmCommandResult(null, string.Empty, ex.Message);
        }
    }

    private static bool IsDefaultNpmCommand(string cliType, string path)
        => string.Equals(Path.GetFileNameWithoutExtension(path), cliType, StringComparison.OrdinalIgnoreCase);

    private static string? Tail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Replace("\r\n", "\n").Trim();
        return normalized.Length <= 8_000 ? normalized : normalized[^8_000..];
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record NpmCommandResult(int? ExitCode, string Stdout, string Stderr);
}
