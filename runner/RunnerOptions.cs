namespace AgentRunner;

/// <summary>
/// Resolved runner configuration for a single remote task run. Every value comes
/// from an environment variable (systemd-friendly) with a small set of required
/// command-line overrides for the per-task identifiers. See
/// docs/operations/setup/linux-runner-host.md for the operator-facing table.
/// </summary>
public sealed class RunnerOptions
{
    public const int ProtocolVersion = AgentStudio.TaskServer.Contracts.TaskServerProtocol.Current;

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

    /// <summary>
    /// Service role. Coding and review use different registered identities,
    /// daemon loops, workspace roots, credentials, and server-side claims.
    /// </summary>
    public string Role { get; init; } = "coding";

    /// <summary>Runner service credential sent as Authorization on every networked-profile request.</summary>
    public string? AuthToken { get; init; }

    /// <summary>Fallback git remote for one-shot runs and claims from older servers.</summary>
    public string? GitRemote { get; init; }

    /// <summary>Optional write-only URL installed as origin.pushurl while fetches keep using <see cref="GitRemote"/>.</summary>
    public string? GitPushRemote { get; init; }

    /// <summary>Directory the runner checks the repo out into on the runner host.</summary>
    public required string WorkDir { get; init; }

    /// <summary>Disposable workspace root used only by the Remote Review Executor.</summary>
    public string ReviewWorkDir { get; init; } = Path.Combine(Path.GetTempPath(), "agent-review-work");

    /// <summary>
    /// Comma-separated environment variable names admitted into review child
    /// processes. The review service account must provision them as read-only.
    /// </summary>
    public IReadOnlyList<string> ReviewCredentialEnvironment { get; init; } = [];

    /// <summary>Durable daemon slot records and detached-worker logs.</summary>
    public string StateDir { get; init; } = Path.Combine(Path.GetTempPath(), "agent-runner-state");

    /// <summary>Branch to check out for the run. When empty, the runner stays on <see cref="BaseBranch"/>.</summary>
    public string? Branch { get; init; }

    /// <summary>Fallback branch when the task branch is absent or unspecified.</summary>
    public required string BaseBranch { get; init; }

    /// <summary>Agent CLI binary to spawn (claude, codex, ...).</summary>
    public required string CliBin { get; init; }

    /// <summary>Codex binary used by the GPT-only project chat work path.</summary>
    public string CodexCliBin { get; init; } = "codex";

    /// <summary>Extra CLI arguments inserted before the prompt is streamed on stdin (space-split, shell-unaware).</summary>
    public required string CliArgs { get; init; }

    /// <summary>
    /// Optional provider-specific arguments for resuming a captured session.
    /// The value must contain <c>{sessionId}</c>. When absent, the provider is
    /// treated as not supporting same-session recovery on this host.
    /// </summary>
    public string? CliResumeArgs { get; init; }

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

        if (overrides.ContainsKey("auth-token"))
            throw new ArgumentException("Runner credentials are not accepted on the command line. Use RUNNER_AUTH_TOKEN_FILE or RUNNER_AUTH_TOKEN.");
        var authTokenFile = Val("auth-token-file", "RUNNER_AUTH_TOKEN_FILE").Trim();
        var directAuthToken = Env("RUNNER_AUTH_TOKEN").Trim();
        if (authTokenFile.Length > 0 && directAuthToken.Length > 0)
            throw new ArgumentException("Configure only one of RUNNER_AUTH_TOKEN_FILE or RUNNER_AUTH_TOKEN.");
        var authToken = authTokenFile.Length > 0 ? ReadAuthTokenFile(authTokenFile) : directAuthToken;

