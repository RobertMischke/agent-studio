using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Runner;

/// <summary>
/// Identity a runner presents when it leases a task from the Task Server
/// (parallel-task-execution.md §8.2C "per-runner identities"; ADR-0060). Today
/// the local orchestrator and its in-process runner share one machine, but the
/// runner split (ADR-0059) needs a stable <see cref="RunnerId"/> so a lease,
/// heartbeat, or stale-write rejection can be attributed to a specific runner —
/// including after a crash handoff to a different host.
///
/// <para>
/// <b>Token precursor (<c>Token-Vorstufe</c>).</b> <see cref="Token"/> is a
/// deterministic, opaque credential derived from the runner id (plus an optional
/// operator secret) — <b>not</b> yet a real, issued, least-privilege auth token.
/// It gives the wire contract and the audit trail a stable "who" field now, so
/// promoting it to a real token later is a swap of the derivation, not a change
/// to the lease contract. It is intentionally not a security boundary yet: the
/// §8.2C split-brain guard is the monotonic <c>fencingToken</c> on the lease, not
/// this credential.
/// </para>
/// </summary>
public sealed record RunnerIdentity(
    string RunnerId,
    string RunnerName,
    string Hostname,
    string BackendName,
    string Token,
    string ProtocolVersion)
{
    /// <summary>
    /// Lease protocol version this runner advertises, so the server can apply the
    /// §8.2C "minimum-version checks before lease acquisition" gate. Bump when the
    /// lease/heartbeat contract changes in a breaking way.
    /// </summary>
    public const string CurrentProtocolVersion = "1";

    /// <summary>Scheme marker for the pre-issuance token stage.</summary>
    public const string TokenPrefix = "rt_pre_";

    /// <summary>
    /// Resolves this backend's runner identity from configuration, falling back
    /// to host/process facts. The backend-name convention mirrors the pickup-lock
    /// owner (<c>Runner:BackendName</c>, else <c>dev</c>/<c>stable</c> from
    /// <c>Environment:IsDev</c>) so the lease owner and the <c>.pickup-lock.json</c>
    /// owner name the same runner during the staged cutover.
    /// </summary>
    public static RunnerIdentity Resolve(IConfiguration? config, string? hostname = null)
    {
        var host = Blank(hostname) ? Environment.MachineName : hostname!.Trim();
        var backend = ResolveBackendName(config);
        var runnerId = Blank(config?["Runner:Id"])
            ? NormalizeId($"{backend}@{host}")
            : NormalizeId(config!["Runner:Id"]);
        var name = Blank(config?["Runner:Name"]) ? runnerId : config!["Runner:Name"]!.Trim();
        var token = DeriveToken(runnerId, config?["Runner:TokenSecret"]);
        return new RunnerIdentity(runnerId, name, host, backend, token, CurrentProtocolVersion);
    }

    /// <summary>
    /// Derives the opaque token precursor. Deterministic in
    /// <paramref name="runnerId"/> (+ optional <paramref name="secret"/>) so a
    /// runner presents the same credential across restarts and tests can assert on
    /// it, while never exposing the secret or the raw id in the token body.
    /// </summary>
    public static string DeriveToken(string runnerId, string? secret)
    {
        var material = $"{NormalizeId(runnerId)}\n{secret ?? string.Empty}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return TokenPrefix + Base64Url(digest)[..24];
    }

    /// <summary>
    /// Backend-name convention shared with <c>TaskRunnerService.ResolveBackendName</c>:
    /// explicit <c>Runner:BackendName</c> wins; otherwise dev/stable is inferred from
    /// <c>Environment:IsDev</c> so the two checkouts produce distinct runner identities.
    /// </summary>
    private static string ResolveBackendName(IConfiguration? cfg)
    {
        var explicitName = cfg?["Runner:BackendName"];
        if (!Blank(explicitName)) return explicitName!.Trim();
        var isDev = cfg?.GetValue<bool>("Environment:IsDev") ?? false;
        return isDev ? "dev" : "stable";
    }

    private static string NormalizeId(string? raw)
        => Blank(raw) ? "runner" : raw!.Trim().ToLowerInvariant();

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}
