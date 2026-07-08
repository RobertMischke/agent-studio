using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Coverage for the runner identity (name + token precursor) a runner presents
/// when leasing a task (§8.2C; ADR-0060).
/// </summary>
public sealed class RunnerIdentityTests
{
    [Fact]
    public void Resolve_Defaults_UseBackendAtHostname()
    {
        var identity = RunnerIdentity.Resolve(Config(), hostname: "MyHost");

        Assert.Equal("stable", identity.BackendName);
        Assert.Equal("stable@myhost", identity.RunnerId);
        Assert.Equal(identity.RunnerId, identity.RunnerName);
        Assert.Equal("MyHost", identity.Hostname);
        Assert.Equal(RunnerIdentity.CurrentProtocolVersion, identity.ProtocolVersion);
        Assert.StartsWith(RunnerIdentity.TokenPrefix, identity.Token);
    }

    [Fact]
    public void Resolve_ExplicitRunnerConfigWins()
    {
        var identity = RunnerIdentity.Resolve(
            Config(
                ("Runner:Id", "Linux-Runner-01"),
                ("Runner:Name", "Linux Runner 01"),
                ("Runner:BackendName", "linux-a")),
            hostname: "MyHost");

        Assert.Equal("linux-runner-01", identity.RunnerId); // ids are normalized lower-case
        Assert.Equal("Linux Runner 01", identity.RunnerName);
        Assert.Equal("linux-a", identity.BackendName);
    }

    [Fact]
    public void Resolve_DevEnvironment_BackendIsDev()
    {
        var identity = RunnerIdentity.Resolve(Config(("Environment:IsDev", "true")), hostname: "MyHost");

        Assert.Equal("dev", identity.BackendName);
        Assert.Equal("dev@myhost", identity.RunnerId);
    }

    [Fact]
    public void DeriveToken_IsDeterministic_AndOpaque()
    {
        var a = RunnerIdentity.DeriveToken("stable@myhost", secret: "s3cr3t");
        var b = RunnerIdentity.DeriveToken("stable@myhost", secret: "s3cr3t");

        Assert.Equal(a, b); // stable across restarts / calls
        Assert.StartsWith(RunnerIdentity.TokenPrefix, a);
        Assert.DoesNotContain("stable@myhost", a); // the raw id never leaks into the token body
        Assert.DoesNotContain("s3cr3t", a);        // nor the secret
    }

    [Fact]
    public void DeriveToken_DiffersBySecretAndById()
    {
        var noSecret = RunnerIdentity.DeriveToken("stable@myhost", secret: null);
        var withSecret = RunnerIdentity.DeriveToken("stable@myhost", secret: "s3cr3t");
        var otherRunner = RunnerIdentity.DeriveToken("dev@myhost", secret: null);

        Assert.NotEqual(noSecret, withSecret);
        Assert.NotEqual(noSecret, otherRunner);
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();
}
