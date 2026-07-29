using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Setup;

internal sealed record JoinPayload(
    int SchemaVersion,
    string ServerUrl,
    string Credential,
    string ReleaseVersion,
    DateTime IssuedAtUtc);

internal static class JoinTokenCodec
{
    private const string Prefix = "aosj1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Encode(JoinPayload payload)
    {
        Validate(payload);
        var body = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, Json));
        var checksum = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes($"{Prefix}.{body}")))[..16]
            .ToLowerInvariant();
        return $"{Prefix}.{body}.{checksum}";
    }

    public static JoinPayload Decode(string token)
    {
        var parts = token.Trim().Split('.');
        if (parts.Length != 3 || parts[0] != Prefix)
            throw new ArgumentException("Join token format is invalid.");
        var expected = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}")))[..16]
            .ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(parts[2].ToLowerInvariant())))
            throw new ArgumentException("Join token checksum is invalid. Copy the token again.");
        try
        {
            var payload = JsonSerializer.Deserialize<JoinPayload>(Base64UrlDecode(parts[1]), Json)
                          ?? throw new ArgumentException("Join token payload is empty.");
            Validate(payload);
            return payload;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Join token payload is invalid.", exception);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Join token encoding is invalid.", exception);
        }
    }

    private static void Validate(JoinPayload payload)
    {
        if (payload.SchemaVersion != 1)
            throw new ArgumentException("Join token schema is not supported.");
        if (string.IsNullOrWhiteSpace(payload.Credential) || payload.Credential.Length < 32)
            throw new ArgumentException("Join token credential is invalid.");
        _ = SetupValidation.RequireServerUrl(payload.ServerUrl, allowLoopbackHttp: true);
        _ = SetupOptions.NormalizeVersion(payload.ReleaseVersion)
            ?? throw new ArgumentException("Join token release version is invalid.");
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
