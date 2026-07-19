namespace AgentStudio.Tests;

internal sealed class ControllableAtomicJsonFileWriter : IAtomicJsonFileWriter
{
    private readonly AtomicJsonFileWriter _inner = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _writesByPath = new(StringComparer.OrdinalIgnoreCase);

    public Func<string, int, bool>? ShouldFail { get; set; }

    public void Write(string path, string content)
    {
        int writeNumber;
        lock (_gate)
        {
            _writesByPath.TryGetValue(path, out var current);
            writeNumber = current + 1;
            _writesByPath[path] = writeNumber;
        }

        if (ShouldFail?.Invoke(path, writeNumber) == true)
            throw new IOException($"Forced JSON write failure for test: {path} (write {writeNumber}).");

        _inner.Write(path, content);
    }

    public int WritesFor(string path)
    {
        lock (_gate) return _writesByPath.GetValueOrDefault(path);
    }
}
