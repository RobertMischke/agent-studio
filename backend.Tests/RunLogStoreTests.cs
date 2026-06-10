using System.Text;
using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Step 5b: the executor's per-run output store keeps one append-only file +
/// one lock per stream, and merges them by timestamp on read. These tests pin
/// the contract the consolidation path (cli.GetOutput) and the live Activity
/// Log depend on.
/// </summary>
public class RunLogStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "runlogstore-tests", Guid.NewGuid().ToString("N"));

    private string RunDir => Path.Combine(_root, "claude-ASS-1");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private static CliOutputLine Line(string stream, string text, DateTime ts) =>
        new() { Stream = stream, Text = text, Timestamp = ts };

    [Fact]
    public void Append_DistinctStreams_WritesSeparateFiles_AndMergesByTimestamp()
    {
        var t0 = new DateTime(2026, 5, 30, 12, 0, 0, DateTimeKind.Utc);
        using (var store = new RunLogStore(RunDir))
        {
            store.Reset();
            // Interleave stdout/stderr out of file order; merge must reorder by ts.
            store.Append(Line("stdout", "out-1", t0.AddMilliseconds(0)));
            store.Append(Line("stderr", "err-1", t0.AddMilliseconds(10)));
            store.Append(Line("stdout", "out-2", t0.AddMilliseconds(20)));
            store.Append(Line("stderr", "err-2", t0.AddMilliseconds(30)));
        }

        // Each stream is its own file (own lock, never shared).
        Assert.True(File.Exists(Path.Combine(RunDir, "stdout.jsonl")));
        Assert.True(File.Exists(Path.Combine(RunDir, "stderr.jsonl")));

        var merged = RunLogStore.ReadMerged(RunDir);
        Assert.Equal(
            new[] { "out-1", "err-1", "out-2", "err-2" },
            merged.Select(l => l.Text).ToArray());
    }

    [Fact]
    public void ReadMerged_FallsBackToLegacySingleFile_WhenNoPerStreamDir()
    {
        // Pre-5b layout: a single "<runDir>.jsonl" file, no directory.
        Directory.CreateDirectory(_root);
        var legacy = RunDir + ".jsonl";
        var opts = new JsonSerializerOptions { WriteIndented = false };
        var t0 = new DateTime(2026, 5, 30, 12, 0, 0, DateTimeKind.Utc);
        var sb = new StringBuilder();
        sb.AppendLine(JsonSerializer.Serialize(Line("stdout", "legacy-1", t0), opts));
        sb.AppendLine(JsonSerializer.Serialize(Line("stdout", "legacy-2", t0.AddSeconds(1)), opts));
        File.WriteAllText(legacy, sb.ToString(), Encoding.UTF8);

        var merged = RunLogStore.ReadMerged(RunDir);
        Assert.Equal(new[] { "legacy-1", "legacy-2" }, merged.Select(l => l.Text).ToArray());
    }

    [Fact]
    public void Reset_ClearsPreviousRunLines()
    {
        using var store = new RunLogStore(RunDir);
        store.Reset();
        store.Append(Line("stdout", "first-run", DateTime.UtcNow));
        Assert.Single(RunLogStore.ReadMerged(RunDir));

        store.Reset();
        Assert.Empty(RunLogStore.ReadMerged(RunDir));
    }

    [Fact]
    public void DeleteRun_RemovesDirectoryAndLegacyFile()
    {
        using (var store = new RunLogStore(RunDir))
        {
            store.Reset();
            store.Append(Line("stdout", "x", DateTime.UtcNow));
        }
        File.WriteAllText(RunDir + ".jsonl", "{}\n", Encoding.UTF8);

        RunLogStore.DeleteRun(RunDir);

        Assert.False(Directory.Exists(RunDir));
        Assert.False(File.Exists(RunDir + ".jsonl"));
        Assert.Empty(RunLogStore.ReadMerged(RunDir));
    }

    [Fact]
    public void ReadMerged_ToleratesMissingPath()
    {
        Assert.Empty(RunLogStore.ReadMerged(Path.Combine(_root, "does-not-exist")));
        Assert.Empty(RunLogStore.ReadMerged(null));
    }
}
