using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Services;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Pins the contract for the concept-docs loader: known topics resolve
/// to a parsed (topic, title, body) record; unknown topics return
/// <c>null</c>; malformed topic ids (path traversal, dots, slashes,
/// uppercase) are refused at the regex gate so the FE cannot read
/// arbitrary repository files via the open endpoint.
///
/// The two committed seed topics (<c>lane-4-auto-review</c> and
/// <c>lane-3-progress</c>) are loaded from the real
/// <c>docs/concept-docs/</c> folder so the test also doubles as a
/// guard against accidentally deleting or renaming them.
/// </summary>
public class ConceptDocsServiceTests
{
    private static ConceptDocsService NewService() =>
        new(NullLogger<ConceptDocsService>.Instance);

    [Fact]
    public void Get_KnownTopic_ReturnsParsedTitleAndBody()
    {
        var svc = NewService();
        var entry = svc.Get("lane-4-auto-review");
        Assert.NotNull(entry);
        Assert.Equal("lane-4-auto-review", entry!.Topic);
        Assert.Equal("Auto-Review", entry.Title);
        Assert.False(entry.Body.StartsWith("# "), "Body must not include the H1 line.");
        Assert.True(entry.Body.Length > 200, "Body should carry the substantive prose, not a stub.");
    }

    [Fact]
    public void Get_BothSeedTopics_LoadCleanly()
    {
        var svc = NewService();
        Assert.NotNull(svc.Get("lane-4-auto-review"));
        Assert.NotNull(svc.Get("lane-3-progress"));
    }

    [Fact]
    public void Get_UnknownTopic_ReturnsNull()
    {
        var svc = NewService();
        Assert.Null(svc.Get("nonexistent"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../secrets")]
    [InlineData("lane/4")]
    [InlineData("Lane-4-Auto-Review")]
    [InlineData(".hidden")]
    [InlineData("")]
    [InlineData("   ")]
    public void Get_MalformedTopic_RefusedAtGate(string topic)
    {
        var svc = NewService();
        Assert.Null(svc.Get(topic));
    }
}
