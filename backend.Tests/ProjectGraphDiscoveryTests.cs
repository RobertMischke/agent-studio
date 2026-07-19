using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.ProjectGraph;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectGraphDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "project-graph-" + Guid.NewGuid().ToString("N"));

    public ProjectGraphDiscoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Scan_DiscoversCurrentProductConstellationAndOnlyInternalEdges()
    {
        var agt = Repo("agt");
        var car = Repo("car");
        var cac = Repo("cac");
        var te = Repo("te");
        var web = Repo("web");

        Write(agt, "agent-taskboard.sln", "Microsoft Visual Studio Solution File\n");
        Write(agt, ".github/workflows/backend-ci.yml", "name: backend\n");
        Write(agt, "backend/OrchestratorApi.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup>
              <PackageReference Include="CodingAgentRunner" Version="0.5.0" />
              <ProjectReference Include="{{Path.GetRelativePath(Path.Combine(agt, "backend"), Path.Combine(te, "src", "TokenEconomy", "TokenEconomy.csproj"))}}" />
              <ProjectReference Include="C:\Secret Folder\Missing.csproj" />
            </ItemGroup></Project>
            """);
        Write(agt, "frontend/package.json", """{"name":"agent-studio","dependencies":{"@angular/core":"^21.0.0","coding-agent-chat":"file:../../../coding-agent-chat","missing-local":"file:C:/Secret Folder/missing-ui"},"devDependencies":{"@playwright/test":"^1.50.0"}}""");
        Write(agt, "frontend/angular.json", """{"projects":{"studio":{"projectType":"application","root":"src"}}}""");
        Write(agt, "frontend/src/app.ts", "export const studio = true;\n");

        Write(car, "CodingAgentRunner.slnx", "<Solution />\n");
        Write(car, "src/CodingAgentRunner/CodingAgentRunner.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>CodingAgentRunner</PackageId></PropertyGroup></Project>""");
        Write(cac, "package.json", """{"name":"coding-agent-chat","dependencies":{"@angular/core":"^21.0.0"}}""");
        Write(cac, "angular.json", """{"projects":{"coding-agent-chat":{"projectType":"library","root":"projects/coding-agent-chat"},"lab":{"projectType":"application","root":"projects/lab"}}}""");
        Write(cac, "projects/coding-agent-chat/index.ts", "export const chat = true;\n");
        Write(cac, "projects/lab/main.ts", "bootstrapApplication();\n");
        Write(te, "TokenEconomy.slnx", "<Solution />\n");
        Write(te, "src/TokenEconomy/TokenEconomy.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>TokenEconomy</PackageId></PropertyGroup></Project>""");
        Write(web, "04-angular-static-final/package.json", """{"name":"agent-studio-website","dependencies":{"@angular/core":"^21.0.0"},"devDependencies":{"typescript":"^5.9.0"}}""");
        Write(web, "04-angular-static-final/angular.json", """{"projects":{"website":{"projectType":"application","root":"src"}}}""");
        Write(web, "04-angular-static-final/src/main.ts", "bootstrapApplication();\n");

        var catalog = ProjectGraphScanner.Scan([
            Target("PROJ-002", "AGT", "Agent Studio", agt, 0),
            Target("PROJ-011", "CAR", "Coding Agent Runner", car, 1),
            Target("PROJ-014", "CAC", "Coding Agent Chat", cac, 2),
            Target("PROJ-015", "TE", "Token Economy", te, 3),
            Target("PROJ-012", "WEB", "Agent Studio Website", web, 4),
        ], NullLogger.Instance, new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(["AGT", "CAR", "CAC", "TE", "WEB"], catalog.Projects.Select(project => project.Key));
        Assert.All(catalog.Projects, project => Assert.Equal("ready", project.Status));
        Assert.All(catalog.Projects, project =>
        {
            Assert.Null(project.SourceRevision);
            Assert.Equal("unavailable", project.SourceState);
        });
        Assert.Contains(catalog.Projects.Single(project => project.Key == "AGT").Workflows, path => path == ".github/workflows/backend-ci.yml");
        Assert.Contains(catalog.Components, component => component.ProjectKey == "AGT" && component.Technologies.Any(value => value.Slug == "aspnet-core"));
        Assert.Contains(catalog.Components, component => component.ProjectKey == "AGT" && component.Technologies.Any(value => value.Slug == "dotnet") && component.Technologies.Any(value => value.Slug == "csharp"));
        Assert.Contains(catalog.Components, component => component.ProjectKey == "WEB" && component.Technologies.Any(value => value.Slug == "angular"));
        Assert.Contains(catalog.Dependencies, edge => EdgeProjects(catalog, edge) == ("AGT", "CAR") && edge.Kind == "package");
        Assert.Contains(catalog.Dependencies, edge => EdgeProjects(catalog, edge) == ("AGT", "CAC") && edge.Kind == "package");
        Assert.Contains(catalog.Dependencies, edge => EdgeProjects(catalog, edge) == ("AGT", "TE") && edge.Kind == "project-reference");
        Assert.DoesNotContain(catalog.Dependencies, edge => edge.Evidence.Contains("@angular/core", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(catalog.Dependencies, edge => edge.Evidence.Contains(agt, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(catalog.Dependencies, edge => edge.Evidence.Contains("file:<local-path>", StringComparison.Ordinal));
        Assert.Contains(catalog.Dependencies, edge => edge.Resolution == "unresolved" && edge.ToComponentId is null && edge.TargetHint!.Contains("<local-path>", StringComparison.Ordinal));
        Assert.DoesNotContain(catalog.Dependencies, edge => edge.Evidence.Contains("Secret Folder", StringComparison.OrdinalIgnoreCase));
        Assert.All(catalog.Components, component => Assert.StartsWith("proj-", component.Id));
        Assert.All(catalog.Components, component => Assert.True(component.Size.Files > 0));
        Assert.True(catalog.Projects.Single(project => project.Key == "AGT").Size.Lines >= 5);
    }

    [Fact]
    public void Scan_ContainsMissingAndUnreadableProjectsWithoutInventingComponents()
    {
        var catalog = ProjectGraphScanner.Scan([
            Target("PROJ-404", "MISS", "Missing", Path.Combine(_root, "missing"), 0),
        ], NullLogger.Instance);

        var project = Assert.Single(catalog.Projects);
        Assert.Equal("unavailable", project.Status);
        Assert.Empty(project.ComponentIds);
        Assert.Contains(project.Warnings, warning => warning.Contains("No repository checkout", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_DoesNotEnterNestedRepositoriesOrDirectoryLinks()
    {
        var repository = Repo("bounded");
        Write(repository, "package.json", """{"name":"bounded-root"}""");
        Write(repository, "nested-checkout/.git", "gitdir: elsewhere\n");
        Write(repository, "nested-checkout/package.json", """{"name":"must-not-be-discovered"}""");

        var outside = Repo("outside");
        Write(outside, "package.json", """{"name":"must-not-escape"}""");
        var link = Path.Combine(repository, "linked-outside");
        var linkCreated = false;
        try
        {
            Directory.CreateSymbolicLink(link, outside);
            linkCreated = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            linkCreated = false;
        }

        var catalog = ProjectGraphScanner.Scan([
            Target("PROJ-BOUND", "BOUND", "Bounded", repository, 0),
        ], NullLogger.Instance);

        var component = Assert.Single(catalog.Components);
        Assert.Equal("bounded-root", component.Name);
        Assert.True(!linkCreated || catalog.Components.Count == 1);
        Assert.DoesNotContain(catalog.Components, item => item.Name is "must-not-be-discovered" or "must-not-escape");
    }

    [Fact]
    public void Scan_ComponentIdentityUsesStableRegistryIdNotMutableShortCode()
    {
        var repository = Repo("identity");
        Write(repository, "src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");

        var before = ProjectGraphScanner.Scan([
            Target("PROJ-777", "OLD", "Identity", repository, 0),
        ], NullLogger.Instance);
        var after = ProjectGraphScanner.Scan([
            Target("PROJ-777", "NEW", "Identity renamed", repository, 0),
        ], NullLogger.Instance);

        Assert.Equal(Assert.Single(before.Components).Id, Assert.Single(after.Components).Id);
        Assert.Equal("PROJ-777", Assert.Single(after.Components).ProjectId);
        Assert.Equal("NEW", Assert.Single(after.Components).ProjectKey);
    }

    [Fact]
    public void Scan_DoesNotFollowManifestFileLinksOutsideRepository()
    {
        var repository = Repo("file-link");
        Write(repository, "package.json", """{"name":"safe-root"}""");
        var outside = Repo("file-link-outside");
        Write(outside, "package.json", """{"name":"must-not-escape"}""");
        var linkedDirectory = Path.Combine(repository, "linked");
        Directory.CreateDirectory(linkedDirectory);
        var linkedManifest = Path.Combine(linkedDirectory, "package.json");
        var linkCreated = false;
        try
        {
            File.CreateSymbolicLink(linkedManifest, Path.Combine(outside, "package.json"));
            linkCreated = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            linkCreated = false;
        }

        var catalog = ProjectGraphScanner.Scan([
            Target("PROJ-LINK", "LINK", "File link", repository, 0),
        ], NullLogger.Instance);

        Assert.Contains(catalog.Components, component => component.Name == "safe-root");
        Assert.DoesNotContain(catalog.Components, component => component.Name == "must-not-escape");
        if (linkCreated)
        {
            Assert.Single(catalog.Components);
            Assert.Contains(Assert.Single(catalog.Projects).Warnings, warning =>
                warning == "Skipped linked file 'linked/package.json'.");
        }
    }

    [Fact]
    public void Scan_SkipsEveryOversizedManifestInputBeforeParsingOrInventory()
    {
        var repository = Repo("oversized-manifests");
        var oversized = new string(' ', (2 * 1024 * 1024) + 1);
        Write(repository, "package.json", oversized);
        Write(repository, "angular.json", oversized);
        Write(repository, "Huge.csproj", oversized);
        Write(repository, "Huge.sln", oversized);
        Write(repository, "Huge.slnx", oversized);
        Write(repository, ".github/workflows/huge.yml", oversized);

        var catalog = ProjectGraphScanner.Scan([
            Target("PROJ-LARGE", "LARGE", "Oversized", repository, 0),
        ], NullLogger.Instance);

        var project = Assert.Single(catalog.Projects);
        Assert.Empty(catalog.Components);
        Assert.Empty(project.Solutions);
        Assert.Empty(project.Workflows);
        Assert.Equal(6, project.Warnings.Count(warning => warning.StartsWith("Skipped oversized manifest", StringComparison.Ordinal)));
        Assert.All(project.Warnings, warning => Assert.Contains("limit 2 MiB", warning, StringComparison.Ordinal));
    }

    private static (string From, string? To) EdgeProjects(ProjectGraphCatalog catalog, ProjectGraphDependency edge)
    {
        var from = catalog.Components.Single(component => component.Id == edge.FromComponentId).ProjectKey;
        var to = edge.ToComponentId is null
            ? null
            : catalog.Components.Single(component => component.Id == edge.ToComponentId).ProjectKey;
        return (from, to);
    }

    private ProjectGraphTarget Target(string id, string key, string name, string path, int order) => new(
        new ProjectRecord
        {
            Id = id,
            ShortCode = key,
            DisplayName = name,
            RepositoryPath = path,
            StorageLocation = Path.Combine(_root, "tasks", id),
            SortOrder = order,
        },
        path);

    private string Repo(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
