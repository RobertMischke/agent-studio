using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Deterministic lockfile decision shared by disposable build gates and Remote
/// Review workspaces. A successful install stamps the current digest beside the
/// dependency directory; a different digest always forces a new install.
/// </summary>
public static class DependencyPreparationState
{
    public const string MarkerFileName = ".nm-state";
    public const string DependencyDirectoryName = "node_modules";

    /// <summary>
    /// The ledger npm writes into <c>node_modules</c> at the end of a successful
    /// <c>npm ci</c> or <c>npm install</c>. Its absence next to a populated
    /// dependency directory means the tree was never finished or was left behind
    /// by an interrupted cache transfer, so the stamped marker cannot be trusted.
    /// </summary>
    public const string InstallLedgerFileName = ".package-lock.json";

    private const string LedgerLinePrefix = "ledger=";
    private const string LedgerPresent = "present";
    private const string LedgerAbsent = "absent";

    private static readonly string[] NpmLockfileNames =
        ["package-lock.json", "npm-shrinkwrap.json"];

    public static ReviewDependencyCacheEvidenceDto Evaluate(
        string installRoot,
        ReviewDependencyScopeDto scope,
        bool installRan = false)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
            return Evidence(scope, "miss", "install-root-missing", "", [], installRan);

        var present = (scope.Lockfiles ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => File.Exists(Path.Combine(installRoot, name)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (present.Length == 0)
            return Evidence(scope, "miss", "no-lockfile", "", [], installRan);

        var hash = ComputeLockHash(installRoot, present);
        var dependencyDirectory = Path.Combine(installRoot, DependencyDirectoryName);
        if (!Directory.Exists(dependencyDirectory))
            return Evidence(scope, "miss", "deps-dir-missing", hash, present, installRan);

        var marker = ReadMarker(installRoot);
        if (marker is null)
            return Evidence(scope, "miss", "marker-missing", hash, present, installRan);
        if (marker.Unreadable)
            return Evidence(scope, "miss", "marker-unreadable", hash, present, installRan);
        if (ExpectsInstallLedger(marker, present) && !HasInstallLedger(installRoot))
            return Evidence(scope, "miss", "install-incomplete", hash, present, installRan);

        return string.Equals(marker.LockHash, hash, StringComparison.Ordinal)
            ? Evidence(scope, "hit", "lock-unchanged", hash, present, installRan)
            : Evidence(scope, "miss", "lock-changed", hash, present, installRan);
    }

    /// <summary>
    /// Stamps the install-complete marker right after a successful install, and
    /// records whether that install left npm's ledger behind. Recording the
    /// observed fact, instead of inferring it from the lockfile name, is what
    /// lets a later run tell a truncated npm tree (ledger was there, now gone)
    /// from a scope whose installer never writes one at all.
    /// </summary>
    public static void Stamp(string installRoot, string lockHash)
    {
        if (string.IsNullOrWhiteSpace(installRoot)
            || string.IsNullOrWhiteSpace(lockHash)
            || !Directory.Exists(installRoot))
            return;
        var ledger = HasInstallLedger(installRoot) ? LedgerPresent : LedgerAbsent;
        File.WriteAllText(
            Path.Combine(installRoot, MarkerFileName),
            lockHash + Environment.NewLine + LedgerLinePrefix + ledger + Environment.NewLine);
    }

    /// <summary>
    /// Positive evidence that this scope's dependency tree lost content: a
    /// marker written when npm's ledger existed, next to a tree that no longer
    /// has it. Deliberately narrow. Absence of a marker proves nothing either
    /// way, and treating "unknown" as "truncated" would quietly disable the
    /// cache for every scope whose installer leaves no ledger.
    /// </summary>
    public static bool IsTruncatedInstall(string installRoot, IReadOnlyList<string>? lockfiles)
    {
        if (!Directory.Exists(Path.Combine(installRoot, DependencyDirectoryName)))
            return false;
        var marker = ReadMarker(installRoot);
        if (marker is null || marker.Unreadable) return false;
        return ExpectsInstallLedger(marker, lockfiles) && !HasInstallLedger(installRoot);
    }

    /// <summary>
    /// True unless the scope shows that its tree was truncated. Publishing a
    /// truncated tree is what turns one interrupted transfer into a cache entry
    /// that fails every later gate before the first test.
    /// </summary>
    public static bool IsPublishable(string installRoot, IReadOnlyList<string>? lockfiles)
        => !IsTruncatedInstall(installRoot, lockfiles);

    /// <summary>
    /// A marker written before the ledger was recorded carries no verdict of its
    /// own. Falling back to the lockfile name keeps the CAC-18 protection for
    /// entries that already exist on a gate host; the worst case is one cold
    /// install after which the marker states the fact explicitly.
    /// </summary>
    private static bool ExpectsInstallLedger(MarkerState marker, IReadOnlyList<string>? lockfiles)
        => marker.LedgerRecorded ?? GovernedByNpm(lockfiles);

    private static bool HasInstallLedger(string installRoot)
        => File.Exists(Path.Combine(
            installRoot,
            DependencyDirectoryName,
            InstallLedgerFileName));

    private static MarkerState? ReadMarker(string installRoot)
    {
        var path = Path.Combine(installRoot, MarkerFileName);
        if (!File.Exists(path)) return null;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch
        {
            return MarkerState.Damaged();
        }

        var hash = lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? "";
        bool? ledger = null;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(LedgerLinePrefix, StringComparison.Ordinal)) continue;
            var value = trimmed[LedgerLinePrefix.Length..];
            if (string.Equals(value, LedgerPresent, StringComparison.Ordinal)) ledger = true;
            else if (string.Equals(value, LedgerAbsent, StringComparison.Ordinal)) ledger = false;
        }
        return new MarkerState(hash, ledger, Unreadable: false);
    }

    private sealed record MarkerState(string LockHash, bool? LedgerRecorded, bool Unreadable)
    {
        public static MarkerState Damaged() => new("", null, true);
    }

    private static bool GovernedByNpm(IReadOnlyList<string>? lockfiles)
        => (lockfiles ?? []).Any(name => NpmLockfileNames.Contains(
            Path.GetFileName(name ?? string.Empty),
            StringComparer.OrdinalIgnoreCase));

    public static string ComputeLockHash(
        string installRoot,
        IReadOnlyList<string> presentLockNames)
    {
        using var sha = SHA256.Create();
        using var stream = new MemoryStream();
        foreach (var name in presentLockNames.OrderBy(value => value, StringComparer.Ordinal))
        {
            var header = Encoding.UTF8.GetBytes(name + "\0");
            stream.Write(header);
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(Path.Combine(installRoot, name));
            }
            catch
            {
                bytes = [];
            }
            stream.Write(bytes);
            stream.WriteByte(0);
        }
        stream.Position = 0;
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static ReviewDependencyCacheEvidenceDto Evidence(
        ReviewDependencyScopeDto scope,
        string state,
        string reason,
        string lockHash,
        IReadOnlyList<string> lockfiles,
        bool installRan)
        => new(
            string.IsNullOrWhiteSpace(scope.WorkingSubdir) ? "." : scope.WorkingSubdir,
            state,
            reason,
            lockHash,
            lockfiles,
            installRan);
}

