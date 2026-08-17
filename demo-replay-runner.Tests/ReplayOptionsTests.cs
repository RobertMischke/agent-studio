using Xunit;

namespace AgentStudio.DemoReplayRunner.Tests;

/// <summary>
/// Configuration is fail-closed: the service refuses to start rather than run
/// against an unverifiable trace or send a credential over a plain connection.
/// </summary>
public sealed class ReplayOptionsTests
{
    private static ReplayOptions Valid() => new()
    {
        ServerUrl = "https://demo.agent-studio.test",
        TracePath = "/opt/demo-replay/trace/replay-trace.json",
        PublicKeyBase64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE",
        AuthToken = "rnr.demo.0123456789012345678901234567",
    };

    [Fact]
    public void A_complete_configuration_is_accepted()
        => ReplayOptions.Validate(Valid());

    [Fact]
    public void A_replay_credential_is_never_accepted_on_the_command_line()
    {
        var ex = Assert.Throws<ArgumentException>(() => ReplayOptions.Parse(["--auth-token", "rnr.demo.secret"]));

        Assert.Contains("DEMO_REPLAY_AUTH_TOKEN_FILE", ex.Message);
    }

    [Fact]
    public void A_trace_without_a_verification_key_is_refused()
        => Assert.Throws<ArgumentException>(() => ReplayOptions.Validate(Valid() with { PublicKeyBase64 = "" }));

    [Fact]
    public void A_public_target_over_plain_http_is_refused()
        => Assert.Throws<ArgumentException>(
            () => ReplayOptions.Validate(Valid() with { ServerUrl = "http://demo.agent-studio.test" }));

    [Fact]
    public void A_public_target_without_a_credential_is_refused()
        => Assert.Throws<ArgumentException>(() => ReplayOptions.Validate(Valid() with { AuthToken = null }));

    [Fact]
    public void A_loopback_target_may_stay_unauthenticated_for_local_verification()
        => ReplayOptions.Validate(Valid() with { ServerUrl = "http://127.0.0.1:5030", AuthToken = null });

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void A_non_positive_epoch_is_refused(long epoch)
        => Assert.Throws<ArgumentException>(() => ReplayOptions.Validate(Valid() with { StartEpoch = epoch }));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(1001.0)]
    public void An_out_of_range_speed_factor_is_refused(double speed)
        => Assert.Throws<ArgumentException>(() => ReplayOptions.Validate(Valid() with { SpeedFactor = speed }));

    [Fact]
    public void A_malformed_server_url_is_refused()
        => Assert.Throws<ArgumentException>(() => ReplayOptions.Validate(Valid() with { ServerUrl = "demo.agent-studio.test" }));
}
