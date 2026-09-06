using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Bus;

namespace AgentStudio.Management;

public sealed record CodexSignInStartRequest(string SshTarget);

public sealed record CodexSignInStartResponse(
    string Handle,
    string HostId,
    string Provider,
    string State,
    string VerificationUrl,
    string UserCode,
    DateTime ExpiresAt);

public sealed record CodexSignInStatusResponse(
    string Handle,
    string HostId,
    string Provider,
    string State,
    string Detail,
    DateTime RequestedAt,
    DateTime ExpiresAt,
    DateTime? CompletedAt);

public sealed class CodexSignInException(string message) : Exception(message);
public sealed class CodexSignInConflictException(string message) : Exception(message);

public static partial class CodexSignInPolicy
{
    public const string Provider = "codex";

    public static string? Validate(string hostId, CodexSignInStartRequest request)
    {
        if (string.IsNullOrWhiteSpace(hostId) || !RunnerIdPattern().IsMatch(hostId.Trim()))
            return "Host identity is required and may contain letters, numbers, dots, underscores, and hyphens.";
        if (string.IsNullOrWhiteSpace(request.SshTarget)
            || !SshTargetPattern().IsMatch(request.SshTarget.Trim()))
            return "SSH target must be a configured alias or user@host without shell characters.";
        return null;
    }

    public static CodexDeviceChallenge ObserveChallenge(
        CodexDeviceChallenge current,
        string? outputLine)
    {
        var line = AnsiPattern().Replace(outputLine ?? string.Empty, string.Empty).Trim();
        var url = current.VerificationUrl;
        var code = current.UserCode;
        if (url is null)
        {
            var match = VerificationUrlPattern().Match(line);
            if (match.Success) url = match.Value.TrimEnd('.', ',', ';', ')');
        }
        if (code is null)
        {
            var match = DeviceCodePattern().Match(line);
            if (match.Success) code = match.Groups[1].Value;
        }
        return new CodexDeviceChallenge(url, code);
    }

