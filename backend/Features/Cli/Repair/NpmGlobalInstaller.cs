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
    public static readonly TimeSpan PreflightTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<IReadOnlyList<NpmExecutableCandidate>> _candidateSource;
    private readonly string _workingDirectory;

    public NpmGlobalInstaller()
        : this(DefaultCandidates, ResolveWorkingDirectory())
    {
    }

    internal NpmGlobalInstaller(
        Func<IReadOnlyList<NpmExecutableCandidate>> candidateSource,
        string workingDirectory)
    {
        _candidateSource = candidateSource;
        _workingDirectory = workingDirectory;
    }

    public virtual async Task<NpmGlobalInstallResult> InstallAsync(
        string packageName,
        NpmGlobalInstallMode mode,
        CancellationToken ct)
    {
        var resolution = await ResolveNpmExecutableAsync(ct);
        if (!resolution.Available || resolution.Candidate is null)
        {
            return new NpmGlobalInstallResult(
                NpmGlobalInstallOutcome.NpmUnavailable,
                null,
                "",
                resolution.Detail,
                null);
        }

        var execution = await RunAsync(
            resolution.Candidate,
            BuildArguments(packageName, mode),
            DefaultTimeout,
            ct);
        return new NpmGlobalInstallResult(
            execution.TimedOut
                ? NpmGlobalInstallOutcome.TimedOut
                : execution.ExitCode == 0
                    ? NpmGlobalInstallOutcome.Installed
                    : NpmGlobalInstallOutcome.InstallFailed,
            execution.ExitCode,
            execution.StandardOutput,
            execution.StandardError,
            resolution.Candidate.NpmPath);
    }

    internal async Task<NpmExecutableResolution> ResolveNpmExecutableAsync(CancellationToken ct)
    {
        var rejected = new List<string>();
        foreach (var candidate in _candidateSource()
                     .DistinctBy(item => $"{item.LaunchPath}\0{item.NpmPath}", StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate.LaunchPath) || !File.Exists(candidate.NpmPath)) continue;

            var preflight = await RunAsync(candidate, ["--version"], PreflightTimeout, ct);
            if (preflight.ExitCode == 0
                && !string.IsNullOrWhiteSpace(preflight.StandardOutput))
            {
                return new NpmExecutableResolution(
                    true,
                    candidate,
                    preflight.StandardOutput.Trim(),
                    $"npm {preflight.StandardOutput.Trim()} resolved at '{candidate.NpmPath}'.");
            }

            var verdict = preflight.TimedOut
                ? "timed out"
                : $"exited {preflight.ExitCode?.ToString() ?? "without an exit code"}";
            rejected.Add($"'{candidate.NpmPath}' {verdict}");
        }

        var detail = rejected.Count == 0
            ? "npm unavailable: no npm executable candidate was found."
            : $"npm unavailable: no candidate passed 'npm --version' ({string.Join("; ", rejected)}).";
        return new NpmExecutableResolution(false, null, null, detail);
    }

    internal static IReadOnlyList<string> BuildArguments(
        string packageName,
        NpmGlobalInstallMode mode)
    {
        var arguments = new List<string> { "install", "--global", packageName };
        if (mode == NpmGlobalInstallMode.ForceRelink) arguments.Add("--force");
        return arguments;
    }

    internal static IReadOnlyList<NpmExecutableCandidate> BuildCandidates(
        bool isWindows,
        string? pathValue,
        string? appData,
        string? programFiles)
    {
        var candidates = new List<NpmExecutableCandidate>();
        var separator = isWindows ? ';' : Path.PathSeparator;
        var pathDirectories = (pathValue ?? "")
            .Split(separator, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => Environment.ExpandEnvironmentVariables(item.Trim().Trim('"')))
            .Where(Path.IsPathRooted)
            .ToArray();

        if (isWindows)
        {
            foreach (var directory in pathDirectories)
            {
                var node = Path.Combine(directory, "node.exe");
                if (!File.Exists(node)) continue;
                candidates.Add(new NpmExecutableCandidate(Path.Combine(directory, "npm.cmd"), null));
                candidates.Add(new NpmExecutableCandidate(
                    Path.Combine(directory, "node_modules", "npm", "bin", "npm-cli.js"),
                    node));
            }

            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                var nodeDirectory = Path.Combine(programFiles, "nodejs");
                var node = Path.Combine(nodeDirectory, "node.exe");
                candidates.Add(new NpmExecutableCandidate(Path.Combine(nodeDirectory, "npm.cmd"), null));
                candidates.Add(new NpmExecutableCandidate(
                    Path.Combine(nodeDirectory, "node_modules", "npm", "bin", "npm-cli.js"),
                    node));
            }

            if (!string.IsNullOrWhiteSpace(appData))
                candidates.Add(new NpmExecutableCandidate(Path.Combine(appData, "npm", "npm.cmd"), null));

            foreach (var directory in pathDirectories)
            {
                candidates.Add(new NpmExecutableCandidate(Path.Combine(directory, "npm.cmd"), null));
                candidates.Add(new NpmExecutableCandidate(Path.Combine(directory, "npm.exe"), null));
            }
        }
        else
        {
            foreach (var directory in pathDirectories)
                candidates.Add(new NpmExecutableCandidate(Path.Combine(directory, "npm"), null));
        }

        return candidates;
    }

    private async Task<NpmProcessResult> RunAsync(
        NpmExecutableCandidate candidate,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = BuildStartInfo(candidate, arguments, _workingDirectory),
            };
            if (!process.Start())
                return new NpmProcessResult(null, "", "npm did not start", false);

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
                return new NpmProcessResult(
                    null,
                    await SafeOutputAsync(stdout),
                    $"npm timed out after {timeout.TotalSeconds:F0} seconds",
                    true);
            }

            return new NpmProcessResult(
                process.ExitCode,
                await SafeOutputAsync(stdout),
                await SafeOutputAsync(stderr),
                false);
        }
        catch (Exception ex)
        {
            return new NpmProcessResult(null, "", ex.Message, false);
        }
    }

    private static ProcessStartInfo BuildStartInfo(
        NpmExecutableCandidate candidate,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrWhiteSpace(candidate.NodePath))
        {
            startInfo.FileName = candidate.NodePath;
            startInfo.ArgumentList.Add(candidate.NpmPath);
        }
        else if (OperatingSystem.IsWindows()
                 && Path.GetExtension(candidate.NpmPath) is ".cmd" or ".bat")
        {
            startInfo.FileName = Environment.GetEnvironmentVariable("ComSpec")
                                 ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(candidate.NpmPath);
        }
        else
        {
            startInfo.FileName = candidate.NpmPath;
        }

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static IReadOnlyList<NpmExecutableCandidate> DefaultCandidates()
        => BuildCandidates(
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("APPDATA"),
            Environment.GetEnvironmentVariable("ProgramFiles"));

    private static string ResolveWorkingDirectory()
    {
        var temp = Path.GetTempPath();
        return Directory.Exists(temp) ? temp : AppContext.BaseDirectory;
    }

    private static async Task<string> SafeOutputAsync(Task<string> output)
    {
        try { return await output; }
        catch { return ""; }
    }
}

public enum NpmGlobalInstallMode
{
    Install,
    ForceRelink,
}

public enum NpmGlobalInstallOutcome
{
    Installed,
    NpmUnavailable,
    InstallFailed,
    TimedOut,
}

public sealed record NpmGlobalInstallResult(
    NpmGlobalInstallOutcome Outcome,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    string? NpmExecutable)
{
    public bool Succeeded => Outcome == NpmGlobalInstallOutcome.Installed;
}

internal sealed record NpmExecutableCandidate(string NpmPath, string? NodePath)
{
    public string LaunchPath => NodePath ?? NpmPath;
}

internal sealed record NpmExecutableResolution(
    bool Available,
    NpmExecutableCandidate? Candidate,
    string? Version,
    string Detail);

internal sealed record NpmProcessResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut);
