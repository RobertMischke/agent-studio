using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunnerCapabilityProbeTests
{
    [Theory]
    [InlineData(1, "", "HTTP 401 Missing bearer authentication", true)]
    [InlineData(1, "", "login required", true)]
    [InlineData(1, "", "ordinary product failure", false)]
    [InlineData(0, "", "HTTP 401 in historical output", false)]
    public void Provider_authentication_failure_requires_a_nonzero_typed_signal(
        int exitCode,
        string stdout,
        string stderr,
        bool expected)
        => Assert.Equal(
            expected,
            RunnerCapabilityProbe.IsProviderAuthenticationFailure(
                new ProcessResult(exitCode, stdout, stderr)));

    [Theory]
    [InlineData("/usr/local/bin/codex", "codex")]
    [InlineData("claude.exe", "claude")]
    public void Provider_identity_is_stable_across_binary_paths(string binary, string expected)
        => Assert.Equal(expected, RunnerCapabilityProbe.Provider(binary));
}
