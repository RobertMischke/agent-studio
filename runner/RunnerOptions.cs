namespace AgentRunner;

/// <summary>
/// Resolved runner configuration for a single remote task run. Every value comes
/// from an environment variable (systemd-friendly) with a small set of required
/// command-line overrides for the per-task identifiers. See
/// docs/operations/setup/linux-runner-host.md for the operator-facing table.
/// </summary>
public sealed class RunnerOptions
{
    /// <summary>Central Task Server base URL, e.g. http://127.0.0.1:5030 or the reverse-proxied central URL.</summary>
    public required string ServerUrl { get; init; }

    /// <summary>Stable per-runner identity used as the lease owner (fencing is per task, not per pid).</summary>
    public required string RunnerId { get; init; }

    /// <summary>Human-facing runner name shown on the board (the project the board assigns, e.g. agent-runner-01).</summary>
    public required string RunnerName { get; init; }

    /// <summary>
    /// Optional pre-registered Task Server client identity. When set, startup
    /// verifies this exact identity instead of silently registering another id
    /// from <see cref="RunnerName"/>. This is the systemd-friendly onboarding
    /// path for hosts whose X-Client-Id was created before the runner install.
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>Reported hostname; defaults to the machine name.</summary>
    public required string Hostname { get; init; }

    /// <summary>Free-form label describing which backend/topology this runner serves.</summary>
    public required string BackendName { get; init; }

    /// <summary>Optional bearer token sent as Authorization on every request (Phase 2 auth).</summary>
    public string? AuthToken { get; init; }

    /// <summary>Git remote the code arrives from. Code is read-only here; results leave via the API.</summary>
    public required string GitRemote { get; init; }

    /// <summary>Directory the runner checks the repo out into on the runner host.</summary>
    public required string WorkDir { get; init; }

    /// <summary>Branch to check out for the run. When empty, the runner stays on <see cref="BaseBranch"/>.</summary>
    public string? Branch { get; init; }

    /// <summary>Fallback branch when the task branch is absent or unspecified.</summary>
    public required string BaseBranch { get; init; }

    /// <summary>Agent CLI binary to spawn (claude, codex, ...).</summary>
    public required string CliBin { get; init; }

    /// <summary>Extra CLI arguments inserted before the prompt is streamed on stdin (space-split, shell-unaware).</summary>
    public required string CliArgs { get; init; }

    /// <summary>Lease TTL requested on acquire/renew; the server clamps to its own bounds.</summary>
    public int TtlSeconds { get; init; }

    /// <summary>Heartbeat cadence; kept well under the TTL so a slow network still renews in time.</summary>
    public int HeartbeatSeconds { get; init; }

    /// <summary>Hard cap on a single CLI run before the runner gives up and reports a blocked completion.</summary>
    public int RunTimeoutSeconds { get; init; }

    /// <summary>Maximum number of concurrently running task slots on this host.</summary>
    public int HostMaxParallelism { get; init; }

    /// <summary>Delay between empty daemon pickup polls.</summary>
    public int PollSeconds { get; init; }

    /// <summary>
    /// When set (<c>--health-check</c>), the runner only probes the Task Server's
    /// liveness and exits: 0 when the server is reachable, 4 when it is not. No task
    /// key is required. This is the readiness probe the reverse-tunnel service uses
    /// to confirm the connection before assigning work.
    /// </summary>
    public bool HealthCheckOnly { get; init; }

    public static string Env(string name, string fallback = "")
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    public static int EnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    /// <summary>
    /// Build options from environment defaults, then apply <c>--key value</c> and
    /// <c>--flag</c> command-line overrides. The single positional argument (if any)
    /// is the task key. Returns the parsed task key alongside the options.
    /// </summary>
    public static (RunnerOptions Options, string? TaskKey, bool Once, bool Help) Parse(string[] args)
    {
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? positional = null;
        var once = true;
        var help = false;
        var healthCheck = false;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "-h" or "--help") { help = true; continue; }
            if (a == "--once") { once = true; continue; }
            if (a == "--poll") { once = false; continue; }
            if (a == "--health-check") { healthCheck = true; continue; }
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var key = a[2..];
                var value = i + 1 < args.Length ? args[++i] : "";
                overrides[key] = value;
                continue;
            }
            positional ??= a;
        }

        string Val(string cliKey, string envName, string fallback = "")
            => overrides.TryGetValue(cliKey, out var v) && v.Length > 0 ? v : Env(envName, fallback);

        var options = new RunnerOptions
        {
            ServerUrl = Val("server", "RUNNER_SERVER_URL", "http://127.0.0.1:5030").TrimEnd('/'),
            RunnerId = Val("runner-id", "RUNNER_ID", $"agent-runner-{Environment.MachineName}".ToLowerInvariant()),
            RunnerName = Val("runner-name", "RUNNER_NAME", "agent-runner-01"),
            ClientId = Val("client-id", "RUNNER_CLIENT_ID").Trim() is { Length: > 0 } clientId ? clientId : null,
            Hostname = Val("hostname", "RUNNER_HOSTNAME", Environment.MachineName),
            BackendName = Val("backend-name", "RUNNER_BACKEND_NAME", "remote-runner"),
            AuthToken = Val("auth-token", "RUNNER_AUTH_TOKEN") is { Length: > 0 } t ? t : null,
            GitRemote = Val("git-remote", "RUNNER_GIT_REMOTE"),
            WorkDir = Val("workdir", "RUNNER_WORKDIR", Path.Combine(Path.GetTempPath(), "agent-runner-work")),
            Branch = Val("branch", "RUNNER_BRANCH") is { Length: > 0 } b ? b : null,
            BaseBranch = Val("base-branch", "RUNNER_BASE_BRANCH", "main"),
            CliBin = Val("cli", "RUNNER_CLI_BIN", "claude"),
            CliArgs = Val("cli-args", "RUNNER_CLI_ARGS", "-p"),
            TtlSeconds = overrides.TryGetValue("ttl", out var ttl) && int.TryParse(ttl, out var ttlV) ? ttlV : EnvInt("RUNNER_TTL_SECONDS", 120),
            HeartbeatSeconds = EnvInt("RUNNER_HEARTBEAT_SECONDS", 30),
            RunTimeoutSeconds = EnvInt("RUNNER_RUN_TIMEOUT_SECONDS", 3600),
            HostMaxParallelism = overrides.TryGetValue("max-parallelism", out var max) && int.TryParse(max, out var maxV) && maxV > 0
                ? maxV : EnvInt("RUNNER_MAX_PARALLELISM", 2),
            PollSeconds = overrides.TryGetValue("poll-seconds", out var poll) && int.TryParse(poll, out var pollV) && pollV > 0
                ? pollV : EnvInt("RUNNER_POLL_SECONDS", 5),
            HealthCheckOnly = healthCheck,
        };

        var taskKey = positional ?? (overrides.TryGetValue("task", out var tk) ? tk : null);
        return (options, string.IsNullOrWhiteSpace(taskKey) ? null : taskKey.Trim(), once, help);
    }
}
