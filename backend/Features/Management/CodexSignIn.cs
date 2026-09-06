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
    string State,
    string VerificationUrl,
    string UserCode,
    DateTime StartedAt,
    DateTime ExpiresAt);

public sealed record CodexSignInStatusResponse(
    string Handle,
    string HostId,
    string State,
    string Detail,
    DateTime StartedAt,
    DateTime ExpiresAt,
    DateTime? CompletedAt,
    bool ProbeRefreshTriggered);

public sealed record CodexDeviceAuthTransportResult(
    int ExitCode,
    bool LoginStatusConfirmed,
    bool ProbeRefreshTriggered,
    string Detail);

public interface ICodexDeviceAuthTransport
{
    Task<CodexDeviceAuthTransportResult> RunAsync(
        string sshTarget,
        Action<string> observeOutput,
        CancellationToken cancellationToken);
}

public interface ICodexSignInService
{
    Task<CodexSignInStartResponse> StartAsync(
        string hostId,
        CodexSignInStartRequest request,
        string actor,
        CancellationToken cancellationToken);

    CodexSignInStatusResponse? Get(string hostId, string handle);
}

public static partial class CodexSignInPolicy
{
    public static string? Validate(string hostId, CodexSignInStartRequest request)
    {
        if (string.IsNullOrWhiteSpace(hostId) || !SafeIdentityPattern().IsMatch(hostId.Trim()))
            return "Host identity is required and may contain letters, numbers, dots, underscores, and hyphens.";
        if (string.IsNullOrWhiteSpace(request.SshTarget)
            || !SshTargetPattern().IsMatch(request.SshTarget.Trim()))
            return "SSH target must be a configured alias or user@host without shell characters.";
        return null;
    }

