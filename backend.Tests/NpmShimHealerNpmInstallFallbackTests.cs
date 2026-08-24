using AgentStudio.Cli;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers <see cref="NpmShimHealer.TryRunNpmInstallGlobalAsync"/> in
/// isolation - the AGT-2673 fallback for a shim that vanished entirely with
/// no orphan or stub left behind, where only <c>npm install -g</c> re-links
/// npm's own bin shims. Spawning and draining a process by explicit path has
/// no Windows dependency of its own (unlike the rest of
/// <see cref="NpmShimHealer"/>, which short-circuits on non-Windows hosts),
/// so these tests stand in a fake executable script for the resolved
/// <c>npm</c> executable - the resolution itself (PATH/PATHEXT search via
/// <c>GenericCliExecutionService.ResolveExecutable</c>) happens at the
/// caller, not inside this method, exactly so it can be tested this way.
/// </summary>
public sealed class NpmShimHealerNpmInstallFallbackTests : IDisposable
{
    private readonly string _dir;

    public NpmShimHealerNpmInstallFallbackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "atp-npm-install-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string WriteFakeNpm(string script)
    {
        var path = Path.Combine(_dir, "fake-npm");
        File.WriteAllText(path, script);
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    [Fact]
    public async Task UnresolvedExecutable_ReturnsFalse_NoThrow()
    {
        var missing = Path.Combine(_dir, "does-not-exist");

        var ok = await NpmShimHealer.TryRunNpmInstallGlobalAsync(missing, NullLogger.Instance, CancellationToken.None);

        Assert.False(ok);
    }

    [Fact]
    public async Task SuccessfulInstall_ReturnsTrue()
    {
        var npm = WriteFakeNpm("#!/bin/sh\nexit 0\n");

        var ok = await NpmShimHealer.TryRunNpmInstallGlobalAsync(npm, NullLogger.Instance, CancellationToken.None);

        Assert.True(ok);
    }

    [Fact]
    public async Task NonZeroExit_ReturnsFalse()
    {
        var npm = WriteFakeNpm("#!/bin/sh\necho 'npm ERR! network timeout' 1>&2\nexit 1\n");

        var ok = await NpmShimHealer.TryRunNpmInstallGlobalAsync(npm, NullLogger.Instance, CancellationToken.None);

        Assert.False(ok);
    }

    /// <summary>
    /// Regression test for the exact bug a prior AGT-2673 attempt's
    /// self-review caught: an unread <c>Process.StandardOutput</c> on a
    /// child expected to produce substantial output is a pipe-buffer
    /// deadlock risk invisible to a trivial fake. This script writes well
    /// past the typical 64 KB OS pipe buffer on both stdout and stderr
    /// before exiting; if the caller does not drain both streams
    /// concurrently with the wait - including on the success path, a second
    /// bug this same self-review pass caught - the child blocks on a full
    /// buffer and this test times out instead of completing quickly.
    /// </summary>
    [Fact]
    public async Task LargeOutputOnBothStreams_DoesNotDeadlock()
    {
        var npm = WriteFakeNpm("""
            #!/bin/sh
            i=0
            while [ $i -lt 20000 ]; do
              echo "npm info install progress line $i of the fake @anthropic-ai/claude-code download"
              echo "npm warn deprecated noisy stderr line $i" 1>&2
              i=$((i + 1))
            done
            exit 0
            """);

        var task = NpmShimHealer.TryRunNpmInstallGlobalAsync(npm, NullLogger.Instance, CancellationToken.None);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.Same(task, completed);
        Assert.True(await task);
    }

    [Fact]
    public async Task Timeout_KillsProcessAndDrainsWithoutThrowing()
    {
        var npm = WriteFakeNpm("#!/bin/sh\nsleep 300\n");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // TryRunNpmInstallGlobalAsync applies its own 3-minute internal
        // timeout; cancelling the passed-in token proves the caller-supplied
        // CancellationToken also short-circuits it rather than blocking the
        // test for the internal budget.
        var ok = await NpmShimHealer.TryRunNpmInstallGlobalAsync(npm, NullLogger.Instance, cts.Token);

        Assert.False(ok);
    }
}
