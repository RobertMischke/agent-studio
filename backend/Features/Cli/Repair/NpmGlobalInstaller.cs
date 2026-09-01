using System.Diagnostics;

namespace AgentStudio.Cli;

/// <summary>
/// Bounded process boundary for installing or relinking one global npm package.
/// Package selection and repair policy live in
/// <see cref="LocalCliRepairService"/>; this class only resolves and executes npm.
/// </summary>
public class NpmGlobalInstaller
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan PreflightTimeout = TimeSpan.FromSeconds(15);

    private readonly Func<string, string?> _environmentVariable;
    private readonly Func<bool> _isWindows;

    public NpmGlobalInstaller()
        : this(Environment.GetEnvironmentVariable, OperatingSystem.IsWindows)
    {
    }

    internal NpmGlobalInstaller(
        Func<string, string?> environmentVariable,
        Func<bool> isWindows)
    {
        _environmentVariable = environmentVariable;
        _isWindows = isWindows;
    }

    public virtual async Task<NpmGlobalInstallResult> InstallAsync(
        string packageName,
        NpmGlobalInstallMode mode,
        CancellationToken ct)
    {
        try
        {
            var resolution = await ResolveCommandAsync(ct);
            if (resolution.Command is null)
            {
                return new NpmGlobalInstallResult(
                    false,
                    null,
                    "",
                    resolution.Detail,
                    NpmGlobalInstallFailureKind.NpmUnavailable);
            }

            var run = await RunAsync(
                resolution.Command,
                BuildArguments(packageName, mode),
                DefaultTimeout,
                ct);
            if (run.TimedOut)
            {
                return new NpmGlobalInstallResult(
                    false,
                    null,
                    run.StandardOutput,
                    "npm install timed out after five minutes",
                    NpmGlobalInstallFailureKind.TimedOut);
            }

            if (run.ExitCode is null)
            {
                return new NpmGlobalInstallResult(
                    false,
                    null,
                    run.StandardOutput,
                    $"npm unavailable after preflight: {run.StandardError}",
                    NpmGlobalInstallFailureKind.NpmUnavailable);
            }

            if (run.ExitCode != 0)
            {
                return new NpmGlobalInstallResult(
                    false,
                    run.ExitCode,
                    run.StandardOutput,
                    run.StandardError,
                    NpmGlobalInstallFailureKind.InstallFailed);
            }

            return new NpmGlobalInstallResult(
                true,
                run.ExitCode,
                run.StandardOutput,
                run.StandardError);
        }
        catch (Exception ex)
        {
            return new NpmGlobalInstallResult(
                false,
                null,
                "",
                $"npm unavailable: {ex.Message}",
                NpmGlobalInstallFailureKind.NpmUnavailable);
        }
    }

    internal async Task<NpmCommandResolution> ResolveCommandAsync(CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (var candidate in DiscoverCandidates()
                     .DistinctBy(item => $"{item.LauncherPath}\0{item.NpmPath}",
                         StringComparer.OrdinalIgnoreCase))
        {
            var preflight = await RunAsync(candidate, ["--version"], PreflightTimeout, ct);
            if (preflight.ExitCode == 0 && !string.IsNullOrWhiteSpace(preflight.StandardOutput))
            {
                var version = preflight.StandardOutput.Trim();
                return new NpmCommandResolution(
                    candidate,
                    version,
                    $"npm {version} resolved from '{candidate.NpmPath}'.");
            }

            failures.Add($"'{candidate.NpmPath}': {SummarizeFailure(preflight)}");
        }

        var detail = failures.Count == 0
            ? "npm unavailable: no npm executable candidate was found in the active Node installation, APPDATA, or PATH."
            : $"npm unavailable: no candidate passed 'npm --version'. {string.Join("; ", failures)}";
        return new NpmCommandResolution(null, null, detail);
    }

    internal static IReadOnlyList<string> BuildArguments(
        string packageName,
        NpmGlobalInstallMode mode)
    {
        var arguments = new List<string> { "install", "--global", packageName };
        if (mode == NpmGlobalInstallMode.ForceRelink) arguments.Add("--force");
        return arguments;
    }

    private IEnumerable<NpmCommand> DiscoverCandidates()
    {
        var path = _environmentVariable("PATH");
        if (_isWindows())
        {
            var commandShell = ResolveWindowsCommandShell(path);
            var node = ResolveFromPath("node.exe", path);
            if (node is not null)
            {
                var nodeDirectory = Path.GetDirectoryName(node)!;
                var npmCli = Path.Combine(nodeDirectory, "node_modules", "npm", "bin", "npm-cli.js");
                if (File.Exists(npmCli))
                    yield return NpmCommand.Direct(node, npmCli, nodeDirectory, [npmCli]);

                var npmCommand = Path.Combine(nodeDirectory, "npm.cmd");
                if (File.Exists(npmCommand))
                    yield return NpmCommand.CommandScript(commandShell, npmCommand, nodeDirectory);
            }

            var appData = _environmentVariable("APPDATA");
            if (!string.IsNullOrWhiteSpace(appData))
            {
                var npmCommand = Path.Combine(appData, "npm", "npm.cmd");
                if (File.Exists(npmCommand))
                {
                    yield return NpmCommand.CommandScript(
                        commandShell,
                        npmCommand,
                        Path.GetDirectoryName(npmCommand)!);
                }
            }

            var pathNpm = ResolveFromPath("npm.cmd", path);
            if (pathNpm is not null)
            {
                yield return NpmCommand.CommandScript(
                    commandShell,
                    pathNpm,
                    Path.GetDirectoryName(pathNpm)!);
            }

            yield break;
        }

        var npm = ResolveFromPath("npm", path);
        if (npm is not null)
            yield return NpmCommand.Direct(npm, npm, Path.GetDirectoryName(npm)!);
    }

    private string ResolveWindowsCommandShell(string? path)
    {
        var configured = _environmentVariable("ComSpec");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        var systemRoot = _environmentVariable("SystemRoot");
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            var systemCommand = Path.Combine(systemRoot, "System32", "cmd.exe");
            if (File.Exists(systemCommand)) return systemCommand;
        }

        return ResolveFromPath("cmd.exe", path) ?? "cmd.exe";
    }

    private static string? ResolveFromPath(string executableName, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var rawDirectory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0) continue;
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, executableName));
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex)
            {
                // Ignore malformed PATH entries and continue to verified candidates.
                SilentCatch.Note(ex, "NpmGlobalInstaller: malformed PATH entry");
            }
        }

        return null;
    }

    private static async Task<NpmCommandRunResult> RunAsync(
        NpmCommand command,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = BuildStartInfo(command, arguments),
            };
            if (!process.Start())
                return new NpmCommandRunResult(null, "", "npm did not start", false);

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, "NpmGlobalInstaller: timeout kill best-effort"); }
                return new NpmCommandRunResult(
                    null,
                    await SafeOutputAsync(stdout),
                    await SafeOutputAsync(stderr),
                    true);
            }

            return new NpmCommandRunResult(
                process.ExitCode,
                await SafeOutputAsync(stdout),
                await SafeOutputAsync(stderr),
                false);
        }
        catch (Exception ex)
        {
            return new NpmCommandRunResult(null, "", ex.Message, false);
        }
    }

    private static ProcessStartInfo BuildStartInfo(
        NpmCommand command,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.LauncherPath,
            WorkingDirectory = command.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (command.IsCommandScript)
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(BuildWindowsCommandLine(command.NpmPath, arguments));
            return startInfo;
        }

        foreach (var argument in command.PrefixArguments) startInfo.ArgumentList.Add(argument);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static string BuildWindowsCommandLine(string commandPath, IReadOnlyList<string> arguments)
        => $"\"{commandPath}\" {string.Join(" ", arguments.Select(QuoteWindowsArgument))}";

    private static string QuoteWindowsArgument(string argument)
        => argument.Any(character => char.IsWhiteSpace(character) || "&|<>^\"".Contains(character))
            ? $"\"{argument.Replace("\"", "\"\"")}\""
            : argument;

    private static string SummarizeFailure(NpmCommandRunResult run)
    {
        if (run.TimedOut) return "preflight timed out";
        var detail = string.IsNullOrWhiteSpace(run.StandardError)
            ? run.StandardOutput
            : run.StandardError;
        detail = string.Join(" ", detail
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(2));
        if (detail.Length > 300) detail = detail[..300] + "...";
        return run.ExitCode is null
            ? $"could not start ({detail})"
            : $"preflight exited {run.ExitCode} ({detail})";
    }

    private static async Task<string> SafeOutputAsync(Task<string> output)
    {
        try { return await output; }
        catch { return ""; }
    }
}