    [GeneratedRegex(@"^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex SshTargetPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex RunnerIdPattern();

    [GeneratedRegex(@"https://auth\.openai\.com/[^\s<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex VerificationUrlPattern();

    [GeneratedRegex(@"(?:^|\s)([A-Z0-9]{3,8}-[A-Z0-9]{3,8})(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceCodePattern();

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiPattern();
}

public sealed record CodexDeviceChallenge(string? VerificationUrl, string? UserCode)
{
    public bool IsComplete => VerificationUrl is not null && UserCode is not null;
}

public interface ICodexSignInSshProcess : IAsyncDisposable
{
    int ExitCode { get; }
    Task WriteStandardInputAsync(string value, CancellationToken cancellationToken);
    Task<string?> ReadOutputLineAsync(CancellationToken cancellationToken);
    Task<string?> ReadErrorLineAsync(CancellationToken cancellationToken);
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void Kill();
}

public interface ICodexSignInSshProcessFactory
{
    ICodexSignInSshProcess Start(ProcessStartInfo startInfo);
}

public sealed class CodexSignInSshProcessFactory : ICodexSignInSshProcessFactory
{
    public ICodexSignInSshProcess Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new CodexSignInException("The SSH sign-in process could not be started.");
            return new SystemCodexSignInSshProcess(process);
        }
        catch (Exception exception) when (exception is not CodexSignInException)
        {
            process.Dispose();
            throw new CodexSignInException(
                $"The SSH sign-in process could not be started: {SafeExcerpt(exception.Message)}");
        }
    }

    private static string SafeExcerpt(string? value)
    {
        var text = string.Join(' ', (value ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length <= 500 ? text : text[..500] + "...";
    }

    private sealed class SystemCodexSignInSshProcess(Process process) : ICodexSignInSshProcess
    {
        public int ExitCode => process.ExitCode;

        public async Task WriteStandardInputAsync(string value, CancellationToken cancellationToken)
        {
            await process.StandardInput.WriteAsync(value.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
        }

        public Task<string?> ReadOutputLineAsync(CancellationToken cancellationToken)
            => process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();

        public Task<string?> ReadErrorLineAsync(CancellationToken cancellationToken)
            => process.StandardError.ReadLineAsync(cancellationToken).AsTask();

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => process.WaitForExitAsync(cancellationToken);

        public void Kill()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }

        public ValueTask DisposeAsync()
        {
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Owns short-lived Codex device-auth processes. The only durable record is a
/// typed operator-feed outcome; challenges exist in memory only until the POST
/// response has been created, and no CLI transcript is retained or logged.
/// </summary>
public sealed class CodexDeviceSignInService(
    ICodexSignInSshProcessFactory processFactory,
    AgentMessageBusBridge bus,
    ILogger<CodexDeviceSignInService> logger,
    TimeProvider? timeProvider = null) : IAsyncDisposable
{
    internal static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan ChallengeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CompletedRetention = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _activeByHost = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<CodexSignInStartResponse> StartAsync(
        string hostId,
        CodexSignInStartRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var validation = CodexSignInPolicy.Validate(hostId, request);
        if (validation is not null) throw new ArgumentException(validation, nameof(request));
        SweepCompleted();

        var requestedAt = _time.GetUtcNow().UtcDateTime;
        var handle = Guid.CreateVersion7().ToString("N");
        var normalizedHostId = hostId.Trim();
        if (!_activeByHost.TryAdd(normalizedHostId, handle))
            throw new CodexSignInConflictException("A Codex sign-in is already pending for this execution host.");
        ICodexSignInSshProcess process;
        try { process = processFactory.Start(BuildStartInfo(request.SshTarget.Trim())); }
        catch
        {
            _activeByHost.TryRemove(normalizedHostId, out _);
            throw;
        }
        var session = new Session(
            handle,
            normalizedHostId,
            actor,
            requestedAt,
            requestedAt + SessionTimeout,
            process);
        if (!_sessions.TryAdd(handle, session))
        {
            _activeByHost.TryRemove(normalizedHostId, out _);
            await process.DisposeAsync();
            throw new CodexSignInException("A unique sign-in session could not be created.");
        }

        _ = RunSessionAsync(session);
        try
        {
            await session.ChallengeReady.Task.WaitAsync(ChallengeTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            await FailAndStopAsync(session, "failed", "Codex did not emit a device-auth URL and code within 20 seconds.");
            throw new CodexSignInException(session.Detail);
        }
        catch (OperationCanceledException)
        {
            await FailAndStopAsync(session, "failed", "The Codex sign-in request was cancelled before a device code was received.");
            throw;
        }

        lock (session.Gate)
        {
            if (session.Challenge is not { IsComplete: true } challenge)
                throw new CodexSignInException(session.Detail);
            var response = new CodexSignInStartResponse(
                session.Handle,
                session.HostId,
                CodexSignInPolicy.Provider,
                "pending",
                challenge.VerificationUrl!,
                challenge.UserCode!,
                session.ExpiresAt);
            session.Challenge = new CodexDeviceChallenge(null, null);
            return response;
        }
    }

    public CodexSignInStatusResponse? Get(string hostId, string handle)
    {
        SweepCompleted();
        if (!_sessions.TryGetValue(handle, out var session)
            || !string.Equals(session.HostId, hostId, StringComparison.Ordinal)) return null;
        lock (session.Gate)
        {
            return new CodexSignInStatusResponse(
                session.Handle,
                session.HostId,
                CodexSignInPolicy.Provider,
                session.State,
                session.Detail,
                session.RequestedAt,
                session.ExpiresAt,
                session.CompletedAt);
        }
    }

    internal static ProcessStartInfo BuildStartInfo(string sshTarget)
    {
        var startInfo = new ProcessStartInfo
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
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("BatchMode=yes");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("ConnectTimeout=10");
        startInfo.ArgumentList.Add("-T");
        startInfo.ArgumentList.Add(sshTarget);
        startInfo.ArgumentList.Add("sudo");
        startInfo.ArgumentList.Add("bash");
        startInfo.ArgumentList.Add("-s");
        return startInfo;
    }

    private async Task RunSessionAsync(Session session)
    {
        using var timeout = new CancellationTokenSource(SessionTimeout, _time);
        try
        {
            await session.Process.WriteStandardInputAsync(RemoteScript, timeout.Token);
            var stdout = PumpAsync(session, stderr: false, timeout.Token);
            var stderr = PumpAsync(session, stderr: true, timeout.Token);
            await session.Process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
            bool authenticated;
            lock (session.Gate) authenticated = session.AuthenticationConfirmed;
            if (session.Process.ExitCode == 0 && authenticated)
                await CompleteAsync(session, "completed", "Codex sign-in completed and a fresh runner provider probe was requested.");
            else
                await CompleteAsync(session, "failed", $"Codex sign-in failed on the host (SSH exit code {session.Process.ExitCode}).");
        }
        catch (OperationCanceledException)
        {
            await FailAndStopAsync(session, "failed", "Codex sign-in timed out after 15 minutes and the remote process was stopped.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Codex device sign-in process failed for host {HostId}", session.HostId);
            await FailAndStopAsync(session, "failed", "Codex sign-in failed before the host confirmed authentication.");
        }
        finally
        {
            await session.Process.DisposeAsync();
        }
    }

    private async Task PumpAsync(Session session, bool stderr, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = stderr
                ? await session.Process.ReadErrorLineAsync(cancellationToken)
                : await session.Process.ReadOutputLineAsync(cancellationToken);
            if (line is null) return;
            lock (session.Gate)
            {
                if (line.Contains("provider-sign-in-status=authenticated", StringComparison.Ordinal))
                    session.AuthenticationConfirmed = true;
                if (!session.Challenge.IsComplete)
                {
                    session.Challenge = CodexSignInPolicy.ObserveChallenge(session.Challenge, line);
                    if (session.Challenge.IsComplete) session.ChallengeReady.TrySetResult(true);
                }
            }
        }
    }

    private async Task FailAndStopAsync(Session session, string state, string detail)
    {
        TryKill(session);
        await CompleteAsync(session, state, detail);
    }

    private async Task CompleteAsync(Session session, string state, string detail)
    {
        lock (session.Gate)
        {
            if (session.CompletedAt is not null) return;
            session.State = state;
            session.Detail = detail;
            session.CompletedAt = _time.GetUtcNow().UtcDateTime;
            session.ChallengeReady.TrySetResult(false);
        }
        _activeByHost.TryRemove(session.HostId, out _);
        await bus.EmitProviderSignInAsync(
            session.HostId,
            CodexSignInPolicy.Provider,
            session.Actor,
            state,
            CancellationToken.None);
    }

    private void SweepCompleted()
    {
        var threshold = _time.GetUtcNow().UtcDateTime - CompletedRetention;
        foreach (var pair in _sessions)
        {
            DateTime? completedAt;
            lock (pair.Value.Gate) completedAt = pair.Value.CompletedAt;
            if (completedAt is not null && completedAt < threshold)
                _sessions.TryRemove(pair.Key, out _);
        }
    }

    private static void TryKill(Session session)
    {
        try { session.Process.Kill(); }
        catch (Exception exception)
        {
            SilentCatch.Note(exception, "CodexDeviceSignInService: bounded SSH process cleanup");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            TryKill(session);
            try { await session.Process.DisposeAsync(); }
            catch (Exception exception) { SilentCatch.Note(exception, "CodexDeviceSignInService: dispose SSH session"); }
        }
        _sessions.Clear();
        _activeByHost.Clear();
    }

    private sealed class Session(
        string handle,
        string hostId,
        string actor,
        DateTime requestedAt,
        DateTime expiresAt,
        ICodexSignInSshProcess process)
    {
        public object Gate { get; } = new();
        public string Handle { get; } = handle;
        public string HostId { get; } = hostId;
        public string Actor { get; } = actor;
        public DateTime RequestedAt { get; } = requestedAt;
        public DateTime ExpiresAt { get; } = expiresAt;
        public ICodexSignInSshProcess Process { get; } = process;
        public TaskCompletionSource<bool> ChallengeReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CodexDeviceChallenge Challenge { get; set; } = new(null, null);
        public string State { get; set; } = "pending";
        public string Detail { get; set; } = "Waiting for the browser device-auth flow to complete.";
        public DateTime? CompletedAt { get; set; }
        public bool AuthenticationConfirmed { get; set; }
    }

    private const string RemoteScript = """
set -euo pipefail
runner_user='agent'
if ! id "$runner_user" >/dev/null 2>&1; then
  echo '[provider-sign-in] The runner user is not installed.' >&2
  exit 41
fi
runner_home="$(getent passwd "$runner_user" | cut -d: -f6)"
codex_bin="$(runuser -u "$runner_user" -- env HOME="$runner_home" sh -lc 'command -v codex')"
if [[ -z "$codex_bin" ]]; then
  echo '[provider-sign-in] Codex is not installed for the runner user.' >&2
  exit 42
fi
set +e
login_pid=''
cleanup_login() {
  if [[ -n "$login_pid" ]]; then
    kill "$login_pid" >/dev/null 2>&1 || true
    wait "$login_pid" >/dev/null 2>&1 || true
  fi
}
trap cleanup_login EXIT
trap 'cleanup_login; exit 129' HUP INT TERM
runuser -u "$runner_user" -- env HOME="$runner_home" \
  timeout --signal=TERM --kill-after=5s 15m "$codex_bin" login --device-auth &
login_pid=$!
wait "$login_pid"
login_exit=$?
login_pid=''
trap - HUP INT TERM
set -e
if [[ "$login_exit" -ne 0 ]]; then
  echo '[provider-sign-in] Codex device authentication did not complete.' >&2
  exit "$login_exit"
fi
if ! runuser -u "$runner_user" -- env HOME="$runner_home" "$codex_bin" login status >/dev/null; then
  echo '[provider-sign-in] Codex login status did not confirm authentication.' >&2
  exit 43
fi
echo 'provider-sign-in-status=authenticated'
units=()
if systemctl cat agent-host.service >/dev/null 2>&1; then
  units+=(agent-host.service)
elif systemctl cat agent-runner.service >/dev/null 2>&1; then
  units+=(agent-runner.service)
fi
if systemctl cat agent-runner-review.service >/dev/null 2>&1; then
  units+=(agent-runner-review.service)
fi
for unit in "${units[@]}"; do
  systemctl restart "$unit"
  printf 'provider-sign-in-restarted=%s\n' "$unit"
done
echo 'provider-sign-in-probe-refresh=requested'
""";
}