    public static (string? VerificationUrl, string? UserCode) ParseDeviceAuthTranscript(
        IEnumerable<string> lines)
    {
        string? url = null;
        string? code = null;
        var expectsCode = false;
        foreach (var raw in lines)
        {
            var line = StripAnsi(raw).Trim();
            var urlMatch = VerificationUrlPattern().Match(line);
            if (urlMatch.Success
                && Uri.TryCreate(urlMatch.Value.TrimEnd('.', ',', ';'), UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps)
            {
                url = uri.AbsoluteUri;
            }

            if (line.Contains("code", StringComparison.OrdinalIgnoreCase)) expectsCode = true;
            var inline = InlineCodePattern().Match(line);
            if (inline.Success) code = inline.Groups["code"].Value;
            else if (expectsCode && DeviceCodePattern().IsMatch(line)) code = line;
        }
        return (url, code);
    }

    private static string StripAnsi(string value) => AnsiPattern().Replace(value, string.Empty);

    [GeneratedRegex(@"^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex SshTargetPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex SafeIdentityPattern();

    [GeneratedRegex(@"https://[^\s<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex VerificationUrlPattern();

    [GeneratedRegex(@"(?i)code(?:\s*\([^)]*\))?\s*:?\s*(?<code>[A-Z0-9]{4,12}(?:-[A-Z0-9]{4,12})+)")]
    private static partial Regex InlineCodePattern();

    [GeneratedRegex(@"^[A-Z0-9]{4,12}(?:-[A-Z0-9]{4,12})+$")]
    private static partial Regex DeviceCodePattern();

    [GeneratedRegex("\\x1B(?:[@-_]|\\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex AnsiPattern();
}

public sealed class CodexSignInService : ICodexSignInService
{
    internal static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ICodexDeviceAuthTransport _transport;
    private readonly AgentMessageBusBridge _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<CodexSignInService> _logger;

    public CodexSignInService(
        ICodexDeviceAuthTransport transport,
        AgentMessageBusBridge bus,
        ILogger<CodexSignInService> logger,
        TimeProvider? time = null)
    {
        _transport = transport;
        _bus = bus;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<CodexSignInStartResponse> StartAsync(
        string hostId,
        CodexSignInStartRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var validation = CodexSignInPolicy.Validate(hostId, request);
        if (validation is not null) throw new ArgumentException(validation, nameof(request));

        var startedAt = _time.GetUtcNow().UtcDateTime;
        var session = new Session(
            Guid.CreateVersion7().ToString("N"),
            hostId.Trim(),
            request.SshTarget.Trim(),
            string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim(),
            startedAt,
            startedAt.Add(SessionLifetime));
        if (!_sessions.TryAdd(session.Handle, session))
            throw new InvalidOperationException("Could not allocate a Codex sign-in session.");

        var prompt = session.Prompt!;
        _ = RunSessionAsync(session);
        try
        {
            return await prompt.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (session.Gate)
            {
                if (ReferenceEquals(session.Prompt, prompt)) session.Prompt = null;
                session.Transcript.Clear();
            }
        }
    }

    public CodexSignInStatusResponse? Get(string hostId, string handle)
    {
        if (!_sessions.TryGetValue(handle, out var session)
            || !string.Equals(session.HostId, hostId, StringComparison.Ordinal)) return null;
        lock (session.Gate)
        {
            return new CodexSignInStatusResponse(
                session.Handle,
                session.HostId,
                session.State,
                session.Detail,
                session.StartedAt,
                session.ExpiresAt,
                session.CompletedAt,
                session.ProbeRefreshTriggered);
        }
    }

    private async Task RunSessionAsync(Session session)
    {
        using var timeout = new CancellationTokenSource(SessionLifetime);
        try
        {
            var result = await _transport.RunAsync(
                session.SshTarget,
                line => ObserveLine(session, line),
                timeout.Token).ConfigureAwait(false);
            if (result.ExitCode == 0 && result.LoginStatusConfirmed)
                Complete(session, "completed", result.Detail, result.ProbeRefreshTriggered);
            else
                Complete(session, "failed", result.Detail, result.ProbeRefreshTriggered);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Complete(session, "failed", "Codex sign-in timed out after 15 minutes.", false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Codex device sign-in failed for host {HostId}", session.HostId);
            Complete(session, "failed", SafeDetail(exception.Message), false);
        }
    }

    private void ObserveLine(Session session, string line)
    {
        lock (session.Gate)
        {
            var prompt = session.Prompt;
            if (session.State != "pending" || prompt is null || prompt.Task.IsCompleted) return;
            session.Transcript.Add(line);
            if (session.Transcript.Count > 80) session.Transcript.RemoveAt(0);
            var parsed = CodexSignInPolicy.ParseDeviceAuthTranscript(session.Transcript);
            if (parsed.VerificationUrl is null || parsed.UserCode is null)
                return;
            session.Transcript.Clear();
            prompt.TrySetResult(new CodexSignInStartResponse(
                session.Handle,
                session.HostId,
                session.State,
                parsed.VerificationUrl,
                parsed.UserCode,
                session.StartedAt,
                session.ExpiresAt));
        }
    }

    private void Complete(Session session, string state, string detail, bool probeRefreshTriggered)
    {
        var completedAt = _time.GetUtcNow().UtcDateTime;
        lock (session.Gate)
        {
            if (session.State != "pending") return;
            session.State = state;
            session.Detail = SafeDetail(detail);
            session.ProbeRefreshTriggered = probeRefreshTriggered;
            session.CompletedAt = completedAt;
            if (session.Prompt is { Task.IsCompleted: false } prompt)
                prompt.TrySetException(new CodexSignInException(session.Detail));
            session.Transcript.Clear();
        }

        _ = _bus.EmitProviderSignInAsync(
            session.HostId,
            "codex",
            session.Actor,
            state);
        _ = RemoveAfterRetentionAsync(session.Handle);
    }

    private async Task RemoveAfterRetentionAsync(string handle)
    {
        await Task.Delay(SessionLifetime).ConfigureAwait(false);
        _sessions.TryRemove(handle, out _);
    }

    private static string SafeDetail(string? value)
    {
        var text = string.Join(' ', (value ?? "Codex sign-in failed.")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length <= 500 ? text : text[..500] + "...";
    }

    private sealed class Session(
        string handle,
        string hostId,
        string sshTarget,
        string actor,
        DateTime startedAt,
        DateTime expiresAt)
    {
        public object Gate { get; } = new();
        public string Handle { get; } = handle;
        public string HostId { get; } = hostId;
        public string SshTarget { get; } = sshTarget;
        public string Actor { get; } = actor;
        public DateTime StartedAt { get; } = startedAt;
        public DateTime ExpiresAt { get; } = expiresAt;
        public List<string> Transcript { get; } = [];
        public TaskCompletionSource<CodexSignInStartResponse>? Prompt { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string State { get; set; } = "pending";
        public string Detail { get; set; } = "Waiting for browser confirmation.";
        public DateTime? CompletedAt { get; set; }
        public bool ProbeRefreshTriggered { get; set; }
    }
}

public sealed class CodexSignInException(string message) : Exception(message);

public sealed class SshCodexDeviceAuthTransport : ICodexDeviceAuthTransport
{
    public async Task<CodexDeviceAuthTransportResult> RunAsync(
        string sshTarget,
        Action<string> observeOutput,
        CancellationToken cancellationToken)
    {
        var startInfo = BuildStartInfo(sshTarget);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start()) throw new CodexSignInException("The SSH sign-in process could not be started.");

        using var registration = cancellationToken.Register(() => TryKill(process));
        var stdout = PumpAsync(process.StandardOutput, observeOutput, cancellationToken);
        var stderr = PumpAsync(process.StandardError, observeOutput, cancellationToken);
        await process.StandardInput.WriteAsync(RemoteScript.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();
        await process.WaitForExitAsync(cancellationToken);
        var lines = (await stdout).Concat(await stderr).ToArray();
        var confirmed = lines.Contains("codex-login-status=ok", StringComparer.Ordinal);
        var probeTriggered = lines.Contains("codex-probe-refresh=triggered", StringComparer.Ordinal);
        var detail = process.ExitCode == 0 && confirmed
            ? probeTriggered
                ? "Codex sign-in completed and runner units were restarted for a fresh provider probe."
                : "Codex sign-in completed. No active runner unit was available to restart; the next probe will publish the result."
            : FailureDetail(process.ExitCode, lines);
        return new CodexDeviceAuthTransportResult(process.ExitCode, confirmed, probeTriggered, detail);
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
        foreach (var argument in new[] { "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-T", sshTarget, "sudo", "bash", "-s" })
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static async Task<IReadOnlyList<string>> PumpAsync(
        StreamReader reader,
        Action<string> observeOutput,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            observeOutput(line);
            if (lines.Count < 120 && IsSafeControlLine(line)) lines.Add(line);
        }
        return lines;
    }

    private static bool IsSafeControlLine(string line) =>
        line.StartsWith("[codex-sign-in]", StringComparison.Ordinal)
        || line is "codex-login-status=ok"
        || line.StartsWith("codex-probe-refresh=", StringComparison.Ordinal);

    private static string FailureDetail(int exitCode, IEnumerable<string> lines)
    {
        var safe = lines
            .Where(line => line.StartsWith("[codex-sign-in]", StringComparison.Ordinal))
            .Select(line => line["[codex-sign-in]".Length..].Trim())
            .LastOrDefault();
        return string.IsNullOrWhiteSpace(safe)
            ? $"Codex sign-in failed with remote exit code {exitCode}."
            : safe;
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

    private const string RemoteScript = """
set -euo pipefail
runner_user=''
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
  configured_user="$(systemctl show --property=User --value "$unit")"
  if [[ -n "$configured_user" ]]; then runner_user="$configured_user"; break; fi
done
if [[ -z "$runner_user" ]]; then runner_user='agent'; fi
if ! id "$runner_user" >/dev/null 2>&1; then
  echo '[codex-sign-in] The configured runner user does not exist.' >&2
  exit 41
fi
runner_home="$(getent passwd "$runner_user" | cut -d: -f6)"
if [[ -z "$runner_home" || ! -d "$runner_home" ]]; then
  echo '[codex-sign-in] The configured runner home directory is unavailable.' >&2
  exit 43
fi

child_pid=''
cleanup() {
  if [[ -n "$child_pid" ]] && kill -0 "$child_pid" >/dev/null 2>&1; then
    kill "$child_pid" >/dev/null 2>&1 || true
    wait "$child_pid" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT
trap 'exit 143' HUP INT TERM
runuser -u "$runner_user" -- env HOME="$runner_home" codex login --device-auth &
child_pid=$!
wait "$child_pid"
child_pid=''
if ! runuser -u "$runner_user" -- env HOME="$runner_home" codex login status >/dev/null 2>&1; then
  echo '[codex-sign-in] codex login status did not confirm authentication.' >&2
  exit 42
fi
echo 'codex-login-status=ok'

restarted=0
for unit in "${units[@]}"; do
  if systemctl is-active --quiet "$unit"; then
    systemctl restart "$unit"
    restarted=$((restarted + 1))
  fi
done
if ((restarted > 0)); then
  echo 'codex-probe-refresh=triggered'
else
  echo 'codex-probe-refresh=next-cadence'
fi
""";
}
