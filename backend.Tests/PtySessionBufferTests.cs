using System.Text;
using OrchestratorApi.Services.Pty;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Regression: <see cref="PtySession"/> captures a child program's raw stdout
/// into a rolling buffer and runs full-buffer regex replaces on every poll. A
/// misbehaving CLI (a model picker that never settles and spin-renders) can
/// emit output without bound, which made the buffer an OOM + O(n^2) churn
/// vector — a silent-host-death path. The buffer must stay bounded while
/// preserving the most recent screen state.
/// </summary>
public class PtySessionBufferTests
{
    [Fact]
    public void AppendBounded_KeepsTail_WhenExceedingCap()
    {
        var sb = new StringBuilder();
        const int cap = 1000;

        // Append far more than the cap in small chunks, like the read loop.
        string last = "";
        for (var i = 0; i < 100; i++)
        {
            last = new string((char)('a' + i % 26), 100);
            PtySession.AppendBounded(sb, last, cap);
        }

        Assert.True(sb.Length <= cap, $"buffer must stay within cap; got {sb.Length}");
        // The most recent chunk is the tail consumers care about.
        Assert.Equal(last, sb.ToString()[^last.Length..]);
    }

    [Fact]
    public void AppendBounded_NoEviction_WhenUnderCap()
    {
        var sb = new StringBuilder();
        PtySession.AppendBounded(sb, "hello", 1000);
        PtySession.AppendBounded(sb, " world", 1000);
        Assert.Equal("hello world", sb.ToString());
    }

    [Fact]
    public void AppendBounded_SingleOversizedChunk_TruncatedToCap()
    {
        var sb = new StringBuilder();
        const int cap = 50;
        var giant = new string('x', cap * 10);
        PtySession.AppendBounded(sb, giant, cap);
        Assert.Equal(cap, sb.Length);
    }
}
