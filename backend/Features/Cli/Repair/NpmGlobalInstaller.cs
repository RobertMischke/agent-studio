using System.Diagnostics;

namespace AgentStudio.Cli;

/// <summary>
/// Bounded process boundary for repairing one already-installed global npm
/// package. Package selection and repair policy live in
/// <see cref="LocalCliRepairService"/>; this class only executes npm.
/// </summary>
public class NpmGlobalInstaller
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public virtual async Task<NpmGlobalInstallResult> InstallAsync(
        string packageName,
        CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ResolveNpmExecutable(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("install");
            process.StartInfo.ArgumentList.Add("--global");
            process.StartInfo.ArgumentList.Add(packageName);

            if (!process.Start())
                return new NpmGlobalInstallResult(false, null, "", "npm did not start");

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DefaultTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { SilentCatch.Note(ex, "NpmGlobalInstaller: timeout kill best-effort"); }
                return new NpmGlobalInstallResult(
                    false,
                    null,
                    await SafeOutputAsync(stdout),
                    "npm install timed out after five minutes");
            }

            var standardOutput = await SafeOutputAsync(stdout);
            var standardError = await SafeOutputAsync(stderr);
            return new NpmGlobalInstallResult(
                process.ExitCode == 0,
                process.ExitCode,
                standardOutput,
                standardError);
        }
        catch (Exception ex)
        {
            return new NpmGlobalInstallResult(false, null, "", ex.Message);
        }
    }

    private static async Task<string> SafeOutputAsync(Task<string> output)
    {
        try { return await output; }
        catch { return ""; }
    }

    private static string ResolveNpmExecutable()
    {
        if (!OperatingSystem.IsWindows()) return "npm";
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrWhiteSpace(appData))
        {
            var npmCommand = Path.Combine(appData, "npm", "npm.cmd");
            if (File.Exists(npmCommand)) return npmCommand;
        }
        return "npm.cmd";
    }
}

public sealed record NpmGlobalInstallResult(
    bool Succeeded,
    int? ExitCode,
    string StandardOutput,
    string StandardError);
