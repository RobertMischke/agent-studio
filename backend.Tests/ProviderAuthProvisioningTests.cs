using AgentStudio.Management;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProviderAuthProvisioningTests
{
    [Fact]
    public void CodexDeviceAuthTranscript_ExtractsUrlAndOneTimeCode()
    {
        var parsed = CodexSignInPolicy.ParseDeviceAuthTranscript([
            "To sign in using device code authentication:",
            "1. Open this URL in your browser",
            "   https://auth.openai.com/codex/device",
            "2. Enter this one-time code (expires in 15 minutes)",
            "   ABCD-EFGH",
        ]);

        Assert.Equal("https://auth.openai.com/codex/device", parsed.VerificationUrl);
        Assert.Equal("ABCD-EFGH", parsed.UserCode);
    }

    [Theory]
    [InlineData("runner;shutdown", "runner")]
    [InlineData("agent@runner", "runner/id")]
    public void CodexSignInPolicy_RejectsUnsafeHostInputs(string sshTarget, string hostId)
    {
        Assert.NotNull(CodexSignInPolicy.Validate(hostId, new CodexSignInStartRequest(sshTarget)));
    }

    [Fact]
    public void CodexSshTransport_PassesOnlyTheValidatedTargetAsData()
    {
        var startInfo = SshCodexDeviceAuthTransport.BuildStartInfo("agent@runner-01");

        Assert.Equal("ssh", startInfo.FileName);
        Assert.Contains("agent@runner-01", startInfo.ArgumentList);
        Assert.DoesNotContain(startInfo.ArgumentList, argument => argument.Contains("codex login", StringComparison.Ordinal));
        Assert.Equal(["sudo", "bash", "-s"], startInfo.ArgumentList.TakeLast(3));
    }

    [Theory]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN")]
    [InlineData("ANTHROPIC_API_KEY")]
    public void Policy_AcceptsOnlyTheTwoClaudeEnvironmentInputs(string environmentVariable)
    {
        var request = new ProviderAuthProvisioningRequest(
            "agent@runner-01",
            "agent-runner-01",
            environmentVariable,
            "sk-ant-oat01-provider-secret-fixture");

        Assert.Null(ProviderAuthProvisioningPolicy.Validate(request));
        Assert.Equal("claude", ProviderAuthProvisioningPolicy.ProviderFor(environmentVariable));
    }

    [Fact]
    public void SshTransport_KeepsSecretOutOfEveryProcessArgument()
    {
        const string secret = "sk-ant-oat01-never-on-the-command-line";
        var startInfo = SshProviderAuthProvisioner.BuildStartInfo(
            "agent@runner-01",
            "CLAUDE_CODE_OAUTH_TOKEN");
        var standardInput = SshProviderAuthProvisioner.BuildStandardInput(
            "CLAUDE_CODE_OAUTH_TOKEN",
            secret);

        Assert.Equal("ssh", startInfo.FileName);
        Assert.DoesNotContain(startInfo.ArgumentList, argument => argument.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(secret, standardInput, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secret)), standardInput);
        Assert.Contains("/etc/agent-runner/provider-auth.env", standardInput);
        Assert.Contains("getent group agent >/dev/null || groupadd --system agent", standardInput);
        Assert.Contains("install -m 0640 -o root -g agent", standardInput);
        Assert.Contains("mv -fT -- \"$provider_auth_install_tmp\" \"$provider_auth_file\"", standardInput);
        Assert.Contains("units+=(agent-host.service)", standardInput);
        Assert.Contains("units+=(agent-runner-review.service)", standardInput);
        Assert.Contains("EnvironmentFile=%s", standardInput);
        Assert.Contains("/proc/${main_pid}/environ", standardInput);
        Assert.DoesNotContain("claude.env", standardInput, StringComparison.Ordinal);
    }
}