/// <summary>
/// Shared dependency-cache transfer protocol for disposable exact-subject
/// workspaces. Content is mirrored by repository-relative path, while candidate
/// and baseline roles receive separate namespaces so build outputs never cross
/// the comparison boundary.
/// </summary>
public sealed class DependencyCacheSession
{
    private const string ContentDirectoryName = "content";
    private const string StagingPrefix = ".staging-";
    private const string DiscardedPrefix = ".discarded-";

    /// <summary>
    /// How long a staging or discard directory must have existed before it counts
    /// as debris from an interrupted run rather than the working set of a save
    /// that is still in flight. Comfortably longer than any gate run budget.
    /// </summary>
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(6);

    private readonly string _workspace;
    private readonly string _cacheRoot;
    private readonly IReadOnlyList<ReviewDependencyScopeDto> _scopes;
    private readonly IReadOnlyList<string> _preserveGlobs;
    private readonly Action<string>? _log;

    private DependencyCacheSession(
        string workspace,
        string cacheRoot,
        IReadOnlyList<ReviewDependencyScopeDto> scopes,
        IReadOnlyList<string> preserveGlobs,
        Action<string>? log)
    {
        _workspace = workspace;
        _cacheRoot = cacheRoot;
        _scopes = scopes;
        _preserveGlobs = preserveGlobs;
        _log = log;
    }

