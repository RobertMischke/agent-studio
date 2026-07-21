using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// A Task Server outage is exactly when the log buffer is at risk of unbounded
/// growth: every failed flush re-queues its batch while the live run keeps
/// producing output. This pins the hard cap so a backlog sheds oldest-first
/// instead of turning a transient outage into a memory blow-up.
/// </summary>
public class LogShipperCapTests
{
    private static LogShipper NewShipper(out System.Collections.Generic.List<string> diag)
    {
        var messages = new System.Collections.Generic.List<string>();
        diag = messages;
        using var http = new HttpClient { BaseAddress = new Uri("http://task-server-unused") };
        var client = new TaskServerClient(http, "runner-under-test");
        var lease = new RunLeaseInfoDto(
            "AGT-1", "runner-under-test", "runner", "host", 1, "backend",
            "lease-1", 1, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2));
        return new LogShipper(client, "AGT-1", lease, messages.Add);
    }

    [Fact]
    public void Pending_buffer_is_capped_and_drops_oldest_when_the_backlog_grows()
    {
        var shipper = NewShipper(out _);

        for (var i = 0; i < 25_000; i++)
            shipper.Add("stdout", $"line-{i}");

        Assert.True(shipper.PendingCount <= 20_000,
            $"pending buffer exceeded its cap: {shipper.PendingCount}");
        Assert.True(shipper.DroppedCount >= 5_000,
            $"expected the overflow to be dropped, dropped {shipper.DroppedCount}");
        Assert.Equal(25_000, shipper.PendingCount + shipper.DroppedCount);
    }

    [Fact]
    public void Output_within_the_cap_is_fully_retained()
    {
        var shipper = NewShipper(out _);

        for (var i = 0; i < 100; i++)
            shipper.Add("stdout", $"line-{i}");

        Assert.Equal(100, shipper.PendingCount);
        Assert.Equal(0, shipper.DroppedCount);
    }
}
