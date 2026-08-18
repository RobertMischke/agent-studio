namespace AgentStudio.HostHealth;

/// <summary>One npm debug log file as seen on disk. Name and timestamp only; contents are never read.</summary>
public sealed record NpmLogFile(string Name, DateTime LastWriteUtc, long Bytes);

/// <summary>
/// Root-cause capture for the "the shims vanished" breakage. npm writes one
/// debug log per invocation into its <c>_logs</c> directory, so an npm run
/// that overlaps the moment the shims disappeared is the evidence that turns
/// "auto-update is suspected" into "auto-update did it".
///
/// <para>
/// Selection is pure and directory-listing driven: the caller supplies what it
/// found, this decides what is inside the window and worth journalling. Nothing
/// here parses log contents; the file name and mtime are the signal, and they
/// carry no credentials.
/// </para>
/// </summary>
public static class NpmActivityWindow
{
    /// <summary>How far back from the observation to look for npm activity.</summary>
    public static readonly TimeSpan DefaultLookBack = TimeSpan.FromMinutes(30);

    /// <summary>Cap on journalled entries, so one pathological _logs directory cannot bloat a JSONL row.</summary>
    public const int DefaultMaxEntries = 5;

    /// <summary>
    /// npm logs written between <paramref name="observedAtUtc"/> minus
    /// <paramref name="lookBack"/> and <paramref name="observedAtUtc"/>,
    /// newest first, capped at <paramref name="maxEntries"/>.
    ///
    /// <para>
    /// Logs stamped after the observation are excluded on purpose: the repair
    /// this feature runs is itself an npm invocation, and its own log must not
    /// be presented as evidence of what caused the breakage.
    /// </para>
    /// </summary>
    public static IReadOnlyList<NpmLogFile> Select(
        IEnumerable<NpmLogFile> logs,
        DateTime observedAtUtc,
        TimeSpan lookBack,
        int maxEntries = DefaultMaxEntries)
    {
        ArgumentNullException.ThrowIfNull(logs);
        if (maxEntries <= 0 || lookBack <= TimeSpan.Zero) return Array.Empty<NpmLogFile>();

        var from = observedAtUtc - lookBack;
        return logs
            .Where(log => log.LastWriteUtc >= from && log.LastWriteUtc <= observedAtUtc)
            .OrderByDescending(log => log.LastWriteUtc)
            .ThenBy(log => log.Name, StringComparer.Ordinal)
            .Take(maxEntries)
            .ToList();
    }
}
