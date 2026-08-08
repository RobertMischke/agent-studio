using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ClientIdentityEndpointsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agent-studio-client-endpoints-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task CorruptIdentity_IsVisibleInTheRegistry_AndReturnsARepairableConflict()
    {
        var identities = Path.Combine(_root, "identities");
        Directory.CreateDirectory(identities);
        File.WriteAllBytes(Path.Combine(identities, "agent-runner-01.json"), new byte[4481]);
        await using var factory = BuildFactory();
        using var client = factory.CreateClient();

        var summaries = await client.GetFromJsonAsync<List<ClientSummary>>("/api/clients/");

        Assert.NotNull(summaries);
        Assert.Contains(summaries!, summary => summary.Id == DefaultClientIdentity.Id);
        var diagnostic = Assert.Single(summaries!, summary => summary.Id == "agent-runner-01");
        Assert.Equal("identity file corrupt: agent-runner-01.json", diagnostic.IdentityFileError);
        Assert.Contains("POST /api/clients/register", diagnostic.IdentityRestoreHint, StringComparison.Ordinal);

        var response = await client.GetAsync("/api/clients/agent-runner-01");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IdentityConflictResponse>();
        Assert.Equal("identity-file-corrupt", body?.Error);
        Assert.Equal("agent-runner-01.json", body?.File);
        Assert.Contains("POST /api/clients/register", body?.Hint, StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["Logging:BackendFile:LogDirectory"] = Path.Combine(_root, "logs"),
                }));
        });

    private sealed record IdentityConflictResponse
    {
        public string Error { get; init; } = "";
        public string File { get; init; } = "";
        public string Hint { get; init; } = "";
    }
}
