extern alias UpdSvc;
using Microsoft.Extensions.Logging.Abstractions;
using UpdSvc::AgentTaskboard.UpdateService;
using Xunit;

namespace AgentStudio.Tests;

public sealed class UpdateVersionTruthTests
{
    [Fact]
    public void RuntimeIdentityAndBranchDeltasRemainDistinct()
    {
        var history = Path.Combine(Path.GetTempPath(), $"update-history-{Guid.NewGuid():N}.jsonl");
        var store = new UpdateStatusStore(history, "checkout1", () => "legacy-static-version",
            NullLogger<UpdateStatusStore>.Instance);
        var deployedAt = new DateTime(2026, 7, 10, 10, 0, 0, DateTimeKind.Utc);
        var runtime = new RuntimeVersion("2026.07.10-1000+a1b2c3d", "a1b2c3d", deployedAt);
        var topology = new VersionTopology(
            new BranchVersion("main", "d4e5f6a", deployedAt.AddDays(1), 0, 3),
            new BranchVersion("develop", "f7a8b9c", deployedAt.AddDays(1), 0, 7));

        store.SetVersionTopology(runtime, topology);
        var status = store.Get();

        Assert.Equal(runtime, status.RunningVersion);
        Assert.Equal("2026.07.10-1000+a1b2c3d", status.ProductVersion);
        Assert.Equal(3, status.MainVersion!.BehindBy);
        Assert.Equal(7, status.DevelopVersion!.BehindBy);
        Assert.NotEqual(status.RunningVersion.Commit, status.MainVersion.Commit);
    }
}
