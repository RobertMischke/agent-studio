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
        try
        {
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
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