    public static DependencyCacheSession Create(
        string cacheParent,
        string repositoryIdentity,
        string workspace,
        IReadOnlyList<ReviewDependencyScopeDto> scopes,
        IReadOnlyList<string>? preserveGlobs = null,
        string? role = null,
        Action<string>? log = null)
        => new(
            workspace,
            CachePath(cacheParent, repositoryIdentity, role),
            NormalizeScopes(scopes),
            NormalizeGlobs(preserveGlobs),
            log);

    public static string CachePath(
        string cacheParent,
        string repositoryIdentity,
        string? role = null)
    {
        var root = Path.Combine(cacheParent, RepositoryKey(repositoryIdentity));
        return string.IsNullOrWhiteSpace(role)
            ? root
            : Path.Combine(root, SafeSegment(role));
    }

    public IReadOnlyList<string> Restore()
    {
        var stopwatch = Stopwatch.StartNew();
        var messages = new List<string>();
        PurgeAbandoned(messages);
        RestoreInto(ContentRoot, _workspace, messages);
        return Complete(stopwatch, messages, "restore", detail: null);
    }

    /// <summary>
    /// Publishes the workspace dependency trees as the repository cache entry.
    /// <para>
    /// Each tree is staged into a sibling directory and only swapped in once its
    /// own move succeeded, so an interrupted or partly failed save can never
    /// leave a half-moved tree readable as an entry. Publication is per item on
    /// purpose: an all-or-nothing swap would delete trees this session never
    /// staged, and consecutive gates on the same repository legitimately cache
    /// different subtrees.
    /// </para>
    /// <para>
    /// A scope whose tree lost content is skipped rather than published, which
    /// is the rule that stops one interrupted transfer from poisoning every
    /// later gate (CAC-18).
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Save()
    {
        var stopwatch = Stopwatch.StartNew();
        var messages = new List<string>();
        PurgeAbandoned(messages);

        var blocked = BlockedDependencyDirectories(messages);
        var staging = SiblingPath(StagingPrefix);
        var published = 0;
        var failed = 0;
        try
        {
            foreach (var relative in CacheDirectories(_workspace))
            {
                if (blocked.Contains(relative)) continue;
                var source = ResolveWithin(_workspace, relative);
                if (!Directory.Exists(source)) continue;
                if (!MoveDirectory(
                        source, ResolveWithin(staging, relative), "save", relative, messages))
                {
                    failed++;
                    continue;
                }
                if (PublishItem(staging, relative, messages)) published++;
                else failed++;
            }

            foreach (var scope in _scopes)
            {
                var marker = Combine(scope.WorkingSubdir, DependencyPreparationState.MarkerFileName);
                if (blocked.Contains(Combine(
                        scope.WorkingSubdir,
                        DependencyPreparationState.DependencyDirectoryName)))
                    continue;
                MoveFile(
                    ResolveWithin(_workspace, marker),
                    ResolveWithin(ContentRoot, marker),
                    "save",
                    marker,
                    messages);
            }
        }
        finally
        {
            TryDelete(staging);
        }

        return Complete(
            stopwatch, messages, "save", $"published={published} failed={failed}");
    }

