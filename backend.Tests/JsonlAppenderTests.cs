using OrchestratorApi.Services.Persistence;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the IJsonlAppender contract: parent directories are created on
/// demand, concurrent appenders serialise correctly, embedded newlines
/// in serialised records are flattened so each on-disk line stays
/// parseable on its own.
/// </summary>
public class JsonlAppenderTests : IDisposable
{
    private readonly string _root;

    public JsonlAppenderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "jsonl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task AppendAsync_CreatesParentDirectoryAndWritesLine()
    {
        var path = Path.Combine(_root, "nested", "subdir", "log.jsonl");
        var appender = new JsonlAppender();
        await appender.AppendAsync(path, new { hello = "world", n = 42 });

        Assert.True(File.Exists(path));
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Single(lines);
        Assert.Contains("\"hello\"", lines[0]);
        Assert.Contains("\"n\":42", lines[0]);
    }

    [Fact]
    public async Task AppendLineAsync_FlattensEmbeddedNewlines()
    {
        var path = Path.Combine(_root, "flatten.jsonl");
        var appender = new JsonlAppender();
        await appender.AppendLineAsync(path, "first\nsecond\r\nthird");

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Single(lines);
        Assert.DoesNotContain("\n", lines[0]);
        Assert.Contains("first", lines[0]);
        Assert.Contains("second", lines[0]);
        Assert.Contains("third", lines[0]);
    }

    [Fact]
    public async Task AppendAsync_ConcurrentAppenders_DoNotInterleave()
    {
        var path = Path.Combine(_root, "concurrent.jsonl");
        var appender = new JsonlAppender();
        const int N = 100;

        await Task.WhenAll(Enumerable.Range(0, N).Select(i =>
            appender.AppendAsync(path, new { id = i, payload = new string('x', 200) })));

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(N, lines.Length);
        // Every line must be parseable JSON on its own.
        foreach (var line in lines)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
        }
    }
}
