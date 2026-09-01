using System.Text;
using System.Text.Json;

namespace AgentRunner;

public sealed record ProviderCredentialFreshness(
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RefreshedAt,
    string Source,
    bool NonInteractiveRefreshPossible);

public delegate ProviderCredentialFreshness? ProviderCredentialFreshnessReader(string provider);

/// <summary>Reads timestamps only. Token values never leave this boundary.</summary>
internal static class ProviderCredentialFreshnessInspector
{
    public static ProviderCredentialFreshness? Read(string provider)
    {
        try
        {
            return provider switch
            {
                AgentCliProcess.CodexCli => ReadCodex(),
                AgentCliProcess.ClaudeCli => ReadClaude(),
                _ => null,
            };
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or FormatException
                                          or ArgumentException
                                          or InvalidOperationException
                                          or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static ProviderCredentialFreshness? ReadCodex()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var directory = string.IsNullOrWhiteSpace(codexHome)
            ? Path.Combine(UserHome(), ".codex")
            : codexHome;
        var path = Path.Combine(directory, "auth.json");
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var refreshedAt = TryDate(root, "last_refresh");
        DateTimeOffset? expiresAt = null;
        var refreshPossible = false;
        if (root.TryGetProperty("tokens", out var tokens)
            && tokens.TryGetProperty("access_token", out var accessToken))
        {
            expiresAt = JwtExpiry(accessToken.GetString());
            refreshPossible = tokens.TryGetProperty("refresh_token", out var refresh)
                              && !string.IsNullOrWhiteSpace(refresh.GetString());
        }
        return new ProviderCredentialFreshness(expiresAt, refreshedAt, "codex auth.json", refreshPossible);
    }

    private static ProviderCredentialFreshness? ReadClaude()
    {
        var path = Path.Combine(UserHome(), ".claude", ".credentials.json");
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return null;
        var durableExpiry = TryEpoch(oauth, "refreshTokenExpiresAt");
        var accessExpiry = TryEpoch(oauth, "expiresAt");
        return new ProviderCredentialFreshness(
            durableExpiry ?? accessExpiry,
            new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
            "Claude credentials",
            oauth.TryGetProperty("refreshToken", out var refresh) && !string.IsNullOrWhiteSpace(refresh.GetString()));
    }

    private static string UserHome()
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home
            ? home
            : Environment.GetEnvironmentVariable("HOME") ?? string.Empty;

    private static DateTimeOffset? TryDate(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
           && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static DateTimeOffset? TryEpoch(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        long raw;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out raw)) { }
        else if (!long.TryParse(value.GetString(), out raw)) return null;
        if (raw > 100_000_000_000) return DateTimeOffset.FromUnixTimeMilliseconds(raw);
        return DateTimeOffset.FromUnixTimeSeconds(raw);
    }

    private static DateTimeOffset? JwtExpiry(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        return TryEpoch(document.RootElement, "exp");
    }
}
