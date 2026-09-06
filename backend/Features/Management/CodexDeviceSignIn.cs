using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Management;

public sealed record CodexSignInStartRequest(string SshTarget);

public sealed record CodexSignInChallengeResponse(
    string Handle,
    string Host,
    string Provider,
    string State,
    string VerificationUrl,
    string UserCode,
    DateTime ExpiresAt);

public sealed record CodexSignInStatusResponse(
    string Handle,
    string Host,
    string Provider,
    string State,
    string Detail,
    DateTime ExpiresAt,
    DateTime? CompletedAt);

public sealed record CodexDeviceAuthTransportResult(bool LoginStatusConfirmed);

public interface ICodexDeviceAuthTransport
{
    Task<CodexDeviceAuthTransportResult> RunAsync(
        string sshTarget,
        Action<string> onOutput,
        CancellationToken cancellationToken);
}

public sealed class CodexSignInException(string message) : Exception(message);
public sealed class CodexSignInConflictException(string message) : Exception(message);

/// <summary>
/// Owns bounded, in-memory device-auth process handles. The verification code
/// is delivered only through the start response and is not retained in the
/// terminal session snapshot, activity event, log, task, or durable store.
/// </summary>
public sealed partial class CodexDeviceSignInService : IDisposable
{
    internal static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ChallengeTimeout = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _activeByHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICodexDeviceAuthTransport _transport;
    private readonly AgentStudio.Bus.AgentMessageBusBridge _activity;
    private readonly TimeProvider _time;
    private int _disposed;

    public CodexDeviceSignInService(
        ICodexDeviceAuthTransport transport,
        AgentStudio.Bus.AgentMessageBusBridge activity,
        TimeProvider? timeProvider = null)
    {
        _transport = transport;
        _activity = activity;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<CodexSignInChallengeResponse> StartAsync(
        string host,
        string sshTarget,
        string actor,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!ProviderAuthProvisioningPolicy.IsValidRunnerId(host))
            throw new ArgumentException("Runner identity is invalid.", nameof(host));
        if (!ProviderAuthProvisioningPolicy.IsValidSshTarget(sshTarget))
            throw new ArgumentException("SSH target must be a configured alias or user@host without shell characters.", nameof(sshTarget));

        var normalizedHost = host.Trim();
        var handle = Guid.CreateVersion7().ToString("N");
        if (!_activeByHost.TryAdd(normalizedHost, handle))
            throw new CodexSignInConflictException($"A Codex sign-in is already pending for {normalizedHost}.");

        var now = _time.GetUtcNow().UtcDateTime;
        var session = new Session(handle, normalizedHost, actor, now.Add(SessionTimeout));
        _sessions[handle] = session;
        _ = RunSessionAsync(session, sshTarget.Trim());

        using var challengeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        challengeTimeout.CancelAfter(ChallengeTimeout);
        try
        {
            return await session.WaitForChallengeAsync(challengeTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            session.Cancel();
            throw new CodexSignInException("Codex did not provide a device-auth code within 30 seconds.");
        }
        catch (OperationCanceledException)
        {
            session.Cancel();
            throw;
        }
        finally
        {
            session.ForgetChallenge();
        }
    }

    public CodexSignInStatusResponse? Get(string host, string handle)
    {
        if (!_sessions.TryGetValue(handle, out var session)
            || !string.Equals(session.Host, host, StringComparison.OrdinalIgnoreCase))
            return null;
        return session.Snapshot();
    }

    private async Task RunSessionAsync(Session session, string sshTarget)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(session.Cancellation.Token);
        bounded.CancelAfter(SessionTimeout);
        try
        {
            var challenge = new ChallengeCapture();
            var result = await _transport.RunAsync(sshTarget, line =>
            {
                if (!challenge.Add(line, out var url, out var code)) return;
                session.PublishChallenge(new CodexSignInChallengeResponse(
                    session.Handle,
                    session.Host,
                    "codex",
                    "pending",
                    url!,
                    code!,
                    session.ExpiresAt));
            }, bounded.Token).ConfigureAwait(false);

            if (!session.ChallengeWasPublished)
                throw new CodexSignInException("Codex exited before it provided a device-auth challenge.");
            if (!result.LoginStatusConfirmed)
                throw new CodexSignInException("Codex sign-in finished, but login status was not confirmed on the host.");

            session.Complete("Codex sign-in completed and the runner provider probe is refreshing.", _time.GetUtcNow().UtcDateTime);
            await AuditAsync(session, "completed").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested)
        {
            var outcome = session.Cancellation.IsCancellationRequested ? "cancelled" : "timed_out";
            session.Fail(
                outcome == "timed_out"
                    ? "Codex sign-in timed out after 15 minutes. The remote process was stopped."
                    : "Codex sign-in was cancelled and the remote process was stopped.",
                _time.GetUtcNow().UtcDateTime);
            session.FailChallenge(new CodexSignInException(session.Snapshot().Detail));
            await AuditAsync(session, outcome).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var detail = exception is CodexSignInException
                ? exception.Message
                : "Codex sign-in failed on the execution host. No credential was retained by Studio.";
            session.Fail(detail, _time.GetUtcNow().UtcDateTime);
            session.FailChallenge(new CodexSignInException(detail));
            await AuditAsync(session, "failed").ConfigureAwait(false);
        }
        finally
        {
            _activeByHost.TryRemove(new KeyValuePair<string, string>(session.Host, session.Handle));
        }
    }

