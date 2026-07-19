using AgentStudio.Shared;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Unit coverage for <see cref="WatchPathComparison"/> — the canonical,
/// OS-aware equality that closes the AGT-1940 watch-path addressing bugs.
/// The service-level regressions (POST 409, PUT/DELETE 404, wrong-project
/// filter) live in <see cref="WatchPathAddressingRegressionTests"/>; this
/// file pins the primitive those fixes stand on.
/// </summary>
public class WatchPathComparisonTests
{
    [Fact]
    public void PathsEqual_TrailingSeparator_Matches()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "wp-" + Guid.NewGuid().ToString("N"), "projects", "demo");
        Assert.True(WatchPathComparison.PathsEqual(baseDir, baseDir + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void PathsEqual_ForwardVsBackSlash_Matches()
    {
        // A client posting the same directory with '/' must resolve the same
        // entry the scanner stamped with the OS separator. Path.GetFullPath
        // collapses both to the platform form before the compare.
        var root = Path.Combine(Path.GetTempPath(), "wp-" + Guid.NewGuid().ToString("N"));
        var withOsSep = Path.Combine(root, "projects", "demo");
        var withForwardSep = $"{root}/projects/demo".Replace('\\', '/');
        Assert.True(WatchPathComparison.PathsEqual(withOsSep, withForwardSep));
    }

    [Fact]
    public void PathsEqual_DifferentDirectories_DoNotMatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "wp-" + Guid.NewGuid().ToString("N"));
        Assert.False(WatchPathComparison.PathsEqual(
            Path.Combine(root, "projects", "alpha"),
            Path.Combine(root, "projects", "beta")));
    }

    [Fact]
    public void PathsEqual_BlankOperands()
    {
        Assert.True(WatchPathComparison.PathsEqual(null, ""));
        Assert.True(WatchPathComparison.PathsEqual("   ", null));
        Assert.False(WatchPathComparison.PathsEqual(null, Path.Combine(Path.GetTempPath(), "x")));
        Assert.False(WatchPathComparison.PathsEqual(Path.Combine(Path.GetTempPath(), "x"), ""));
    }

    [Fact]
    public void PathsEqual_CaseFollowsOs()
    {
        var root = Path.Combine(Path.GetTempPath(), "wp-" + Guid.NewGuid().ToString("N"));
        var lower = Path.Combine(root, "projects", "demo");
        var upper = Path.Combine(root, "projects", "DEMO");

        // Windows filesystems are case-insensitive → the two spellings address
        // the same directory. Linux/macOS are case-sensitive → they are two
        // different directories, which is exactly why the old OrdinalIgnoreCase
        // compare returned the WRONG project on Linux.
        Assert.Equal(OperatingSystem.IsWindows(), WatchPathComparison.PathsEqual(lower, upper));
    }
}
