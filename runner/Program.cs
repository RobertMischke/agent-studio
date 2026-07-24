using AgentRunner;

// Standalone remote runner. With a task key it performs the RM-5 one-shot run;
// without one (or with --poll) it continuously fills bounded host slots. See
// docs/operations/setup/linux-runner-host.md.

var (options, taskKey, once, help) = RunnerOptions.Parse(args);

if (help)
{
    PrintUsage();
    return 0;
}

void Log(string message) => Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] [runner] {message}");

var daemonMode = taskKey is null || !once;
Log($"agent-runner starting: server={options.ServerUrl} role={options.Role} mode={(daemonMode ? "daemon" : "one-shot")} task={taskKey ?? "(assigned projects)"}");
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // let the finally-blocks release the lease cleanly
    Log("shutdown requested (Ctrl+C); finishing teardown...");
    shutdown.Cancel();
};

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
        agent-runner - standalone remote task runner (RM-5)

        Usage:
          agent-runner <TASK-KEY> [options]
          agent-runner --task <TASK-KEY> [options]
          agent-runner --poll [options]
          agent-runner --health-check [--server <url>]

        Most configuration comes from environment variables (see the runbook,
        docs/operations/setup/linux-runner-host.md). Command-line flags override:

          --health-check          Probe the Task Server and exit (0 reachable,
                                  4 not). Readiness check for the tunnel service.
          --server <url>          Task Server base URL       (RUNNER_SERVER_URL)
          --runner-id <id>        Stable runner identity     (RUNNER_ID)
          --runner-name <name>    Board-facing runner name   (RUNNER_NAME)
          --role <coding|review>  Separate service role      (RUNNER_ROLE)
          --client-id <id>        Attribution label only     (RUNNER_CLIENT_ID)
          --git-remote <url>      Origin the code arrives on  (RUNNER_GIT_REMOTE)
          --git-push-remote <url> Write-only origin pushurl   (RUNNER_GIT_PUSH_REMOTE)
          --branch <name>         Branch to check out         (RUNNER_BRANCH)
          --base-branch <name>    Fallback branch             (RUNNER_BASE_BRANCH)
          --workdir <path>        Checkout + results dir      (RUNNER_WORKDIR)
          --review-workdir <path> Disposable review root      (RUNNER_REVIEW_WORKDIR)
          --cli <bin>             Agent CLI binary            (RUNNER_CLI_BIN)
          --cli-args "<args>"     Headless CLI args           (RUNNER_CLI_ARGS)
          --auth-token-file <p>   Protected credential file  (RUNNER_AUTH_TOKEN_FILE)
          --ttl <seconds>         Requested lease TTL         (RUNNER_TTL_SECONDS)
          --max-parallelism <n>   Daemon host slots            (RUNNER_MAX_PARALLELISM, default 2)
          --poll-seconds <n>      Empty-queue poll delay       (RUNNER_POLL_SECONDS, default 5)
          --poll                  Run continuously (also the default without a task key)
          -h, --help              Show this help
        """);
}
