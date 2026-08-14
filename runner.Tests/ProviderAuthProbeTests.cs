using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// Stage S2 of docs/operations/token-refresh-ohne-tunnel.md: the runner may only
/// advertise <c>provider-auth</c> as ready when it actually asked the CLI. Every
/// test drives the probe through an injected launcher, so the suite never needs a
/// real claude/codex installation and never starts a process.
/// </summary>
public sealed class ProviderAuthProbeTests
{
    private static ProviderAuthLauncher Answers(int exitCode, string stdout = "", string stderr = "")
        => (_, _, _) => Task.FromResult(new ProcessResult(exitCode, stdout, stderr));

    private static ProviderAuthProbe Probe(
        ProviderAuthLauncher launcher,
        bool binaryExists = true,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? ttl = null,
        TimeSpan? timeout = null,
        TimeSpan? degradedRetry = null,
        int negativeConfirmationCount = ProviderAuthProbe.DefaultNegativeConfirmationCount,
        Action<string>? log = null)
        => new(
            launcher,
            _ => binaryExists,
            clock,
            ttl,
            timeout,
            degradedRetry,
            negativeConfirmationCount,
            log);

    [Theory]
    [InlineData(0, "Not logged in. Run `claude auth login` to sign in.")]
    [InlineData(1, "Error: login required")]
    [InlineData(1, "HTTP 401 Unauthorized")]
    [InlineData(1, "OAuth token expired")]
    public async Task A_dead_session_is_unavailable_after_negative_confirmation(int exitCode, string output)
    {
        var probe = Probe(Answers(exitCode, output));

        var first = await probe.RefreshAsync("claude", CancellationToken.None);
        var status = await probe.RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, first.Status);
        Assert.Equal(ProviderAuthProbe.Unavailable, status.Status);
        Assert.Contains("no usable session", status.Detail, StringComparison.Ordinal);
        Assert.Contains("claude auth status --text", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_indeterminate_probe_breaks_the_consecutive_logout_streak()
    {
        var attempt = 0;
        var probe = Probe((_, _, _) => Interlocked.Increment(ref attempt) switch
        {
            1 => Task.FromResult(new ProcessResult(1, "", "Not logged in")),
            2 => ArtificialBusyHostTimeout(),
            _ => Task.FromResult(new ProcessResult(1, "", "Not logged in")),
        });

        var firstLogout = await probe.RefreshAsync("claude", CancellationToken.None);
        var indeterminate = await probe.RefreshAsync("claude", CancellationToken.None);
        var restartedStreak = await probe.RefreshAsync("claude", CancellationToken.None);
        var confirmedLogout = await probe.RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, firstLogout.Status);
        Assert.Equal(ProviderAuthProbe.Ready, indeterminate.Status);
        Assert.Equal(ProviderAuthProbe.Ready, restartedStreak.Status);
        Assert.Equal(ProviderAuthProbe.Unavailable, confirmedLogout.Status);
    }

