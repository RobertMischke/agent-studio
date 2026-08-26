using System.Diagnostics;

namespace AgentStudio.Cli;

public interface INpmGlobalInstaller
{
    Task<NpmInstallResult> InstallAsync(string packageName, CancellationToken ct);
}

public sealed record NpmInstallResult(
    bool Started,
    int? ExitCode,
    string? StandardOutput,
    string? StandardError,
    string? Error)
;

/// <summary>Bounded process boundary for <c>npm install --global package</c>.</summary>
public sealed class NpmGlobalInstaller(ILogger<NpmGlobalInstaller> logger) : INpmGlobalInstaller
{
    internal static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
    private const int OutputLimit = 4_000;

    public async Task<NpmInstallResult> InstallAsync(string packageName, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "npm.cmd" : "npm",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            process.StartInfo.ArgumentList.Add("install");
            process.StartInfo.ArgumentList.Add("--global");
            process.StartInfo.ArgumentList.Add(packageName);

            if (!process.Start())
                return new NpmInstallResult(false, null, null, null, "npm process did not start");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(InstallTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                var error = ct.IsCancellationRequested
                    ? "npm install was cancelled"
                    : $"npm install timed out after {InstallTimeout.TotalMinutes:0} minutes";
                return new NpmInstallResult(
                    true,
                    null,
                    await BoundAfterStopAsync(stdout),
                    await BoundAfterStopAsync(stderr),
                    error);
            }

            return new NpmInstallResult(
                true,
                process.ExitCode,
                Bound(await stdout),
                Bound(await stderr),
                process.ExitCode == 0 ? null : $"npm install exited {process.ExitCode}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "npm global repair process failed to start");
            return new NpmInstallResult(false, null, null, null, ex.Message);
        }
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "NpmGlobalInstaller: bounded install kill"); }
    }

    private static async Task<string?> BoundAfterStopAsync(Task<string> output)
    {
        try { return Bound(await output.WaitAsync(TimeSpan.FromSeconds(2))); }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "NpmGlobalInstaller: bounded output capture");
            return null;
        }
    }

    private static string? Bound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var redacted = CredentialRedactor.Redact(value.Trim());
        return redacted.Length <= OutputLimit ? redacted : redacted[^OutputLimit..];
    }
}