internal sealed record NpmCommand(
    string LauncherPath,
    string NpmPath,
    string WorkingDirectory,
    IReadOnlyList<string> PrefixArguments,
    bool IsCommandScript)
{
    public static NpmCommand Direct(
        string launcherPath,
        string npmPath,
        string workingDirectory,
        IReadOnlyList<string>? prefixArguments = null)
        => new(launcherPath, npmPath, workingDirectory, prefixArguments ?? [], false);

    public static NpmCommand CommandScript(
        string commandShell,
        string npmPath,
        string workingDirectory)
        => new(commandShell, npmPath, workingDirectory, [], true);
}

internal sealed record NpmCommandResolution(
    NpmCommand? Command,
    string? Version,
    string Detail);

internal sealed record NpmCommandRunResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

public enum NpmGlobalInstallMode
{
    Install,
    ForceRelink,
}

public enum NpmGlobalInstallFailureKind
{
    NpmUnavailable,
    TimedOut,
    InstallFailed,
}

public sealed record NpmGlobalInstallResult(
    bool Succeeded,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    NpmGlobalInstallFailureKind? FailureKind = null)
{
    public string Outcome => Succeeded
        ? "succeeded"
        : FailureKind switch
        {
            NpmGlobalInstallFailureKind.NpmUnavailable => "npm-unavailable",
            NpmGlobalInstallFailureKind.TimedOut => "timed-out",
            _ => "failed",
        };
}
