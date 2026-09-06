using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AgentStudio.Bus;

namespace AgentStudio.Management;

public sealed record CodexSignInRequest(string SshTarget);

public sealed record CodexSignInResponse(
    string Handle,
    string RunnerId,
    string Host,
    string State,
    string Detail,
    DateTime RequestedAt,
    DateTime ExpiresAt,
    string? VerificationUrl,
    string? UserCode,
    DateTime? CompletedAt = null);

public sealed class CodexSignInException(string message) : Exception(message);

public sealed record CodexSignInVerification(bool Succeeded, string Detail, IReadOnlyList<string> RestartedServices);

public interface ICodexDeviceAuthProcess : IAsyncDisposable
{
    IAsyncEnumerable<string> ReadOutputAsync(CancellationToken cancellationToken);
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
    Task TerminateAsync();
}

public interface ICodexSignInTransport
{
    Task<ICodexDeviceAuthProcess> StartAsync(string sshTarget, CancellationToken cancellationToken);
    Task<CodexSignInVerification> VerifyAndRefreshAsync(string sshTarget, CancellationToken cancellationToken);
}

/// <summary>
/// Owns short-lived Codex device-auth sessions in memory. The remote Codex
/// process writes its credential only into the runner user's native Codex
/// store. Studio retains no token and clears the one-time code when a session
/// reaches a terminal outcome.
/// </summary>
public sealed partial class CodexSignInService : IAsyncDisposable
{
    internal static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan TranscriptTimeout = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ICodexSignInTransport _transport;
    private readonly AgentMessageBusBridge _bus;
    private readonly ILogger<CodexSignInService> _logger;

    public CodexSignInService(
        ICodexSignInTransport transport,
        AgentMessageBusBridge bus,
        ILogger<CodexSignInService> logger)
    {
        _transport = transport;
        _bus = bus;
        _logger = logger;
    }

