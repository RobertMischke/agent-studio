using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

internal static class RunnerCapabilityProbe
{
    public static IReadOnlyList<AdvertisedCapabilityDto> Advertise(
        RunnerOptions options,
        bool gitPushReady,
        bool gitWorkflowPushReady = true,
        string? gitDetail = null,
        ProviderAuthProbe? providerAuth = null,
        TaskServerConnectivitySnapshot? connectivity = null)
    {
        var list = new List<AdvertisedCapabilityDto>
        {
            Capability(
                options.Role == "review"
                    ? CapabilityProtocol.ReviewExecutor
                    : CapabilityProtocol.CodingExecutor,
                "executor",
                typeof(RunnerCapabilityProbe).Assembly.GetName().Version?.ToString(),
                options.Role),
            Capability(CapabilityProtocol.GitFetch, "source", ToolVersion("git"), "git"),
            Capability(CapabilityProtocol.RepositoryAccess, "source", null, options.GitRemote ?? "server-routed"),
            Capability(CapabilityProtocol.Disk, "foundation", null, Path.GetPathRoot(options.WorkDir)),
            Capability(
                CapabilityProtocol.TaskServerConnectivity,
                "foundation",
                null,
                new Uri(options.ServerUrl).Authority,
                connectivity?.Status == TaskServerConnectivityStates.Unreachable ? "unavailable" : "ready",
                ConnectivityDetail(connectivity)),
            Capability($"platform:{Platform()}", "platform", RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString()),
        };
        if (options.Role == "coding")
        {
            // The advertised status is the only lever that keeps a card away from
            // a host whose provider login is gone: the task server admits a claim
            // only while every required capability reads exactly "ready"
            // (task-server/TaskServerCapabilityStore.cs:506). Reporting mere PATH
            // presence as "ready" is what burns cards after an expired token, so
            // this asks the CLI itself - see docs/operations/token-refresh-ohne-tunnel.md.
            AddCodingCliCapabilities(
                list,
                options,
                providerAuth ?? ProviderAuthProbe.Shared);
            list.Add(Capability(
                CapabilityProtocol.GitPush,
                "source",
                ToolVersion("git"),
                options.GitPushRemote ?? options.GitRemote ?? "server-routed",
                gitPushReady ? "ready" : "unavailable",
                gitDetail));
            list.Add(Capability(
                CapabilityProtocol.GitWorkflowPush,
                "source",
                ToolVersion("git"),
                options.GitPushRemote ?? options.GitRemote ?? "server-routed",
                !gitPushReady
                    ? "unavailable"
                    : gitWorkflowPushReady
                        ? GitPushProbe.Ready
                        : GitPushProbe.ReadyNoWorkflowScope,
                gitDetail));
            // T1 canary mechanism (car-migration-plan §4): a CAR-engined host says
            // so, and the canary cards request exactly this key through their
            // RequiredCapabilities - cohorts 1 -> 5 -> default, no special path.
            if (options.ExecEngine == RunnerOptions.ExecEngineCar)
            {
                list.Add(Capability(
                    "exec-engine:car",
                    "executor",
                    typeof(CodingAgentRunner.CliRunner).Assembly.GetName().Version?.ToString(),
                    options.ExecEngine));
            }
        }
        else
        {
            AddCodingCliCapabilities(
                list,
                options,
                providerAuth ?? ProviderAuthProbe.Shared);
            list.Add(Capability(CapabilityProtocol.Vision, "review", null, "remote-review"));
            list.Add(Capability(ReviewCapabilities.SemanticReview, "review", null, "remote-review"));
            list.Add(Capability(ReviewCapabilities.GitMaterialization, "review", ToolVersion("git"), "git"));
            list.Add(Capability(ReviewCapabilities.SourceBundleMaterialization, "review", null, "artifact"));
            list.Add(Capability(ReviewCapabilities.BaselineComparison, "review", null, "merge-base"));
            list.Add(Capability(ReviewCapabilities.DependencyPreparation, "review", null, "build-profile"));
        }
        AddToolchain(list, CapabilityProtocol.DotNet, "dotnet");
        AddToolchain(list, CapabilityProtocol.Node, "node");
        AddToolchain(list, CapabilityProtocol.Playwright, "playwright");
        return list;
    }

