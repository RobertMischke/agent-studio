namespace AgentStudio.Persistence;

/// <summary>
/// Injectable atomic-write boundary for small JSON state stores. Tests can
/// force a write failure without relying on platform-specific file permissions.
/// </summary>
public interface IAtomicJsonFileWriter
{
    void Write(string path, string content);
}

public sealed class AtomicJsonFileWriter : IAtomicJsonFileWriter
{
    public void Write(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var backupPath = $"{path}.{Guid.NewGuid():N}.bak";
        try
        {
            File.WriteAllText(tempPath, content);
            ReplaceWithRetry(tempPath, path, backupPath);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            RecoverOrRemoveBackup(backupPath, path);
        }
    }

    /// <summary>
    /// Swaps the temp file into place so that CONCURRENT PLAIN READERS never
    /// observe a missing, locked, or truncated destination. On Windows,
    /// <c>File.Move(overwrite: true)</c> is not reader-transparent: a reader
    /// holding the destination open (FileShare.Read, no Delete) makes the
    /// rename fail, and a reader opening mid-swap gets a sharing violation.
    /// <c>File.Replace</c> (ReplaceFile) keeps the destination name valid
    /// throughout, so new opens see either the old or the new content. Windows
    /// can remove the destination after a failed ReplaceFile operation when no
    /// backup name is supplied, so every attempt supplies a same-volume backup.
    /// A swap attempt that collides with an in-flight reader is retried briefly;
    /// the first-ever write (no destination yet) has no readers and uses the
    /// plain move.
    /// </summary>
    private static void ReplaceWithRetry(string tempPath, string path, string backupPath)
    {
        if (!File.Exists(path))
        {
            File.Move(tempPath, path, overwrite: true);
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException) when (attempt < 200)
            {
                RecoverOrRemoveBackup(backupPath, path);
                Thread.Sleep(1);
            }
        }
    }

    private static void RecoverOrRemoveBackup(string backupPath, string path)
    {
        if (!File.Exists(backupPath)) return;
        if (File.Exists(path))
        {
            File.Delete(backupPath);
            return;
        }

        File.Move(backupPath, path);
    }
}

/// <summary>Typed failure surfaced by strict project metadata writes.</summary>
public sealed class ProjectPersistenceException : IOException
{
    public ProjectPersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
