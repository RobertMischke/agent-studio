using AgentStudio.Cli;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct policy-matrix test for <see cref="NpmShimHealer.ShouldAttemptNpmInstallFallback"/> -
/// no filesystem, process, clock, or DI setup, per the repo's "pure policy
/// first" convention (docs/quality/dotnet-backend.md). The stateful
/// <c>File.Exists</c> check that produces <c>shimExists</c> lives in
/// <see cref="NpmShimHealer.TryHealClaudeAsync"/>; this test only covers the
/// decision itself.
/// </summary>
public sealed class NpmShimHealerFallbackPolicyTests
{
    [Fact]
    public void ShimMissing_PackagePresent_Attempts()
    {
        Assert.True(NpmShimHealer.ShouldAttemptNpmInstallFallback(shimExists: false, packagePresent: true));
    }

    [Fact]
    public void ShimMissing_PackageAbsent_DoesNotAttempt()
    {
        // A truly-uninstalled CLI is an operator decision, not a self-heal target.
        Assert.False(NpmShimHealer.ShouldAttemptNpmInstallFallback(shimExists: false, packagePresent: false));
    }

    [Fact]
    public void ShimPresent_PackagePresent_DoesNotAttempt()
    {
        Assert.False(NpmShimHealer.ShouldAttemptNpmInstallFallback(shimExists: true, packagePresent: true));
    }

    [Fact]
    public void ShimPresent_PackageAbsent_DoesNotAttempt()
    {
        Assert.False(NpmShimHealer.ShouldAttemptNpmInstallFallback(shimExists: true, packagePresent: false));
    }
}
