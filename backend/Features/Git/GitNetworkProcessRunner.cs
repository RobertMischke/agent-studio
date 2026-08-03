using System.ComponentModel;
using System.Diagnostics;

namespace AgentStudio.Git;

internal enum GitProcessFailureKind
{
    None,
    TimedOut,
    Cancelled,
    StartFailure,
    ResourceExhaustion,
}

internal sealed record GitProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    GitProcessFailureKind FailureKind)
{
    public bool Success => FailureKind == GitProcessFailureKind.None && ExitCode == 0;
}

/// <summary>
/// Owns bounded backend Git child-process execution. Output pipes are drained
/// concurrently, and every timeout or cancellation terminates the full process
/// tree before returning. This prevents a DNS or remote stall from retaining
/// Git processes and their inherited pipe handles for the backend lifetime.
/// </summary>
internal static class GitNetworkProcessRunner
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TerminationDrainTimeout = TimeSpan.FromSeconds(3);

    internal static GitProcessResult Run(
        ProcessStartInfo startInfo,
        string? stdin = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RunAsync(startInfo, stdin, timeout ?? DefaultTimeout, cancellationToken)
            .GetAwaiter()
            .GetResult();

    internal static async Task<GitProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        string? stdin,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Git process timeout must be positive.");
        if (cancellationToken.IsCancellationRequested)
            return Failed(GitProcessFailureKind.Cancelled, "git operation cancelled");

        Process? process = null;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);

        try
        {
            process = startProcess is null
                ? Process.Start(startInfo)
                : startProcess(startInfo);
            if (process is null)
                return Failed(GitProcessFailureKind.StartFailure, "git process did not start");

            stdout = process.StandardOutput.ReadToEndAsync(bounded.Token);
            stderr = process.StandardError.ReadToEndAsync(bounded.Token);

            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), bounded.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(bounded.Token).ConfigureAwait(false);
            return new GitProcessResult(
                process.ExitCode,
                stdout.Result,
                stderr.Result,
                GitProcessFailureKind.None);
        }
        catch (OperationCanceledException)
        {
            var cancelled = cancellationToken.IsCancellationRequested;
            await TerminateAndDrainAsync(process, stdout, stderr).ConfigureAwait(false);
            return Failed(
                cancelled ? GitProcessFailureKind.Cancelled : GitProcessFailureKind.TimedOut,
                cancelled
                    ? "git operation cancelled"
                    : $"git operation timed out after {timeout.TotalSeconds:0.###} seconds");
        }
        catch (Exception ex)
        {
            await TerminateAndDrainAsync(process, stdout, stderr).ConfigureAwait(false);
            var exhausted = IsResourceExhaustion(ex);
            return Failed(
                exhausted ? GitProcessFailureKind.ResourceExhaustion : GitProcessFailureKind.StartFailure,
                exhausted
                    ? $"git process resource exhaustion: {ex.Message}"
                    : $"git process failed: {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static GitProcessResult Failed(GitProcessFailureKind kind, string error)
        => new(-1, string.Empty, error, kind);

    private static async Task TerminateAndDrainAsync(
        Process? process,
        Task<string>? stdout,
        Task<string>? stderr)
    {
        if (process is null) return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "GitNetworkProcessRunner: process-tree termination");
        }

        var drains = new List<Task>(3);
        try { drains.Add(process.WaitForExitAsync(CancellationToken.None)); }
        catch (Exception ex) { SilentCatch.Note(ex, "GitNetworkProcessRunner: exit wait setup"); }
        if (stdout is not null) drains.Add(stdout);
        if (stderr is not null) drains.Add(stderr);
        if (drains.Count == 0) return;

        try
        {
            await Task.WhenAll(drains)
                .WaitAsync(TerminationDrainTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "GitNetworkProcessRunner: bounded post-termination drain");
        }
    }

    private static bool IsResourceExhaustion(Exception exception)
    {
        if (exception is OutOfMemoryException) return true;
        if (exception is Win32Exception win32
            && win32.NativeErrorCode is 4 or 8 or 14 or 18 or 23 or 24)
            return true;

        var message = exception.Message;
        return message.Contains("too many open files", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not enough memory", StringComparison.OrdinalIgnoreCase)
            || message.Contains("resource temporarily unavailable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("insufficient system resources", StringComparison.OrdinalIgnoreCase);
    }
}