    /// <summary>
    /// The dependency directories that must not reach the cache because their
    /// scope shows a truncated tree. Only that scope is held back; a healthy
    /// sibling scope still publishes.
    /// </summary>
    private HashSet<string> BlockedDependencyDirectories(ICollection<string> messages)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in _scopes)
        {
            if (DependencyPreparationState.IsPublishable(
                    ResolveWithin(_workspace, scope.WorkingSubdir), scope.Lockfiles))
                continue;
            blocked.Add(Combine(
                scope.WorkingSubdir, DependencyPreparationState.DependencyDirectoryName));
            Emit(
                messages,
                "dependency-cache save skipped " +
                $"scope={DisplayScope(scope.WorkingSubdir)} reason=install-incomplete");
        }
        return blocked;
    }

    /// <summary>
    /// Drops the repository cache entry so the next gate reinstalls from the
    /// lockfile. Called when a gate failed in a way that a corrupted dependency
    /// tree explains, which is the only way a broken entry stops repeating.
    /// </summary>
    public IReadOnlyList<string> Evict(string reason)
    {
        var stopwatch = Stopwatch.StartNew();
        var messages = new List<string>();
        PurgeAbandoned(messages);

        var state = "absent";
        if (Directory.Exists(ContentRoot))
        {
            var discarded = SiblingPath(DiscardedPrefix);
            try
            {
                Directory.Move(ContentRoot, discarded);
                state = "removed";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                state = $"failed:{exception.GetType().Name}";
            }
            TryDelete(discarded);
        }

        foreach (var scope in _scopes)
            Emit(messages, $"dependency-cache evicted scope={DisplayScope(scope.WorkingSubdir)} reason={reason}");
        return Complete(stopwatch, messages, "evict", $"state={state} reason={reason}");
    }

    private string ContentRoot => Path.Combine(_cacheRoot, ContentDirectoryName);

    /// <summary>
    /// Moves the cached trees and markers of one entry into the workspace. A
    /// failed item is reported and skipped: the scope simply reinstalls, because
    /// the read-side check rejects any tree that arrived incomplete.
    /// </summary>
    private void RestoreInto(string sourceRoot, string destinationRoot, ICollection<string> messages)
    {
        foreach (var relative in CacheDirectories(sourceRoot))
        {
            var source = ResolveWithin(sourceRoot, relative);
            if (!Directory.Exists(source)) continue;
            MoveDirectory(
                source, ResolveWithin(destinationRoot, relative), "restore", relative, messages);
        }

        foreach (var scope in _scopes)
        {
            var marker = Combine(scope.WorkingSubdir, DependencyPreparationState.MarkerFileName);
            MoveFile(
                ResolveWithin(sourceRoot, marker),
                ResolveWithin(destinationRoot, marker),
                "restore",
                marker,
                messages);
        }
    }

    /// <summary>
    /// Swaps one fully staged tree in as that item's cache entry. The previous
    /// copy is renamed aside first and put back if the swap itself fails, so the
    /// item is always either its old tree or the complete new one.
    /// </summary>
    private bool PublishItem(string staging, string relative, ICollection<string> messages)
    {
        var source = ResolveWithin(staging, relative);
        var destination = ResolveWithin(ContentRoot, relative);
        var discarded = SiblingPath(DiscardedPrefix);
        var replaced = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(destination))
            {
                Directory.Move(destination, discarded);
                replaced = true;
            }
            Directory.Move(source, destination);
            TryDelete(discarded);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (replaced && !Directory.Exists(destination))
            {
                try { Directory.Move(discarded, destination); }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    Emit(
                        messages,
                        $"dependency-cache save item={relative} state=rollback-failed " +
                        $"reason={rollback.GetType().Name}");
                }
            }
            Emit(
                messages,
                $"dependency-cache save item={relative} state=publish-failed " +
                $"reason={exception.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Removes staging and discard directories left by an interrupted run. They
    /// are never readable as an entry, but they would otherwise grow without
    /// bound on the gate host. Only directories older than
    /// <see cref="AbandonedAfter"/> are touched, so a save running concurrently
    /// on the same cache root never has its staging tree deleted underneath it.
    /// </summary>
    private void PurgeAbandoned(ICollection<string> messages)
    {
        if (!Directory.Exists(_cacheRoot)) return;
        var cutoff = DateTime.UtcNow - AbandonedAfter;
        IReadOnlyList<string> leftovers;
        try
        {
            leftovers = Directory.EnumerateDirectories(_cacheRoot)
                .Where(IsTransferDirectory)
                .Where(path => Directory.GetCreationTimeUtc(path) < cutoff)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var leftover in leftovers)
        {
            if (TryDelete(leftover))
                Emit(messages, $"dependency-cache purged item={Path.GetFileName(leftover)} state=removed");
        }
    }

    private static bool IsTransferDirectory(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(StagingPrefix, StringComparison.Ordinal)
               || name.StartsWith(DiscardedPrefix, StringComparison.Ordinal);
    }

    private string SiblingPath(string prefix)
        => Path.Combine(_cacheRoot, prefix + Guid.NewGuid().ToString("N")[..12]);

    /// <summary>
    /// Best-effort recursive delete. Retries once after clearing read-only
    /// attributes, which Windows enforces on files a package manager or Git left
    /// marked and which would otherwise let debris accumulate under the shared
    /// cache root.
    /// </summary>
    private static bool TryDelete(string path)
    {
        if (!Directory.Exists(path)) return true;
        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                ClearReadOnly(path);
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception retry) when (retry is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private static void ClearReadOnly(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // One unreadable entry must not abort clearing the rest.
            }
        }
    }

    private IReadOnlyList<string> Complete(
        Stopwatch stopwatch,
        List<string> messages,
        string operation,
        string? detail)
    {
        stopwatch.Stop();
        var summary =
            $"dependency-cache {operation} repository={Path.GetFileName(_cacheRoot)} " +
            $"scopes={_scopes.Count} durationMs={stopwatch.ElapsedMilliseconds}" +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail);
        messages.Add(summary);
        _log?.Invoke(summary);
        return messages;
    }

    private void Emit(ICollection<string> messages, string message)
    {
        messages.Add(message);
        _log?.Invoke(message);
    }

    private static string DisplayScope(string workingSubdir)
        => string.IsNullOrWhiteSpace(workingSubdir) ? "." : workingSubdir;

    private IReadOnlyList<string> CacheDirectories(string sourceRoot)
    {
        if (!Directory.Exists(sourceRoot)) return [];
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in _scopes)
        {
            candidates.Add(Combine(
                scope.WorkingSubdir,
                DependencyPreparationState.DependencyDirectoryName));
            candidates.Add(Combine(scope.WorkingSubdir, ".angular"));
        }

        foreach (var pattern in _preserveGlobs)
        {
            if (!HasWildcard(pattern))
            {
                candidates.Add(pattern);
                continue;
            }
            foreach (var relative in EnumerateDirectories(sourceRoot))
            {
                if (MatchesGlob(pattern, relative)) candidates.Add(relative);
            }
        }

        return candidates
            .Where(relative => Directory.Exists(ResolveWithin(sourceRoot, relative)))
            .OrderBy(relative => relative.Count(ch => ch == '/'))
            .ThenBy(relative => relative, StringComparer.OrdinalIgnoreCase)
            .Aggregate(
                new List<string>(),
                (selected, relative) =>
                {
                    if (!selected.Any(parent => IsSameOrChild(relative, parent)))
                        selected.Add(relative);
                    return selected;
                });
    }

    private static IEnumerable<string> EnumerateDirectories(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var child in children)
            {
                var relative = Path.GetRelativePath(root, child).Replace('\\', '/');
                yield return relative;
                var name = Path.GetFileName(child);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(child);
                }
                catch
                {
                    continue;
                }
                if (name is ".git" or "node_modules"
                    || attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                pending.Push(child);
            }
        }
    }

    /// <summary>
    /// Moves one cached directory. Returns false when the move did not put the
    /// tree where the caller expects it, which is what makes a partly failed
    /// save abort instead of publishing a hole.
    /// </summary>
    private bool MoveDirectory(
        string source,
        string destination,
        string operation,
        string relative,
        ICollection<string> messages)
    {
        if (!Directory.Exists(source)) return false;
        try
        {
            if (Directory.Exists(destination))
            {
                if (operation == "restore")
                {
                    messages.Add($"dependency-cache restore skipped item={relative} reason=destination-exists");
                    return false;
                }
                Directory.Delete(destination, recursive: true);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(source, destination);
            messages.Add($"dependency-cache {operation} item={relative} state=moved");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var message =
                $"dependency-cache {operation} item={relative} state=failed " +
                $"reason={exception.GetType().Name}";
            messages.Add(message);
            _log?.Invoke(message);
            return false;
        }
    }

    private void MoveFile(
        string source,
        string destination,
        string operation,
        string relative,
        ICollection<string> messages)
    {
        if (!File.Exists(source)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination, overwrite: operation == "save");
            messages.Add($"dependency-cache {operation} item={relative} state=moved");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var message =
                $"dependency-cache {operation} item={relative} state=failed " +
                $"reason={exception.GetType().Name}";
            messages.Add(message);
            _log?.Invoke(message);
        }
    }

    private static IReadOnlyList<ReviewDependencyScopeDto> NormalizeScopes(
        IReadOnlyList<ReviewDependencyScopeDto>? scopes)
        => (scopes ?? [])
            .Select(scope => new ReviewDependencyScopeDto(
                NormalizeRelative(scope.WorkingSubdir, allowEmpty: true) ?? "",
                scope.Lockfiles
                    .Select(lockfile => NormalizeRelative(lockfile, allowEmpty: false))
                    .Where(lockfile => lockfile is not null)
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(lockfile => lockfile, StringComparer.Ordinal)
                    .ToArray()))
            .DistinctBy(scope => scope.WorkingSubdir, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> NormalizeGlobs(IReadOnlyList<string>? globs)
        => (globs ?? [])
            .Select(glob => NormalizeRelative(glob, allowEmpty: false, allowWildcards: true))
            .Where(glob => glob is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(glob => glob, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? NormalizeRelative(
        string? value,
        bool allowEmpty,
        bool allowWildcards = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return allowEmpty ? "" : null;
        var normalized = value.Replace('\\', '/').Trim('/');
        if (Path.IsPathRooted(normalized)
            || normalized.Split('/').Any(part => part is "" or "." or "..")
            || (!allowWildcards && normalized.IndexOfAny(['*', '?']) >= 0))
            return null;
        return normalized;
    }

    private static string ResolveWithin(string root, string relative)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = string.IsNullOrEmpty(relative)
            ? canonicalRoot
            : Path.GetFullPath(Path.Combine(
                canonicalRoot,
                relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(
                canonicalRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Dependency cache path escaped its configured root.");
        return candidate;
    }

    private static string RepositoryKey(string identity)
    {
        var canonical = identity.Trim();
        if (Path.IsPathFullyQualified(canonical))
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonical));
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }

    private static bool MatchesGlob(string pattern, string path)
        => MatchesSegments(
            pattern.Split('/', StringSplitOptions.RemoveEmptyEntries),
            0,
            path.Split('/', StringSplitOptions.RemoveEmptyEntries),
            0);

    private static bool MatchesSegments(
        IReadOnlyList<string> pattern,
        int patternIndex,
        IReadOnlyList<string> path,
        int pathIndex)
    {
        if (patternIndex == pattern.Count) return pathIndex == path.Count;
        if (pattern[patternIndex] == "**")
        {
            return MatchesSegments(pattern, patternIndex + 1, path, pathIndex)
                   || (pathIndex < path.Count
                       && MatchesSegments(pattern, patternIndex, path, pathIndex + 1));
        }
        return pathIndex < path.Count
               && System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                   pattern[patternIndex],
                   path[pathIndex],
                   ignoreCase: OperatingSystem.IsWindows())
               && MatchesSegments(pattern, patternIndex + 1, path, pathIndex + 1);
    }

    private static bool IsSameOrChild(string candidate, string parent)
        => candidate.Equals(parent, StringComparison.OrdinalIgnoreCase)
           || candidate.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);

    private static bool HasWildcard(string value) => value.IndexOfAny(['*', '?']) >= 0;

    private static string Combine(string left, string right)
        => string.IsNullOrWhiteSpace(left) ? right : left.TrimEnd('/') + "/" + right;

    private static string SafeSegment(string value)
        => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());
}
