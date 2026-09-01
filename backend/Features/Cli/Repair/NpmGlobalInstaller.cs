using System.Diagnostics;

namespace AgentStudio.Cli;

/// <summary>
/// Bounded process boundary for installing or relinking one global npm package.
/// Package selection and repair policy live in
/// <see cref="LocalCliRepairService"/>; this class resolves and verifies npm
/// before executing it.
/// </summary>
public class NpmGlobalInstaller
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PreflightTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<IReadOnlyList<NpmInvocation>> _resolveCandidates;
    private readonly Func<NpmInvocation, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<NpmProcessExecution>>
        _runProcess;

    public NpmGlobalInstaller()
        : this(ResolveNpmCandidates, RunProcessAsync)
    {
    }

    internal NpmGlobalInstaller(
        Func<IReadOnlyList<NpmInvocation>> resolveCandidates,
        Func<NpmInvocation, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<NpmProcessExecution>> runProcess)
    {
        _resolveCandidates = resolveCandidates;
        _runProcess = runProcess;
    }

    public virtual async Task<NpmGlobalInstallResult> InstallAsync(
        string packageName,
        NpmGlobalInstallMode mode,
        CancellationToken ct)
    {
        try
        {
            var resolution = await ResolveNpmExecutableAsync(ct);
            if (!resolution.Available || resolution.Invocation is null)
            {
                return new NpmGlobalInstallResult(
                    false,
                    null,
                    "",
                    resolution.Detail,
                    NpmGlobalInstallOutcome.NpmUnavailable);
            }

            var execution = await _runProcess(
                resolution.Invocation,
                BuildArguments(packageName, mode),
                DefaultTimeout,
                ct);
            if (execution.Cancelled)
            {
                return new NpmGlobalInstallResult(
                    false,
                    null,
                    execution.StandardOutput,
                    "npm install was cancelled",
                    NpmGlobalInstallOutcome.Cancelled);
            }
            if (execution.TimedOut)
            {
                return new NpmGlobalInstallResult(
                    false,
                    null,
                    execution.StandardOutput,
                    "npm install timed out after five minutes",
                    NpmGlobalInstallOutcome.TimedOut);
            }
            if (!execution.Started)
            {
                return new NpmGlobalInstallResult(
                    false,
                    null,
                    execution.StandardOutput,
                    $"npm unavailable: verified executable could not start ({execution.LaunchError ?? "unknown error"})",
                    NpmGlobalInstallOutcome.NpmUnavailable);
            }

            return new NpmGlobalInstallResult(
                execution.ExitCode == 0,
                execution.ExitCode,
                execution.StandardOutput,
                execution.StandardError,
                execution.ExitCode == 0
                    ? NpmGlobalInstallOutcome.Installed
                    : NpmGlobalInstallOutcome.Failed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new NpmGlobalInstallResult(
                false,
                null,
                "",
                "npm install was cancelled",
                NpmGlobalInstallOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            return new NpmGlobalInstallResult(
                false,
                null,
                "",
                $"npm unavailable: {ex.Message}",
                NpmGlobalInstallOutcome.NpmUnavailable);
        }
    }

    internal async Task<NpmExecutableResolution> ResolveNpmExecutableAsync(CancellationToken ct)
    {
        var candidates = _resolveCandidates();
        if (candidates.Count == 0)
        {
            return NpmExecutableResolution.Unavailable(
                "npm unavailable: no executable candidate was found beside the active Node installation, in APPDATA, or on PATH");
        }

        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var execution = await _runProcess(candidate, ["--version"], PreflightTimeout, ct);
            if (execution.Cancelled)
            {
                ct.ThrowIfCancellationRequested();
                failures.Add($"{candidate.Source}: preflight cancelled");
                continue;
            }
            if (execution.Started && !execution.TimedOut && execution.ExitCode == 0)
            {
                var version = FirstNonEmptyLine(execution.StandardOutput)
                              ?? FirstNonEmptyLine(execution.StandardError)
                              ?? "unknown";
                return new NpmExecutableResolution(true, candidate, version, "npm preflight passed");
            }

            failures.Add(execution switch
            {
                { TimedOut: true } => $"{candidate.Source}: npm --version timed out",
                { Started: false } => $"{candidate.Source}: could not start",
                _ => $"{candidate.Source}: npm --version exited {execution.ExitCode?.ToString() ?? "without an exit code"}",
            });
        }

        return NpmExecutableResolution.Unavailable(
            $"npm unavailable: no candidate passed npm --version ({string.Join("; ", failures)})");
    }

    internal static IReadOnlyList<string> BuildArguments(
        string packageName,
        NpmGlobalInstallMode mode)
    {
        var arguments = new List<string> { "install", "--global", packageName };
        if (mode == NpmGlobalInstallMode.ForceRelink) arguments.Add("--force");
        return arguments;
    }

    internal static IReadOnlyList<NpmInvocation> ResolveNpmCandidates()
    {
        var isWindows = OperatingSystem.IsWindows();
        var candidates = new List<NpmInvocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodePaths = ResolveOnPath(isWindows ? ["node.exe", "node"] : ["node"])
            .ToList();

        if (isWindows)
        {
            AddExisting(nodePaths, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "nodejs",
                "node.exe"));
            AddExisting(nodePaths, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "nodejs",
                "node.exe"));
        }

        foreach (var nodePath in nodePaths)
        {
            var nodeDirectory = Path.GetDirectoryName(nodePath);
            if (string.IsNullOrWhiteSpace(nodeDirectory)) continue;
            AddNodeNpmCandidate(candidates, seen, nodePath, nodeDirectory, "active-node");
        }

        if (isWindows)
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrWhiteSpace(appData))
            {
                var npmRoot = Path.Combine(appData, "npm");
                var npmCli = Path.Combine(npmRoot, "node_modules", "npm", "bin", "npm-cli.js");
                var nodePath = nodePaths.FirstOrDefault();
                if (nodePath is not null && File.Exists(npmCli))
                {
                    AddCandidate(candidates, seen, new NpmInvocation(
                        npmCli,
                        nodePath,
                        [npmCli],
                        Path.GetDirectoryName(nodePath)!,
                        "appdata-npm-cli"));
                }
                AddCommandCandidate(candidates, seen, Path.Combine(npmRoot, "npm.cmd"), "appdata-npm-command");
            }
        }

        foreach (var npmPath in ResolveOnPath(isWindows ? ["npm.cmd", "npm.exe", "npm"] : ["npm"]))
            AddCommandCandidate(candidates, seen, npmPath, "path-npm");

        return candidates;
    }

    private static void AddNodeNpmCandidate(
        List<NpmInvocation> candidates,
        HashSet<string> seen,
        string nodePath,
        string nodeDirectory,
        string source)
    {
        var npmCli = Path.Combine(nodeDirectory, "node_modules", "npm", "bin", "npm-cli.js");
        if (File.Exists(npmCli))
        {
            AddCandidate(candidates, seen, new NpmInvocation(
                npmCli,
                nodePath,
                [npmCli],
                nodeDirectory,
                source + "-npm-cli"));
        }

        if (OperatingSystem.IsWindows())
            AddCommandCandidate(candidates, seen, Path.Combine(nodeDirectory, "npm.cmd"), source + "-npm-command");
    }

    private static void AddCommandCandidate(
        List<NpmInvocation> candidates,
        HashSet<string> seen,
        string npmPath,
        string source)
    {
        if (!File.Exists(npmPath)) return;
        var fullPath = Path.GetFullPath(npmPath);
        var workingDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory)) return;

        if (OperatingSystem.IsWindows()
            && string.Equals(Path.GetExtension(fullPath), ".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var commandShell = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(commandShell) || !File.Exists(commandShell))
                commandShell = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            AddCandidate(candidates, seen, new NpmInvocation(
                fullPath,
                commandShell,
                ["/d", "/c", fullPath],
                workingDirectory,
                source));
            return;
        }

        AddCandidate(candidates, seen, new NpmInvocation(
            fullPath,
            fullPath,
            [],
            workingDirectory,
            source));
    }

    private static void AddCandidate(
        List<NpmInvocation> candidates,
        HashSet<string> seen,
        NpmInvocation candidate)
    {
        var key = $"{candidate.ExecutablePath}\0{string.Join('\0', candidate.PrefixArguments)}";
        if (seen.Add(key)) candidates.Add(candidate);
    }

    private static IReadOnlyList<string> ResolveOnPath(IReadOnlyList<string> commandNames)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return [];
        var result = new List<string>();
        foreach (var rawDirectory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = Environment.ExpandEnvironmentVariables(rawDirectory.Trim().Trim('"'));
            if (!Path.IsPathRooted(directory) || !Directory.Exists(directory)) continue;
            foreach (var commandName in commandNames)
                AddExisting(result, Path.Combine(directory, commandName));
        }
        return result;
    }

    private static void AddExisting(List<string> result, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !File.Exists(path)) return;
        var fullPath = Path.GetFullPath(path);
        if (!result.Contains(fullPath, StringComparer.OrdinalIgnoreCase)) result.Add(fullPath);
    }

    private static async Task<NpmProcessExecution> RunProcessAsync(
        NpmInvocation invocation,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = invocation.ExecutablePath,
                    WorkingDirectory = invocation.WorkingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            foreach (var argument in invocation.PrefixArguments)
                process.StartInfo.ArgumentList.Add(argument);
            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);

            if (!process.Start())
                return NpmProcessExecution.NotStarted("process start returned false");

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, "NpmGlobalInstaller: timeout kill best-effort"); }
                return new NpmProcessExecution(
                    true,
                    null,
                    await SafeOutputAsync(stdout),
                    await SafeOutputAsync(stderr),
                    !ct.IsCancellationRequested,
                    ct.IsCancellationRequested,
                    null);
            }

            return new NpmProcessExecution(
                true,
                process.ExitCode,
                await SafeOutputAsync(stdout),
                await SafeOutputAsync(stderr),
                false,
                false,
                null);
        }
        catch (Exception ex)
        {
            return NpmProcessExecution.NotStarted(ex.Message);
        }
    }

    private static async Task<string> SafeOutputAsync(Task<string> output)
    {
        try { return await output; }
        catch { return ""; }
    }

    private static string? FirstNonEmptyLine(string value)
        => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);
}

internal sealed record NpmInvocation(
    string NpmPath,
    string ExecutablePath,
    IReadOnlyList<string> PrefixArguments,
    string WorkingDirectory,
    string Source);

internal sealed record NpmExecutableResolution(
    bool Available,
    NpmInvocation? Invocation,
    string? Version,
    string Detail)
{
    public static NpmExecutableResolution Unavailable(string detail)
        => new(false, null, null, detail);
}

internal sealed record NpmProcessExecution(
    bool Started,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Cancelled,
    string? LaunchError)
{
    public static NpmProcessExecution NotStarted(string error)
        => new(false, null, "", "", false, false, error);
}

public enum NpmGlobalInstallMode
{
    Install,
    ForceRelink,
}

public enum NpmGlobalInstallOutcome
{
    Installed,
    Failed,
    NpmUnavailable,
    TimedOut,
    Cancelled,
}

public sealed record NpmGlobalInstallResult(
    bool Succeeded,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    NpmGlobalInstallOutcome Outcome);
