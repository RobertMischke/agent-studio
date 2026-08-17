using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The AGT-W34 slice S4 admission matrix. The public read-only demo edge is
/// deny-by-default: a request is admitted only when it is same-origin, over TLS,
/// uses a safe method, stays inside the body ceiling, and matches the explicit
/// endpoint allowlist. Every other outcome is a typed denial.
/// </summary>
public sealed class PublicDemoEdgePolicyTests
{
    private static readonly PublicDemoLimits Limits = new(16 * 1024);

    private static PublicDemoVerdict Evaluate(
        string method,
        string path,
        bool https = true,
        bool sameOrigin = true,
        long? contentLength = null)
        => PublicDemoEdgePolicy.Evaluate(
            new PublicDemoRequest(method, path, https, sameOrigin, contentLength), Limits);

    [Theory]
    [InlineData("/api/tasks")]
    [InlineData("/api/tasks/grouped")]
    [InlineData("/api/tasks/DEMO-14")]
    [InlineData("/api/tasks/DEMO-14/conversation")]
    [InlineData("/api/tasks/DEMO-14/results/report.md")]
    [InlineData("/api/projects")]
    [InlineData("/api/projects/demo-app/wiki/tree")]
    [InlineData("/api/projects/demo-app/wiki/files/architecture/overview.md")]
    [InlineData("/api/projects/demo-app/workbenches/agt-w34")]
    [InlineData("/api/orchestrator/context/project:demo-app")]
    [InlineData("/api/bus/demo-app/recent")]
    [InlineData("/api/auth/status")]
    [InlineData("/hubs/jobs")]
    [InlineData("/index.html")]
    public void AllowlistedReads_AreAdmitted(string path)
        => Assert.Equal(PublicDemoOutcome.Allow, Evaluate("GET", path).Outcome);

    [Theory]
    [InlineData("POST", "/api/tasks")]
    [InlineData("PUT", "/api/tasks/DEMO-14")]
    [InlineData("PATCH", "/api/tasks/DEMO-14")]
    [InlineData("DELETE", "/api/tasks/DEMO-14")]
    [InlineData("POST", "/api/auth/login")]
    [InlineData("POST", "/api/runner/claim")]
    [InlineData("POST", "/api/v1/management/commands")]
    [InlineData("POST", "/api/clients/register")]
    [InlineData("POST", "/api/tasks/batch-move")]
    public void UnsafeMethods_ReturnTheReadOnlyDenial(string method, string path)
    {
        var verdict = Evaluate(method, path);
        Assert.Equal(403, verdict.Status);
        Assert.Equal("public-demo-read-only", verdict.Error);
    }

    /// <summary>
    /// The read-only verdict must win over the allowlist so a probe against an
    /// unmapped path still learns the launch invariant rather than a 404 it could
    /// mistake for "route exists elsewhere".
    /// </summary>
    [Fact]
    public void UnsafeMethod_OnUnknownPath_StillReadsAsReadOnly()
        => Assert.Equal("public-demo-read-only", Evaluate("POST", "/api/does-not-exist").Error);

    [Theory]
    [InlineData("/api/auth/users")]
    [InlineData("/api/auth/runners")]
    [InlineData("/api/admin/config")]
    [InlineData("/api/diagnostics")]
    [InlineData("/api/devtools/e2e-jobs")]
    [InlineData("/api/filesystem-layer/snapshot")]
    [InlineData("/api/supervisor/status")]
    [InlineData("/api/v1/management/status")]
    [InlineData("/api/git/inventory")]
    [InlineData("/api/cli/maintenance-model")]
    [InlineData("/api/runner/global")]
    [InlineData("/api/runner/lease/DEMO-14")]
    [InlineData("/api/projects/demo-app/security")]
    [InlineData("/api/projects/demo-app/security/files/report.md")]
    [InlineData("/api/projects/demo-app/steering/files/notes.md")]
    [InlineData("/api/projects/demo-app/publish-status")]
    [InlineData("/api/tasks/DEMO-14/files/prompt.md")]
    [InlineData("/api/tasks/DEMO-14/git/diff")]
    [InlineData("/api/workspace/settings")]
    [InlineData("/api/maintenance/status")]
    public void NonAllowlistedReads_AreDenied(string path)
    {
        var verdict = Evaluate("GET", path);
        Assert.Equal(404, verdict.Status);
        Assert.Equal("public-demo-endpoint-denied", verdict.Error);
    }

    [Fact]
    public void PlainHttp_IsRefusedBeforeAnythingElse()
    {
        var verdict = Evaluate("GET", "/api/tasks", https: false);
        Assert.Equal(426, verdict.Status);
        Assert.Equal("public-demo-https-required", verdict.Error);
    }

    [Fact]
    public void CrossOriginRequest_IsRefusedEvenOnAnAllowlistedRead()
    {
        var verdict = Evaluate("GET", "/api/tasks", sameOrigin: false);
        Assert.Equal(403, verdict.Status);
        Assert.Equal("public-demo-cross-origin-denied", verdict.Error);
    }

    [Fact]
    public void OversizedBody_IsRefused()
    {
        var verdict = Evaluate("POST", "/hubs/jobs/negotiate", contentLength: 64 * 1024);
        Assert.Equal(413, verdict.Status);
        Assert.Equal("public-demo-body-too-large", verdict.Error);
    }

    [Fact]
    public void SignalRNegotiate_IsTheOnlyAdmittedPost()
    {
        Assert.Equal(PublicDemoOutcome.Allow, Evaluate("POST", "/hubs/jobs/negotiate").Outcome);
        Assert.Equal("public-demo-read-only", Evaluate("POST", "/hubs/jobs").Error);
        Assert.Equal("public-demo-read-only", Evaluate("POST", "/hubs/other/negotiate").Error);
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/healthz/drain")]
    public void HealthProbes_SurviveEveryOtherRule(string path)
        => Assert.Equal(PublicDemoOutcome.Allow, Evaluate("GET", path, https: false, sameOrigin: false).Outcome);

    [Fact]
    public void TrailingSlash_DoesNotBypassTheAllowlist()
    {
        Assert.Equal(PublicDemoOutcome.Allow, Evaluate("GET", "/api/tasks/").Outcome);
        Assert.Equal("public-demo-endpoint-denied", Evaluate("GET", "/api/diagnostics/").Error);
    }
}
