using System.Text;
using System.Text.Json;

namespace AgentRunner;

public sealed record ProviderCredentialFreshness(
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? AccessTokenExpiresAt,
    DateTimeOffset? LastModifiedAt,
    bool CanRefreshNonInteractively,
    bool ShouldRefresh,
    bool Warning,
    string Detail)
{
    public static ProviderCredentialFreshness Unknown { get; } = new(
        null,
        null,
        null,
        false,
        false,
        false,
        "Credential expiry is not available from the configured authentication source.");
}

/// <summary>
/// Reads only credential metadata. Token values never leave this boundary. A
/// refresh-token-backed access-token expiry requests an early CLI status probe;
/// it is not presented as a re-auth deadline because the CLI can rotate it.
/// </summary>
public sealed class ProviderCredentialInspector
{
    public static readonly TimeSpan ExpiryWarningLead = TimeSpan.FromDays(14);
    public static readonly TimeSpan AccessRefreshLead = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan AgeWarning = TimeSpan.FromDays(30);

    public static ProviderCredentialInspector Disabled { get; } = new(enabled: false);

    private readonly bool _enabled;
    private readonly Func<string, string?> _environment;
    private readonly Func<string?> _home;

    public ProviderCredentialInspector(
        bool enabled = true,
        Func<string, string?>? environment = null,
        Func<string?>? home = null)
    {
        _enabled = enabled;
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _home = home ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public ProviderCredentialFreshness Inspect(string provider, DateTimeOffset now)
    {
        if (!_enabled) return ProviderCredentialFreshness.Unknown;
        var normalized = provider.Trim().ToLowerInvariant();

        var environmentExpiry = normalized switch
        {
            "codex" => JwtExpiry(_environment("CODEX_ACCESS_TOKEN")),
            "claude" => JwtExpiry(_environment("CLAUDE_CODE_OAUTH_TOKEN")),
            _ => null,
        };
        if (environmentExpiry is not null)
            return FromMetadata(environmentExpiry, null, null, false, now);

        var path = CredentialPath(normalized);
        if (path is null || !File.Exists(path)) return ProviderCredentialFreshness.Unknown;
        try
        {
            var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return InspectJson(document.RootElement, modified, now);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return ProviderCredentialFreshness.Unknown with
            {
                Detail = $"Credential metadata could not be inspected ({exception.GetType().Name}); the active CLI probe remains authoritative.",
            };
        }
    }

    public static ProviderCredentialFreshness InspectJson(
        JsonElement root,
        DateTimeOffset? lastModifiedAt,
        DateTimeOffset now)
    {
        var metadata = new CredentialMetadata();
        Visit(root, metadata);
        var actionableExpiry = metadata.RefreshTokenExpiresAt
                               ?? (!metadata.HasRefreshToken ? metadata.GenericExpiresAt : null);
        return FromMetadata(
            actionableExpiry,
            metadata.AccessTokenExpiresAt ?? metadata.GenericExpiresAt,
            lastModifiedAt,
            metadata.HasRefreshToken,
            now);
    }

    private string? CredentialPath(string provider)
    {
        var home = _home();
        if (provider == "codex")
        {
            var codexHome = _environment("CODEX_HOME");
            var root = string.IsNullOrWhiteSpace(codexHome)
                ? string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".codex")
                : codexHome;
            return root is null ? null : Path.Combine(root, "auth.json");
        }
        if (provider == "claude")
        {
            var claudeHome = _environment("CLAUDE_CONFIG_DIR");
            var root = string.IsNullOrWhiteSpace(claudeHome)
                ? string.IsNullOrWhiteSpace(home) ? null : Path.Combine(home, ".claude")
                : claudeHome;
            return root is null ? null : Path.Combine(root, ".credentials.json");
        }
        return null;
    }

    private static ProviderCredentialFreshness FromMetadata(
        DateTimeOffset? expiresAt,
        DateTimeOffset? accessTokenExpiresAt,
        DateTimeOffset? lastModifiedAt,
        bool canRefresh,
        DateTimeOffset now)
    {
        var shouldRefresh = canRefresh
                            && accessTokenExpiresAt is { } accessExpiry
                            && accessExpiry - now <= AccessRefreshLead;
        var expiryWarning = expiresAt is { } expiry && expiry - now <= ExpiryWarningLead;
        var ageWarning = expiresAt is null
                         && lastModifiedAt is { } modified
                         && now - modified >= AgeWarning;
        var warning = expiryWarning || ageWarning;
        var detail = expiryWarning
            ? expiresAt!.Value <= now
                ? "Credentials expired; re-authentication may be required."
                : $"Credentials expire at {expiresAt.Value.UtcDateTime:O}; renew before the deadline."
            : ageWarning
                ? $"Credential metadata is {Math.Floor((now - lastModifiedAt!.Value).TotalDays):0} days old; validate renewal before it becomes a hard failure."
                : shouldRefresh
                    ? "The access token is near expiry; requesting a non-interactive CLI validation/refresh."
                    : expiresAt is not null
                        ? $"Credential expiry is {expiresAt.Value.UtcDateTime:O}."
                        : "No actionable credential expiry was exposed; the active CLI probe remains authoritative.";
        return new ProviderCredentialFreshness(
            expiresAt,
            accessTokenExpiresAt,
            lastModifiedAt,
            canRefresh,
            shouldRefresh,
            warning,
            detail);
    }

    private static void Visit(JsonElement element, CredentialMetadata metadata)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var name = property.Name.Replace("_", string.Empty).ToLowerInvariant();
                if (name.Contains("refreshtoken", StringComparison.Ordinal))
                {
                    if (name.Contains("expire", StringComparison.Ordinal))
                        metadata.RefreshTokenExpiresAt ??= ReadExpiry(property.Value);
                    else if (property.Value.ValueKind == JsonValueKind.String
                             && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                        metadata.HasRefreshToken = true;
                }
                else if (name is "expiresat" or "expiration" or "expiry")
                {
                    metadata.GenericExpiresAt ??= ReadExpiry(property.Value);
                }
                else if (name.Contains("accesstoken", StringComparison.Ordinal)
                         || name.Contains("idtoken", StringComparison.Ordinal))
                {
                    metadata.AccessTokenExpiresAt ??= JwtExpiry(
                        property.Value.ValueKind == JsonValueKind.String
                            ? property.Value.GetString()
                            : null);
                }
                Visit(property.Value, metadata);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) Visit(item, metadata);
        }
    }

    private static DateTimeOffset? ReadExpiry(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numeric))
            return FromUnix(numeric);
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        if (long.TryParse(text, out numeric)) return FromUnix(numeric);
        return DateTimeOffset.TryParse(text, out var parsed) ? parsed.ToUniversalTime() : null;
    }

    private static DateTimeOffset? FromUnix(long value)
    {
        try
        {
            return value >= 1_000_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static DateTimeOffset? JwtExpiry(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return json.RootElement.TryGetProperty("exp", out var exp) ? ReadExpiry(exp) : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    private sealed class CredentialMetadata
    {
        public bool HasRefreshToken { get; set; }
        public DateTimeOffset? RefreshTokenExpiresAt { get; set; }
        public DateTimeOffset? GenericExpiresAt { get; set; }
        public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    }
}
