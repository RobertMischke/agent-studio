extern alias Runner;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

using RClient = Runner::AgentRunner.TaskServerClient;
using RClaim = Runner::AgentRunner.RunnerClaimRequest;

namespace AgentStudio.Tests;

[CollectionDefinition(WebApplicationFactorySerialCollection.Name, DisableParallelization = true)]
public sealed class WebApplicationFactorySerialCollection
{
    public const string Name = "WebApplicationFactorySerial";
}

[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class NetworkedSecurityEndpointTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "studio-networked-endpoints-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Real_endpoints_bootstrap_session_csrf_and_runner_enrollment_fail_closed()
    {
        using var factory = BuildFactory();
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsJsonAsync("/api/clients/register", new { displayName = "open-runner", kind = "service" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/hubs/jobs/negotiate", null)).StatusCode);

        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://studio.test"), HandleCookies = true });
        var bootstrap = await browser.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            username = "first.owner",
            password = "correct horse battery staple!",
            displayName = "First Owner"
        });
        bootstrap.EnsureSuccessStatusCode();
        var auth = await bootstrap.Content.ReadFromJsonAsync<AuthStatusResponse>();
        Assert.NotNull(auth?.CsrfToken);
        Assert.Contains(bootstrap.Headers.GetValues("Set-Cookie"), value =>
            value.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase)
            && value.Contains("Secure", StringComparison.OrdinalIgnoreCase)
            && value.Contains("SameSite=Strict", StringComparison.OrdinalIgnoreCase));

        var missingCsrf = await browser.PostAsJsonAsync("/api/auth/runner-enrollments", new { name = "runner-01" });
        Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);

        using var enrollmentRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/runner-enrollments")
        {
            Content = JsonContent.Create(new { name = "runner-01" })
        };
        enrollmentRequest.Headers.Add("X-CSRF-Token", auth!.CsrfToken);
        var enrollmentResponse = await browser.SendAsync(enrollmentRequest);
        enrollmentResponse.EnsureSuccessStatusCode();
        var enrollment = await enrollmentResponse.Content.ReadFromJsonAsync<OneTimeEnrollmentResponse>();
        Assert.StartsWith("enr.", enrollment!.EnrollmentCode);

        var enrolledResponse = await anonymous.PostAsJsonAsync("/api/auth/runner-enroll", new { code = enrollment.EnrollmentCode });
        enrolledResponse.EnsureSuccessStatusCode();
        var enrolled = await enrolledResponse.Content.ReadFromJsonAsync<OneTimeSecretResponse>();
        Assert.StartsWith("rnr.", enrolled!.Secret);

        using var selfRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/runner");
        selfRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", enrolled.Secret);
        var selfResponse = await anonymous.SendAsync(selfRequest);
        selfResponse.EnsureSuccessStatusCode();
        var self = await selfResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(enrolled.RunnerId, self.GetProperty("id").GetString());

        var replay = await anonymous.PostAsJsonAsync("/api/auth/runner-enroll", new { code = enrollment.EnrollmentCode });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        using var runner = new RClient(anonymous, enrolled.RunnerId, authToken: enrolled.Secret);
        Assert.Equal(enrolled.RunnerId, await runner.RegisterAsync(enrolled.RunnerName, "service", CancellationToken.None));
        var claim = await runner.ClaimAsync(new RClaim(
            enrolled.RunnerId, enrolled.RunnerName, "runner-host", 42, "remote-runner", AvailableSlots: 1),
            CancellationToken.None);
        Assert.NotEqual(Runner::AgentRunner.RunnerClaimStatus.Invalid, claim.Status);

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logoutRequest.Headers.Add("X-CSRF-Token", auth.CsrfToken);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAsync(logoutRequest)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/tasks")).StatusCode);
    }

    private WebApplicationFactory<Program> BuildFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Test");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
            ["Security:Profile"] = "networked",
            ["AllowedHosts"] = "studio.test"
        }));
    });

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch { }
    }
}
