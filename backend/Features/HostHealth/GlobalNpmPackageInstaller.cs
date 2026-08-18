using System.Diagnostics;

using AgentStudio.Diagnostics;

namespace AgentStudio.HostHealth;

/// <summary>Outcome of one <c>npm install -g</c>: what happened, how long it took, and the tail of what npm said.</summary>
public sealed record GlobalNpmInstallResult(bool Succeeded, int? ExitCode, double DurationMs, string Output, string? Error);

/// <summary>Seam so the repair coordinator can be tested without a real npm.</summary>
public interface IGlobalNpmPackageInstaller
{
    Task<GlobalNpmInstallResult> InstallGlobalAsync(string packageId, CancellationToken ct);
}

/// <summary>
/// The single bounded side effect this feature owns: reinstall one global npm
/// package. It exists because the observed control-plane breakage - package
/// present, bin shims gone - is exactly what a global reinstall fixes, and
/// because the operator had to run this by hand twice within six days.
///
/// <para>
/// This starts <c>npm</c>, never a coding-agent CLI. The rate limit that keeps
/// it from becoming an install loop lives in <see cref="LocalCliRepairThrottle"/>,
/// and the decision that it may run at all lives in
/// <see cref="LocalCliInstallDiagnosis"/>; this class only executes.
/// </para>
/// </summary>
public sealed class GlobalNpmPackageInstaller : IGlobalNpmPackageInstaller
{
    /// <summary>The claude-code payload is a few hundred megabytes; a cold install on a slow link needs room.</summary>
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How much npm output is kept for the journal. Enough to explain a failure, small enough for one JSONL row.</summary>
    private const int OutputTailChars = 2000;

    private readonly ILogger<GlobalNpmPackageInstaller> _logger;

    public GlobalNpmPackageInstaller(ILogger<GlobalNpmPackageInstaller> logger) => _logger = logger;

    public async Task<GlobalNpmInstallResult> InstallGlobalAsync(string packageId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "npm.cmd" : "npm",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("--global");
        startInfo.ArgumentList.Add(packageId);

        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new(false, null, Elapsed(startedAt), "", "npm did not start");
            }

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(InstallTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, "GlobalNpmPackageInstaller: killing a timed-out npm"); }
                return new(false, null, Elapsed(startedAt), "", $"npm install --global {packageId} timed out");
            }

            var output = Tail(await stdout + await stderr);
            var succeeded = process.ExitCode == 0;
            if (!succeeded)
            {
                _logger.LogWarning("npm install --global {PackageId} exited {ExitCode}: {Output}",
                    packageId, process.ExitCode, output);
            }
            return new(succeeded, process.ExitCode, Elapsed(startedAt), output,
                succeeded ? null : $"npm exited {process.ExitCode}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "npm install --global {PackageId} could not be started", packageId);
            return new(false, null, Elapsed(startedAt), "", ex.Message);
        }
    }

    private static double Elapsed(long startedAt)
        => Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

    private static string Tail(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= OutputTailChars) return trimmed;
        return string.Concat("...", trimmed.AsSpan(trimmed.Length - OutputTailChars));
    }
}
