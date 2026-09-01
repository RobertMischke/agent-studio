using System.Text;
using System.Text.Json;

namespace AgentRunner;

/// <summary>
/// Secret-free metadata read from a provider credential store. Token values
/// never leave the parser and are never included in logs or advertisements.
/// </summary>
public sealed record ProviderCredentialFreshness(
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt,
    bool HasRefreshMaterial);

public delegate ProviderCredentialFreshness? ProviderCredentialInspector(string provider);

public static class ProviderCredentialStore
{
    public static ProviderCredentialFreshness? Inspect(string provider)
    {
        if (provider == "claude"
            && HasEnvironmentCredential("CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY"))
            return null;
        if (provider == "codex" && HasEnvironmentCredential("OPENAI_API_KEY"))
            return null;
        var path = CredentialPath(provider);
        return path is null ? null : InspectFile(path);
    }

    private static bool HasEnvironmentCredential(params string[] names)
        => names.Any(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)));

    internal static ProviderCredentialFreshness? InspectFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var expiries = new List<ExpiryCandidate>();
            var hasRefreshMaterial = false;
            Visit(document.RootElement, string.Empty, expiries, ref hasRefreshMaterial);

            // Access-token JWTs normally expire every hour and are not a hard
            // credential deadline when a refresh token is present. Prefer an
            // explicit refresh-token expiry; otherwise suppress that noisy
            // rolling deadline and let the bounded CLI probe perform refresh.
            var refreshExpiry = expiries
                .Where(candidate => candidate.RefreshSpecific)
                .Select(candidate => candidate.Value)
                .Order()
                .FirstOrDefault();
            DateTimeOffset? expiresAt = refreshExpiry == default
                ? hasRefreshMaterial
                    ? null
                    : expiries.Select(candidate => candidate.Value).Order().FirstOrDefault()
                : refreshExpiry;
            if (expiresAt == default) expiresAt = null;

            return new ProviderCredentialFreshness(
                File.GetLastWriteTimeUtc(path),
                expiresAt,
                hasRefreshMaterial);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or FormatException)
        {
            // Credential readability must never become an auth verdict. The CLI
            // status command remains the source of truth for validity.
            return null;
        }
    }

    private static string? CredentialPath(string provider)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile)) return null;
        return provider switch
        {
            "codex" => Path.Combine(
                Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(userProfile, ".codex"),
                "auth.json"),
            "claude" => Path.Combine(
                Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(userProfile, ".claude"),
                ".credentials.json"),
            _ => null,
        };
    }

    private static void Visit(
        JsonElement element,
        string path,
        ICollection<ExpiryCandidate> expiries,
        ref bool hasRefreshMaterial)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                var normalized = property.Name.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
                if (normalized.Contains("refresh", StringComparison.Ordinal)
                    && property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    hasRefreshMaterial = true;
                }
                if (IsExpiryName(normalized)
                    && TryParseTimestamp(property.Value, out var explicitExpiry))
                {
                    expiries.Add(new ExpiryCandidate(
                        explicitExpiry,
                        propertyPath.Contains("refresh", StringComparison.OrdinalIgnoreCase)));
                }
                if (property.Value.ValueKind == JsonValueKind.String
                    && TryParseJwtExpiry(property.Value.GetString(), out var jwtExpiry))
                {
                    expiries.Add(new ExpiryCandidate(jwtExpiry, RefreshSpecific: false));
                }
                Visit(property.Value, propertyPath, expiries, ref hasRefreshMaterial);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                Visit(item, path, expiries, ref hasRefreshMaterial);
        }
    }

    private static bool IsExpiryName(string normalized)
        => normalized is "exp" or "expiry" or "expiration" or "expires" or "expiresat"
           || normalized.EndsWith("expiresat", StringComparison.Ordinal)
           || normalized.EndsWith("expiry", StringComparison.Ordinal)
           || normalized.EndsWith("expiration", StringComparison.Ordinal);

    private static bool TryParseTimestamp(JsonElement value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (DateTimeOffset.TryParse(text, out timestamp)) return true;
            if (!long.TryParse(text, out var numeric)) return false;
            return TryParseUnixTimestamp(numeric, out timestamp);
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return TryParseUnixTimestamp(number, out timestamp);
        return false;
    }

    private static bool TryParseUnixTimestamp(long value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        try
        {
            timestamp = value > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
            return timestamp.Year is >= 2000 and <= 2200;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryParseJwtExpiry(string? token, out DateTimeOffset expiry)
    {
        expiry = default;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 3) return false;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return document.RootElement.TryGetProperty("exp", out var exp)
                   && exp.TryGetInt64(out var seconds)
                   && TryParseUnixTimestamp(seconds, out expiry);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }

    private sealed record ExpiryCandidate(DateTimeOffset Value, bool RefreshSpecific);
}
