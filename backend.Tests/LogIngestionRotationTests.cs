using System.Text;

using Xunit;

namespace AgentStudio.Tests;

public sealed class LogIngestionRotationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "atp-log-rotation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AppendBounded_RotatesAtEightMiB_AndRetainsNewestTailWithMarker()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "cli-output.log");
        WriteOversizedLineLog(path);
        const string newest = "[21:45:00.000] [stdout] newest-delivery";

        var appended = LogIngestionEndpoints.AppendBounded(
            path,
            newest,
            deliveryReceipt: null,
            markerTimestamp: new DateTime(2026, 7, 25, 21, 45, 0));

        Assert.True(appended);
        var info = new FileInfo(path);
        Assert.True(
            info.Length <= LogIngestionEndpoints.CliOutputLogCapBytes,
            $"Rotated log remained {info.Length} bytes.");
        var content = File.ReadAllText(path);
        Assert.StartsWith("[21:45:00.000] [system] [cli-output-rotated]", content, StringComparison.Ordinal);
        Assert.Contains("old-tail-line", content, StringComparison.Ordinal);
        Assert.EndsWith(newest, content, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendBounded_DuplicateReceipt_DoesNotAppendTwice()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "cli-output.log");
        const string receipt = "[runner-log-delivery:abc123]";
        var payload = $"[21:45:00.000] [stdout] once{Environment.NewLine}"
                      + $"[21:45:00.000] [system] {receipt}";

        Assert.True(LogIngestionEndpoints.AppendBounded(
            path, payload, receipt, new DateTime(2026, 7, 25, 21, 45, 0)));
        Assert.False(LogIngestionEndpoints.AppendBounded(
            path, payload, receipt, new DateTime(2026, 7, 25, 21, 45, 1)));

        Assert.Equal(1, File.ReadLines(path).Count(line => line.Contains("once", StringComparison.Ordinal)));
    }

    private static void WriteOversizedLineLog(string path)
    {
        var line = Encoding.UTF8.GetBytes(
            "[20:00:00.000] [stdout] old-tail-line abcdefghijklmnopqrstuvwxyz"
            + Environment.NewLine);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        while (stream.Length <= LogIngestionEndpoints.CliOutputLogCapBytes)
            stream.Write(line);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
