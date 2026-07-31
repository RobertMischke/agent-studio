using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using AgentStudio.Diagnostics;

namespace AgentStudio.Registry;

public sealed record ProjectUrlPortOccupant(int ProcessId, string ProcessName);

public interface IProjectUrlPortInspector
{
    ProjectUrlPortOccupant? FindListener(int port);
}

/// <summary>
/// Resolves the process that owns a local TCP listener. Preview starts use this
/// before spawning a command so an existing application is reported directly
/// instead of degrading into a later readiness timeout.
/// </summary>
public sealed class ProjectUrlPortInspector : IProjectUrlPortInspector
{
    public ProjectUrlPortOccupant? FindListener(int port)
    {
        if (port is <= 0 or > 65535) return null;
        try
        {
            if (!IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port))
                return null;
        }
        catch
        {
            return null;
        }

        var processId = OperatingSystem.IsWindows()
            ? FindWithNetstat(port)
            : FindWithLsof(port) ?? (OperatingSystem.IsLinux() ? FindInProc(port) : null);
        if (processId is not > 0) return null;

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            return new ProjectUrlPortOccupant(processId.Value, process.ProcessName);
        }
        catch
        {
            return null;
        }
    }

    private static int? FindWithLsof(int port)
    {
        var output = Run("lsof", ["-nP", $"-iTCP:{port}", "-sTCP:LISTEN", "-Fp"]);
        if (output == null) return null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (line.Length > 1 && line[0] == 'p'
                && int.TryParse(line.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                return pid;
        return null;
    }

    private static int? FindWithNetstat(int port)
    {
        var output = Run("netstat", ["-ano", "-p", "tcp"]);
        if (output == null) return null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5 || !fields[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)
                || !EndpointHasPort(fields[1], port))
                continue;
            if (int.TryParse(fields[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                return pid;
        }
        return null;
    }

    private static int? FindInProc(int port)
    {
        var inodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            try
            {
                foreach (var line in File.ReadLines(path).Skip(1))
                {
                    var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length > 9 && fields[3] == "0A" && EndpointHasHexPort(fields[1], port))
                        inodes.Add(fields[9]);
                }
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, $"URL Preview port inspection could not read {path}.");
            }
        }
        if (inodes.Count == 0) return null;

        foreach (var processDirectory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(processDirectory), out var pid)) continue;
            try
            {
                foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(processDirectory, "fd")))
                {
                    var target = new FileInfo(fd).LinkTarget;
                    if (target != null && target.StartsWith("socket:[", StringComparison.Ordinal)
                        && inodes.Contains(target[8..^1]))
                        return pid;
                }
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, $"URL Preview port inspection could not read process {pid}.");
            }
        }
        return null;
    }

    private static bool EndpointHasPort(string endpoint, int port)
    {
        var separator = endpoint.LastIndexOf(':');
        return separator >= 0
            && int.TryParse(endpoint.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value == port;
    }

    private static bool EndpointHasHexPort(string endpoint, int port)
    {
        var separator = endpoint.LastIndexOf(':');
        return separator >= 0
            && int.TryParse(endpoint.AsSpan(separator + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            && value == port;
    }

    private static string? Run(string fileName, IReadOnlyList<string> arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(2_000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, $"URL Preview port inspection could not stop {fileName}."); }
                return null;
            }
            return output;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, $"URL Preview port inspection could not run {fileName}.");
            return null;
        }
    }
}

public sealed class ProjectUrlPortOccupiedException(ProjectUrlDiagnostic diagnostic)
    : InvalidOperationException(diagnostic.Summary)
{
    public ProjectUrlDiagnostic Diagnostic { get; } = diagnostic;
}
