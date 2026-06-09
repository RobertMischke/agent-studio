using System.Security.Cryptography;
using System.Text;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// The decision returned by <see cref="DepsState.Evaluate"/> for one install
/// root: whether a dependency install must run before the agent starts, the
/// content hash of the install root's lockfiles, and a short reason for the
/// timeline / logs.
/// </summary>
public sealed record DepsEnsureDecision(
    bool InstallNeeded,
    string LockHash,
    string Reason,
    IReadOnlyList<string> Lockfiles);

/// <summary>
/// Slice A (ASS-1664) <c>deps-ensure</c> pre-step support: a lock-hash marker
/// that lets a recycled worktree SKIP <c>npm install</c> / <c>dotnet restore</c>
/// when the lockfiles have not changed since the last install.
///
/// <para>
/// The recycling pool preserves <c>node_modules</c> across tasks (the checkout
/// pre-step cleans with <c>-e node_modules</c>, never <c>-x</c>), so the only
/// thing that should re-trigger a (slow) install is an actual change to the
/// dependency lockfiles. After a successful install the caller stamps a
/// <c>.nm-state</c> marker holding the hash of the lockfiles at install time;
/// the next run compares the current lockfile hash against the marker and runs
/// the install <b>only</b> on a mismatch (or a missing marker / missing
/// <c>node_modules</c>).
/// </para>
///
/// <para>
/// Stateless + deterministic so it is unit-testable without running a package
/// manager: <see cref="Evaluate"/> decides, the runner runs the install, then
/// <see cref="Stamp"/> records the new state.
/// </para>
/// </summary>
public static class DepsState
{
    /// <summary>The per-install-root marker file holding the last-installed lock hash.</summary>
    public const string MarkerFileName = ".nm-state";

    /// <summary>The dependency cache directory that recycling preserves.</summary>
    public const string DepsDirName = "node_modules";

    /// <summary>
    /// Decide whether <paramref name="installRoot"/> needs a dependency install.
    /// <paramref name="lockFileNames"/> are install-root-relative lockfile names
    /// (e.g. <c>package-lock.json</c>, <c>pnpm-lock.yaml</c>). Install is needed
    /// when: no lockfile is present is treated as "nothing to install"
    /// (<c>InstallNeeded = false</c>); otherwise when <c>node_modules</c> is
    /// absent, the marker is missing, or the current lock hash differs from the
    /// stamped one.
    /// </summary>
    public static DepsEnsureDecision Evaluate(string installRoot, IReadOnlyList<string> lockFileNames)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
            return new DepsEnsureDecision(false, "", "install-root-missing", Array.Empty<string>());

        var present = (lockFileNames ?? Array.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Where(n => File.Exists(Path.Combine(installRoot, n)))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        if (present.Length == 0)
            return new DepsEnsureDecision(false, "", "no-lockfile", Array.Empty<string>());

        var hash = ComputeLockHash(installRoot, present);

        var depsDir = Path.Combine(installRoot, DepsDirName);
        if (!Directory.Exists(depsDir))
            return new DepsEnsureDecision(true, hash, "deps-dir-missing", present);

        var marker = Path.Combine(installRoot, MarkerFileName);
        if (!File.Exists(marker))
            return new DepsEnsureDecision(true, hash, "marker-missing", present);

        string stamped;
        try { stamped = File.ReadAllText(marker).Trim(); }
        catch { return new DepsEnsureDecision(true, hash, "marker-unreadable", present); }

        return string.Equals(stamped, hash, StringComparison.Ordinal)
            ? new DepsEnsureDecision(false, hash, "lock-unchanged", present)
            : new DepsEnsureDecision(true, hash, "lock-changed", present);
    }

    /// <summary>
    /// Records <paramref name="lockHash"/> in <paramref name="installRoot"/>'s
    /// <c>.nm-state</c> marker. Call this only AFTER a successful install so a
    /// failed install does not falsely mark the cache as up to date.
    /// </summary>
    public static void Stamp(string installRoot, string lockHash)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot)) return;
        File.WriteAllText(Path.Combine(installRoot, MarkerFileName), lockHash ?? "");
    }

    /// <summary>
    /// Stable SHA-256 over each present lockfile's relative name + bytes, in a
    /// deterministic order, so a rename or a content edit both change the hash.
    /// </summary>
    public static string ComputeLockHash(string installRoot, IReadOnlyList<string> presentLockNames)
    {
        using var sha = SHA256.Create();
        using var ms = new MemoryStream();
        foreach (var name in presentLockNames.OrderBy(n => n, StringComparer.Ordinal))
        {
            var header = Encoding.UTF8.GetBytes(name + "\0");
            ms.Write(header, 0, header.Length);
            byte[] bytes;
            try { bytes = File.ReadAllBytes(Path.Combine(installRoot, name)); }
            catch { bytes = Array.Empty<byte>(); }
            ms.Write(bytes, 0, bytes.Length);
            ms.WriteByte(0);
        }
        ms.Position = 0;
        return Convert.ToHexString(sha.ComputeHash(ms)).ToLowerInvariant();
    }
}
