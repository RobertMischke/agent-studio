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
    /// <summary>
    /// How long a swap keeps retrying while a concurrent reader holds the
    /// destination open. Readers of these stores are short plain opens
    /// (<c>File.ReadAllText</c>); one millisecond between attempts lets them
    /// finish, and two seconds is far beyond any read that is not a hang.
    /// </summary>
    private static readonly TimeSpan SwapRetryBudget = TimeSpan.FromSeconds(2);

    public void Write(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, content);
            MoveWithRetry(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Swaps the temp file into place so that CONCURRENT PLAIN READERS never
    /// observe a missing, locked, or truncated destination.
    ///
    /// <para><c>File.Move(overwrite: true)</c> is a single rename-with-replace
    /// (<c>rename(2)</c> on POSIX, <c>FileRenameInformation</c> with
    /// ReplaceIfExists on NTFS): the destination name always resolves to either
    /// the old or the new file. What it does NOT tolerate is a reader holding
    /// the destination open without delete sharing - Windows then refuses the
    /// rename (<see cref="IOException"/> sharing violation or
    /// <see cref="UnauthorizedAccessException"/> access denied) and the writer
    /// must retry a moment later instead of dropping the mutation.</para>
    ///
    /// <para><c>File.Replace</c> (ReplaceFile) is the wrong tool here despite
    /// its name: Windows implements it as two renames (destination to backup,
    /// then replacement to destination), so between them the destination name
    /// does not exist and a concurrent open fails with FileNotFound - measured
    /// at roughly twenty missed opens per write under a spinning reader, versus
    /// none with the single rename. It is also no more tolerant of an open
    /// reader, because the first of its two renames needs the same delete
    /// access.</para>
    /// </summary>
    private static void MoveWithRetry(string tempPath, string path)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                File.Move(tempPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException
                && started.Elapsed < SwapRetryBudget)
            {
                Thread.Sleep(1);
            }
        }
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
