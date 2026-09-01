using System.Text;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ProviderAuthFailureClassifierTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Not logged in. Run codex login.", ProviderAuthFailureKind.SignedOut)]
    [InlineData("invalid credentials", ProviderAuthFailureKind.SignedOut)]
    [InlineData("HTTP 401 Unauthorized", ProviderAuthFailureKind.Indeterminate)]
    [InlineData("authentication failed during token refresh; try again", ProviderAuthFailureKind.Transient)]
    [InlineData("network unavailable while refreshing the access token", ProviderAuthFailureKind.Transient)]
    public void Auth_failures_require_specific_evidence(
        string output,
        ProviderAuthFailureKind expected)
    {
        var evidence = ProviderAuthFailureClassifier.Classify(
            new ProcessResult(1, "", output),
            Now);

        Assert.Equal(expected, evidence.Kind);
    }

    [Fact]
    public void Rate_limit_precedes_auth_phrases_and_carries_the_reset()
    {
        var reset = Now.AddHours(2);

        var evidence = ProviderAuthFailureClassifier.Classify(
            new ProcessResult(
                1,
                "",
                $"authentication failed: HTTP 429 rate_limit_exceeded resetsAt={reset.ToUnixTimeSeconds()}"),
            Now);

        Assert.Equal(ProviderAuthFailureKind.RateLimited, evidence.Kind);
        Assert.Equal(reset, evidence.RetryAt);
    }

    [Fact]
    public void Credential_metadata_warns_before_refresh_credential_expiry()
    {
        var expiry = Now.AddDays(13);
        using var document = JsonDocument.Parse(
            $$"""{"refresh_token":"secret-never-rendered","refresh_token_expires_at":{{expiry.ToUnixTimeMilliseconds()}}}""");

        var freshness = ProviderCredentialInspector.InspectJson(
            document.RootElement,
            Now.AddDays(-2),
            Now);

        Assert.True(freshness.MetadataAvailable);
        Assert.True(freshness.Warning);
        Assert.Equal(expiry, freshness.ExpiresAt);
        Assert.DoesNotContain("secret-never-rendered", freshness.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Old_credential_metadata_warns_when_the_provider_exposes_no_expiry()
    {
        using var document = JsonDocument.Parse("{}");

        var freshness = ProviderCredentialInspector.InspectJson(
            document.RootElement,
            Now.AddDays(-31),
            Now);

        Assert.True(freshness.Warning);
        Assert.Null(freshness.ExpiresAt);
        Assert.Contains("31 days old", freshness.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_token_makes_near_access_expiry_non_interactively_refreshable_not_a_reauth_warning()
    {
        var accessToken = JwtWithExpiry(Now.AddMinutes(5));
        using var document = JsonDocument.Parse(
            $$"""{"refresh_token":"fixture","access_token":"{{accessToken}}"}""");

        var freshness = ProviderCredentialInspector.InspectJson(
            document.RootElement,
            Now,
            Now);

        Assert.True(freshness.CanRefreshNonInteractively);
        Assert.True(freshness.ShouldRefresh);
        Assert.False(freshness.Warning);
        Assert.Contains("non-interactive", freshness.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ready", ProviderAuthOperationalStates.Authenticated, true)]
    [InlineData("ready", ProviderAuthOperationalStates.CredentialsExpiring, true)]
    [InlineData("ready", ProviderAuthOperationalStates.TransientError, false)]
    [InlineData("unavailable", ProviderAuthOperationalStates.SignedOut, false)]
    public void Only_a_successful_provider_probe_is_server_recovery_proof(
        string status,
        string operationalState,
        bool expected)
        => Assert.Equal(
            expected,
            ProviderAuthCapabilityPolicy.IsRecoveryProof(
                CapabilityProtocol.ProviderAuthentication("codex"),
                status,
                operationalState));

    private static string JwtWithExpiry(DateTimeOffset expiry)
    {
        var header = Base64Url("{\"alg\":\"none\"}");
        var payload = Base64Url($"{{\"exp\":{expiry.ToUnixTimeSeconds()}}}");
        return $"{header}.{payload}.fixture";
    }

    private static string Base64Url(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
