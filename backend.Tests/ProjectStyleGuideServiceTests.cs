using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectStyleGuideServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "style-guide-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BuildCatalogue_DetectsTechnologiesAndFiltersFrontmatterApplicability()
    {
        Directory.CreateDirectory(Path.Combine(_root, "frontend", "src"));
        Directory.CreateDirectory(Path.Combine(_root, "backend"));
        Directory.CreateDirectory(Path.Combine(_root, "docs", "quality"));
        File.WriteAllText(Path.Combine(_root, "frontend", "angular.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "frontend", "src", "panel.scss"), ".panel {}");
        File.WriteAllText(Path.Combine(_root, "backend", "App.csproj"), "<Project />");

        WriteGuide(
            "angular-components.md",
            "angular-components",
            "Angular component guide",
            "{\"projects\":[\"*\"],\"technologies\":[\"angular\"],\"taskAreas\":[\"frontend\"]}");
        WriteGuide(
            "dotnet-backend.md",
            "dotnet-backend",
            ".NET backend guide",
            "{\"projects\":[\"Demo\"],\"technologies\":[\"dotnet\"],\"taskAreas\":[\"backend\"]}");
        WriteGuide(
            "react.md",
            "react-components",
            "React guide",
            "{\"projects\":[\"*\"],\"technologies\":[\"react\"],\"taskAreas\":[\"frontend\"]}");
        File.WriteAllText(Path.Combine(_root, "docs", "quality", "README.md"), "# Ordinary index");

        var catalogue = ProjectStyleGuideService.BuildCatalogue("Demo", _root);

        Assert.Contains("angular", catalogue.Technologies);
        Assert.Contains("dotnet", catalogue.Technologies);
        Assert.Contains("csharp", catalogue.Technologies);
        Assert.Contains("scss", catalogue.Technologies);
        Assert.Collection(
            catalogue.Guides.OrderBy(guide => guide.Id),
            angular =>
            {
                Assert.Equal("angular-components", angular.Id);
                Assert.Equal("quality/angular-components.md", angular.RelPath);
                Assert.Equal(["frontend"], angular.AppliesTo.TaskAreas);
            },
            dotnet => Assert.Equal("dotnet-backend", dotnet.Id));
        Assert.Empty(catalogue.Warnings);
    }

    [Fact]
    public void ParseGuide_RejectsMalformedAppliesToInsteadOfGuessing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "quality"));
        var path = Path.Combine(_root, "docs", "quality", "bad.md");
        File.WriteAllText(path,
            "---\nstyleGuideId: bad\ntitle: Bad\nappliesTo: not-json\n---\n# Bad");

        Assert.Null(ProjectStyleGuideService.ParseGuide(path, _root));
    }

    [Fact]
    public void BuildCatalogue_EmptyApplicabilityDoesNotActAsImplicitWildcardAndInvalidGuideIsReported()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "quality"));
        File.WriteAllText(Path.Combine(_root, "App.csproj"), "<Project />");
        WriteGuide(
            "empty.md",
            "empty",
            "Empty mapping",
            "{\"projects\":[],\"technologies\":[],\"taskAreas\":[\"backend\"]}");
        File.WriteAllText(Path.Combine(_root, "docs", "quality", "broken.md"),
            "---\nstyleGuideId: broken\ntitle: Broken\nappliesTo: not-json\n---\n# Broken");

        var catalogue = ProjectStyleGuideService.BuildCatalogue("Demo", _root);

        Assert.Empty(catalogue.Guides);
        var warning = Assert.Single(catalogue.Warnings);
        Assert.Equal("quality/broken.md", warning.RelPath);
        Assert.Contains("excluded", warning.Message);
    }

    private void WriteGuide(string fileName, string id, string title, string appliesTo)
    {
        File.WriteAllText(Path.Combine(_root, "docs", "quality", fileName),
            $"---\nstyleGuideId: {id}\ntitle: {title}\nversion: 1\nsummary: summary\n" +
            $"promptSummary: Follow {id}.\nappliesTo: {appliesTo}\n---\n# {title}\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }
}