    public async Task<CodexSignInResponse> StartAsync(
        string runnerId,
        CodexSignInRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var validation = ProviderAuthProvisioningPolicy.ValidateTarget(request.SshTarget, runnerId);
        if (validation is not null) throw new ArgumentException(validation, nameof(request));

        var handle = Guid.CreateVersion7().ToString("N");
        var requestedAt = DateTime.UtcNow;
        var session = new Session(
            handle,
            runnerId.Trim(),
            request.SshTarget.Trim(),
            actor,
            requestedAt,
            requestedAt.Add(SessionTimeout));
        if (!_sessions.TryAdd(handle, session))
            throw new CodexSignInException("A Codex sign-in session could not be allocated.");

        try
        {
            session.Process = await _transport.StartAsync(session.Host, cancellationToken);
            session.Monitor = MonitorAsync(session);
        }
        catch (Exception exception)
        {
            await FinishAsync(session, "failed", "The remote Codex sign-in process could not be started.");
            throw new CodexSignInException(
                $"The remote Codex sign-in process could not be started: {SafeExcerpt(exception.Message)}");
        }

        using var transcriptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        transcriptTimeout.CancelAfter(TranscriptTimeout);
        try
        {
            await session.TranscriptReady.Task.WaitAsync(transcriptTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await session.Process.TerminateAsync();
            await FinishAsync(session, "failed", "Codex did not provide a device-auth URL and code within 30 seconds.");
            throw new CodexSignInException("Codex did not provide a device-auth URL and code within 30 seconds.");
        }

        var snapshot = Snapshot(session);
        if (snapshot.State == "failed") throw new CodexSignInException(snapshot.Detail);
        return snapshot;
    }

    public CodexSignInResponse? Get(string runnerId, string handle)
    {
        if (!_sessions.TryGetValue(handle, out var session)
            || !string.Equals(session.RunnerId, runnerId, StringComparison.Ordinal)) return null;
        return Snapshot(session);
    }

    private async Task MonitorAsync(Session session)
    {
        using var timeout = new CancellationTokenSource(SessionTimeout);
        try
        {
            await foreach (var line in session.Process!.ReadOutputAsync(timeout.Token))
            {
                CaptureTranscript(session, line);
            }

            var exitCode = await session.Process.WaitForExitAsync(timeout.Token);
            if (exitCode != 0)
            {
                await FinishAsync(session, "failed", "Codex device authentication ended before sign-in completed.");
                return;
            }

            var verification = await _transport.VerifyAndRefreshAsync(session.Host, timeout.Token);
            await FinishAsync(
                session,
                verification.Succeeded ? "completed" : "failed",
                verification.Detail);
        }
        catch (OperationCanceledException)
        {
            await session.Process!.TerminateAsync();
            await FinishAsync(session, "failed", "Codex sign-in timed out after 15 minutes and the remote process was stopped.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Codex device-auth session failed for runner {RunnerId} handle {Handle}; errorType={ErrorType}",
                session.RunnerId, session.Handle, exception.GetType().Name);
            await session.Process!.TerminateAsync();
            await FinishAsync(session, "failed", "Codex sign-in failed while Studio was checking the host outcome.");
        }
        finally
        {
            if (session.Process is not null) await session.Process.DisposeAsync();
        }
    }

    private static void CaptureTranscript(Session session, string line)
    {
        lock (session.Gate)
        {
            if (session.State != "pending") return;
            if (session.VerificationUrl is null)
            {
                var url = UrlPattern().Match(line);
                if (url.Success && Uri.TryCreate(url.Value.TrimEnd('.', ',', ';'), UriKind.Absolute, out var parsed)
                    && parsed.Scheme == Uri.UriSchemeHttps)
                    session.VerificationUrl = parsed.AbsoluteUri;
            }
            if (session.UserCode is null)
            {
                var code = CodePattern().Match(line);
                if (code.Success) session.UserCode = code.Value.ToUpperInvariant();
            }
            if (session.VerificationUrl is not null && session.UserCode is not null)
                session.TranscriptReady.TrySetResult();
        }
    }

    private async Task FinishAsync(Session session, string state, string detail)
    {
        lock (session.Gate)
        {
            if (session.State != "pending") return;
            session.State = state;
            session.Detail = detail;
            session.CompletedAt = DateTime.UtcNow;
            session.VerificationUrl = null;
            session.UserCode = null;
            session.TranscriptReady.TrySetResult();
        }
        if (Interlocked.Exchange(ref session.AuditWritten, 1) == 0)
        {
            await _bus.EmitProviderSignInAsync(
                session.Host,
                "codex",
                session.Actor,
                state == "completed" ? "completed" : "failed");
        }
    }

    private static CodexSignInResponse Snapshot(Session session)
    {
        lock (session.Gate)
        {
            return new CodexSignInResponse(
                session.Handle,
                session.RunnerId,
                session.Host,
                session.State,
                session.Detail,
                session.RequestedAt,
                session.ExpiresAt,
                session.VerificationUrl,
                session.UserCode,
                session.CompletedAt);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            if (session.Process is not null) await session.Process.TerminateAsync();
        }
    }

    private static string SafeExcerpt(string? value, int maxLength = 300)
    {
        var text = string.Join(' ', (value ?? "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    [GeneratedRegex(@"https://[^\s<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"\b[A-Z0-9]{4,8}-[A-Z0-9]{4,8}\b", RegexOptions.IgnoreCase)]
    private static partial Regex CodePattern();

    private sealed class Session(
        string handle,
        string runnerId,
        string host,
        string actor,
        DateTime requestedAt,
        DateTime expiresAt)
    {
        public object Gate { get; } = new();
        public string Handle { get; } = handle;
        public string RunnerId { get; } = runnerId;
        public string Host { get; } = host;
        public string Actor { get; } = actor;
        public DateTime RequestedAt { get; } = requestedAt;
        public DateTime ExpiresAt { get; } = expiresAt;
        public string State { get; set; } = "pending";
        public string Detail { get; set; } = "Complete sign-in in the browser. Studio is waiting for Codex on the host.";
        public string? VerificationUrl { get; set; }
        public string? UserCode { get; set; }
        public DateTime? CompletedAt { get; set; }
        public ICodexDeviceAuthProcess? Process { get; set; }
        public Task? Monitor { get; set; }
        public TaskCompletionSource TranscriptReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AuditWritten;
    }
}

/// <summary>
/// Runs Codex through the same validated SSH target shape as provider-auth
/// provisioning. The remote timeout owns cleanup even if Studio or its SSH
/// connection disappears during the browser step.
/// </summary>
public sealed class SshCodexSignInTransport : ICodexSignInTransport
{
    private static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(45);

    public async Task<ICodexDeviceAuthProcess> StartAsync(
        string sshTarget,
        CancellationToken cancellationToken)
    {
        var process = Start(sshTarget);
        await process.StandardInput.WriteAsync(LoginScript.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();
        return new SshDeviceAuthProcess(process);
    }

    public async Task<CodexSignInVerification> VerifyAndRefreshAsync(
        string sshTarget,
        CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(VerificationTimeout);
        using var process = Start(sshTarget);
        await process.StandardInput.WriteAsync(VerificationScript.AsMemory(), bounded.Token);
        await process.StandardInput.FlushAsync(bounded.Token);
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(bounded.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(bounded.Token);
        try
        {
            await process.WaitForExitAsync(bounded.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new CodexSignInVerification(false, "Codex sign-in completed, but the host status probe timed out.", []);
        }
        var stdout = await stdoutTask;
        _ = await stderrTask;
        var restarted = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("codex-sign-in-unit=", StringComparison.Ordinal))
            .Select(line => line["codex-sign-in-unit=".Length..].Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var succeeded = process.ExitCode == 0
            && stdout.Contains("codex-sign-in-status=ok", StringComparison.Ordinal);
        return new CodexSignInVerification(
            succeeded,
            succeeded
                ? "Codex reports signed in. Active runner units were restarted so a fresh provider probe can advertise the capability."
                : "The device flow ended, but `codex login status` did not confirm a signed-in runner user.",
            restarted);
    }

    internal static ProcessStartInfo BuildStartInfo(string sshTarget)
    {
        var info = new ProcessStartInfo
        {
            FileName = "ssh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("-o");
        info.ArgumentList.Add("BatchMode=yes");
        info.ArgumentList.Add("-o");
        info.ArgumentList.Add("ConnectTimeout=10");
        info.ArgumentList.Add("-T");
        info.ArgumentList.Add(sshTarget);
        info.ArgumentList.Add("sudo");
        info.ArgumentList.Add("bash");
        info.ArgumentList.Add("-s");
        return info;
    }

    internal static string LoginScriptForTests() => LoginScript;
    internal static string VerificationScriptForTests() => VerificationScript;

    private static Process Start(string sshTarget)
    {
        var process = new Process { StartInfo = BuildStartInfo(sshTarget) };
        try
        {
            if (!process.Start()) throw new CodexSignInException("The SSH process could not be started.");
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            SilentCatch.Note(exception, "SshCodexSignInTransport: bounded SSH process cleanup");
        }
    }

    private sealed class SshDeviceAuthProcess : ICodexDeviceAuthProcess
    {
        private readonly Process _process;
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        private readonly Task _stdout;
        private readonly Task _stderr;

        public SshDeviceAuthProcess(Process process)
        {
            _process = process;
            _stdout = PumpAsync(process.StandardOutput);
            _stderr = PumpAsync(process.StandardError);
            _ = CloseOutputAsync();
        }

        public async IAsyncEnumerable<string> ReadOutputAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var line in _output.Reader.ReadAllAsync(cancellationToken)) yield return line;
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
            => WaitAsync(cancellationToken);

        public Task TerminateAsync()
        {
            TryKill(_process);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _process.Dispose();
            return ValueTask.CompletedTask;
        }

        private async Task PumpAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line) await _output.Writer.WriteAsync(line);
        }

        private async Task CloseOutputAsync()
        {
            try
            {
                await Task.WhenAll(_stdout, _stderr);
                _output.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                _output.Writer.TryComplete(exception);
            }
        }

        private async Task<int> WaitAsync(CancellationToken cancellationToken)
        {
            await _process.WaitForExitAsync(cancellationToken);
            return _process.ExitCode;
        }
    }

    private const string LoginScript = """
set -euo pipefail
runner_user='agent'
for unit in agent-host.service agent-runner.service agent-runner-review.service; do
  if systemctl cat "$unit" >/dev/null 2>&1; then
    configured_user="$(systemctl show --property=User --value "$unit")"
    if [[ -n "$configured_user" && "$configured_user" != 'root' ]]; then runner_user="$configured_user"; break; fi
  fi
done
runner_home="$(getent passwd "$runner_user" | cut -d: -f6)"
[[ -n "$runner_home" ]] || { echo '[codex-sign-in] Runner user has no home directory.' >&2; exit 41; }
codex_bin="$(sudo -u "$runner_user" -H sh -lc 'command -v codex')"
[[ -n "$codex_bin" ]] || { echo '[codex-sign-in] Codex is not installed for the runner user.' >&2; exit 42; }
exec timeout --signal=TERM --kill-after=5s 900s sudo -u "$runner_user" -H env HOME="$runner_home" "$codex_bin" login --device-auth
""";

    private const string VerificationScript = """
set -euo pipefail
runner_user='agent'
for unit in agent-host.service agent-runner.service agent-runner-review.service; do
  if systemctl cat "$unit" >/dev/null 2>&1; then
    configured_user="$(systemctl show --property=User --value "$unit")"
    if [[ -n "$configured_user" && "$configured_user" != 'root' ]]; then runner_user="$configured_user"; break; fi
  fi
done
runner_home="$(getent passwd "$runner_user" | cut -d: -f6)"
codex_bin="$(sudo -u "$runner_user" -H sh -lc 'command -v codex')"
[[ -n "$runner_home" && -n "$codex_bin" ]] || exit 43
sudo -u "$runner_user" -H env HOME="$runner_home" "$codex_bin" login status >/dev/null 2>&1
echo 'codex-sign-in-status=ok'
for unit in agent-host.service agent-runner.service agent-runner-review.service; do
  if systemctl is-active --quiet "$unit"; then
    systemctl restart "$unit"
    printf 'codex-sign-in-unit=%s\n' "$unit"
  fi
done
""";
}
