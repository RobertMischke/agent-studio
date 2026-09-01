using System.Text;
using System.Text.Json;

namespace AgentRunner;

public sealed record ProviderCredentialFreshness(
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ModifiedAt,
    string Detail);

/// <summary>
/// Reads expiry metadata only from the runner user's provider credential file.
/// Token values are never returned, logged, or copied. Unknown or changed file
/// formats degrade quietly and leave the active CLI status probe authoritative.
/// </summary>
public static class ProviderCredentialMonitor
{
    public static readonly TimeSpan ExpiryWarningWindow = TimeSpan.FromDays(14);

    private static readonly HashSet<string> ExpiryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "expiresAt", "expires_at", "expiry", "expires", "expiration",
    };

    private static readonly HashSet<string> JwtNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "accessToken", "access_token", "idToken", "id_token",
    };

    public static ProviderCredentialFreshness Inspect(string cliBinary, string? homeDirectory = null)
    {
        var provider = RunnerCapabilityProbe.Provider(cliBinary);
        var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = provider switch
        {
            "codex" => Path.Combine(home, ".codex", "auth.json"),
            "claude" => Path.Combine(home, ".claude", ".credentials.json"),
            _ => null,
        };
        if (path is null)
            return new ProviderCredentialFreshness(null, null, "No credential metadata format is known for this provider.");

        try
        {
            if (!File.Exists(path))
                return new ProviderCredentialFreshness(null, null, "No provider credential file was found; process authentication remains authoritative.");
            var modifiedAt = File.GetLastWriteTimeUtc(path);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var expiresAt = FindExpiry(document.RootElement, 0);
            return new ProviderCredentialFreshness(
                expiresAt,
                new DateTimeOffset(DateTime.SpecifyKind(modifiedAt, DateTimeKind.Utc)),
                expiresAt is null
                    ? "Credential age is monitored, but this file exposes no supported expiry metadata."
                    : $"Credential expiry metadata was read without exposing credential values.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ProviderCredentialFreshness(
                null,
                null,
                $"Credential freshness metadata is temporarily unreadable ({exception.GetType().Name}).");
        }
    }

    private static DateTimeOffset? FindExpiry(JsonElement element, int depth)
    {
        if (depth > 12) return null;
        if (element.ValueKind == JsonValueKind.Object)
        {
            DateTimeOffset? best = null;
            foreach (var property in element.EnumerateObject())
            {
                DateTimeOffset? candidate = null;
                if (ExpiryNames.Contains(property.Name))
                    candidate = ParseTimestamp(property.Value);
                else if (JwtNames.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.String)
                    candidate = ParseJwtExpiry(property.Value.GetString());
                candidate ??= FindExpiry(property.Value, depth + 1);
                if (candidate is not null && (best is null || candidate < best)) best = candidate;
            }
            return best;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray()
                .Select(item => FindExpiry(item, depth + 1))
                .Where(value => value is not null)
                .Min();
        }
        return null;
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numeric))
            return numeric > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(numeric)
                : DateTimeOffset.FromUnixTimeSeconds(numeric);
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (long.TryParse(text, out numeric))
                return numeric > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(numeric)
                    : DateTimeOffset.FromUnixTimeSeconds(numeric);
            if (DateTimeOffset.TryParse(text, out var parsed)) return parsed;
        }
        return null;
    }

    private static DateTimeOffset? ParseJwtExpiry(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return document.RootElement.TryGetProperty("exp", out var expiry)
                ? ParseTimestamp(expiry)
                : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }
}
