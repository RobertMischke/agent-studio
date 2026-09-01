using System.Globalization;
using System.Text.Json;

namespace AgentRunner;

/// <summary>
/// Non-secret credential metadata read from the provider-owned credential file.
/// The runner never returns token material and treats an unknown format as
/// unknown freshness instead of guessing that a login is invalid.
/// </summary>
public sealed record ProviderCredentialFreshness(
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? ExpiresAt,
    string? Warning)
{
    public bool NeedsAttention => !string.IsNullOrWhiteSpace(Warning);
}

internal static class ProviderCredentialFreshnessReader
{
    public static readonly TimeSpan ExpiryWarningWindow = TimeSpan.FromDays(14);
    public static readonly TimeSpan CodexStaleRefreshAge = TimeSpan.FromDays(30);

    public static ProviderCredentialFreshness Read(
        string provider,
        DateTimeOffset now,
        string? userProfile = null)
    {
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = CredentialPath(provider, userProfile);
        if (path is null || !File.Exists(path))
            return new ProviderCredentialFreshness(null, null, null);

        try
        {
            var json = File.ReadAllText(path);
            var modifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            return Parse(provider, json, modifiedAt, now);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or ArgumentOutOfRangeException
                                          or FormatException)
        {
            return new ProviderCredentialFreshness(
                null,
                null,
                $"credential freshness could not be read ({exception.GetType().Name}); the active login probe remains authoritative.");
        }
    }

    internal static ProviderCredentialFreshness Parse(
        string provider,
        string json,
        DateTimeOffset modifiedAt,
        DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        return provider switch
        {
            "claude" => ParseClaude(document.RootElement, modifiedAt, now),
            "codex" => ParseCodex(document.RootElement, modifiedAt, now),
            _ => new ProviderCredentialFreshness(modifiedAt, null, null),
        };
    }

    private static ProviderCredentialFreshness ParseClaude(
        JsonElement root,
        DateTimeOffset modifiedAt,
        DateTimeOffset now)
    {
        if (!root.TryGetProperty("claudeAiOauth", out var oauth))
            return new ProviderCredentialFreshness(modifiedAt, null, null);

        // `expiresAt` is the short-lived access-token deadline. The CLI can
        // rotate that token normally, so presenting it as a re-auth warning
        // would create a permanent false alarm. Only an explicit refresh-token
        // expiry means operator attention may be needed.
        var expiresAt = EpochMilliseconds(oauth, "refreshTokenExpiresAt");
        return WithExpiry(modifiedAt, expiresAt, now);
    }

    private static ProviderCredentialFreshness ParseCodex(
        JsonElement root,
        DateTimeOffset modifiedAt,
        DateTimeOffset now)
    {
        var updatedAt = modifiedAt;
        if (root.TryGetProperty("last_refresh", out var refresh)
            && refresh.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                refresh.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            updatedAt = parsed;
        }

        var age = now - updatedAt;
        var warning = age >= CodexStaleRefreshAge
            ? $"credentials have not refreshed since {updatedAt:O}; verify or renew the shared Codex login before it becomes unavailable."
            : null;
        return new ProviderCredentialFreshness(updatedAt, null, warning);
    }

    private static ProviderCredentialFreshness WithExpiry(
        DateTimeOffset updatedAt,
        DateTimeOffset? expiresAt,
        DateTimeOffset now)
    {
        if (expiresAt is null)
            return new ProviderCredentialFreshness(updatedAt, null, null);
        var warning = expiresAt <= now
            ? $"credentials reached their recorded expiry at {expiresAt:O}; a fresh login probe is required."
            : expiresAt - now <= ExpiryWarningWindow
                ? $"credentials expire at {expiresAt:O}; renew them before Ready cards are held."
                : null;
        return new ProviderCredentialFreshness(updatedAt, expiresAt, warning);
    }

    private static DateTimeOffset? EpochMilliseconds(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value)) return null;
        long milliseconds;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out milliseconds))
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out milliseconds))
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        return null;
    }

    private static string? CredentialPath(string provider, string userProfile) => provider switch
    {
        "codex" => Path.Combine(userProfile, ".codex", "auth.json"),
        "claude" => Path.Combine(userProfile, ".claude", ".credentials.json"),
        _ => null,
    };
}
