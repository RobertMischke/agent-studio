using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunnerReleaseIdentityTests
{
    [Fact]
    public void Current_symlink_target_name_is_the_advertised_release()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-host-release-{Guid.NewGuid():N}");
        var releaseId = "agt-2650b-20260812T064049Z-ca5cbd6ff";
        var release = Path.Combine(root, "releases", releaseId);
        var current = Path.Combine(root, "current");
        try
        {
            Directory.CreateDirectory(release);
            Directory.CreateSymbolicLink(current, release);

            Assert.Equal(releaseId, RunnerReleaseIdentity.Resolve(
                current + Path.DirectorySeparatorChar, configured: ""));
            Assert.Equal(releaseId, RunnerReleaseIdentity.Resolve(
                release + Path.DirectorySeparatorChar, configured: ""));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Explicit_release_identity_wins_for_non_symlink_packages()
    {
        Assert.Equal(
            "release-explicit",
            RunnerReleaseIdentity.Resolve(AppContext.BaseDirectory, " release-explicit "));
    }
}
