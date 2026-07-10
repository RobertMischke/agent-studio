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
Log($"agent-runner starting: server={options.ServerUrl} mode={(daemonMode ? "daemon" : "one-shot")} task={taskKey ?? "(assigned projects)"}");

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // let the finally-blocks release the lease cleanly
    Log("shutdown requested (Ctrl+C); finishing teardown...");
    shutdown.Cancel();
};

using var client = new TaskServerClient(options);

try
{
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

        Most configuration comes from environment variables (see the runbook,
        docs/operations/setup/linux-runner-host.md). Command-line flags override:

          --server <url>          Task Server base URL       (RUNNER_SERVER_URL)
          --runner-id <id>        Stable runner identity     (RUNNER_ID)
          --runner-name <name>    Board-facing runner name   (RUNNER_NAME)
          --git-remote <url>      Origin the code arrives on  (RUNNER_GIT_REMOTE)
          --branch <name>         Branch to check out         (RUNNER_BRANCH)
          --base-branch <name>    Fallback branch             (RUNNER_BASE_BRANCH)
          --workdir <path>        Checkout + results dir      (RUNNER_WORKDIR)
          --cli <bin>             Agent CLI binary            (RUNNER_CLI_BIN)
          --cli-args "<args>"     Headless CLI args           (RUNNER_CLI_ARGS)
          --auth-token <token>    Bearer token               (RUNNER_AUTH_TOKEN)
          --ttl <seconds>         Requested lease TTL         (RUNNER_TTL_SECONDS)
          --max-parallelism <n>   Daemon host slots            (RUNNER_MAX_PARALLELISM, default 2)
          --poll-seconds <n>      Empty-queue poll delay       (RUNNER_POLL_SECONDS, default 5)
          --poll                  Run continuously (also the default without a task key)
          -h, --help              Show this help
        """);
}
