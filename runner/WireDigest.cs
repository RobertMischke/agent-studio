using System.Security.Cryptography;
using System.Text;

namespace AgentRunner;

internal static class WireDigest
{
    public static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();
}
