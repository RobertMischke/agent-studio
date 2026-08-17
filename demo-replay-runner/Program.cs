using System.Reflection;
using System.Runtime.InteropServices;
using AgentStudio.DemoReplayRunner;

if (args.Contains("--version"))
{
    var version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    Console.WriteLine($"demo-replay-runner {version}");
    return 0;
}

void Log(string message)
    => Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] [demo-replay-runner] {message}");

ReplayOptions options;
bool once;
try
{
    var parsed = ReplayOptions.Parse(args);
    if (parsed.Help) { PrintUsage(); return 0; }
    options = parsed.Options;
    once = parsed.Once;
}
catch (ArgumentException ex)
{
    Log($"configuration rejected reason={ex.Message}");
    return 4;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
using var sigterm = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
    ? null
    : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; shutdown.Cancel(); });

using var client = new ReplayClient(options);

if (options.HealthCheckOnly)
{
    var reason = await client.ProbeHealthAsync(shutdown.Token);
    if (reason is null) { Log("health ok"); return 0; }
    Log($"health failed reason={reason}");
    return 4;
}

try
{
    var trace = ReplayTraceLoader.Load(options.TracePath, options.PublicKeyBase64);
    var session = new ReplaySession(client, trace, options, Log);
    await session.RunAsync(once, shutdown.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Log("shutdown requested, replay drained");
    return 130;
}
catch (InvalidOperationException ex)
{
    Log($"replay refused to start reason={ex.Message}");
    return 4;
}

static void PrintUsage() => Console.WriteLine(
    """
    demo-replay-runner - replays a signed fixed trace into the public demo instance.

    This service has no CLI, no Git, no repository, and no claim path. It can
    reach exactly one origin on two paths and it can only emit pre-signed frames
    that the server labels Simulated.

      --once                     Run a single cycle and exit.
      --health-check             Probe the demo server and exit.
      --server <url>             DEMO_REPLAY_SERVER_URL (default http://127.0.0.1:5030)
      --trace <path>             DEMO_REPLAY_TRACE_FILE
      --public-key-file <path>   DEMO_REPLAY_PUBLIC_KEY_FILE (or DEMO_REPLAY_PUBLIC_KEY)
      --auth-token-file <path>   DEMO_REPLAY_AUTH_TOKEN_FILE (never on the command line)
      --runner-id <id>           DEMO_REPLAY_RUNNER_ID (default demo-runner-replay)
      --allow-insecure-http      DEMO_REPLAY_ALLOW_INSECURE_HTTP

    Additional environment variables:
      DEMO_REPLAY_EPOCH                  First replay epoch (default 1)
      DEMO_REPLAY_SPEED                  Playback speed factor (default 1.0)
      DEMO_REPLAY_CYCLE_PAUSE_SECONDS    Pause between cycles (default 60)
      DEMO_REPLAY_REQUEST_TIMEOUT_SECONDS Per-request timeout (default 30)

    Exit codes: 0 clean, 4 configuration or trace refused, 130 cancelled.
    """);
