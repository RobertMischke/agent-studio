using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Management;

public sealed record WindowsTunnelRegisterRequest(
    string SshTarget,
    int RemotePort,
    int TaskServerPort,
    int IntervalMinutes,
    int ProbeIntervalSeconds,
    int FailureThreshold);

public sealed record WindowsTunnelTaskStatus(
    string TaskName,
    bool Registered,
    string? State,
    DateTime? LastRunTime,
    int? LastTaskResult,
    DateTime? NextRunTime);

public sealed record WindowsTunnelKeeperHealth(
    string? Status,
    string? Message,
    DateTime? ObservedAt,
    int? RepairAttempts);

public sealed record WindowsTunnelWatchdogHealth(
    DateTime? LastHealSucceededAt,
    DateTime? LastHealFailedAt,
    DateTime? LastProbeFailedAt,
    string? LastEvent,
    DateTime? LastEventAt);

/// <summary>
/// "registered/running/last heal" for the interim Windows control-plane tunnel
/// (AGT-2664). <see cref="Platform"/> is "unsupported" on a non-Windows Studio
/// host, so the UI can show a quiet not-applicable state instead of an error.
/// </summary>
public sealed record WindowsTunnelStatusResponse(
    string Platform,
    DateTime ObservedAt,
    WindowsTunnelTaskStatus? KeeperTask,
    WindowsTunnelKeeperHealth? KeeperHealth,
    WindowsTunnelTaskStatus? WatchdogTask,
    WindowsTunnelWatchdogHealth? WatchdogHealth,
    bool AlarmActive,
    string? Detail);

public sealed record WindowsTunnelRegistrationResponse(
    string Platform,
    bool Ok,
    bool Elevated,
    string? Detail,
    DateTime RequestedAt);

public interface IWindowsTunnelProvisioner
{
    Task<WindowsTunnelStatusResponse> GetStatusAsync(CancellationToken cancellationToken);

    Task<WindowsTunnelRegistrationResponse> RegisterAsync(
        WindowsTunnelRegisterRequest request,
        CancellationToken cancellationToken);
}

public sealed class WindowsTunnelProvisioningException(string message) : Exception(message);

