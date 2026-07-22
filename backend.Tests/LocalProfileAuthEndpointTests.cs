using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The local security profile must report itself truthfully from every auth
/// endpoint. Login/bootstrap/session previously hardcoded "networked" in their
/// AuthStatus response, which mislabelled the shape the browser stores after a
/// local-profile sign-in. These tests pin the derived profile.
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class LocalProfileAuthEndpointTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "studio-local-auth-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Local_profile_status_bootstrap_and_login_report_the_local_profile()
    {
        using var factory = BuildFactory();
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        // The local profile keeps the client-identity boundary; the browser stamps
        // the seeded bootstrap identity on every /api write, so mirror that here.
        browser.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        const string password = "correct horse battery staple!";

        var status = await browser.GetFromJsonAsync<AuthStatusResponse>("/api/auth/status");
        Assert.Equal(SecurityProfiles.Local, status!.Profile);

        var bootstrap = await browser.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            username = "local.owner",
            password,
            displayName = "Local Owner"
        });
        bootstrap.EnsureSuccessStatusCode();
        var bootstrapped = await bootstrap.Content.ReadFromJsonAsync<AuthStatusResponse>();
        Assert.Equal(SecurityProfiles.Local, bootstrapped!.Profile);
        Assert.True(bootstrapped.Authenticated);

        var login = await browser.PostAsJsonAsync("/api/auth/login", new { username = "local.owner", password });
        login.EnsureSuccessStatusCode();
        var loggedIn = await login.Content.ReadFromJsonAsync<AuthStatusResponse>();
        Assert.Equal(SecurityProfiles.Local, loggedIn!.Profile);
        Assert.True(loggedIn.Authenticated);
        Assert.Equal("local.owner", loggedIn.User!.Username);
    }

    private WebApplicationFactory<Program> BuildFactory() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Test");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
            ["Security:Profile"] = "local"
        }));
    });

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch { }
    }
}
