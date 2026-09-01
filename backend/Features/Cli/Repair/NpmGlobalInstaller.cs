using System.Diagnostics;

namespace AgentStudio.Cli;

/// <summary>
/// Bounded process boundary for installing or relinking one global npm package.
/// Package selection and repair policy live in
/// <see cref="LocalCliRepairService"/>; this class only resolves and executes npm.
/// </summary>
public class NpmGlobalInstaller
{
    public const string NpmUnavailableFailureKind = "npm-unavailable";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan PreflightTimeout = TimeSpan.FromSeconds(15);

    private readonly Func<IReadOnlyList<NpmInvocation>> _resolveCandidates;

    public NpmGlobalInstaller()
        : this(ResolveNpmInvocations)
    {
    }

    internal NpmGlobalInstaller(Func<IReadOnlyList<NpmInvocation>> resolveCandidates)
        => _resolveCandidates = resolveCandidates;

    public virtual async Task<NpmGlobalInstallResult> InstallAsync(
        string packageName,
        NpmGlobalInstallMode mode,
        CancellationToken ct)
    {
        try
        {
            var availability = await ResolveAvailableNpmAsync(ct);
            if (availability.Invocation is null)
            {
                return new NpmGlobalInstallResult(
                    false,
                    null,
                    "",
                    availability.Detail,
                    NpmUnavailableFailureKind);
            }

            var execution = await RunAsync(
                availability.Invocation,
                BuildArguments(packageName, mode),
                DefaultTimeout,
                ct);
            if (execution.TimedOut)
            {
                return new NpmGlobalInstallResult(
                    false,
                    null,
                    execution.StandardOutput,
                    "npm install timed out after five minutes",
                    "npm-install-timeout",
                    availability.Invocation.NpmPath,
                    availability.Version);
            }

            return new NpmGlobalInstallResult(
                execution.ExitCode == 0,
                execution.ExitCode,
                execution.StandardOutput,
                execution.StandardError,
                execution.ExitCode == 0 ? null : "npm-install-failed",
                availability.Invocation.NpmPath,
                availability.Version);
        }
        catch (Exception ex)
        {
            return new NpmGlobalInstallResult(
                false,
                null,
                "",
                $"npm execution failed: {ex.Message}",
                "npm-execution-failed");
        }
    }