    private Task AuditAsync(Session session, string outcome)
        => _activity.EmitProviderSignInAsync(session.Host, "codex", session.Actor, outcome);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var session in _sessions.Values) session.Cancel();
    }

    private sealed class Session
    {
        private readonly object _gate = new();
        private string _state = "pending";
        private string _detail = "Waiting for the browser sign-in to complete.";
        private DateTime? _completedAt;
        private TaskCompletionSource<CodexSignInChallengeResponse>? _challenge =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _challengeWasPublished;

        public Session(string handle, string host, string actor, DateTime expiresAt)
        {
            Handle = handle;
            Host = host;
            Actor = actor;
            ExpiresAt = expiresAt;
        }

        public string Handle { get; }
        public string Host { get; }
        public string Actor { get; }
        public DateTime ExpiresAt { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public bool ChallengeWasPublished
        {
            get { lock (_gate) return _challengeWasPublished; }
        }

        public Task<CodexSignInChallengeResponse> WaitForChallengeAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return (_challenge ?? throw new InvalidOperationException("The device-auth challenge is no longer available."))
                    .Task.WaitAsync(cancellationToken);
            }
        }

        public void PublishChallenge(CodexSignInChallengeResponse challenge)
        {
            lock (_gate)
            {
                _challengeWasPublished = true;
                _challenge?.TrySetResult(challenge);
            }
        }

        public void FailChallenge(Exception exception)
        {
            lock (_gate) _challenge?.TrySetException(exception);
        }

        public void ForgetChallenge()
        {
            lock (_gate) _challenge = null;
        }

        public void Complete(string detail, DateTime completedAt) => SetTerminal("completed", detail, completedAt);
        public void Fail(string detail, DateTime completedAt) => SetTerminal("failed", detail, completedAt);
        public void Cancel() => Cancellation.Cancel();

        private void SetTerminal(string state, string detail, DateTime completedAt)
        {
            lock (_gate)
            {
                _state = state;
                _detail = detail;
                _completedAt = completedAt;
            }
        }

        public CodexSignInStatusResponse Snapshot()
        {
            lock (_gate)
            {
                return new CodexSignInStatusResponse(
                    Handle, Host, "codex", _state, _detail, ExpiresAt, _completedAt);
            }
        }
    }

    private sealed partial class ChallengeCapture
    {
        private readonly object _gate = new();
        private string? _url;
        private string? _code;
        private bool _delivered;

        public bool Add(string line, out string? url, out string? code)
        {
            lock (_gate)
            {
                if (_delivered)
                {
                    url = null;
                    code = null;
                    return false;
                }
                var plain = AnsiPattern().Replace(line, "");
                var urlMatch = UrlPattern().Match(plain);
                if (urlMatch.Success && IsOfficialVerificationUrl(urlMatch.Value)) _url = urlMatch.Value.TrimEnd('.', ',', ')');
                var codeMatch = CodePattern().Match(plain);
                if (codeMatch.Success) _code = codeMatch.Value;
                url = _url;
                code = _code;
                _delivered = _url is not null && _code is not null;
                if (_delivered)
                {
                    _url = null;
                    _code = null;
                }
                return _delivered;
            }
        }

        private static bool IsOfficialVerificationUrl(string value)
            => Uri.TryCreate(value.TrimEnd('.', ',', ')'), UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && (uri.Host.Equals("openai.com", StringComparison.OrdinalIgnoreCase)
                   || uri.Host.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase)
                   || uri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase)
                   || uri.Host.EndsWith(".chatgpt.com", StringComparison.OrdinalIgnoreCase));

        [GeneratedRegex(@"https://[^\s<>]+", RegexOptions.IgnoreCase)]
        private static partial Regex UrlPattern();

        [GeneratedRegex(@"\b[A-Z0-9]{4}(?:-[A-Z0-9]{4})+\b", RegexOptions.IgnoreCase)]
        private static partial Regex CodePattern();

        [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])")]
        private static partial Regex AnsiPattern();
    }
}

