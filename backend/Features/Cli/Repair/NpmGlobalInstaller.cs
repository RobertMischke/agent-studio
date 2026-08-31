using System.Diagnostics;

namespace AgentStudio.Cli;

/// <summary>
/// Bounded process boundary for installing or relinking one global npm package.
/// Package selection and repair policy live in
/// <see cref="LocalCliRepairService"/>; this class only executes npm.
/// </summary>
public class NpmGlobalInstaller
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public virtual async Task<NpmGlobalInstallResult> InstallAsync(
        string packageName,
        NpmGlobalInstallMode mode,
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
            foreach (var argument in BuildArguments(packageName, mode))
                process.StartInfo.ArgumentList.Add(argument);

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

    internal static IReadOnlyList<string> BuildArguments(
        string packageName,
        NpmGlobalInstallMode mode)
    {
        var arguments = new List<string> { "install", "--global", packageName };
        if (mode == NpmGlobalInstallMode.ForceRelink) arguments.Add("--force");
        return arguments;
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

public enum NpmGlobalInstallMode
{
    Install,
    ForceRelink,
}

public sealed record NpmGlobalInstallResult(
    bool Succeeded,
    int? ExitCode,
    string StandardOutput,
    string StandardError);
