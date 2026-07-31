using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class ProjectUrlPortInspectorTests
{
    [Fact]
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