    internal async Task<NpmAvailability> ResolveAvailableNpmAsync(CancellationToken ct)
    {
        IReadOnlyList<NpmInvocation> candidates;
        try
        {
            candidates = _resolveCandidates();
        }
        catch (Exception ex)
        {
            return new NpmAvailability(
                null,
                null,
                $"npm unavailable: executable resolution failed ({ex.Message}).");
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var preflight = await RunAsync(candidate, ["--version"], PreflightTimeout, ct);
                if (preflight.ExitCode == 0 && !string.IsNullOrWhiteSpace(preflight.StandardOutput))
                {
                    return new NpmAvailability(
                        candidate,
                        preflight.StandardOutput.Trim(),
                        $"npm {preflight.StandardOutput.Trim()} available at '{candidate.NpmPath}'.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Try the next fully resolved candidate. Raw preflight stderr is
                // intentionally not promoted into the repair journal.
                SilentCatch.Note(ex, "NpmGlobalInstaller: npm preflight candidate failed");
            }
        }

        return new NpmAvailability(
            null,
            null,
            "npm unavailable: no resolved npm command passed the 'npm --version' preflight.");
    }

    internal static IReadOnlyList<string> BuildArguments(
        string packageName,
        NpmGlobalInstallMode mode)
    {
        var arguments = new List<string> { "install", "--global", packageName };
        if (mode == NpmGlobalInstallMode.ForceRelink) arguments.Add("--force");
        return arguments;
    }

    internal static IReadOnlyList<NpmInvocation> ResolveNpmInvocations()
        => ResolveNpmInvocations(
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("APPDATA"),
            Environment.SystemDirectory,
            Path.GetTempPath());

    internal static IReadOnlyList<NpmInvocation> ResolveNpmInvocations(
        bool isWindows,
        string? path,
        string? appData,
        string? systemDirectory,
        string workingDirectory)
    {
        var resolvedWorkingDirectory = Directory.Exists(workingDirectory)
            ? Path.GetFullPath(workingDirectory)
            : AppContext.BaseDirectory;
        var candidates = new List<NpmInvocation>();

        if (!isWindows)
        {
            var npm = FindOnPath(path, ["npm"]);
            if (npm is not null)
                candidates.Add(new NpmInvocation(npm, [], npm, resolvedWorkingDirectory));
            return candidates;
        }

        var node = FindOnPath(path, ["node.exe"]);
        if (node is not null)
        {
            var nodeDirectory = Path.GetDirectoryName(node)!;
            var npmCli = Path.Combine(nodeDirectory, "node_modules", "npm", "bin", "npm-cli.js");
            if (File.Exists(npmCli))
            {
                candidates.Add(new NpmInvocation(
                    node,
                    [Path.GetFullPath(npmCli)],
                    Path.GetFullPath(npmCli),
                    resolvedWorkingDirectory));
            }

            AddWindowsCommandCandidate(
                candidates,
                Path.Combine(nodeDirectory, "npm.cmd"),
                systemDirectory,
                resolvedWorkingDirectory);
        }

        if (!string.IsNullOrWhiteSpace(appData))
        {
            AddWindowsCommandCandidate(
                candidates,
                Path.Combine(appData, "npm", "npm.cmd"),
                systemDirectory,
                resolvedWorkingDirectory);
        }

        var pathNpm = FindOnPath(path, ["npm.cmd", "npm.exe"]);
        if (pathNpm is not null)
        {
            if (string.Equals(Path.GetExtension(pathNpm), ".cmd", StringComparison.OrdinalIgnoreCase))
            {
                AddWindowsCommandCandidate(
                    candidates,
                    pathNpm,
                    systemDirectory,
                    resolvedWorkingDirectory);
            }
            else
            {
                candidates.Add(new NpmInvocation(pathNpm, [], pathNpm, resolvedWorkingDirectory));
            }
        }

        return candidates
            .DistinctBy(candidate => candidate.NpmPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddWindowsCommandCandidate(
        ICollection<NpmInvocation> candidates,
        string npmCommand,
        string? systemDirectory,
        string workingDirectory)
    {
        if (!File.Exists(npmCommand) || string.IsNullOrWhiteSpace(systemDirectory)) return;
        var commandHost = Path.Combine(systemDirectory, "cmd.exe");
        if (!File.Exists(commandHost)) return;
        candidates.Add(new NpmInvocation(
            Path.GetFullPath(commandHost),
            [],
            Path.GetFullPath(npmCommand),
            workingDirectory,
            UsesWindowsCommandHost: true));
    }

    private static string? FindOnPath(string? path, IReadOnlyList<string> names)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = segment.Trim().Trim('"');
            if (!Path.IsPathRooted(directory)) continue;
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    private static async Task<NpmProcessResult> RunAsync(
        NpmInvocation invocation,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
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
        if (invocation.UsesWindowsCommandHost)
        {
            process.StartInfo.Arguments = BuildWindowsCommandArguments(invocation.NpmPath, arguments);
        }
        else
        {
            foreach (var argument in invocation.PrefixArguments.Concat(arguments))
                process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start()) return new NpmProcessResult(null, "", "npm did not start", false);

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return new NpmProcessResult(
                null,
                await SafeOutputAsync(stdout),
                await SafeOutputAsync(stderr),
                true);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        return new NpmProcessResult(
            process.ExitCode,
            await SafeOutputAsync(stdout),
            await SafeOutputAsync(stderr),
            false);
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "NpmGlobalInstaller: process kill best-effort"); }
    }

    internal static string BuildWindowsCommandArguments(
        string npmCommand,
        IReadOnlyList<string> arguments)
    {
        if (npmCommand.Contains('"') || arguments.Any(argument => argument.Contains('"')))
            throw new ArgumentException("npm command paths and arguments cannot contain a double quote");
        var command = string.Join(" ", new[] { $"\"{npmCommand}\"" }.Concat(
            arguments.Select(argument => argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument)));
        return $"/d /s /c \"{command}\"";
    }

    private static async Task<string> SafeOutputAsync(Task<string> output)
    {
        try { return await output; }
        catch { return ""; }
    }
}

internal sealed record NpmInvocation(
    string ExecutablePath,
    IReadOnlyList<string> PrefixArguments,
    string NpmPath,
    string WorkingDirectory,
    bool UsesWindowsCommandHost = false);

internal sealed record NpmAvailability(
    NpmInvocation? Invocation,
    string? Version,
    string Detail);

internal sealed record NpmProcessResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);

public enum NpmGlobalInstallMode
{
    Install,
    ForceRelink,
}

public sealed record NpmGlobalInstallResult(
    bool Succeeded,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    string? FailureKind = null,
    string? NpmExecutable = null,
    string? NpmVersion = null);
