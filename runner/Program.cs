using AgentRunner;

// Standalone remote runner (RM-5, Runner-Split C). Runs one task end-to-end on a
// Linux host against the local Studio's Task Server API and exits. See
// docs/operations/setup/linux-runner-host.md.

var (options, taskKey, once, help) = RunnerOptions.Parse(args);

if (help)
{
    PrintUsage();
    return 0;
}

if (taskKey is null)
{
    Console.Error.WriteLine("error: no task key given. Pass it positionally or with --task <TASK-KEY>.");
    PrintUsage();
    return 64; // EX_USAGE
}

void Log(string message) => Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] [runner] {message}");

Log($"agent-runner starting: server={options.ServerUrl} task={taskKey} once={once}");

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // let the finally-blocks release the lease cleanly
    Log("shutdown requested (Ctrl+C); finishing teardown...");
    shutdown.Cancel();
};

using var client = new TaskServerClient(options);
var runner = new RemoteTaskRunner(options, client, Log);

try
{
    var exitCode = await runner.RunAsync(taskKey, shutdown.Token);
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
          -h, --help              Show this help
        """);
}