    [Fact]
    public async Task A_live_session_is_the_only_thing_that_earns_ready()
    {
        var status = await Probe(Answers(0, "Logged in as Agent Studio (subscription)"))
            .RefreshAsync("codex", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, status.Status);
        Assert.True(status.IsReady);
        Assert.Contains("codex login status", status.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("unverified", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Linux_auth_probe_launch_is_niced_and_keeps_the_exact_cli_argv()
    {
        var command = ProviderAuthProbe.LowerPriorityLaunch(
            "/opt/claude/bin/claude",
            ["auth", "status", "--text"]);

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal("nice", command.FileName);
            Assert.Equal(
                ["-n", "10", "--", "/opt/claude/bin/claude", "auth", "status", "--text"],
                command.Arguments);
        }
        else
        {
            Assert.Equal("/opt/claude/bin/claude", command.FileName);
            Assert.Equal(["auth", "status", "--text"], command.Arguments);
        }
    }

    [Fact]
    public void Default_timeout_allows_busy_node_cli_startup()
        => Assert.Equal(TimeSpan.FromSeconds(30), ProviderAuthProbe.DefaultTimeout);

    [Fact]
    public async Task A_missing_binary_is_unavailable_and_nothing_is_launched()
    {
        var launched = false;
        var probe = Probe(
            (_, _, _) => { launched = true; return Task.FromResult(new ProcessResult(0, "", "")); },
            binaryExists: false);

        var status = await probe.RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Unavailable, status.Status);
        Assert.Contains("was not found", status.Detail, StringComparison.Ordinal);
        Assert.False(launched);
    }

    [Fact]
    public async Task An_indeterminate_busy_probe_keeps_bootstrap_ready_and_logs_degradation()
    {
        var log = new List<string>();
        var probe = Probe(
            (_, _, _) => ArtificialBusyHostTimeout(),
            timeout: TimeSpan.FromMilliseconds(50),
            log: log.Add);

        var status = await probe.RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, status.Status);
        Assert.Contains(log, line =>
            line.Contains("probe-degraded", StringComparison.Ordinal)
            && line.Contains("did not answer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_launcher_that_throws_is_indeterminate_and_keeps_presence_ready()
    {
        var log = new List<string>();
        var probe = Probe(
            (_, _, _) => throw new InvalidOperationException("spawn refused by the host"),
            log: log.Add);

        var status = await probe.RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, status.Status);
        Assert.Contains(log, line =>
            line.Contains("could not be started", StringComparison.Ordinal)
            && line.Contains("spawn refused by the host", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unsupported_status_subcommand_stays_ready_and_admits_it_proved_nothing()
    {
        // A CLI version that renamed the subcommand must not drain the host: an
        // argument-parser rejection is "could not ask", not "the login is gone".
        var status = await Probe(Answers(2, "", "error: unrecognized subcommand 'auth'\nUsage: claude [OPTIONS]"))
            .RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, status.Status);
        Assert.Contains("unverified", status.Detail, StringComparison.Ordinal);
        Assert.Contains(ProviderAuthProbe.ConceptPath, status.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Empty_output_is_indeterminate_whatever_the_exit_code(int exitCode)
    {
        var log = new List<string>();
        var probe = Probe(Answers(exitCode), log: log.Add);

        var status = await probe.RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, status.Status);
        Assert.Contains(log, line =>
            line.Contains("probe-degraded", StringComparison.Ordinal)
            && line.Contains("empty output", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_provider_keeps_the_presence_check_instead_of_guessing_a_command()
    {
        var launched = false;
        var probe = Probe((_, _, _) => { launched = true; return Task.FromResult(new ProcessResult(0, "", "")); });

        var status = await probe.RefreshAsync("agent-wrapper.sh", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, status.Status);
        Assert.Contains("no auth status command is known", status.Detail, StringComparison.Ordinal);
        Assert.False(launched);
        Assert.Null(ProviderAuthProbe.AuthStatusArguments("agent-wrapper"));
    }

    [Fact]
    public void Without_a_wired_launcher_the_status_degrades_to_the_path_check_and_says_so()
    {
        var present = new ProviderAuthProbe(launcher: null, executableExists: _ => true).Current("claude");
        Assert.Equal(ProviderAuthProbe.Ready, present.Status);
        Assert.Contains("no auth probe is wired", present.Detail, StringComparison.Ordinal);
        Assert.Contains(ProviderAuthProbe.ConceptPath, present.Detail, StringComparison.Ordinal);

        var absent = new ProviderAuthProbe(launcher: null, executableExists: _ => false).Current("claude");
        Assert.Equal(ProviderAuthProbe.Unavailable, absent.Status);
    }

    [Fact]
    public async Task The_verdict_is_cached_for_the_ttl_and_refreshed_behind_the_advertisement()
    {
        var calls = 0;
        var now = new DateTimeOffset(2026, 7, 28, 8, 0, 0, TimeSpan.Zero);
        var probe = Probe(
            (_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new ProcessResult(0, "Logged in", ""));
            },
            clock: () => now,
            ttl: TimeSpan.FromMinutes(5));

        await probe.RefreshAsync("claude", CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref calls));

        // Four minutes and a dozen advertisements later: still one child process.
        now = now.AddMinutes(4);
        for (var i = 0; i < 12; i++) Assert.Equal(ProviderAuthProbe.Ready, probe.Current("claude").Status);
        Assert.Equal(1, Volatile.Read(ref calls));

        // Past the TTL the stale verdict is still served, and the refresh happens
        // behind it - the caller is never blocked on the CLI.
        now = now.AddMinutes(2);
        Assert.Equal(ProviderAuthProbe.Ready, probe.Current("claude").Status);
        await WaitUntil(() => Volatile.Read(ref calls) == 2);
    }

    [Fact]
    public async Task Busy_timeout_does_not_flip_last_good_and_a_later_success_recovers_without_restart()
    {
        var attempt = 0;
        var log = new List<string>();
        var probe = Probe(
            (_, _, _) => Interlocked.Increment(ref attempt) switch
            {
                1 => Task.FromResult(new ProcessResult(0, "Login method: Claude Max account", "")),
                2 => ArtificialBusyHostTimeout(),
                _ => Task.FromResult(new ProcessResult(0, "Login method: Claude Max account", "")),
            },
            timeout: TimeSpan.FromMilliseconds(50),
            log: log.Add);

        var initial = await probe.RefreshAsync("claude", CancellationToken.None);
        var underLoad = await probe.RefreshAsync("claude", CancellationToken.None);
        var recovered = await probe.RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Ready, initial.Status);
        Assert.Equal(initial, underLoad);
        Assert.Equal(ProviderAuthProbe.Ready, recovered.Status);
        Assert.Contains(log, line => line.Contains("probe-degraded", StringComparison.Ordinal));
        Assert.Equal(3, Volatile.Read(ref attempt));
    }

    [Fact]
    public async Task Periodic_refresh_clears_a_confirmed_logout_without_restarting_the_daemon()
    {
        var now = new DateTimeOffset(2026, 8, 14, 8, 0, 0, TimeSpan.Zero);
        var loggedIn = false;
        var probe = Probe(
            (_, _, _) => Task.FromResult(
                loggedIn
                    ? new ProcessResult(0, "Login method: Claude Max account", "")
                    : new ProcessResult(1, "", "Not logged in")),
            clock: () => now,
            ttl: TimeSpan.FromMinutes(5));

        await probe.RefreshAsync("claude", CancellationToken.None);
        var unavailable = await probe.RefreshAsync("claude", CancellationToken.None);
        loggedIn = true;
        now = now.AddMinutes(6);

        var staleAdvertisement = probe.Current("claude");
        await WaitUntil(() => probe.Current("claude").Status == ProviderAuthProbe.Ready);
        var recovered = probe.Current("claude");

        Assert.Equal(ProviderAuthProbe.Unavailable, unavailable.Status);
        Assert.Equal(ProviderAuthProbe.Unavailable, staleAdvertisement.Status);
        Assert.Equal(ProviderAuthProbe.Ready, recovered.Status);
    }

    [Fact]
    public async Task The_advertisement_carries_the_observed_status_and_detail()
    {
        var probe = Probe(Answers(1, "", "Not logged in"));
        var options = CodingOptions();

        await probe.RefreshAsync(options.CliBin, CancellationToken.None);
        await probe.RefreshAsync(options.CliBin, CancellationToken.None);
        var advertised = RunnerCapabilityProbe.Advertise(options, gitPushReady: true, providerAuth: probe);

        var auth = Assert.Single(
            advertised,
            item => item.Key == CapabilityProtocol.ProviderAuthentication("claude"));
        Assert.Equal(ProviderAuthProbe.Unavailable, auth.Status);
        Assert.Contains("no usable session", auth.Detail!, StringComparison.Ordinal);
        // The status only bites because the capability stays a claim requirement:
        // the task server admits a claim while every required key reads "ready".
        Assert.Contains(
            CapabilityProtocol.ProviderAuthentication("claude"),
            RunnerCapabilityProbe.CodingRequirements(options));
    }

    [Fact]
    public async Task The_detail_never_carries_a_token_shaped_string()
    {
        var probe = Probe(Answers(1, "", "invalid api key sk-ant-api03-AAAABBBBCCCCDDDDEEEEFFFF"));
        await probe.RefreshAsync("claude", CancellationToken.None);
        var status = await probe.RefreshAsync("claude", CancellationToken.None);

        Assert.Equal(ProviderAuthProbe.Unavailable, status.Status);
        Assert.Contains("[redacted]", status.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant-api03", status.Detail, StringComparison.Ordinal);
    }

    private static RunnerOptions CodingOptions() => new()
    {
        ServerUrl = "http://task-server",
        RunnerId = "runner-test",
        RunnerName = "runner-test",
        Hostname = "test-host",
        BackendName = "test",
        GitRemote = "https://github.com/example/repo.git",
        WorkDir = Path.GetTempPath(),
        BaseBranch = "main",
        CliBin = "claude",
        CliArgs = "",
    };

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The background refresh did not run within 5s.");
            await Task.Delay(10);
        }
    }

    private static Task<ProcessResult> ArtificialBusyHostTimeout()
        => Task.FromCanceled<ProcessResult>(new CancellationToken(canceled: true));
}
