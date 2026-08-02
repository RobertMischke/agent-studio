using System.Diagnostics;

namespace AgentStudio.Shared;

/// <summary>
/// Builds the platform shell invocation used for configured command strings.
/// Windows command shims are resolved by <c>cmd.exe</c>; Unix-like hosts use
/// <c>/bin/sh</c>.
/// </summary>
internal static class PlatformShellCommand
{
    internal static ProcessStartInfo CreateStartInfo(
        string workingDirectory,
        string command,
        bool? isWindows = null)
    {
        var windows = isWindows ?? OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = windows ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(windows ? "/c" : "-c");
        psi.ArgumentList.Add(command);
        return psi;
    }
}