    public static IReadOnlyList<string> CodingRequirements(RunnerOptions options)
        => new[]
        {
            CapabilityProtocol.CodingExecutor,
            CapabilityProtocol.CliExecution(AgentCliProcess.ConfiguredCliType(options)),
            CapabilityProtocol.ProviderAuthentication(AgentCliProcess.ConfiguredCliType(options)),
            CapabilityProtocol.GitFetch,
            CapabilityProtocol.RepositoryAccess,
            CapabilityProtocol.Disk,
            CapabilityProtocol.TaskServerConnectivity,
        }
        .Concat(options.RequiredCapabilities)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// Host-wide requirements for the legacy card-selecting claim plane. The
    /// server adds the candidate card's CLI execution and authentication keys,
    /// so a mixed host is not accidentally forced through its configured
    /// default provider when it claims a card for its other CLI.
    /// </summary>
    public static IReadOnlyList<string> CodingHostRequirements(RunnerOptions options)
        => new[]
        {
            CapabilityProtocol.CodingExecutor,
            CapabilityProtocol.GitFetch,
            CapabilityProtocol.RepositoryAccess,
            CapabilityProtocol.Disk,
            CapabilityProtocol.TaskServerConnectivity,
        }
        .Concat(options.RequiredCapabilities)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public static IReadOnlyList<string> ReviewRequirements(RunnerOptions options)
        => new[]
        {
            CapabilityProtocol.ReviewExecutor,
            CapabilityProtocol.GitFetch,
            CapabilityProtocol.RepositoryAccess,
            CapabilityProtocol.Disk,
            CapabilityProtocol.TaskServerConnectivity,
            ReviewCapabilities.SemanticReview,
            ReviewCapabilities.BaselineComparison,
            ReviewCapabilities.DependencyPreparation,
        }
        .Concat(options.RequiredCapabilities)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public static IReadOnlyList<string> ReviewRegistrationCapabilities(RunnerOptions options)
        => ReviewRequirements(options)
            .Concat(new[]
            {
                ReviewCapabilities.ReviewExecutor,
                ReviewCapabilities.GitMaterialization,
                ReviewCapabilities.SourceBundleMaterialization,
                ReviewCapabilities.VisionReview,
            })
            .Concat(CodingCliBinaries(options).SelectMany(item => new[]
            {
                CapabilityProtocol.CliExecution(item.CliType),
                CapabilityProtocol.ProviderAuthentication(item.CliType),
            }))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public static HostTelemetrySnapshotDto? Telemetry(HostTelemetrySample? sample)
        => sample is null
            ? null
            : new HostTelemetrySnapshotDto(
                sample.Timestamp,
                sample.CpuPercent,
                sample.Load1,
                sample.Load5,
                sample.Load15,
                sample.MemoryUsedBytes,
                sample.MemoryTotalBytes,
                sample.SwapInBytesPerSecond,
                sample.SwapOutBytesPerSecond,
                sample.CpuStealPercent,
                sample.IoWaitPercent,
                sample.CpuCores,
                sample.ActiveSlots,
                DiskFreeBytes(),
                DiskTotalBytes(),
                sample.TaskServerConnectionStatus,
                sample.TaskServerConnectionObservedAt,
                sample.TaskServerConnectionFailureStartedAt,
                sample.TaskServerConnectionConsecutiveFailures,
                sample.TaskServerConnectionEscalatedAt,
                sample.TaskServerConnectionLastError,
                sample.TaskServerConnectionLastRecoveredAt);

    private static string ConnectivityDetail(TaskServerConnectivitySnapshot? connectivity)
    {
        if (connectivity is null || connectivity.Status == TaskServerConnectivityStates.Unknown)
            return "Task Server route has not completed its first observed request yet.";
        if (connectivity.Status == TaskServerConnectivityStates.Reachable)
            return $"Task Server route reachable; observed {connectivity.ObservedAt:o}.";
        return $"Task Server route unavailable since {connectivity.FailureStartedAt:o}; " +
               $"{connectivity.ConsecutiveFailures} consecutive request failures. " +
               (connectivity.LastError ?? "No transport detail was captured.");
    }

    public static string Provider(string cliBinary)
    {
        var name = Path.GetFileNameWithoutExtension(cliBinary).Trim().ToLowerInvariant();
        return name.Length == 0 ? "unknown" : name;
    }

    public static bool IsProviderAuthenticationFailure(ProcessResult result)
    {
        if (result.ExitCode == 0) return false;
        var provider = ProviderOutputEvidenceExtractor.Extract(result.StdOut);
        var evidence = $"{provider.TerminalEvent}\n{result.StdErr}";
        if (ProviderLimitEvidenceParser.Detect([evidence], DateTimeOffset.UtcNow) is not null)
            return false;
        return ProviderAuthProbe.IndicatesNoUsableSession(evidence);
    }