/// <summary>
/// Bounds for the operator-supplied registration fields, mirroring the
/// [ValidateRange]/[ValidatePattern] bounds already enforced by
/// register-tunnel-keeper.ps1 and register-tunnel-watchdog.ps1 so an invalid
/// request never reaches a process launch.
/// </summary>
public static partial class WindowsTunnelProvisioningPolicy
{
    public static string? Validate(WindowsTunnelRegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SshTarget) || !SshTargetPattern().IsMatch(request.SshTarget.Trim()))
            return "SSH target must be a configured alias or user@host without shell characters.";
        if (request.RemotePort is < 1 or > 65535)
            return "Remote port must be between 1 and 65535.";
        if (request.TaskServerPort is < 1 or > 65535)
            return "Task Server port must be between 1 and 65535.";
        if (request.IntervalMinutes is < 1 or > 60)
            return "Keeper interval must be between 1 and 60 minutes.";
        if (request.ProbeIntervalSeconds is < 10 or > 3600)
            return "Watchdog probe interval must be between 10 and 3600 seconds.";
        if (request.FailureThreshold is < 1 or > 20)
            return "Watchdog failure threshold must be between 1 and 20.";
        return null;
    }

    [GeneratedRegex(@"^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex SshTargetPattern();
}

/// <summary>
/// Shells the repository-owned PowerShell scripts under
/// deploy/windows/agent-runner-tunnel/ to read Scheduled Task status and to
/// run the self-elevating registration flow (AGT-2664). Both scripts are the
/// implementation; this class only launches them and parses their one JSON
/// line of stdout, following the WindowsWorktreeOrphanSweeper /
/// SshProviderAuthProvisioner precedent already used elsewhere in this file's
/// namespace.
/// </summary>
public sealed class PowerShellWindowsTunnelProvisioner(IHostEnvironment environment) : IWindowsTunnelProvisioner
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(180);

    public async Task<WindowsTunnelStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsTunnelStatusResponse(
                "unsupported", DateTime.UtcNow, null, null, null, null, false,
                "The Windows tunnel keeper and watchdog only run on a Windows control-plane host.");
        }

        var scriptPath = ResolveScriptPath(environment.ContentRootPath, "tunnel-status.ps1");
        if (!File.Exists(scriptPath))
        {
            return new WindowsTunnelStatusResponse(
                "windows", DateTime.UtcNow, null, null, null, null, false,
                $"Status script not found at {scriptPath}.");
        }

        var startInfo = BuildPowerShellStartInfo(scriptPath, []);
        string stdout;
        int exitCode;
        try
        {
            (stdout, _, exitCode) = await RunAsync(startInfo, StatusTimeout, cancellationToken);
        }
        catch (WindowsTunnelProvisioningException exception)
        {
            // Status is polled on a timer, so a launch failure or a hung
            // Task Scheduler must read as a state, not as a 500 on every tick.
            return new WindowsTunnelStatusResponse(
                "windows", DateTime.UtcNow, null, null, null, null, false,
                SafeExcerpt(exception.Message));
        }

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return new WindowsTunnelStatusResponse(
                "windows", DateTime.UtcNow, null, null, null, null, false,
                "The status probe did not return a result.");
        }

        try
        {
            return ParseStatus(stdout);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException)
        {
            return new WindowsTunnelStatusResponse(
                "windows", DateTime.UtcNow, null, null, null, null, false,
                "The status probe returned a result this version of Studio could not parse.");
        }
    }

    public async Task<WindowsTunnelRegistrationResponse> RegisterAsync(
        WindowsTunnelRegisterRequest request,
        CancellationToken cancellationToken)
    {
        var validation = WindowsTunnelProvisioningPolicy.Validate(request);
        if (validation is not null) throw new ArgumentException(validation, nameof(request));

        var requestedAt = DateTime.UtcNow;
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsTunnelRegistrationResponse(
                "unsupported", false, false,
                "The Windows tunnel keeper and watchdog only run on a Windows control-plane host.",
                requestedAt);
        }

        var scriptPath = ResolveScriptPath(environment.ContentRootPath, "setup-windows-tunnel.ps1");
        if (!File.Exists(scriptPath))
            throw new WindowsTunnelProvisioningException($"Setup script not found at {scriptPath}.");

        var arguments = new[]
        {
            "-SshTarget", request.SshTarget.Trim(),
            "-RemotePort", request.RemotePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-TaskServerPort", request.TaskServerPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-IntervalMinutes", request.IntervalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ProbeIntervalSeconds", request.ProbeIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-FailureThreshold", request.FailureThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var startInfo = BuildPowerShellStartInfo(scriptPath, arguments);
        var (stdout, stderr, exitCode) = await RunAsync(startInfo, RegistrationTimeout, cancellationToken);

        var outcome = TryParseRegistration(stdout, requestedAt);
        if (outcome is not null) return outcome;

        throw new WindowsTunnelProvisioningException(
            exitCode == 0
                ? "The setup script did not print a recognizable result."
                : $"The setup script exited with code {exitCode}: {SafeExcerpt(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}");
    }

    internal static string ResolveScriptPath(string contentRootPath, string scriptFileName)
        => Path.GetFullPath(Path.Combine(
            contentRootPath, "..", "deploy", "windows", "agent-runner-tunnel", scriptFileName));

    internal static ProcessStartInfo BuildPowerShellStartInfo(string scriptPath, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    internal static WindowsTunnelStatusResponse ParseStatus(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var keeper = root.GetProperty("keeper");
        var watchdog = root.GetProperty("watchdog");
        return new WindowsTunnelStatusResponse(
            "windows",
            GetDateTime(root, "observedAt") ?? DateTime.UtcNow,
            ParseTaskStatus(keeper.GetProperty("task")),
            ParseKeeperHealth(keeper.GetProperty("health")),
            ParseTaskStatus(watchdog.GetProperty("task")),
            ParseWatchdogHealth(watchdog.GetProperty("health")),
            watchdog.TryGetProperty("alarmActive", out var alarm) && alarm.ValueKind == JsonValueKind.True,
            null);
    }

    internal static WindowsTunnelRegistrationResponse? TryParseRegistration(string stdout, DateTime requestedAt)
    {
        var line = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(candidate => candidate.StartsWith('{') && candidate.EndsWith('}'));
        if (line is null) return null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
            var elevated = root.TryGetProperty("elevated", out var elevatedElement)
                           && elevatedElement.ValueKind == JsonValueKind.True;
            var detail = BuildRegistrationDetail(root, ok);
            return new WindowsTunnelRegistrationResponse("windows", ok, elevated, detail, requestedAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildRegistrationDetail(JsonElement root, bool ok)
    {
        if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String)
            return errorElement.GetString() ?? "Registration failed.";
        if (!ok) return "Registration did not complete successfully.";

        var parts = new List<string>();
        if (root.TryGetProperty("keeper", out var keeper)
            && keeper.TryGetProperty("registered", out var keeperRegistered)
            && keeperRegistered.ValueKind == JsonValueKind.True)
            parts.Add("keeper registered");
        if (root.TryGetProperty("watchdog", out var watchdog)
            && watchdog.TryGetProperty("registered", out var watchdogRegistered)
            && watchdogRegistered.ValueKind == JsonValueKind.True)
            parts.Add("watchdog registered");
        return parts.Count > 0
            ? $"Scheduled tasks registered: {string.Join(", ", parts)}."
            : "Registration completed.";
    }

    private static WindowsTunnelTaskStatus ParseTaskStatus(JsonElement task) => new(
        GetString(task, "taskName") ?? "",
        task.TryGetProperty("registered", out var registered) && registered.ValueKind == JsonValueKind.True,
        GetString(task, "state"),
        GetDateTime(task, "lastRunTime"),
        task.TryGetProperty("lastTaskResult", out var result) && result.ValueKind == JsonValueKind.Number
            ? result.GetInt32()
            : null,
        GetDateTime(task, "nextRunTime"));

    private static WindowsTunnelKeeperHealth ParseKeeperHealth(JsonElement health) => new(
        GetString(health, "status"),
        GetString(health, "message"),
        GetDateTime(health, "observedAt"),
        health.TryGetProperty("repairAttempts", out var attempts) && attempts.ValueKind == JsonValueKind.Number
            ? attempts.GetInt32()
            : null);

    private static WindowsTunnelWatchdogHealth ParseWatchdogHealth(JsonElement health) => new(
        GetDateTime(health, "lastHealSucceededAt"),
        GetDateTime(health, "lastHealFailedAt"),
        GetDateTime(health, "lastProbeFailedAt"),
        GetString(health, "lastEvent"),
        GetDateTime(health, "lastEventAt"));

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime? GetDateTime(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
           && DateTime.TryParse(
               value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunAsync(
        ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new WindowsTunnelProvisioningException("The PowerShell process could not be started.");
        }
        catch (Exception exception) when (exception is not WindowsTunnelProvisioningException)
        {
            throw new WindowsTunnelProvisioningException(
                $"The PowerShell process could not be started: {SafeExcerpt(exception.Message)}");
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(bounded.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(bounded.Token);
        try
        {
            await process.WaitForExitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new WindowsTunnelProvisioningException(
                $"PowerShell did not finish within {timeout.TotalSeconds:0} seconds.");
        }

        return (await stdoutTask, await stderrTask, process.ExitCode);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            SilentCatch.Note(exception, "PowerShellWindowsTunnelProvisioner: bounded process cleanup");
        }
    }

    private static string SafeExcerpt(string? value, int maxLength = 500)
    {
        var text = string.Join(' ', (value ?? "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }
}
