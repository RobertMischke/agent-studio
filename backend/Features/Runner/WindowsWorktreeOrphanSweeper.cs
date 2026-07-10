using System.Diagnostics;
using System.Text.Json;

namespace AgentStudio.Runner;

/// <summary>
/// Boot-only recovery for detached development helpers that predate (or escaped)
/// the per-run Windows job object. These processes have no active-jobs entry,
/// but their command line still points into an ephemeral ass-worktrees checkout.
/// </summary>
internal static class WindowsWorktreeOrphanSweeper
{
    internal sealed record ProcessSnapshot(int ProcessId, string? Name, string? CommandLine, string? ExecutablePath);

    internal static IReadOnlyList<ProcessSnapshot> SelectCandidates(
        IEnumerable<ProcessSnapshot> processes,
        int currentProcessId)
        => processes.Where(p =>
                p.ProcessId > 0
                && p.ProcessId != currentProcessId
                && IsDevelopmentHelper(p.Name)
                && (ContainsWorktreePath(p.CommandLine) || ContainsWorktreePath(p.ExecutablePath)))
            .ToList();

    internal static void Sweep(ILogger logger)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var snapshots = ReadProcessSnapshots();
            var candidates = SelectCandidates(snapshots, Environment.ProcessId);
            var reaped = 0;
            foreach (var candidate in candidates)
            {
                if (!TryTaskKillTree(candidate.ProcessId, logger)) continue;
                reaped++;
                logger.LogWarning(
                    "worktree-orphan-boot-reaped pid={Pid} process={Process} commandLine={CommandLine}",
                    candidate.ProcessId, candidate.Name, candidate.CommandLine);
            }

            logger.LogInformation(
                "worktree-orphan-boot-sweep scanned={Scanned} candidates={Candidates} reaped={Reaped}",
                snapshots.Count, candidates.Count, reaped);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "worktree-orphan-boot-sweep failed; startup continues with recorded-PID orphan recovery");
        }
    }

    private static bool IsDevelopmentHelper(string? name)
    {
        var normalized = Path.GetFileNameWithoutExtension(name ?? string.Empty);
        return normalized.Equals("node", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("esbuild", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsWorktreePath(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Replace('/', '\\').Contains("\\ass-worktrees\\", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ProcessSnapshot> ReadProcessSnapshots()
    {
        const string script =
            "$ErrorActionPreference='Stop'; " +
            "@(Get-CimInstance Win32_Process | Select-Object ProcessId,Name,CommandLine,ExecutablePath) | ConvertTo-Json -Compress";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Could not start PowerShell process inventory.");

        var json = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15_000) || process.ExitCode != 0)
            throw new InvalidOperationException($"Process inventory failed: {error}");
        if (string.IsNullOrWhiteSpace(json)) return [];

        using var document = JsonDocument.Parse(json);
        var elements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement.Clone()];
        return elements.Select(e => new ProcessSnapshot(
                e.GetProperty("ProcessId").GetInt32(),
                GetString(e, "Name"),
                GetString(e, "CommandLine"),
                GetString(e, "ExecutablePath")))
            .ToList();
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryTaskKillTree(int pid, ILogger logger)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill.exe",
                ArgumentList = { "/F", "/T", "/PID", pid.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null) return false;
            process.WaitForExit(5_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "worktree-orphan boot taskkill failed for PID {Pid}", pid);
            return false;
        }
    }
}
