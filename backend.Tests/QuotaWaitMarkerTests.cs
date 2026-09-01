using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2699: <see cref="QuotaWaitMarker.Clear"/> now reports whether it
/// actually deleted a file, so <c>ProjectRunner.ClearQuotaWait</c> can skip
/// an index-cache invalidation on the common no-op case (a candidate that
/// never had a quota-wait marker).
/// </summary>
public sealed class QuotaWaitMarkerTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "atp-quota-wait-marker-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Clear_WithNoMarkerFile_ReturnsFalse()
    {
        Directory.CreateDirectory(_folder);

        Assert.False(QuotaWaitMarker.Clear(_folder, NullLogger.Instance));
    }

    [Fact]
    public void Clear_OnMissingFolder_ReturnsFalse()
    {
        Assert.False(QuotaWaitMarker.Clear(
            Path.Combine(_folder, "does-not-exist"), NullLogger.Instance));
    }

    [Fact]
    public void Clear_WithExistingMarkerFile_DeletesItAndReturnsTrue()
    {
        QuotaWaitMarker.Write(_folder, new QuotaWaitRecord
        {
            CliType = "claude",
            ResetAt = DateTime.UtcNow.AddHours(1),
            ThresholdMinutes = 30,
            Reason = "claude: limited",
        });
        Assert.True(File.Exists(Path.Combine(_folder, QuotaWaitMarker.FileName)));

        Assert.True(QuotaWaitMarker.Clear(_folder, NullLogger.Instance));

        Assert.False(File.Exists(Path.Combine(_folder, QuotaWaitMarker.FileName)));
    }
}
