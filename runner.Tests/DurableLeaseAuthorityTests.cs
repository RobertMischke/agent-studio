using System.Net;
using System.Text;
using System.Text.Json;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DurableLeaseAuthorityTests
{
    [Fact]
    public async Task Controlled_time_keeps_the_generation_alive_for_ten_minutes_and_stops_before_expiry()
    {
        using var temp = new TempDirectory();
        var now = new DateTime(2026, 7, 29, 8, 0, 0, DateTimeKind.Utc);
        var options = Options(temp.Path);
        var lease = Lease(now, expiresAt: now.AddMinutes(15));
        var authority = DurableLeaseAuthority.Open(
            temp.Path,
            lease.ExpiresAt,
            TimeSpan.FromMinutes(1),
            initiallyConfirmed: true,
            () => now);
        using var http = new HttpClient(new OfflineHandler())
        {
            BaseAddress = new Uri("http://localhost"),
        };
        using var client = new TaskServerClient(http, options.RunnerId);
        using var stop = new CancellationTokenSource();
        var observed = new List<DateTime>();
        var heartbeat = new LeaseHeartbeat(
            client,
            options,
            lease,
            _ => { },
            (delay, _) =>
            {
                now += delay;
                observed.Add(now);
                return Task.CompletedTask;
            },
            authority: authority,
            utcNow: () => now);

        await heartbeat.RunAsync(stop, CancellationToken.None);

        Assert.Contains(observed, value => value >= lease.AcquiredAt.AddMinutes(10));
        Assert.Equal(lease.ExpiresAt.AddMinutes(-1), authority.StopBeforeUtc);
        Assert.Equal(authority.StopBeforeUtc, now);
        Assert.True(heartbeat.LeaseLost);
        Assert.Equal("rejected", authority.Snapshot.State);
        Assert.Contains("deadline exhausted", authority.Snapshot.Detail);
    }

    [Fact]
    public async Task Reconnection_renews_fence_before_replay_is_allowed()
    {
        using var temp = new TempDirectory();
        var now = new DateTime(2026, 7, 29, 9, 0, 0, DateTimeKind.Utc);
        var options = Options(temp.Path);
        var lease = Lease(now, now.AddMinutes(15));
        var renewed = lease with { ExpiresAt = now.AddMinutes(16) };
        var authority = DurableLeaseAuthority.Open(
            temp.Path,
            lease.ExpiresAt,
            TimeSpan.FromMinutes(1),
            initiallyConfirmed: true,
            () => now);
        using var stop = new CancellationTokenSource();
        using var http = new HttpClient(new FailThenRenewHandler(renewed))
        {
            BaseAddress = new Uri("http://localhost"),
        };
        using var client = new TaskServerClient(http, options.RunnerId);
        var replayAllowedDuringBackoff = true;
        var delayCalls = 0;
        var heartbeat = new LeaseHeartbeat(
            client,
            options,
            lease,
            _ => { },
            (delay, _) =>
            {
                if (delayCalls++ == 0)
                    replayAllowedDuringBackoff = authority.ReplayAllowed;
                else
                    stop.Cancel();
                now += delay;
                return Task.CompletedTask;
            },
            authority: authority,
            utcNow: () => now);

        await heartbeat.RunAsync(stop, CancellationToken.None);

        Assert.False(replayAllowedDuringBackoff);
        Assert.True(authority.ReplayAllowed);
        Assert.Equal("confirmed", authority.Snapshot.State);
        Assert.Equal(
            "fenced lease renewal reconciled before report replay",
            authority.Snapshot.Detail);
        Assert.Equal(renewed.ExpiresAt.AddMinutes(-1), authority.StopBeforeUtc);
    }

    [Fact]
    public void Stop_before_and_uncertain_replay_state_survive_runner_restart()
    {
        using var temp = new TempDirectory();
        var now = new DateTime(2026, 7, 29, 10, 0, 0, DateTimeKind.Utc);
        var expires = now.AddMinutes(15);
        var authority = DurableLeaseAuthority.Open(
            temp.Path,
            expires,
            TimeSpan.FromSeconds(30),
            initiallyConfirmed: true,
            () => now);
        authority.MarkUncertain("Task Server partitioned");

        var restarted = DurableLeaseAuthority.Open(
            temp.Path,
            expires.AddHours(1),
            TimeSpan.FromSeconds(30),
            initiallyConfirmed: false,
            () => now.AddMinutes(1));

        Assert.False(restarted.ReplayAllowed);
        Assert.Equal(expires.AddSeconds(-30), restarted.StopBeforeUtc);
        Assert.Equal(
            restarted.Snapshot,
            DurableLeaseAuthority.Read(temp.Path));
    }

    [Fact]
    public void A_new_live_claim_replaces_stale_authority_in_a_reused_worker_directory()
    {
        using var temp = new TempDirectory();
        var now = new DateTime(2026, 7, 29, 11, 0, 0, DateTimeKind.Utc);
        var oldExpiry = now.AddMinutes(2);
        var newExpiry = now.AddMinutes(15);
        var old = DurableLeaseAuthority.Open(
            temp.Path,
            oldExpiry,
            TimeSpan.FromSeconds(30),
            initiallyConfirmed: true,
            () => now);
        old.MarkUncertain("old attempt lost transport");

        var current = DurableLeaseAuthority.Open(
            temp.Path,
            newExpiry,
            TimeSpan.FromSeconds(30),
            initiallyConfirmed: true,
            () => now.AddSeconds(1));

        Assert.True(current.ReplayAllowed);
        Assert.Equal(newExpiry, current.Snapshot.LeaseExpiresAtUtc);
        Assert.Equal(newExpiry.AddSeconds(-30), current.StopBeforeUtc);
    }

    private static RunnerOptions Options(string root) => new()
    {
        ServerUrl = "http://localhost",
        RunnerId = "autonomy-runner",
        RunnerName = "autonomy-runner",
        Hostname = "autonomy-host",
        BackendName = "test",
        WorkDir = root,
        BaseBranch = "main",
        CliBin = "/bin/sh",
        CliArgs = "",
        TtlSeconds = 900,
        HeartbeatSeconds = 60,
    };

    private static RunLeaseInfoDto Lease(DateTime acquiredAt, DateTime expiresAt)
        => new(
            "AGT-2396",
            "autonomy-runner",
            "autonomy-runner",
            "autonomy-host",
            1234,
            "test",
            "lease-autonomy",
            17,
            acquiredAt,
            expiresAt);

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new HttpRequestException("Task Server partitioned");
    }

    private sealed class FailThenRenewHandler(
        RunLeaseInfoDto renewed) : HttpMessageHandler
    {
        private int _calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new HttpRequestException("Task Server partitioned");
            var payload = JsonSerializer.Serialize(new RunLeaseResponse(
                "Renewed",
                true,
                renewed));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "runner-authority-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
