using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectSourceCatalogTests
{
    [Fact]
    public void Catalog_ExposesLocalDefaultAndReservedExtensionPoints()
    {
        Assert.Equal(ProjectSourceCatalog.LocalFolder, new ProjectRecord().SourceType);
        Assert.Contains(ProjectSourceCatalog.All, source =>
            source.Id == ProjectSourceCatalog.LocalFolder && source.Available);
        Assert.Contains(ProjectSourceCatalog.All, source =>
            source.Id == "remote-git" && !source.Available);
        Assert.Contains(ProjectSourceCatalog.All, source =>
            source.Id == "cloud" && !source.Available);
    }

    [Fact]
    public void ProjectSummary_PreservesConfiguredSourceType()
    {
        var summary = ProjectSummary.From(new ProjectRecord
        {
            Id = "PROJ-001",
            SourceType = ProjectSourceCatalog.LocalFolder,
            DisplayName = "Demo",
        });

        Assert.Equal(ProjectSourceCatalog.LocalFolder, summary.SourceType);
    }

    [Fact]
    public async Task GetProjectSources_ReturnsThePublicCatalogContract()
    {
        var root = Path.Combine(Path.GetTempPath(), "project-source-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Test");
                    builder.ConfigureAppConfiguration((_, config) =>
                        config.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["TaskRepository"] = root,
                            ["Logging:BackendFile:LogDirectory"] = Path.Combine(root, "logs"),
                        }));
                });
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/api/project-sources");
            response.EnsureSuccessStatusCode();
            var catalog = await response.Content.ReadFromJsonAsync<List<ProjectSourceDescriptor>>();

            Assert.NotNull(catalog);
            Assert.Contains(catalog, source => source.Id == ProjectSourceCatalog.LocalFolder && source.Available);
            Assert.Contains(catalog, source => source.Id == "remote-git" && !source.Available);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
