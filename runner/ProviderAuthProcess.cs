using System.Globalization;

namespace AgentRunner;

/// <summary>
/// Starts provider status commands below normal CPU priority. Linux agent hosts
/// use <c>nice</c> as the parent process so Node inherits the lower priority
/// before its runtime startup competes with active suites.
/// </summary>
internal static class ProviderAuthProcess
{
    internal const int NiceAdjustment = 10;

    public static Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return ProcessRunner.RunAsync(fileName, arguments, ct: ct);

        var nice = NiceExecutable();
        if (nice is null)
            throw new InvalidOperationException(
                "Provider authentication probing requires the 'nice' utility on Unix hosts.");
        var command = BuildNiceCommand(nice, fileName, arguments);
        return ProcessRunner.RunAsync(command.FileName, command.Arguments, ct: ct);
    }

    internal static ProviderAuthLaunchCommand BuildNiceCommand(
        string niceExecutable,
        string fileName,
        IReadOnlyList<string> arguments)
        => new(
            niceExecutable,
            [
                "-n",
                NiceAdjustment.ToString(CultureInfo.InvariantCulture),
                "--",
                fileName,
                .. arguments,
            ]);

    private static string? NiceExecutable()
    {
        foreach (var candidate in new[] { "/usr/bin/nice", "/bin/nice" })
            if (File.Exists(candidate)) return candidate;
        return null;
    }
}

internal sealed record ProviderAuthLaunchCommand(
    string FileName,
    IReadOnlyList<string> Arguments);
