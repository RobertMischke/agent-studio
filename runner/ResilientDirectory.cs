namespace AgentRunner;

/// <summary>
/// Recursive delete that survives the two filesystem facts a plain
/// <see cref="Directory.Delete(string, bool)"/> trips over in a runner workspace.
///
/// 1. Git marks everything under <c>.git/objects</c> read-only. On Linux the
///    directory write bit still permits unlink, so the naive delete works; on
///    Windows the read-only <i>file</i> attribute is enforced and the delete
///    throws <see cref="UnauthorizedAccessException"/>. Every runner directory
///    that ever held a clone is affected - review workspaces, worktrees, push
///    probes.
/// 2. A reparse point (junction/symlink) must be removed, never followed, or a
///    cleanup can walk out of its own root and delete the link target.
///
/// This mirrors the backend's WorktreeTaskLifecycle cleanup, which already had
/// to solve exactly this for managed worktrees.
/// </summary>
public static class ResilientDirectory
{
    /// <summary>
    /// Delete <paramref name="path"/> and everything below it. Missing paths are
    /// a no-op, so callers do not need their own existence check.
    /// </summary>
    public static void Delete(string path)
    {
        if (!Directory.Exists(path)) return;
        DeleteWithoutFollowingReparsePoints(path);
    }

    /// <summary>
    /// Best-effort variant for teardown paths where a still-unwinding child
    /// process may hold a handle and failing would hide the real result.
    /// </summary>
    public static bool TryDelete(string path)
    {
        try
        {
            Delete(path);
            return !Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteWithoutFollowingReparsePoints(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            ClearReadOnly(path, attributes);
            Directory.Delete(path);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var entryAttributes = File.GetAttributes(entry);
            if ((entryAttributes & FileAttributes.Directory) == 0)
            {
                ClearReadOnly(entry, entryAttributes);
                File.Delete(entry);
                continue;
            }

            if ((entryAttributes & FileAttributes.ReparsePoint) != 0)
            {
                ClearReadOnly(entry, entryAttributes);
                Directory.Delete(entry);
                continue;
            }

            DeleteWithoutFollowingReparsePoints(entry);
        }

        ClearReadOnly(path, attributes);
        Directory.Delete(path);
    }

    private static void ClearReadOnly(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReadOnly) == 0) return;
        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }
}
