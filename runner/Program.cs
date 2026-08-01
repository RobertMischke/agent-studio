using AgentRunner;
using System.Runtime.InteropServices;
using System.Reflection;

// Standalone agent host. With a task key it performs the RM-5 one-shot run;
// without one (or with --poll) it continuously fills bounded host slots. See
// docs/operations/setup/linux-runner-host.md.

if (args is ["--version"])
{
    var assembly = typeof(RunnerOptions).Assembly;
    var informational = assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion
        ?? assembly.GetName().Version?.ToString(3)
        ?? "unknown";
    Console.WriteLine($"agent-host {informational}");
    return 0;
}

if (args is ["--detached-worker", var detachedSpec])
    return await DurableAgentProcess.RunWorkerAsync(detachedSpec);

var (options, taskKey, once, help) = RunnerOptions.Parse(args);

if (help)
{
    PrintUsage();
    return 0;
}

void Log(string message) => Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] [agent-host] {message}");

var daemonMode = taskKey is null || !once;
Log($"agent-host starting: server={options.ServerUrl} role={options.Role} mode={(daemonMode ? "daemon" : "one-shot")} task={taskKey ?? "(assigned projects)"}");
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Log(daemonMode
        ? "shutdown requested (Ctrl+C); draining daemon without stopping detached jobs..."
        : "shutdown requested (Ctrl+C); cancelling one-shot run...");
    shutdown.Cancel();
};
using var sigterm = !OperatingSystem.IsWindows()
    ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        Log(daemonMode
            ? "planned shutdown requested (SIGTERM); stopping claims and flushing durable slot state..."
            : "shutdown requested (SIGTERM); cancelling one-shot run...");
        shutdown.Cancel();
    })
    : null;

using var client = new TaskServerClient(options);

// Readiness probe (--health-check): confirm the Task Server is reachable over the
// tunnel and exit, without touching a task. This is the check the reverse-tunnel
// service runs so a down connection is reported once and cleanly (exit 4) instead
// of cascading through a launch attempt. No task key is required.
if (options.HealthCheckOnly)
{
    var health = await client.ProbeHealthAsync(shutdown.Token);
    if (health is null)
    {
        Log($"health-check ok: task server reachable at {options.ServerUrl}");
        return 0;
    }
    Log($"health-check failed: cannot reach the task server at {options.ServerUrl} ({health}). " +
        "The reverse tunnel / autossh service is likely down.");
    return 4;
}


try
{
    await client.EnsureCompatibleAsync(shutdown.Token);
    if (options.Role == "review")
    {
        if (!daemonMode)
            throw new ArgumentException("Remote Review Executor runs as a polling service and does not accept coding task keys.");
        await new RemoteReviewDaemon(options, client, Log).RunAsync(shutdown.Token);
        Log("review daemon stopped");
        return 0;
    }
    if (daemonMode)
    {
        await new RemoteRunnerDaemon(options, client, Log).RunAsync(shutdown.Token);
        Log("daemon stopped");
        return 0;
    }

    var exitCode = await new RemoteTaskRunner(options, client, Log).RunAsync(taskKey!, shutdown.Token);
    Log($"done, exit code {exitCode}");
    return exitCode;
}
catch (OperationCanceledException)
{
    Log("cancelled");
    return 130; // 128 + SIGINT
}
catch (TaskServerException ex)
{
    Log($"task server error: {ex.Message}");
    return 4;
}
catch (HttpRequestException ex)
{
    Log($"could not reach the task server at {options.ServerUrl}: {ex.Message}");
    return 4;
}
catch (Exception ex)
{
    Log($"unhandled error: {ex}");
    return 1;
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        agent-host - standalone remote task host (RM-5)

        Usage:
          agent-host <TASK-KEY> [options]
          agent-host --task <TASK-KEY> [options]
          agent-host --poll [options]
          agent-host --health-check [--server <url>]

        Most configuration comes from environment variables (see the runbook,
        docs/operations/setup/linux-runner-host.md). Command-line flags override:

          --health-check          Probe the Task Server and exit (0 reachable,
                                  4 not). Readiness check for the tunnel service.
          --version               Print release version and Git SHA, then exit.
          --server <url>          Task Server base URL       (RUNNER_SERVER_URL)
          --runner-id <id>        Stable runner identity     (RUNNER_ID)
          --runner-name <name>    Board-facing runner name   (RUNNER_NAME)
          --role <coding|review>  Separate service role      (RUNNER_ROLE)
          --client-id <id>        Attribution label only     (RUNNER_CLIENT_ID)
          --git-remote <url>      Startup probe/one-shot URL  (RUNNER_GIT_REMOTE)
          --git-push-remote <url> Probe/one-shot push URL     (RUNNER_GIT_PUSH_REMOTE)
          --branch <name>         Branch to check out         (RUNNER_BRANCH)
          --base-branch <name>    Fallback branch             (RUNNER_BASE_BRANCH)
          --workdir <path>        Checkout + results dir      (RUNNER_WORKDIR)
          --review-workdir <path> Disposable review root      (RUNNER_REVIEW_WORKDIR)
          --state-dir <path>      Durable slot/process state  (RUNNER_STATE_DIR)
          RUNNER_CLAIM_MAX_LOAD_PER_CORE                      Load/core threshold (default 1.5)
          RUNNER_LOAD_GATE_SUSTAINED_SECONDS                  High-load window (default 120)
          --cli <bin>             Agent CLI binary            (RUNNER_CLI_BIN)
          --cli-args "<args>"     Headless CLI args           (RUNNER_CLI_ARGS)
          --auth-token-file <p>   Protected credential file  (RUNNER_AUTH_TOKEN_FILE)
          --tls-certificate-sha256 <hex>
                                  Private-CA/rehearsal certificate pin
                                                            (RUNNER_TLS_CERTIFICATE_SHA256)
          --ttl <seconds>         Requested lease TTL         (RUNNER_TTL_SECONDS)
          --max-parallelism <n>   Bootstrap/fallback host slots (RUNNER_MAX_PARALLELISM, default 2)
          --poll-seconds <n>      Empty-queue poll delay       (RUNNER_POLL_SECONDS, default 5)
          --poll                  Run continuously (also the default without a task key)
          -h, --help              Show this help

        RUNNER_* remains the bootstrap-compatible environment prefix and takes
        precedence. Every setting also accepts its matching AGENT_HOST_* alias.
        """);
}
