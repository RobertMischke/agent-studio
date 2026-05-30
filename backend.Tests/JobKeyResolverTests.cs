using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Registry;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// F45a — canonical-format detection, legacy-to-canonical translation,
/// and the build / parse round-trip for <see cref="TaskKeyResolver"/>.
/// </summary>
public class TaskKeyResolverTests : IDisposable
{
    private readonly string _root;
    private readonly IConfiguration _config;
    private readonly ProjectRegistry _projects;
    private readonly TaskKeyResolver _resolver;

    public TaskKeyResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rdo-jobkey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        _projects = new ProjectRegistry(_config, NullLogger<ProjectRegistry>.Instance);
        _resolver = new TaskKeyResolver(_projects, NullLogger<TaskKeyResolver>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Theory]
    [InlineData("PROJ-001::foo", true)]
    [InlineData("PROJ-123::a-b-c-slug", true)]
    [InlineData("PROJ-1::foo", false)]            // too few digits
    [InlineData("PROJ-001foo", false)]            // missing separator
    [InlineData("C:\\path::slug", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCanonical_MatchesShape(string? key, bool expected)
    {
        Assert.Equal(expected, TaskKeyResolver.IsCanonical(key));
    }

    [Fact]
    public void Build_ComposesCanonical()
    {
        Assert.Equal("PROJ-001::f44-foo", TaskKeyResolver.Build("PROJ-001", "f44-foo"));
    }

    [Fact]
    public void Build_RejectsBadProjectId()
    {
        Assert.Throws<ArgumentException>(() => TaskKeyResolver.Build("foo", "slug"));
        Assert.Throws<ArgumentException>(() => TaskKeyResolver.Build("PROJ-1", "slug"));
        Assert.Throws<ArgumentException>(() => TaskKeyResolver.Build("PROJ-001", ""));
    }

    [Fact]
    public void Parse_SplitsCanonicalKey()
    {
        var (proj, slug) = TaskKeyResolver.Parse("PROJ-007::my-slug");
        Assert.Equal("PROJ-007", proj);
        Assert.Equal("my-slug", slug);
    }

    [Fact]
    public void Parse_ThrowsForLegacyFormat()
    {
        Assert.Throws<FormatException>(() => TaskKeyResolver.Parse("C:/path::slug"));
    }

    [Fact]
    public void Parse_HandlesSlugsContainingColons()
    {
        // The separator is the first "::" only; downstream slugs that
        // happen to contain a colon are returned intact.
        var (proj, slug) = TaskKeyResolver.Parse("PROJ-001::weird::slug::form");
        Assert.Equal("PROJ-001", proj);
        Assert.Equal("weird::slug::form", slug);
    }

    [Fact]
    public void ToCanonical_PassesThroughAlreadyCanonical()
    {
        Assert.Equal("PROJ-001::foo", _resolver.ToCanonical("PROJ-001::foo"));
    }

    [Fact]
    public void ToCanonical_TranslatesLegacyKey_WhenProjectIsRegistered()
    {
        var storage = Path.Combine(_root, "projects", "demo");
        var record = _projects.EnsureProjectForStorage(storage, "Demo", DefaultWorkspace.Id);

        var legacy = $"{storage}::f44-foo";
        var canonical = _resolver.ToCanonical(legacy);

        Assert.Equal($"{record.Id}::f44-foo", canonical);
    }

    [Fact]
    public void ToCanonical_ReturnsNull_WhenLegacyPathIsUnknown()
    {
        var legacy = "C:/nonexistent/path::slug";
        Assert.Null(_resolver.ToCanonical(legacy));
    }

    [Fact]
    public void ToCanonicalOrOriginal_PassthroughOnUnknown()
    {
        var legacy = "C:/nonexistent/path::slug";
        Assert.Equal(legacy, _resolver.ToCanonicalOrOriginal(legacy));
    }

    [Fact]
    public void ToCanonical_AcceptsBackslashPaths()
    {
        // Storage normalises on read; matching the same path with the
        // platform-native slash style should also resolve.
        var stored = "C:\\Projects\\demo";
        _projects.EnsureProjectForStorage(stored, "Demo", DefaultWorkspace.Id);

        var legacy = "C:\\Projects\\demo::my-slug";
        var canonical = _resolver.ToCanonical(legacy);

        Assert.Equal("PROJ-001::my-slug", canonical);
    }

    [Fact]
    public void ToCanonical_NullAndEmpty_ReturnNull()
    {
        Assert.Null(_resolver.ToCanonical(null));
        Assert.Null(_resolver.ToCanonical(""));
    }

    [Fact]
    public void BuildAndParse_RoundTrip()
    {
        var key = TaskKeyResolver.Build("PROJ-042", "a-very-long-slug-with-dashes");
        var (proj, slug) = TaskKeyResolver.Parse(key);
        Assert.Equal("PROJ-042", proj);
        Assert.Equal("a-very-long-slug-with-dashes", slug);
    }
}
