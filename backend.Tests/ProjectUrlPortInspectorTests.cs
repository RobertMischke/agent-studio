using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectUrlPortInspectorTests
{
    [Fact]
    public void ParseNetstatListenerPid_IgnoresNonListeningConnectionsAndSimilarPorts()
    {
        const string output = """
          TCP    127.0.0.1:4200    127.0.0.1:53100    ESTABLISHED    1111
          TCP    0.0.0.0:14200     0.0.0.0:0          LISTENING      2222
          TCP    [::]:4200         [::]:0             LISTENING      9123
          """;

        var processId = ProjectUrlPortInspector.ParseNetstatListenerPid(output, 4200);

        Assert.Equal(9123, processId);
    }

    [Fact]
    public void ParseLsofPid_ReturnsTheFirstReportedListenerOwner()
    {
        Assert.Equal(9123, ProjectUrlPortInspector.ParseLsofPid("p9123\nf9\n"));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void FindListener_ReportsTheProcessOwningARealLocalPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var occupant = new ProjectUrlPortInspector().FindListener(port);

        Assert.NotNull(occupant);
        Assert.Equal(Environment.ProcessId, occupant.ProcessId);
        Assert.Equal(Process.GetCurrentProcess().ProcessName, occupant.ProcessName);
    }
}
