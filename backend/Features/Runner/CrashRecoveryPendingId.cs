using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Runner;

internal static class CrashRecoveryPendingId
{
    public static string Create(
        string projectName,
        string worktreePath,
        string classification,
        DateTime firstObservedAt)
    {
        var canonicalWorktree = Path.GetFullPath(worktreePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows())
            canonicalWorktree = canonicalWorktree.ToUpperInvariant();

        var observedAtUtc = firstObservedAt.Kind switch
        {
            DateTimeKind.Utc => firstObservedAt,
            DateTimeKind.Local => firstObservedAt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(firstObservedAt, DateTimeKind.Utc)
        };
        var material = string.Join('\n',
            projectName.Trim().ToUpperInvariant(),
            canonicalWorktree,
            classification.Trim().ToLowerInvariant(),
            observedAtUtc.ToString("O", CultureInfo.InvariantCulture));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
