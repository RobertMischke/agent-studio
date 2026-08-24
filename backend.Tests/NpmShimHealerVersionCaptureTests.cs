using AgentStudio.Cli;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers <see cref="NpmShimHealer.ReadInstalledVersion"/> in isolation -
/// the one piece of the healer's Windows-only repair pass with no Windows
/// dependency, since it is a plain <c>package.json</c> read. This is the
/// root-cause evidence mechanism: it must return the on-disk version even
/// when the CLI binary itself is currently broken, which is the whole
/// reason <see cref="NpmShimHealer.TryHealClaudeAsync"/> fires in the first
/// place. Exercised directly here because the rest of
/// <see cref="NpmShimHealer.TryHealClaudeAsync"/> short-circuits on
/// non-Windows hosts and cannot be driven end-to-end in this environment.
/// </summary>
public sealed class NpmShimHealerVersionCaptureTests : IDisposable
{
    private readonly string _wrapDir;

    public NpmShimHealerVersionCaptureTests()
    {
        _wrapDir = Path.Combine(Path.GetTempPath(), "atp-npm-shim-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wrapDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_wrapDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ReadsVersionFromPackageJson()
    {
        File.WriteAllText(Path.Combine(_wrapDir, "package.json"), """{"name":"@anthropic-ai/claude-code","version":"2.1.234"}""");

        var version = NpmShimHealer.ReadInstalledVersion(_wrapDir, NullLogger.Instance);

        Assert.Equal("2.1.234", version);
    }

    [Fact]
    public void MissingPackageJson_ReturnsNull_NoThrow()
    {
        var version = NpmShimHealer.ReadInstalledVersion(_wrapDir, NullLogger.Instance);
        Assert.Null(version);
    }

    [Fact]
    public void MalformedPackageJson_ReturnsNull_NoThrow()
    {
        File.WriteAllText(Path.Combine(_wrapDir, "package.json"), "{not valid json");

        var version = NpmShimHealer.ReadInstalledVersion(_wrapDir, NullLogger.Instance);

        Assert.Null(version);
    }

    [Fact]
    public void PackageJsonWithoutVersionField_ReturnsNull()
    {
        File.WriteAllText(Path.Combine(_wrapDir, "package.json"), """{"name":"@anthropic-ai/claude-code"}""");

        var version = NpmShimHealer.ReadInstalledVersion(_wrapDir, NullLogger.Instance);

        Assert.Null(version);
    }

    [Fact]
    public void ReadDoesNotRequireTheBinaryToWork()
    {
        // The whole point: this simulates the exact broken-shim shape from
        // the recurring host defect (package present, executable missing/
        // broken) and proves the version read still succeeds - unlike a
        // `claude --version` probe, which would fail here by construction.
        File.WriteAllText(Path.Combine(_wrapDir, "package.json"), """{"version":"2.1.231"}""");
        // No bin/claude.exe at all - the broken-shim shape.

        var version = NpmShimHealer.ReadInstalledVersion(_wrapDir, NullLogger.Instance);

        Assert.Equal("2.1.231", version);
    }
}