    private static void AddToolchain(
        ICollection<AdvertisedCapabilityDto> capabilities,
        string key,
        string executable)
    {
        if (!OnPath(executable)) return;
        capabilities.Add(Capability(key, "toolchain", ToolVersion(executable), executable));
    }

    private static void AddCodingCliCapabilities(
        ICollection<AdvertisedCapabilityDto> capabilities,
        RunnerOptions options,
        ProviderAuthProbe providerAuth)
    {
        foreach (var (cliType, binary) in CodingCliBinaries(options))
        {
            var auth = providerAuth.Current(binary);
            var binaryAvailable = ProviderAuthProbe.ExecutableExists(binary);
            capabilities.Add(Capability(
                CapabilityProtocol.CliExecution(cliType),
                "cli-execution",
                binaryAvailable ? "available" : null,
                binary,
                binaryAvailable ? ProviderAuthProbe.Ready : ProviderAuthProbe.Unavailable,
                binaryAvailable
                    ? $"CLI binary '{binary}' is available for {cliType} cards."
                    : $"CLI binary '{binary}' was not found; {cliType} cards cannot execute."));
            capabilities.Add(Capability(
                CapabilityProtocol.ProviderAuthentication(cliType),
                "provider-auth",
                binaryAvailable ? "available" : null,
                cliType,
                auth.Status,
                auth.Detail,
                auth.Condition,
                auth.EvidenceObservedAt?.UtcDateTime,
                auth.ExpiresAt?.UtcDateTime,
                auth.RetryAt?.UtcDateTime,
                auth.CredentialUpdatedAt?.UtcDateTime));
        }
    }

    internal static IReadOnlyList<(string CliType, string Binary)> CodingCliBinaries(
        RunnerOptions options)
    {
        var configuredType = AgentCliProcess.ConfiguredCliType(options);
        var binaries = new List<(string CliType, string Binary)>
        {
            (configuredType, options.CliBin),
        };
        if (configuredType != AgentCliProcess.ClaudeCli
            && !string.IsNullOrWhiteSpace(options.ClaudeCliBin))
        {
            binaries.Add((AgentCliProcess.ClaudeCli, options.ClaudeCliBin));
        }
        if (configuredType != AgentCliProcess.CodexCli
            && !string.IsNullOrWhiteSpace(options.CodexCliBin))
        {
            binaries.Add((AgentCliProcess.CodexCli, options.CodexCliBin));
        }
        return binaries;
    }

    private static AdvertisedCapabilityDto Capability(
        string key,
        string category,
        string? version,
        string? identity,
        string status = "ready",
        string? detail = null,
        string? condition = null,
        DateTime? evidenceObservedAt = null,
        DateTime? expiresAt = null,
        DateTime? retryAt = null,
        DateTime? credentialUpdatedAt = null)
        => new(
            key,
            category,
            status,
            version,
            identity,
            detail,
            condition,
            evidenceObservedAt,
            expiresAt,
            retryAt,
            credentialUpdatedAt);

    private static string Platform()
        => $"{(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other")}:{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";

    internal static bool OnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return false;
        var names = OperatingSystem.IsWindows()
            ? new[] { executable, executable + ".exe", executable + ".cmd" }
            : new[] { executable };
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => names.Any(name => File.Exists(Path.Combine(directory, name))));
    }

    private static string? ToolVersion(string executable)
        => OnPath(executable) ? "available" : null;

    private static long? DiskFreeBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(Environment.CurrentDirectory)!).AvailableFreeSpace; }
        catch { return null; }
    }

    private static long? DiskTotalBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(Environment.CurrentDirectory)!).TotalSize; }
        catch { return null; }
    }
}

/// <summary>One observed provider-authentication verdict plus the sentence an operator can act on.</summary>
/// <param name="Status">Exactly what goes on the wire: <c>ready</c> or <c>unavailable</c>.</param>
/// <param name="Detail">One line, no secrets, safe to show on the capability panel.</param>
/// <param name="ObservedAt">When the verdict was taken - the TTL is measured from here.</param>
/// <param name="ProbeDegraded">Whether the latest probe was indeterminate and this verdict was retained.</param>
public sealed record ProviderAuthStatus(
    string Status,
    string Detail,
    DateTimeOffset ObservedAt,
    bool ProbeDegraded = false,
    string Condition = ProviderAuthConditions.Unverified,
    DateTimeOffset? EvidenceObservedAt = null,
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? RetryAt = null,
    DateTimeOffset? CredentialUpdatedAt = null)
{
    public bool IsReady => Status == ProviderAuthProbe.Ready;
}

