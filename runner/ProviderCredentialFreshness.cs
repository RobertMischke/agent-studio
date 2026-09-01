using System.Text;
using System.Text.Json;

namespace AgentRunner;

/// <summary>
/// Secret-free freshness facts read from a provider credential file. Access and
/// refresh token values never leave this boundary.
/// </summary>
public sealed record ProviderCredentialFreshness(
    string Source,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RefreshedAt,
    bool NonInteractiveRefreshAvailable);

/// <summary>
/// Reads the credential formats currently emitted by Codex and Claude. Unknown
/// or changing formats degrade to no expiry rather than inventing a deadline.
/// </summary>
public sealed class ProviderCredentialFreshnessProbe
{
    private readonly string _userProfile;

    public ProviderCredentialFreshnessProbe(string? userProfile = null)
    {
        _userProfile = userProfile
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public ProviderCredentialFreshness? Inspect(string provider)
    {
        var path = CredentialPath(provider);
        if (path is null || !File.Exists(path)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return provider switch
            {
                "claude" => InspectClaude(document.RootElement, path),
                "codex" => InspectCodex(document.RootElement, path),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? CredentialPath(string provider) => provider switch
    {
        "claude" => Path.Combine(_userProfile, ".claude", ".credentials.json"),
        "codex" => Path.Combine(_userProfile, ".codex", "auth.json"),
        _ => null,
    };

    private static ProviderCredentialFreshness InspectClaude(JsonElement root, string path)
    {
        if (!TryProperty(root, "claudeAiOauth", out var oauth))
            return new ProviderCredentialFreshness(path, null, FileTimestamp(path), false);

        var refreshExpiry = ReadTimestamp(oauth, "refreshTokenExpiresAt");
        var accessExpiry = ReadTimestamp(oauth, "expiresAt");
        var canRefresh = HasNonEmptyString(oauth, "refreshToken");
        return new ProviderCredentialFreshness(
            path,
            refreshExpiry ?? accessExpiry,
            FileTimestamp(path),
            canRefresh);
    }

    private static ProviderCredentialFreshness InspectCodex(JsonElement root, string path)
    {
        TryProperty(root, "tokens", out var tokens);
        var accessExpiry = ReadJwtExpiry(tokens, "access_token");
        var refreshedAt = ReadTimestamp(root, "last_refresh") ?? FileTimestamp(path);
        var canRefresh = HasNonEmptyString(tokens, "refresh_token");
        return new ProviderCredentialFreshness(path, accessExpiry, refreshedAt, canRefresh);
    }

    private static DateTimeOffset? ReadJwtExpiry(JsonElement parent, string name)
    {
        if (!TryProperty(parent, name, out var token)
            || token.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(token.GetString()))
            return null;

        var segments = token.GetString()!.Split('.');
        if (segments.Length != 3) return null;
        try
        {
            var payload = segments[1]
                .Replace('-', '+')
                .Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return ReadTimestamp(document.RootElement, "exp");
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement parent, string name)
    {
        if (!TryProperty(parent, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var parsed))
            return parsed.ToUniversalTime();
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var epoch)) return null;
        try
        {
            return epoch > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : DateTimeOffset.FromUnixTimeSeconds(epoch);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool HasNonEmptyString(JsonElement parent, string name)
        => TryProperty(parent, name, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString());

    private static bool TryProperty(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value))
            return true;
        value = default;
        return false;
    }

    private static DateTimeOffset? FileTimestamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return null; }
    }
}
