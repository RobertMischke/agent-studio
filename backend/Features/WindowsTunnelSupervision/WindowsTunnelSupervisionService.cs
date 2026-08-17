using System.Diagnostics;

namespace AgentStudio.WindowsTunnelSupervision;

public interface IWindowsTunnelSupervisionService
{
    Task<WindowsTunnelSupervisionStatus> GetStatusAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reads local facts about the two Scheduled Tasks the guided setup registers
/// on a Windows-hosted Task Server (<see cref="ScheduledTaskInspector"/> for
/// <c>schtasks /Query</c>, <see cref="WatchdogLogReader"/> for the watchdog's
/// journal) and hands them to the pure
/// <see cref="WindowsTunnelSupervisionPolicy"/> for interpretation. Querying a
/// Scheduled Task needs no elevation; only registering one does.
/// </summary>
public sealed class WindowsTunnelSupervisionService(
    IScheduledTaskInspector scheduledTasks,
    IWatchdogLogReader watchdogLog,
    IConfiguration configuration)
    : IWindowsTunnelSupervisionService
{
    private const string DefaultKeeperTaskName = "AgentRunner-TunnelKeeper";
    private const string DefaultWatchdogTaskName = "AgentRunner-TunnelWatchdog";
    private const int LogTailLines = 400;

    public async Task<WindowsTunnelSupervisionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            var notApplicable = new ScheduledTaskStatus("", ScheduledTaskPresence.NotApplicable, null, null);
            return new WindowsTunnelSupervisionStatus(
                IsWindowsHost: false,
                Keeper: notApplicable,
                Watchdog: notApplicable,
                LastHealAt: null,
                LastHealDetail: null,
                ConsecutiveHealFailures: 0,
                Detail: "This Task Server is not running on Windows; tunnel supervision only applies to a "
                    + "Windows control-plane host.");
        }

        var keeperTaskName = configuration["WindowsTunnelSupervision:KeeperTaskName"] ?? DefaultKeeperTaskName;
        var watchdogTaskName = configuration["WindowsTunnelSupervision:WatchdogTaskName"] ?? DefaultWatchdogTaskName;
        var logPath = configuration["WindowsTunnelSupervision:WatchdogLogPath"];

        var keeperQuery = scheduledTasks.QueryAsync(keeperTaskName, cancellationToken);
        var watchdogQuery = scheduledTasks.QueryAsync(watchdogTaskName, cancellationToken);
        await Task.WhenAll(keeperQuery, watchdogQuery);

        var keeperResult = keeperQuery.Result;
        var watchdogResult = watchdogQuery.Result;
        var keeper = WindowsTunnelSupervisionPolicy.ParseScheduledTaskStatus(
            keeperTaskName, keeperResult.ExitCode, keeperResult.Stdout);
        var watchdog = WindowsTunnelSupervisionPolicy.ParseScheduledTaskStatus(
            watchdogTaskName, watchdogResult.ExitCode, watchdogResult.Stdout);

        string detail;
        (string? At, string? Detail, int ConsecutiveFailures) heal = (null, null, 0);
        if (string.IsNullOrWhiteSpace(logPath))
        {
            detail = "Set WindowsTunnelSupervision:WatchdogLogPath in appsettings to show heal history "
                + "from the watchdog journal.";
        }
        else
        {
            var tail = await watchdogLog.ReadTailAsync(logPath, LogTailLines, cancellationToken);
            heal = WindowsTunnelSupervisionPolicy.ParseHealHistory(tail);
            detail = tail is null
                ? $"No watchdog journal found yet at {logPath}."
                : "Read from the watchdog journal tail.";
        }

        return new WindowsTunnelSupervisionStatus(
            IsWindowsHost: true,
            Keeper: keeper,
            Watchdog: watchdog,
            LastHealAt: heal.At,
            LastHealDetail: heal.Detail,
            ConsecutiveHealFailures: heal.ConsecutiveFailures,
            Detail: detail);
    }
}

public sealed record ScheduledTaskQueryResult(int ExitCode, string Stdout);

public interface IScheduledTaskInspector
{
    Task<ScheduledTaskQueryResult> QueryAsync(string taskName, CancellationToken cancellationToken);
}

/// <summary>
/// Shells out to <c>schtasks /Query</c>, the only supported way to read
/// Scheduled Task state without taking a dependency on the Task Scheduler COM
/// surface. Read-only; needs no elevation.
/// </summary>
public sealed class SchtasksScheduledTaskInspector : IScheduledTaskInspector
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    public async Task<ScheduledTaskQueryResult> QueryAsync(string taskName, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/Query");
        startInfo.ArgumentList.Add("/TN");
        startInfo.ArgumentList.Add(taskName);
        startInfo.ArgumentList.Add("/FO");
        startInfo.ArgumentList.Add("LIST");
        startInfo.ArgumentList.Add("/V");

        using var process = new Process { StartInfo = startInfo };
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(QueryTimeout);
        try
        {
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(bounded.Token);
            await process.WaitForExitAsync(bounded.Token);
            var stdout = await stdoutTask;
            return new ScheduledTaskQueryResult(process.ExitCode, stdout);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ScheduledTaskQueryResult(-1, "");
        }
    }
}

public interface IWatchdogLogReader
{
    Task<string?> ReadTailAsync(string path, int maxLines, CancellationToken cancellationToken);
}

/// <summary>Reads the last N lines of the watchdog's append-only journal file.</summary>
public sealed class WatchdogLogReader : IWatchdogLogReader
{
    public async Task<string?> ReadTailAsync(string path, int maxLines, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        return string.Join('\n', lines.Length <= maxLines ? lines : lines[^maxLines..]);
    }
}