/// <summary>
/// Runs one bounded, non-agent status command and returns its exit code and output.
/// This is the seam that keeps the actual child process out of the capability
/// layer: the composition root supplies it (see <see cref="ProviderAuthProbe"/>),
/// tests supply a fake, and this file never starts a coding-agent CLI itself.
/// </summary>
public delegate Task<ProcessResult> ProviderAuthLauncher(
    string fileName,
    IReadOnlyList<string> arguments,
    CancellationToken ct);

/// <summary>
/// Honest <c>provider-auth</c> status for the capability advertisement
/// (docs/operations/token-refresh-ohne-tunnel.md, stage S2 "aktive Probe").
///
/// <para>Until now the runner advertised <c>ready</c> as soon as the CLI binary
/// sat on PATH, so a host with an expired or revoked login kept claiming coding
/// cards and burned every one of them. This asks the CLI whether it still has a
/// session - <c>claude auth status</c> / <c>codex login status</c> - under a
/// bounded timeout, and maps the answer onto the two statuses the claim admission
/// understands.</para>
///
/// <para><b>Cached, never per claim.</b> The verdict is taken at most once per
/// <see cref="DefaultTtl"/>; the advertisement loop (every 60 s) and every claim
/// read the cached value. An expired entry is refreshed behind the last known
/// verdict, so no daemon loop ever waits on a child process.</para>
///
/// <para><b>Last-good with negative confirmation.</b> Only repeated, explicit
/// logout output may replace a ready verdict with <c>unavailable</c>. A timeout,
/// empty output, launch failure, or unsupported command is indeterminate: the
/// probe retains its last verdict and emits a degraded diagnostic. A later
/// successful probe replaces either verdict, so recovery never needs a daemon
/// restart.</para>
///
/// <para><b>Wiring (open connection point).</b> Without a launcher the probe
/// degrades to the old PATH check - it just says so in the detail instead of
/// claiming proof. The composition root wires the real one in a single line, and
/// it belongs there rather than here because the CLI-invocation guard keeps
/// coding-agent spawns inside the execution layer.</para>
/// </summary>
public sealed class ProviderAuthProbe
{
    public const string Ready = "ready";
    public const string Limited = "limited";
    public const string Unavailable = "unavailable";
    public const string ConceptPath = "docs/operations/token-refresh-ohne-tunnel.md";

    /// <summary>Idle cost is one child process per host per five minutes.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    /// <summary>Degraded and signed-out providers are retried on each minute advertisement.</summary>
    public static readonly TimeSpan DefaultRecoveryRetry = TimeSpan.FromMinutes(1);

    /// <summary>Quiet credential warning window shared with the frontend.</summary>
    public static readonly TimeSpan DefaultExpiryWarning = TimeSpan.FromDays(14);

    /// <summary>Node-based CLIs may need this long to start on a saturated review host.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Two explicit logout answers are required before claim admission closes.</summary>
    public const int DefaultNegativeConfirmations = 2;

    /// <summary>The instance the advertisement reads from when no probe is passed in.</summary>
    public static ProviderAuthProbe Shared { get; } = new();

    private readonly object _sync = new();
    private readonly Func<string, bool> _executableExists;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _timeout;
    private readonly int _negativeConfirmations;
    private ProviderAuthLauncher? _launcher;
    private ProviderCredentialInspector? _credentialInspector;
    private Action<string>? _diagnosticLog;
    private readonly Dictionary<string, ProviderAuthCacheEntry> _observed =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _refreshInFlight =
        new(StringComparer.Ordinal);

    public ProviderAuthProbe(
        ProviderAuthLauncher? launcher = null,
        Func<string, bool>? executableExists = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? ttl = null,
        TimeSpan? timeout = null,
        int negativeConfirmations = DefaultNegativeConfirmations,
        Action<string>? diagnosticLog = null,
        ProviderCredentialInspector? credentialInspector = null)
    {
        if (negativeConfirmations < 1)
            throw new ArgumentOutOfRangeException(nameof(negativeConfirmations));
        _launcher = launcher;
        _executableExists = executableExists ?? ExecutableExists;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _ttl = ttl ?? DefaultTtl;
        _timeout = timeout ?? DefaultTimeout;
        _negativeConfirmations = negativeConfirmations;
        _diagnosticLog = diagnosticLog;
        _credentialInspector = credentialInspector;
    }

