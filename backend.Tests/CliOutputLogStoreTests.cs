using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Regression cover for the "Failed to persist CLI output line" flood that
/// helped take the backend down on 2026-06-03: when the per-job output target
/// went unwritable mid-stream, every streamed CLI line triggered a fresh
/// Directory.CreateDirectory + FileStream open + fsync that threw, the
/// exception was swallowed to a bare false, and the read loop logged a warning
/// per line. These tests pin the breaker + reason-capture contract that bounds
/// the I/O storm and surfaces the cause.
/// </summary>
public class CliOutputLogStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "clioutputlogstore-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private static CliOutputLine Line(string text) =>
        new() { Stream = "stdout", Text = text, Timestamp = DateTime.UtcNow };

    [Fact]
    public void Append_WritablePath_PersistsLine_AndReportsNoError()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "ok", "stdout.jsonl");
        using var store = new CliOutputLogStore(path);

        Assert.True(store.Append(Line("hello")));
        Assert.Null(store.LastErrorMessage);
        Assert.Equal(0, store.TotalFailures);

        var read = CliOutputLogStore.ReadAll(path);
        Assert.Single(read);
        Assert.Equal("hello", read[0].Text);
    }

    [Fact]
    public void Append_TargetUnwritableMidStream_BoundsFilesystemAttempts_AndCapturesReason()
    {
        // Make the would-be parent directory a FILE so EnsureDirectory throws
        // on every attempt - a deterministic, cross-platform stand-in for "job
        // folder vanished / quarantined / disk full while the CLI streamed".
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "target");
        File.WriteAllText(blocker, "i am a file, not a directory");
        var path = Path.Combine(blocker, "stdout.jsonl");

        using var store = new CliOutputLogStore(path);

        const int lines = 1000;
        for (var i = 0; i < lines; i++)
            Assert.False(store.Append(Line($"line-{i}")));

        // The breaker must stop hammering the filesystem: a tiny burst of real
        // attempts, then cheap short-circuits for the rest. Pre-fix every one
        // of the 1000 lines attempted (and failed) a full open + fsync.
        Assert.True(
            store.TotalFailures < 50,
            $"expected the breaker to bound filesystem attempts well under the line count; got {store.TotalFailures} of {lines}");

        // The cause is captured, not swallowed to a bare false.
        Assert.NotNull(store.LastErrorMessage);
    }

    [Fact]
    public void Append_RecoversWhenTargetBecomesWritable_AndClearsError()
    {
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "target");
        File.WriteAllText(blocker, "blocker");
        var path = Path.Combine(blocker, "stdout.jsonl");

        using var store = new CliOutputLogStore(path);

        // A few failures, but stay under the backoff threshold so recovery is
        // immediate (no dependence on the cooldown window elapsing in a test).
        for (var i = 0; i < 3; i++)
            Assert.False(store.Append(Line($"fail-{i}")));
        Assert.NotNull(store.LastErrorMessage);

        // Target becomes writable: drop the blocking file so the parent dir
        // can be created.
        File.Delete(blocker);

        Assert.True(store.Append(Line("recovered")));
        Assert.Null(store.LastErrorMessage);

        var read = CliOutputLogStore.ReadAll(path);
        Assert.Single(read);
        Assert.Equal("recovered", read[0].Text);
    }
}