/// <summary>
/// Runs the fixed Codex device-auth command as the systemd runner user over
/// SSH. The remote script confirms login status and restarts active runner
/// units so their startup capability advertisement carries the new state.
/// </summary>
public sealed class SshCodexDeviceAuthTransport : ICodexDeviceAuthTransport
{
    public async Task<CodexDeviceAuthTransportResult> RunAsync(
        string sshTarget,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        var startInfo = BuildStartInfo(sshTarget);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new CodexSignInException("The SSH device-auth process could not be started.");

        try
        {
            await process.StandardInput.WriteAsync(RemoteScript.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            var stdout = ReadLinesAsync(process.StandardOutput, onOutput, cancellationToken);
            var stderr = ReadLinesAsync(process.StandardError, onOutput, cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
            throw new CodexSignInException($"Codex sign-in failed on the execution host (exit code {process.ExitCode}).");
        return new CodexDeviceAuthTransportResult(LoginStatusConfirmed: true);
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
        info.ArgumentList.Add("-tt");
        info.ArgumentList.Add(sshTarget);
        info.ArgumentList.Add("sudo");
        info.ArgumentList.Add("bash");
        info.ArgumentList.Add("-s");
        return info;
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> onOutput, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            onOutput(line);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            SilentCatch.Note(exception, "SshCodexDeviceAuthTransport: bounded SSH process cleanup");
        }
    }

    internal const string RemoteScript = """
set -euo pipefail
runner_user="$(systemctl show agent-host.service -p User --value 2>/dev/null || true)"
[[ -n "$runner_user" ]] || runner_user="$(systemctl show agent-runner-review.service -p User --value 2>/dev/null || true)"
[[ -n "$runner_user" ]] || runner_user='agent-runner'
runner_home="$(getent passwd "$runner_user" | cut -d: -f6)"
[[ -n "$runner_home" ]] || { echo '[codex-sign-in] Runner user has no home directory.' >&2; exit 41; }

child=''
cleanup() {
  if [[ -n "$child" ]]; then
    kill -TERM "$child" 2>/dev/null || true
    wait "$child" 2>/dev/null || true
  fi
}
trap cleanup EXIT HUP INT TERM

runuser -u "$runner_user" -- env HOME="$runner_home" PATH="$runner_home/.local/bin:$runner_home/.npm-global/bin:/usr/local/bin:/usr/bin:/bin" \
  timeout --signal=TERM --kill-after=5s 900s codex login --device-auth &
child="$!"
wait "$child"
child=''
runuser -u "$runner_user" -- env HOME="$runner_home" PATH="$runner_home/.local/bin:$runner_home/.npm-global/bin:/usr/local/bin:/usr/bin:/bin" \
  codex login status >/dev/null

for unit in agent-host.service agent-runner-review.service; do
  if systemctl is-active --quiet "$unit"; then
    systemctl restart "$unit"
  fi
done
echo '[codex-sign-in] login-status=confirmed probe-refresh=requested'
""";
}
