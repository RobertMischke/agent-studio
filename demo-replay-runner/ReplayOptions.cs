namespace AgentStudio.DemoReplayRunner;

/// <summary>
/// Environment-first configuration for the replay-only service. It mirrors the
/// standalone runner's parsing conventions but exposes a much smaller surface:
/// there is no workdir, no repository, no branch, no CLI binary, and no Git
/// remote, because this service never touches any of them.
/// </summary>
public sealed record ReplayOptions
{
    public required string ServerUrl { get; init; }
    public required string TracePath { get; init; }
    public required string PublicKeyBase64 { get; init; }
    public string? AuthToken { get; init; }
    public string RunnerId { get; init; } = "demo-runner-replay";
    public long StartEpoch { get; init; } = 1;
    public double SpeedFactor { get; init; } = 1.0;
    public int CyclePauseSeconds { get; init; } = 60;
    public int RequestTimeoutSeconds { get; init; } = 30;
    public bool AllowInsecureHttp { get; init; }
    public bool HealthCheckOnly { get; init; }

    public static (ReplayOptions Options, bool Once, bool Help) Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var once = false;
        var help = false;
        var healthCheck = false;
        var cli = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--help" or "-h": help = true; continue;
                case "--once": once = true; continue;
                case "--health-check": healthCheck = true; continue;
                case "--allow-insecure-http": cli["allow-insecure-http"] = "1"; continue;
                case "--auth-token":
                    throw new ArgumentException("Refusing a replay credential on the command line. Use DEMO_REPLAY_AUTH_TOKEN_FILE.");
            }
            if (arg.StartsWith("--", StringComparison.Ordinal) && index + 1 < args.Length)
                cli[arg[2..]] = args[++index];
        }

        var tokenFile = Val(cli, "auth-token-file", "DEMO_REPLAY_AUTH_TOKEN_FILE", null);
        var directToken = Env("DEMO_REPLAY_AUTH_TOKEN", null);
        if (tokenFile is not null && directToken is not null)
            throw new ArgumentException("Set either DEMO_REPLAY_AUTH_TOKEN or DEMO_REPLAY_AUTH_TOKEN_FILE, not both.");

        var serverUrl = (Val(cli, "server", "DEMO_REPLAY_SERVER_URL", "http://127.0.0.1:5030") ?? "").TrimEnd('/');
        var allowInsecureHttp = cli.ContainsKey("allow-insecure-http") || OptIn("DEMO_REPLAY_ALLOW_INSECURE_HTTP");
        var options = new ReplayOptions
        {
            ServerUrl = serverUrl,
            TracePath = Val(cli, "trace", "DEMO_REPLAY_TRACE_FILE", "/opt/demo-replay/trace/replay-trace.json")!,
            PublicKeyBase64 = ReadPublicKey(Val(cli, "public-key-file", "DEMO_REPLAY_PUBLIC_KEY_FILE", null))
                              ?? Env("DEMO_REPLAY_PUBLIC_KEY", "")!,
            AuthToken = tokenFile is not null ? ReadTokenFile(tokenFile) : directToken,
            RunnerId = Val(cli, "runner-id", "DEMO_REPLAY_RUNNER_ID", "demo-runner-replay")!,
            StartEpoch = EnvLong("DEMO_REPLAY_EPOCH", 1),
            SpeedFactor = EnvDouble("DEMO_REPLAY_SPEED", 1.0),
            CyclePauseSeconds = EnvInt("DEMO_REPLAY_CYCLE_PAUSE_SECONDS", 60),
            RequestTimeoutSeconds = EnvInt("DEMO_REPLAY_REQUEST_TIMEOUT_SECONDS", 30),
            AllowInsecureHttp = allowInsecureHttp,
            HealthCheckOnly = healthCheck,
        };

        Validate(options);
        return (options, once, help);
    }

    internal static void Validate(ReplayOptions options)
    {
        if (!Uri.TryCreate(options.ServerUrl, UriKind.Absolute, out var uri))
            throw new ArgumentException("DEMO_REPLAY_SERVER_URL must be an absolute URL.");
        var loopback = uri.IsLoopback;
        if (!loopback && uri.Scheme != Uri.UriSchemeHttps && !options.AllowInsecureHttp)
            throw new ArgumentException("A non-loopback replay target requires HTTPS.");
        if (!loopback && string.IsNullOrWhiteSpace(options.AuthToken))
            throw new ArgumentException("A non-loopback replay target requires a replay credential.");
        if (string.IsNullOrWhiteSpace(options.PublicKeyBase64))
            throw new ArgumentException("A replay trace verification key is required. Set DEMO_REPLAY_PUBLIC_KEY or DEMO_REPLAY_PUBLIC_KEY_FILE.");
        if (options.StartEpoch <= 0)
            throw new ArgumentException("DEMO_REPLAY_EPOCH must be positive.");
        if (options.SpeedFactor is <= 0 or > 1000)
            throw new ArgumentException("DEMO_REPLAY_SPEED must be greater than zero and at most 1000.");
        if (options.CyclePauseSeconds < 0)
            throw new ArgumentException("DEMO_REPLAY_CYCLE_PAUSE_SECONDS must not be negative.");
    }

    private static string? ReadPublicKey(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : File.ReadAllText(path).Trim();

    private static string ReadTokenFile(string path)
    {
        if (!File.Exists(path))
            throw new ArgumentException($"The replay credential file '{path}' does not exist.");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            var exposed = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                          | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & exposed) != 0)
                throw new ArgumentException($"The replay credential file '{path}' must not be readable beyond its owner.");
        }
        var token = File.ReadAllText(path).Trim();
        if (token.Length == 0) throw new ArgumentException($"The replay credential file '{path}' is empty.");
        return token;
    }

    private static string? Val(IReadOnlyDictionary<string, string> cli, string key, string envName, string? fallback)
        => cli.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : Env(envName, fallback);

    private static string? Env(string name, string? fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static bool OptIn(string name) => Env(name, null) is "1" or "true" or "TRUE" or "True";

    private static int EnvInt(string name, int fallback)
        => int.TryParse(Env(name, null), out var value) && value >= 0 ? value : fallback;

    private static long EnvLong(string name, long fallback)
        => long.TryParse(Env(name, null), out var value) && value > 0 ? value : fallback;

    private static double EnvDouble(string name, double fallback)
        => double.TryParse(Env(name, null), System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
}