    /// <summary>
    /// Connection point for the composition root: hand the probe a way to start a
    /// short-lived status command. Any verdict taken before this point was a
    /// presence check only and is dropped.
    /// </summary>
    public void UseLauncher(ProviderAuthLauncher launcher, Action<string>? diagnosticLog = null)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        lock (_sync)
        {
            _launcher = launcher;
            _diagnosticLog = diagnosticLog;
            _observed.Clear();
            _refreshInFlight.Clear();
        }
    }

    /// <summary>Wires secret-free credential metadata inspection in production.</summary>
    public void UseCredentialInspector(ProviderCredentialInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        lock (_sync)
        {
            _credentialInspector = inspector;
            _observed.Clear();
            _refreshInFlight.Clear();
        }
    }

    /// <summary>
    /// The status to advertise right now. Never blocks: a fresh verdict is
    /// returned as is, a stale one is served while a refresh runs behind it, and
    /// the very first call answers from what can be decided without a child
    /// process - marked "unverified" so the detail never overstates the evidence.
    /// </summary>
    public ProviderAuthStatus Current(string cliBinary)
    {
        lock (_sync)
        {
            _observed.TryGetValue(cliBinary, out var known);
            var now = _clock();
            var retryDue = known?.Status.Condition == ProviderAuthConditions.RateLimited
                ? known.Status.RetryAt <= now
                : known is not null
                  && known.Status.Condition != ProviderAuthConditions.Authenticated
                  && known.Status.Condition != ProviderAuthConditions.CredentialsExpiring
                  && now - known.Status.ObservedAt >= DefaultRecoveryRetry;
            if (known is not null && !retryDue && now - known.Status.ObservedAt < _ttl)
                return known.Status;

            if (_launcher is not null && _refreshInFlight.Add(cliBinary))
            {
                _ = Task.Run(async () =>
                {
                    // A failed refresh must never take the daemon loop with it;
                    // ObserveAsync already turns every failure into a verdict.
                    try { await RefreshAsync(cliBinary, CancellationToken.None); }
                    catch { /* keep serving the last known verdict */ }
                });
            }
            if (known is not null) return known.Status;

            var bootstrap = PresenceOnly(cliBinary, probeWired: _launcher is not null);
            _observed[cliBinary] = new ProviderAuthCacheEntry(bootstrap, 0);
            return bootstrap;
        }
    }

    /// <summary>
    /// Takes a verdict now and caches it. Awaitable so a host can prove its login
    /// before the first advertisement instead of one refresh interval later.
    /// </summary>
    public async Task<ProviderAuthStatus> RefreshAsync(string cliBinary, CancellationToken ct)
    {
        try
        {
            var observation = await ObserveAsync(cliBinary, ct);
            return ApplyObservation(cliBinary, observation);
        }
        finally
        {
            lock (_sync) _refreshInFlight.Remove(cliBinary);
        }
    }

    /// <summary>
    /// Feeds a provider-owned terminal rejection back into the same state
    /// machine as the idle probe. Ordinary CLI/tool crashes never call this
    /// path. Authentication failures receive an immediate independent status
    /// probe; quota failures open a provider-limited window instead.
    /// </summary>
    public async Task<ProviderAuthStatus> RecordExecutionOutcomeAsync(
        string cliBinary,
        ExecutionOutcomeDecision decision,
        CancellationToken ct)
    {
        var facts = decision.RawFacts;
        var evidenceText = $"{facts.ProviderTerminalEvent}\n{facts.StdErr}";
        if (decision.Outcome == ExecutionOutcomeKind.QuotaExceeded)
        {
            var limit = ProviderLimitEvidenceParser.Detect([evidenceText], _clock());
            if (limit is null) return Current(cliBinary);
            return ApplyObservation(
                cliBinary,
                new ProviderAuthObservation(
                    ProviderAuthObservationKind.RateLimited,
                    $"rate-limited until {limit.RetryAt:O}: {Excerpt(limit.Detail)}",
                    RetryAt: limit.RetryAt));
        }
        if (decision.Outcome != ExecutionOutcomeKind.AuthenticationFailure)
            return Current(cliBinary);

        _ = ApplyObservation(
            cliBinary,
            new ProviderAuthObservation(
                ProviderAuthObservationKind.LoggedOut,
                $"provider terminal reports no usable session: {Excerpt(evidenceText)}"));
        return await RefreshAsync(cliBinary, ct);
    }

    private ProviderAuthStatus ApplyObservation(
        string cliBinary,
        ProviderAuthObservation observation)
    {
        ProviderAuthCacheEntry decision;
        ProviderAuthCacheEntry? previous;
        lock (_sync)
        {
            _observed.TryGetValue(cliBinary, out previous);
            decision = Decide(previous, observation, _negativeConfirmations, _clock());
            _observed[cliBinary] = decision;
        }
        LogTransition(cliBinary, previous, decision, observation);
        return decision.Status;
    }

    /// <summary>
    /// The status command per provider, or null when this runner has none for it.
    /// Null means "keep the presence check": inventing a command for an unknown
    /// wrapper binary would drain the host on its first advertisement.
    /// </summary>
    public static IReadOnlyList<string>? AuthStatusArguments(string provider) => provider switch
    {
        "claude" => ["auth", "status", "--text"],
        "codex" => ["login", "status"],
        _ => null,
    };

    /// <summary>
    /// Builds the low-contention Linux invocation used by the composition root.
    /// ArgumentList keeps configured paths and arguments out of shell parsing.
    /// </summary>
    internal static ProviderAuthProcessInvocation LowPriorityInvocation(
        string fileName,
        IReadOnlyList<string> arguments,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (!OperatingSystem.IsLinux())
            return new ProviderAuthProcessInvocation(fileName, arguments, false);

        var nice = new[] { "/usr/bin/nice", "/bin/nice" }.FirstOrDefault(fileExists);
        return nice is null
            ? new ProviderAuthProcessInvocation(fileName, arguments, false)
            : new ProviderAuthProcessInvocation(
                nice,
                ["-n", "10", "--", fileName, .. arguments],
                true);
    }

    private async Task<ProviderAuthObservation> ObserveAsync(string cliBinary, CancellationToken ct)
    {
        ProviderAuthLauncher? launcher;
        ProviderCredentialInspector? credentialInspector;
        lock (_sync)
        {
            launcher = _launcher;
            credentialInspector = _credentialInspector;
        }
        if (launcher is null)
            return Indeterminate("no auth probe launcher is wired");

        var provider = RunnerCapabilityProbe.Provider(cliBinary);
        if (!_executableExists(cliBinary))
            return new ProviderAuthObservation(
                ProviderAuthObservationKind.BinaryMissing,
                $"CLI binary '{cliBinary}' was not found; provider '{provider}' cannot authenticate a run.");

        var arguments = AuthStatusArguments(provider);
        if (arguments is null)
            return Indeterminate(
                $"unverified: no auth status command is known for provider '{provider}'; "
                + $"binary presence only. See {ConceptPath}.");

        var command = $"{provider} {string.Join(' ', arguments)}";
        ProcessResult result;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(_timeout);
        try
        {
            result = await launcher(cliBinary, arguments, bounded.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Indeterminate($"'{command}' did not answer within {_timeout.TotalSeconds:0}s.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Indeterminate(
                $"'{command}' could not be started: "
                + Excerpt($"{exception.GetType().Name}: {exception.Message}"));
        }

        ProviderCredentialFreshness? freshness = null;
        try { freshness = credentialInspector?.Invoke(provider); }
        catch { /* credential metadata is advisory; the CLI verdict remains authoritative */ }
        return Interpret(command, result, freshness);
    }

    private ProviderAuthObservation Interpret(
        string command,
        ProcessResult result,
        ProviderCredentialFreshness? freshness)
    {
        var text = $"{result.StdOut}\n{result.StdErr}";
        var limit = ProviderLimitEvidenceParser.Detect([text], _clock());
        if (limit is not null)
            return new ProviderAuthObservation(
                ProviderAuthObservationKind.RateLimited,
                $"rate-limited until {limit.RetryAt:O}: {Excerpt(limit.Detail)}",
                RetryAt: limit.RetryAt,
                Credential: freshness);
        if (IndicatesNoUsableSession(text))
            return new ProviderAuthObservation(
                ProviderAuthObservationKind.LoggedOut,
                $"'{command}' reports no usable session (exit {result.ExitCode}): {Excerpt(text)}",
                Credential: freshness);
        if (result.Success && !string.IsNullOrWhiteSpace(text))
            return new ProviderAuthObservation(
                ProviderAuthObservationKind.Authenticated,
                $"'{command}' confirmed an active session.",
                Credential: freshness);
        if (IndicatesUnsupportedCommand(text))
            return Indeterminate(
                $"unverified: '{command}' is not supported by the installed CLI (exit {result.ExitCode}): "
                + $"{Excerpt(text)} Binary presence only. See {ConceptPath}.");
        return Indeterminate(string.IsNullOrWhiteSpace(text)
            ? $"'{command}' returned empty output (exit {result.ExitCode})."
            : $"'{command}' failed without a logout signal (exit {result.ExitCode}): {Excerpt(text)}");
    }

    private static ProviderAuthObservation Indeterminate(string detail)
        => new(ProviderAuthObservationKind.Indeterminate, detail);

    private static ProviderAuthCacheEntry Decide(
        ProviderAuthCacheEntry? previous,
        ProviderAuthObservation observation,
        int negativeConfirmations,
        DateTimeOffset observedAt)
    {
        if (observation.Kind == ProviderAuthObservationKind.Authenticated)
        {
            var expiresAt = observation.Credential?.ExpiresAt;
            var expiring = expiresAt is not null
                           && expiresAt <= observedAt.Add(DefaultExpiryWarning);
            var detail = expiring
                ? $"{observation.Detail} Credentials expire at {expiresAt:O}; re-authenticate before this deadline."
                : observation.Detail;
            return new ProviderAuthCacheEntry(
                new ProviderAuthStatus(
                    Ready,
                    detail,
                    observedAt,
                    Condition: expiring
                        ? ProviderAuthConditions.CredentialsExpiring
                        : ProviderAuthConditions.Authenticated,
                    EvidenceObservedAt: observedAt,
                    ExpiresAt: expiresAt,
                    CredentialUpdatedAt: observation.Credential?.UpdatedAt),
                0);
        }
        if (observation.Kind == ProviderAuthObservationKind.BinaryMissing)
            return new ProviderAuthCacheEntry(
                new ProviderAuthStatus(
                    Unavailable,
                    observation.Detail,
                    observedAt,
                    Condition: ProviderAuthConditions.BinaryMissing,
                    EvidenceObservedAt: observedAt),
                0);

        var retained = previous?.Status
            ?? new ProviderAuthStatus(
                Ready,
                "unverified: the auth probe has not confirmed a session yet; binary presence only.",
                observedAt,
                Condition: ProviderAuthConditions.Unverified);
        if (observation.Kind == ProviderAuthObservationKind.RateLimited)
            return new ProviderAuthCacheEntry(
                new ProviderAuthStatus(
                    Limited,
                    observation.Detail,
                    observedAt,
                    Condition: ProviderAuthConditions.RateLimited,
                    EvidenceObservedAt: observedAt,
                    RetryAt: observation.RetryAt,
                    CredentialUpdatedAt: observation.Credential?.UpdatedAt),
                0);
        if (observation.Kind == ProviderAuthObservationKind.Indeterminate)
            return new ProviderAuthCacheEntry(
                retained with
                {
                    Detail = $"probe degraded: {observation.Detail} Retaining last status '{retained.Status}'.",
                    ObservedAt = observedAt,
                    ProbeDegraded = true,
                    Condition = retained.Status == Unavailable
                        ? retained.Condition
                        : ProviderAuthConditions.TransientError,
                },
                0);

        var failures = Math.Min(negativeConfirmations, (previous?.ConsecutiveLogoutSignals ?? 0) + 1);
        if (failures < negativeConfirmations)
            return new ProviderAuthCacheEntry(
                retained with
                {
                    Detail = $"probe degraded: explicit logout confirmation {failures}/{negativeConfirmations}; "
                             + $"retaining last status '{retained.Status}'. {observation.Detail}",
                    ObservedAt = observedAt,
                    ProbeDegraded = true,
                    Condition = retained.Status == Unavailable
                        ? retained.Condition
                        : ProviderAuthConditions.TransientError,
                },
                failures);
        return new ProviderAuthCacheEntry(
            new ProviderAuthStatus(
                Unavailable,
                observation.Detail,
                observedAt,
                Condition: ProviderAuthConditions.SignedOut,
                EvidenceObservedAt: observedAt,
                CredentialUpdatedAt: observation.Credential?.UpdatedAt),
            failures);
    }

    private void LogTransition(
        string cliBinary,
        ProviderAuthCacheEntry? previous,
        ProviderAuthCacheEntry decision,
        ProviderAuthObservation observation)
    {
        Action<string>? log;
        lock (_sync) log = _diagnosticLog;
        if (log is null) return;
        if (decision.Status.ProbeDegraded)
        {
            log(
                $"runner-provider-auth-probe-degraded binary={cliBinary} "
                + $"outcome={observation.Kind.ToString().ToLowerInvariant()} "
                + $"retainedStatus={decision.Status.Status} "
                + $"logoutConfirmations={decision.ConsecutiveLogoutSignals}/{_negativeConfirmations} "
                + $"detail={observation.Detail}");
        }
        else if (previous?.Status.Status == Unavailable && decision.Status.Status == Ready)
        {
            log($"runner-provider-auth-probe-recovered binary={cliBinary} detail={decision.Status.Detail}");
        }
    }

    private ProviderAuthStatus PresenceOnly(string cliBinary, bool probeWired)
    {
        var provider = RunnerCapabilityProbe.Provider(cliBinary);
        if (!_executableExists(cliBinary)) return BinaryMissing(cliBinary, provider);
        return new ProviderAuthStatus(
            Ready,
            probeWired
                ? $"unverified: the auth probe for '{provider}' has not answered yet; binary presence only."
                : $"unverified: no auth probe is wired on this host; '{provider}' binary presence only. "
                  + $"See {ConceptPath}.",
            _clock());
    }

    private ProviderAuthStatus BinaryMissing(string cliBinary, string provider)
        => new(
            Unavailable,
            $"CLI binary '{cliBinary}' was not found; provider '{provider}' cannot authenticate a run.",
            _clock(),
            Condition: ProviderAuthConditions.BinaryMissing,
            EvidenceObservedAt: _clock());

    /// <summary>Phrases that mean the session is gone, whatever the exit code says.</summary>
    private static readonly string[] NoUsableSessionSignals =
    [
        "not logged in", "not signed in", "logged out", "no active session",
        "not authenticated", "no credentials", "login required", "please log in",
        "please login", "re-authenticate", "reauthenticate", "oauth token expired",
        "access token expired", "refresh token expired", "token has expired",
        "authentication failed", "invalid api key", "unauthorized",
        "401 missing bearer", "401 missing basic", "401 unauthorized",
    ];

    /// <summary>
    /// Phrases that mean the question could not be asked, not that the answer was
    /// no - an argument parser rejecting the status subcommand.
    /// </summary>
    private static readonly string[] UnsupportedCommandSignals =
    [
        "unknown command", "unrecognized subcommand", "unrecognised subcommand",
        "unknown option", "unknown flag", "unexpected argument", "invalid choice",
        "no such command", "did you mean", "usage:", "command not found",
    ];

    public static bool IndicatesNoUsableSession(string? text)
        => Matches(text, NoUsableSessionSignals);

    public static bool IndicatesUnsupportedCommand(string? text)
        => Matches(text, UnsupportedCommandSignals);

    private static bool Matches(string? text, string[] signals)
        => !string.IsNullOrWhiteSpace(text)
           && signals.Any(signal => text.Contains(signal, StringComparison.OrdinalIgnoreCase));

    /// <summary>Anything token-shaped is stripped before a detail reaches the board.</summary>
    private static readonly Regex SecretShaped = new(
        @"\b(?:sk-[A-Za-z0-9_\-]{6,}|[A-Za-z0-9_\-]{40,})\b",
        RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private static string Excerpt(string? value, int maxChars = 200)
    {
        var single = Whitespace.Replace(value ?? string.Empty, " ").Trim();
        var redacted = SecretShaped.Replace(single, "[redacted]");
        return redacted.Length <= maxChars ? redacted : redacted[..maxChars] + "...";
    }

    /// <summary>PATH lookup that also accepts a configured absolute binary path.</summary>
    public static bool ExecutableExists(string cliBinary)
    {
        if (string.IsNullOrWhiteSpace(cliBinary)) return false;
        if (cliBinary.Contains(Path.DirectorySeparatorChar)
            || cliBinary.Contains(Path.AltDirectorySeparatorChar))
        {
            var candidates = OperatingSystem.IsWindows()
                ? new[] { cliBinary, cliBinary + ".exe", cliBinary + ".cmd" }
                : [cliBinary];
            return candidates.Any(File.Exists);
        }
        return RunnerCapabilityProbe.OnPath(cliBinary);
    }
}

internal enum ProviderAuthObservationKind
{
    Authenticated,
    LoggedOut,
    RateLimited,
    Indeterminate,
    BinaryMissing,
}

internal sealed record ProviderAuthObservation(
    ProviderAuthObservationKind Kind,
    string Detail,
    DateTimeOffset? RetryAt = null,
    ProviderCredentialFreshness? Credential = null);

internal sealed record ProviderAuthCacheEntry(
    ProviderAuthStatus Status,
    int ConsecutiveLogoutSignals);

internal sealed record ProviderAuthProcessInvocation(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool LowerPriority);