        var options = new RunnerOptions
        {
            ServerUrl = Val("server", "RUNNER_SERVER_URL", "http://127.0.0.1:5030").TrimEnd('/'),
            RunnerId = Val("runner-id", "RUNNER_ID", $"agent-runner-{Environment.MachineName}".ToLowerInvariant()),
            RunnerName = Val("runner-name", "RUNNER_NAME", "agent-runner-01"),
            ClientId = Val("client-id", "RUNNER_CLIENT_ID").Trim() is { Length: > 0 } clientId ? clientId : null,
            Hostname = Val("hostname", "RUNNER_HOSTNAME", Environment.MachineName),
            BackendName = Val("backend-name", "RUNNER_BACKEND_NAME", "remote-runner"),
            Role = Val("role", "RUNNER_ROLE", "coding").Trim().ToLowerInvariant(),
            AuthToken = authToken.Length > 0 ? authToken : null,
            GitRemote = Val("git-remote", "RUNNER_GIT_REMOTE").Trim() is { Length: > 0 } gitRemote ? gitRemote : null,
            GitPushRemote = Val("git-push-remote", "RUNNER_GIT_PUSH_REMOTE").Trim() is { Length: > 0 } gitPushRemote ? gitPushRemote : null,
            WorkDir = Val("workdir", "RUNNER_WORKDIR", Path.Combine(Path.GetTempPath(), "agent-runner-work")),
            ReviewWorkDir = Val(
                "review-workdir",
                "RUNNER_REVIEW_WORKDIR",
                Path.Combine(Path.GetTempPath(), "agent-review-work")),
            ReviewCredentialEnvironment = Val(
                    "review-credential-env",
                "RUNNER_REVIEW_CREDENTIAL_ENV")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StateDir = Val("state-dir", "RUNNER_STATE_DIR",
                Path.Combine(Val("workdir", "RUNNER_WORKDIR", Path.Combine(Path.GetTempPath(), "agent-runner-work")), ".runner-state")),
            Branch = Val("branch", "RUNNER_BRANCH") is { Length: > 0 } b ? b : null,
            BaseBranch = Val("base-branch", "RUNNER_BASE_BRANCH", "main"),
            CliBin = Val("cli", "RUNNER_CLI_BIN", "claude"),
            CodexCliBin = Val("codex-cli", "RUNNER_CODEX_CLI_BIN", "codex"),
            CliArgs = Val("cli-args", "RUNNER_CLI_ARGS", "-p"),
            CliResumeArgs = Val("cli-resume-args", "RUNNER_CLI_RESUME_ARGS").Trim() is { Length: > 0 } resumeArgs
                ? resumeArgs
                : null,
            TtlSeconds = overrides.TryGetValue("ttl", out var ttl) && int.TryParse(ttl, out var ttlV) ? ttlV : EnvInt("RUNNER_TTL_SECONDS", 120),
            HeartbeatSeconds = EnvInt("RUNNER_HEARTBEAT_SECONDS", 30),
            RunTimeoutSeconds = EnvInt("RUNNER_RUN_TIMEOUT_SECONDS", 3600),
            HostMaxParallelism = overrides.TryGetValue("max-parallelism", out var max) && int.TryParse(max, out var maxV) && maxV > 0
                ? maxV : EnvInt("RUNNER_MAX_PARALLELISM", 2),
            PollSeconds = overrides.TryGetValue("poll-seconds", out var poll) && int.TryParse(poll, out var pollV) && pollV > 0
                ? pollV : EnvInt("RUNNER_POLL_SECONDS", 5),
            HealthCheckOnly = healthCheck,
        };

        var serverUri = new Uri(options.ServerUrl, UriKind.Absolute);
        if (serverUri.Scheme != Uri.UriSchemeHttps && !serverUri.IsLoopback)
            throw new ArgumentException("RUNNER_SERVER_URL must use HTTPS unless it is a loopback address.");
        if (!serverUri.IsLoopback && string.IsNullOrWhiteSpace(options.AuthToken))
            throw new ArgumentException("RUNNER_AUTH_TOKEN is required for a non-loopback Task Server.");
        if (options.Role is not ("coding" or "review"))
            throw new ArgumentException("RUNNER_ROLE must be 'coding' or 'review'.");
        if (options.Role == "review"
            && string.Equals(
                Path.GetFullPath(options.WorkDir),
                Path.GetFullPath(options.ReviewWorkDir),
                StringComparison.Ordinal))
            throw new ArgumentException("Review and coding workspace roots must be different.");
        if (options.CliResumeArgs is not null
            && !options.CliResumeArgs.Contains("{sessionId}", StringComparison.Ordinal))
            throw new ArgumentException("RUNNER_CLI_RESUME_ARGS must contain the {sessionId} placeholder.");

        var taskKey = positional ?? (overrides.TryGetValue("task", out var tk) ? tk : null);
        return (options, string.IsNullOrWhiteSpace(taskKey) ? null : taskKey.Trim(), once, help);
    }

    private static string ReadAuthTokenFile(string path)
    {
        if (!File.Exists(path)) throw new ArgumentException($"RUNNER_AUTH_TOKEN_FILE does not exist: {path}");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new ArgumentException("RUNNER_AUTH_TOKEN_FILE must not be accessible to other users.");
        }
        var token = File.ReadAllText(path).Trim();
        if (token.Length == 0) throw new ArgumentException("RUNNER_AUTH_TOKEN_FILE is empty.");
        return token;
    }
}
