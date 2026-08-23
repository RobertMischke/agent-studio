using System.Collections.Concurrent;
using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The swap contract of <see cref="AtomicJsonFileWriter"/>: a concurrent plain
/// reader (<c>File.ReadAllText</c>, the way every task.json consumer opens the
/// file) may collide with the swap and see a transient sharing violation, but
/// it must never see the destination name missing, a truncated document, or a
/// stale temp file left behind. On Windows the missing-name case is exactly
/// what <c>File.Replace</c> (ReplaceFile's two renames) produced; the single
/// rename-with-replace keeps the name continuously valid.
/// </summary>
public sealed class AtomicJsonFileWriterTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "atomic-json-writer-" + Guid.NewGuid().ToString("N"));

    public AtomicJsonFileWriterTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Write_UnderConcurrentPlainReader_KeepsTheDestinationNameValidAndWhole()
    {
        var path = Path.Combine(_dir, "task.json");
        var writer = new AtomicJsonFileWriter();
        writer.Write(path, """{"id":"fixture","tags":[]}""");

        var failures = new ConcurrentQueue<Exception>();
        var reading = true;
        long successfulReads = 0;
        using var started = new ManualResetEventSlim();
        var reader = Task.Run(() =>
        {
            started.Set();
            while (Volatile.Read(ref reading))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    if (document.RootElement.GetProperty("id").GetString() != "fixture")
                        throw new InvalidDataException("reader saw a foreign document");
                    Interlocked.Increment(ref successfulReads);
                }
                catch (IOException ex) when (IsSharingViolation(ex))
                {
                    // A reader that opens while the rename is in flight can be
                    // refused for that instant; that is the one tolerated
                    // collision. FileNotFound, partial JSON and foreign ids
                    // are failures.
                    Thread.Yield();
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            }
        });
        started.Wait();

        var large = Enumerable.Range(0, 2_000).Select(i => $"\"integration:test-{i:D4}\"");
        var bigDocument = $$"""{"id":"fixture","tags":[{{string.Join(",", large)}}]}""";
        const string smallDocument = """{"id":"fixture","tags":["integration:pending"]}""";
        try
        {
            for (var i = 0; i < 60; i++)
                writer.Write(path, i % 2 == 0 ? bigDocument : smallDocument);
        }
        finally
        {
            Volatile.Write(ref reading, false);
            await reader.WaitAsync(Deadline);
        }

        Assert.Empty(failures);
        Assert.True(Interlocked.Read(ref successfulReads) > 0, "the reader must have observed the file");
        Assert.Equal(smallDocument, File.ReadAllText(path));
        Assert.Equal(["task.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Write_FirstWrite_CreatesDirectoryAndFile()
    {
        var path = Path.Combine(_dir, "nested", "deeper", "state.json");
        new AtomicJsonFileWriter().Write(path, """{"ok":true}""");

        Assert.Equal("""{"ok":true}""", File.ReadAllText(path));
        Assert.Equal(["state.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact]
    public void Write_Overwrite_ReplacesContentWithoutLeavingTempFiles()
    {
        var path = Path.Combine(_dir, "state.json");
        var writer = new AtomicJsonFileWriter();
        writer.Write(path, """{"version":1}""");
        writer.Write(path, """{"version":2}""");

        Assert.Equal("""{"version":2}""", File.ReadAllText(path));
        Assert.Equal(["state.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    private static bool IsSharingViolation(IOException exception)
    {
        if (exception is FileNotFoundException or DirectoryNotFoundException) return false;
        if (!OperatingSystem.IsWindows()) return false;
        return (exception.HResult & 0xffff) is 32 or 33;
    }
}
