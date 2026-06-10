using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// ADR-0026: per-project autonomy level persists through the
/// <see cref="ProjectSettingsService"/> store and survives a service
/// restart (the store reads the JSON file lazily on the first call).
/// Out-of-range values are clamped server-side.
/// </summary>
public class ProjectSettingsAutonomyTests : IDisposable
{
    private readonly string _workspace;

    public ProjectSettingsAutonomyTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-settings-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void DefaultAutonomyLevel_IsNull_ReadAsBalanced()
    {
        var svc = Build();
        var settings = svc.Get("demo");
        Assert.Null(settings.AutonomyLevel);
    }

    [Fact]
    public void SetAutonomyLevel_PersistsAndSurvivesReload()
    {
        var svc = Build();
        svc.SetAutonomyLevel("demo", 4);
        Assert.Equal(4, svc.Get("demo").AutonomyLevel);

        // Re-build to force a fresh load from disk.
        var reloaded = Build();
        Assert.Equal(4, reloaded.Get("demo").AutonomyLevel);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(7, 4)]
    [InlineData(99, 4)]
    public void SetAutonomyLevel_ClampsToZeroFour(int input, int expected)
    {
        var svc = Build();
        svc.SetAutonomyLevel("demo", input);
        Assert.Equal(expected, svc.Get("demo").AutonomyLevel);
    }

    [Fact]
    public void SetAutonomyLevel_NullClearsTheValue()
    {
        var svc = Build();
        svc.SetAutonomyLevel("demo", 3);
        Assert.Equal(3, svc.Get("demo").AutonomyLevel);

        svc.SetAutonomyLevel("demo", null);
        Assert.Null(svc.Get("demo").AutonomyLevel);
    }

    [Fact]
    public void DifferentProjects_KeepIndependentLevels()
    {
        var svc = Build();
        svc.SetAutonomyLevel("alpha", 0);
        svc.SetAutonomyLevel("beta", 4);

        Assert.Equal(0, svc.Get("alpha").AutonomyLevel);
        Assert.Equal(4, svc.Get("beta").AutonomyLevel);
    }

    private ProjectSettingsService Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
            })
            .Build();
        return new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
    }
}
