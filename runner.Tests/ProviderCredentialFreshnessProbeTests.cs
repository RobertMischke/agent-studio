using System.Text;
using System.Text.Json;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ProviderCredentialFreshnessProbeTests
{
    [Fact]
    public async Task Claude_refresh_expiry_is_read_without_exposing_tokens()
    {
        using var temp = new TempDirectory();
        var expiresAt = new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);
        var path = Path.Combine(temp.Path, ".credentials.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            claudeAiOauth = new
            {
                accessToken = "secret-access-token",
                refreshToken = "secret-refresh-token",
                expiresAt = expiresAt.AddHours(-1).ToUnixTimeMilliseconds(),
                refreshTokenExpiresAt = expiresAt.ToUnixTimeMilliseconds(),
            },
        }));

        var freshness = ProviderCredentialFreshnessProbe.ReadClaude(path);

        Assert.NotNull(freshness);
        Assert.Equal(expiresAt, freshness.ExpiresAt);
        Assert.True(freshness.RequiresReauthenticationAtExpiry);
        Assert.True(freshness.NonInteractiveRefreshAvailable);
        Assert.DoesNotContain("secret", freshness.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Codex_records_refresh_age_but_does_not_treat_access_jwt_as_reauth_expiry()
    {
        using var temp = new TempDirectory();
        var refreshedAt = new DateTimeOffset(2026, 8, 28, 17, 37, 25, TimeSpan.Zero);
        var accessExpiry = new DateTimeOffset(2026, 9, 1, 13, 0, 0, TimeSpan.Zero);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { exp = accessExpiry.ToUnixTimeSeconds() })))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var path = Path.Combine(temp.Path, "auth.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            last_refresh = refreshedAt,
            tokens = new { access_token = $"header.{payload}.signature", refresh_token = "secret" },
        }));

        var freshness = ProviderCredentialFreshnessProbe.ReadCodex(path);

        Assert.NotNull(freshness);
        Assert.Null(freshness.ExpiresAt);
        Assert.Equal(refreshedAt, freshness.RefreshedAt);
        Assert.True(freshness.NonInteractiveRefreshAvailable);
        Assert.Contains(accessExpiry.ToString("o"), freshness.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", freshness.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_provider_shape_degrades_without_throwing_or_exposing_content()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "auth.json");
        await File.WriteAllTextAsync(path, "{\"tokens\":\"unexpected-secret-shape\"}");

        var freshness = ProviderCredentialFreshnessProbe.ReadCodex(path);

        Assert.NotNull(freshness);
        Assert.Null(freshness.ExpiresAt);
        Assert.False(freshness.NonInteractiveRefreshAvailable);
        Assert.DoesNotContain("secret", freshness.Detail, StringComparison.Ordinal);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"provider-credential-freshness-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
