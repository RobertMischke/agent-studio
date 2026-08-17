using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The AGT-W34 slice S4 browser boundary: same-origin admission and the two
/// content-security policies. Seeded document content (Wiki pages, Dossiers,
/// evidence, screenshots) is sandboxed separately from the application shell so a
/// script fragment that survived the scrub gate still has no origin authority.
/// </summary>
public sealed class PublicDemoBrowserBoundaryTests
{
    [Theory]
    [InlineData("https://demo.example.org")]
    [InlineData("HTTPS://DEMO.EXAMPLE.ORG")]
    public void MatchingOrigin_IsSameOrigin(string origin)
        => Assert.True(PublicDemoBrowserBoundary.IsSameOrigin(origin, "https", "demo.example.org"));

    [Theory]
    [InlineData("https://evil.example.org")]
    [InlineData("http://demo.example.org")]
    [InlineData("https://demo.example.org:8443")]
    [InlineData("null")]
    [InlineData("not-a-url")]
    public void ForeignOrigin_IsRejected(string origin)
        => Assert.False(PublicDemoBrowserBoundary.IsSameOrigin(origin, "https", "demo.example.org"));

    /// <summary>
    /// A direct navigation and a non-browser client send no Origin. Those are
    /// judged by the rest of the policy (TLS, method, allowlist), not here.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AbsentOrigin_DefersToTheRestOfThePolicy(string? origin)
        => Assert.True(PublicDemoBrowserBoundary.IsSameOrigin(origin, "https", "demo.example.org"));

    [Theory]
    [InlineData("/api/projects/demo-app/wiki/files/architecture/overview.md")]
    [InlineData("/api/projects/demo-app/wiki/assets/diagram.png")]
    [InlineData("/api/projects/demo-app/workbenches/agt-w34")]
    [InlineData("/api/tasks/DEMO-14/results/report.html")]
    [InlineData("/api/tasks/DEMO-14/attachments/shot.png")]
    [InlineData("/api/concept-docs/orchestrator")]
    public void SeededDocuments_GetTheSandboxedPolicy(string path)
    {
        var policy = PublicDemoBrowserBoundary.ContentSecurityPolicyFor(path);
        Assert.Equal(PublicDemoBrowserBoundary.SeededDocumentCsp, policy);
        Assert.StartsWith("sandbox;", policy);
        Assert.Contains("default-src 'none'", policy);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/api/tasks")]
    [InlineData("/api/projects/demo-app/snapshot")]
    public void ApplicationSurface_GetsTheSelfOnlyPolicy(string path)
    {
        var policy = PublicDemoBrowserBoundary.ContentSecurityPolicyFor(path);
        Assert.Equal(PublicDemoBrowserBoundary.ApplicationCsp, policy);
        Assert.DoesNotContain("sandbox", policy);
    }

    /// <summary>No remote embeds, no framing, no form posts, in either policy.</summary>
    [Theory]
    [InlineData(PublicDemoBrowserBoundary.ApplicationCsp)]
    [InlineData(PublicDemoBrowserBoundary.SeededDocumentCsp)]
    public void BothPolicies_CloseTheEmbedAndFramingHoles(string policy)
    {
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Contains("form-action 'none'", policy);
        Assert.Contains("base-uri 'none'", policy);
        Assert.Contains("object-src 'none'", policy);
        Assert.DoesNotContain("http://", policy);
        Assert.DoesNotContain("https://", policy);
    }
}
