using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// The 2026-08-23 acceptance scenario, expressed as behaviour rather than
/// implementation: a simulated session-limit response must PAUSE the offending
/// CLI, must not escalate anything, must leave a mixed host's other CLIs
/// claiming, and must RESUME on its own when the window resets.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ProviderLimitGateTests
{
    private static readonly DateTimeOffset Night = new(2026, 8, 23, 22, 0, 0, TimeSpan.Zero);

    /// <summary>The exact line the operator saw when the fleet went dark.</summary>
    private const string SessionLimitReply =
        "You've hit your session limit · resets 12:20am";

    private static ProviderLimitSignal Signal(string text, DateTimeOffset now)
        => ProviderLimitDetector.Detect(text, now);

    /// <summary>A Claude <c>rate_limit_event</c> frame that rejects the request.</summary>
    private static string RejectedFrame(DateTimeOffset resetAt, string window = "five_hour")
        => "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{"
           + $"\"rate_limit_type\":\"{window}\",\"status\":\"rejected\","
           + $"\"resets_at\":\"{resetAt:o}\"}}}}";

    // ---- PAUSE ---------------------------------------------------------

    [Fact]
    public void An_account_limit_arms_a_hold_for_that_cli()
    {
        var now = Night;
        var gate = new ProviderLimitGate(clock: () => now);

        var hold = gate.Record("claude", Signal(SessionLimitReply, now));

        Assert.NotNull(hold);
        Assert.Equal("claude", hold!.CliType);
        Assert.True(gate.IsLimited("claude"));
        // The provider stated a wall-clock reset with no zone, so the gate falls
        // back to its own bounded pause and says so.
        Assert.False(hold.ResetWasStated);
        Assert.Equal(now + ProviderLimitPolicy.DefaultPause, hold.LimitedUntil);
        Assert.Contains("estimated", hold.Describe());
    }

    [Fact]
    public void A_provider_stated_reset_is_used_verbatim_plus_grace()
    {
        var now = Night;
        var gate = new ProviderLimitGate(clock: () => now);
        var resetAt = new DateTimeOffset(2026, 8, 24, 0, 20, 0, TimeSpan.Zero);

        var hold = gate.Record(
            "claude",
            Signal(RejectedFrame(resetAt), now));

        Assert.NotNull(hold);
        Assert.True(hold!.ResetWasStated);
        Assert.Equal(resetAt + ProviderLimitPolicy.ResetGrace, hold.LimitedUntil);
        Assert.DoesNotContain("estimated", hold.Describe());
    }

    [Fact]
    public void A_per_request_throttle_never_pauses_the_cli()
    {
        // Pausing a whole CLI for one slow request would trade the escalation
        // storm for an idle fleet.
        var now = Night;
        var gate = new ProviderLimitGate(clock: () => now);

        Assert.Null(gate.Record("claude", Signal("Error: 429 too many requests", now)));
        Assert.False(gate.IsLimited("claude"));
    }

    [Fact]
    public void A_repeated_rejection_inside_a_hold_never_shortens_it()
    {
        // The provider is still saying no. Letting a later, shorter default
        // overwrite a longer stated reset would resume the fleet early and
        // restart the storm.
        var now = Night;
        var gate = new ProviderLimitGate(clock: () => now);
        var farReset = now.AddHours(4);

        var first = gate.Record(
            "claude",
            Signal(RejectedFrame(farReset), now));
        var second = gate.Record("claude", Signal(SessionLimitReply, now));

        Assert.Equal(first!.LimitedUntil, second!.LimitedUntil);
        Assert.Equal(farReset + ProviderLimitPolicy.ResetGrace, second.LimitedUntil);
    }

    // ---- RESUME --------------------------------------------------------

    [Fact]
    public void The_hold_lifts_itself_when_the_window_resets()
    {
        // The 09:18 operator handshake that ended the incident is exactly what
        // this removes: recovery is a clock, not a person.
        var now = Night;
        var gate = new ProviderLimitGate(clock: () => now);
        gate.Record("claude", Signal(SessionLimitReply, now));
        Assert.True(gate.IsLimited("claude"));

        now = Night + ProviderLimitPolicy.DefaultPause + TimeSpan.FromSeconds(1);

        Assert.False(gate.IsLimited("claude"));
        Assert.Empty(gate.Active());
    }

    [Fact]
    public void An_operator_may_lift_a_hold_early()
    {
        var now = Night;
        var gate = new ProviderLimitGate(clock: () => now);
        gate.Record("claude", Signal(SessionLimitReply, now));

        Assert.True(gate.Clear("claude"));
        Assert.False(gate.IsLimited("claude"));
        Assert.False(gate.Clear("claude"));
    }

    // ---- DURABILITY ----------------------------------------------------

    [Fact]
    public void A_hold_survives_a_daemon_restart()
    {
        // A restart mid-outage must not forget the pause and walk straight back
        // into the storm.
        using var temp = new TempDirectory();
        var now = Night;
        var first = new ProviderLimitGate(temp.Path, () => now);
        first.Record("claude", Signal(SessionLimitReply, now));

        var restarted = new ProviderLimitGate(temp.Path, () => now);

        Assert.True(restarted.IsLimited("claude"));
        Assert.Equal("claude", Assert.Single(restarted.Active()).CliType);
    }

    [Fact]
    public void An_expired_hold_is_not_restored_after_a_restart()
    {
        using var temp = new TempDirectory();
        var now = Night;
        new ProviderLimitGate(temp.Path, () => now)
            .Record("claude", Signal(SessionLimitReply, now));

        var later = Night.AddHours(6);
        var restarted = new ProviderLimitGate(temp.Path, () => later);

        Assert.False(restarted.IsLimited("claude"));
    }

    [Fact]
    public void A_corrupt_gate_file_does_not_stop_the_daemon()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, ProviderLimitGate.FileName), "{not json");

        var gate = new ProviderLimitGate(temp.Path, () => Night);

        Assert.Empty(gate.Active());
    }

    // ---- MIXED FLEET ---------------------------------------------------

    [Fact]
    public async Task A_limited_claude_withdraws_only_the_claude_capabilities()
    {
        using var temp = new TempDirectory();
        var (options, probe, claude, codex) = await MixedHostAsync(temp);
        var now = Night;
        var gate = new ProviderLimitGate(clock: () => now);
        gate.Record("claude", Signal(SessionLimitReply, now));

        var advertised = RunnerCapabilityProbe.Advertise(
            options,
            gitPushReady: true,
            providerAuth: probe,
            providerLimits: gate);

        // Claude is withdrawn: admission requires exactly "ready".
        Assert.Equal(
            CapabilityAdvertisedStatuses.Limited,
            Status(advertised, CapabilityProtocol.CliExecution("claude")));
        Assert.Equal(
            CapabilityAdvertisedStatuses.Limited,
            Status(advertised, CapabilityProtocol.ProviderAuthentication("claude")));

        // Codex is untouched, so codex cards keep running on the same host.
        Assert.Equal(
            CapabilityAdvertisedStatuses.Ready,
            Status(advertised, CapabilityProtocol.CliExecution("codex")));
        Assert.Equal(
            ProviderAuthProbe.Ready,
            Status(advertised, CapabilityProtocol.ProviderAuthentication("codex")));

        Assert.False(
            RunnerCapabilityProbe.AllCodingClisLimited(options, gate),
            "A host with a healthy codex must keep claiming.");
        _ = claude;
        _ = codex;
    }

    [Fact]
    public async Task The_advertised_detail_names_the_limit_and_its_reset()
    {
        using var temp = new TempDirectory();
        var (options, probe, _, _) = await MixedHostAsync(temp);
        var now = Night;
        var resetAt = new DateTimeOffset(2026, 8, 24, 0, 20, 0, TimeSpan.Zero);
        var gate = new ProviderLimitGate(clock: () => now);
        gate.Record(
            "claude",
            Signal(RejectedFrame(resetAt), now));

        var advertised = RunnerCapabilityProbe.Advertise(
            options,
            gitPushReady: true,
            providerAuth: probe,
            providerLimits: gate);

        var detail = Assert.Single(
            advertised,
            item => item.Key == CapabilityProtocol.CliExecution("claude")).Detail;
        Assert.Contains("claude: limited until 00:21 UTC", detail);
        Assert.Contains("five_hour", detail);
    }

    [Fact]
    public async Task A_genuine_logout_stays_diagnosable_behind_a_limit()
    {
        // A parked account and a dead credential need different operator
        // actions, so a limit must not overwrite an "unavailable" auth verdict.
        using var temp = new TempDirectory();
        var claude = Path.Combine(temp.Path, "claude");
        await File.WriteAllTextAsync(claude, "");
        var options = HostOptions(temp, claude, claudeBin: claude, codexBin: "");
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(new ProcessResult(1, "", "Not logged in")),
            File.Exists);
        await probe.RefreshAsync(claude, CancellationToken.None);
        await probe.RefreshAsync(claude, CancellationToken.None);
        var now = Night;
        var gate = new ProviderLimitGate(clock: () => now);
        gate.Record("claude", Signal(SessionLimitReply, now));

        var advertised = RunnerCapabilityProbe.Advertise(
            options,
            gitPushReady: true,
            providerAuth: probe,
            providerLimits: gate);

        Assert.Equal(
            ProviderAuthProbe.Unavailable,
            Status(advertised, CapabilityProtocol.ProviderAuthentication("claude")));
    }

    [Fact]
    public async Task A_single_cli_host_stops_claiming_while_limited_and_resumes_after()
    {
        using var temp = new TempDirectory();
        var claude = Path.Combine(temp.Path, "claude");
        await File.WriteAllTextAsync(claude, "");
        var options = HostOptions(temp, claude, claudeBin: claude, codexBin: "");
        var now = Night;
        var gate = new ProviderLimitGate(clock: () => now);

        Assert.False(RunnerCapabilityProbe.AllCodingClisLimited(options, gate));

        gate.Record("claude", Signal(SessionLimitReply, now));
        Assert.True(RunnerCapabilityProbe.AllCodingClisLimited(options, gate));
        Assert.Equal(
            now + ProviderLimitPolicy.DefaultPause,
            RunnerCapabilityProbe.EarliestLimitReset(options, gate));

        now = Night + ProviderLimitPolicy.DefaultPause + TimeSpan.FromSeconds(1);
        Assert.False(RunnerCapabilityProbe.AllCodingClisLimited(options, gate));
        Assert.Null(RunnerCapabilityProbe.EarliestLimitReset(options, gate));
    }

    // ---- NO ESCALATION -------------------------------------------------

    [Fact]
    public void A_provider_limited_run_returns_the_card_to_ready_not_to_review()
    {
        // The lane is the whole point: "5-human-review" here is the escalation
        // storm, "2-ready" is a card waiting to be claimed again.
        var outcome = new RunOutcome(RunOutcomeKind.ProviderLimited, "claude: limited until 00:20 UTC");

        Assert.Equal("2-ready", outcome.TargetState);
        Assert.NotEqual(new RunOutcome(RunOutcomeKind.Unknown, null).TargetState, outcome.TargetState);
    }

    // ---- helpers -------------------------------------------------------

    private static string? Status(IReadOnlyList<AdvertisedCapabilityDto> advertised, string key)
        => Assert.Single(advertised, item => item.Key == key).Status;

    private static RunnerOptions HostOptions(
        TempDirectory temp,
        string cliBin,
        string claudeBin,
        string codexBin)
        => new()
        {
            ServerUrl = "http://task-server",
            RunnerId = "runner-test",
            RunnerName = "runner-test",
            Hostname = "test-host",
            BackendName = "test",
            GitRemote = "https://github.com/example/repo.git",
            WorkDir = temp.Path,
            BaseBranch = "main",
            CliBin = cliBin,
            ClaudeCliBin = claudeBin,
            CodexCliBin = codexBin,
            CliArgs = "",
        };

    private static async Task<(RunnerOptions Options, ProviderAuthProbe Probe, string Claude, string Codex)>
        MixedHostAsync(TempDirectory temp)
    {
        var claude = Path.Combine(temp.Path, "claude");
        var codex = Path.Combine(temp.Path, "codex");
        await File.WriteAllTextAsync(claude, "");
        await File.WriteAllTextAsync(codex, "");
        var options = HostOptions(temp, codex, claude, codex);
        var probe = new ProviderAuthProbe(
            (_, _, _) => Task.FromResult(new ProcessResult(0, "Logged in", "")),
            File.Exists);
        await probe.RefreshAsync(claude, CancellationToken.None);
        await probe.RefreshAsync(codex, CancellationToken.None);
        return (options, probe, claude, codex);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"provider-limit-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
