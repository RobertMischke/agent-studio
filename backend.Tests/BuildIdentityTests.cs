using System.Reflection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BuildIdentityTests
{
    [Fact]
    public void MissingManifest_ProducesExplicitLegacyDirtyIdentityWithoutTimestampFreshness()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Release:BuildManifestPath"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.json")
        }).Build();

        var identity = BuildIdentity.Load(config, Assembly.GetExecutingAssembly());

        Assert.True(identity.Legacy);
        Assert.True(identity.Dirty);
        Assert.Equal("untagged", identity.Tag);
        Assert.Null(identity.BuiltAt);
    }
}
