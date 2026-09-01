using System.Text;
using System.Text.Json;

namespace AgentRunner;

/// <summary>Non-secret timing facts read from a provider-owned credential file.</summary>
public sealed record ProviderCredentialFreshness(
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RefreshedAt,
    string Detail,
    bool RequiresReauthenticationAtExpiry = false);

public delegate ProviderCredentialFreshness? ProviderCredentialFreshnessReader(string provider);

/// <summary>
/// Reads only expiry and refresh timestamps. Provider token values never leave
/// this boundary and are not retained after an optional JWT payload decode.
/// </summary>
public static class ProviderCredentialFreshnessProbe
{
    public static ProviderCredentialFreshness? Read(string provider)
    {
        try
        {
            return provider switch
            {
                "claude" => ReadClaude(CredentialPath(
                    "CLAUDE_CONFIG_DIR",
                    ".claude",
                    ".credentials.json")),
                "codex" => ReadCodex(CredentialPath(
                    "CODEX_HOME",
                    ".codex",
                    "auth.json")),
                _ => null,
            };
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or FormatException)
        {
            return new ProviderCredentialFreshness(
                null,
                null,
                $"credential freshness unavailable: {exception.GetType().Name}");
        }
    }

    internal static ProviderCredentialFreshness? ReadClaude(string path)
    {
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
            return new(null, File.GetLastWriteTimeUtc(path), "Claude credential format has no OAuth timing data.");
        var refreshExpiry = ReadEpochMilliseconds(oauth, "refreshTokenExpiresAt");
        var accessExpiry = ReadEpochMilliseconds(oauth, "expiresAt");
        var expiresAt = refreshExpiry ?? accessExpiry;
        return new(
            expiresAt,
            File.GetLastWriteTimeUtc(path),
            refreshExpiry is not null
                ? "Claude refresh-credential expiry is known."
                : accessExpiry is not null
                    ? "Claude access-credential expiry is known; the CLI may refresh it non-interactively."
                    : "Claude credential expiry is unknown.",
            RequiresReauthenticationAtExpiry: refreshExpiry is not null);
    }

    internal static ProviderCredentialFreshness? ReadCodex(string path)
    {
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        DateTimeOffset? refreshedAt = null;
        if (root.TryGetProperty("last_refresh", out var refresh)
            && refresh.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(refresh.GetString(), out var parsedRefresh))
            refreshedAt = parsedRefresh.ToUniversalTime();

        // Codex currently stores no refresh-token expiry. The access-token JWT
        // gives a bounded validity check, but it is deliberately described as
        // refreshable so the UI does not mistake it for mandatory re-login.
        DateTimeOffset? accessExpiry = null;
        if (root.TryGetProperty("tokens", out var tokens)
            && tokens.TryGetProperty("access_token", out var accessToken)
            && accessToken.ValueKind == JsonValueKind.String)
            accessExpiry = ReadJwtExpiry(accessToken.GetString());
        return new(
            null,
            refreshedAt ?? File.GetLastWriteTimeUtc(path),
            accessExpiry is null
                ? "Codex credential expiry is unknown; last refresh age is monitored."
                : $"Codex access credential is refreshable; its current validity ends at {accessExpiry:o}.");
    }

    private static string CredentialPath(string environmentName, string defaultDirectory, string fileName)
    {
        var configured = Environment.GetEnvironmentVariable(environmentName);
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), defaultDirectory)
            : configured;
        return Path.Combine(directory, fileName);
    }

    private static DateTimeOffset? ReadEpochMilliseconds(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var milliseconds))
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), out milliseconds))
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        return null;
    }

    private static DateTimeOffset? ReadJwtExpiry(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var segments = token.Split('.');
        if (segments.Length != 3) return null;
        var payload = segments[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        if (!document.RootElement.TryGetProperty("exp", out var exp)
            || !exp.TryGetInt64(out var seconds)) return null;
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }
}
