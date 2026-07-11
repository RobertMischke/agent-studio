using AgentStudio.Cli;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Burn-rate projection math (AGT-2055 req 6): does a window's current pace
/// project past its cap before it resets?
/// </summary>
public sealed class QuotaWindowProjectionTests
{
    // Fixed clock; scripts never touch the wall clock.
    private static readonly DateTime Now = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

    private static CliQuotaCapsService Caps()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // No TaskRepository -> LocalAppData path that won't exist in CI ->
                // every GetCap returns the 95% default. That is exactly what we want.
                ["TaskRepository"] = Path.Combine(Path.GetTempPath(), "atp-proj-" + Guid.NewGuid().ToString("N")),
            })
            .Build();
        return new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, config);
    }

    [Theory]
    [InlineData("5-hour", 5 * 60)]
    [InlineData("5h session", 5 * 60)]
    [InlineData("Weekly", 7 * 24 * 60)]
    [InlineData("7-day", 7 * 24 * 60)]
    [InlineData("Monthly", 30 * 24 * 60)]
    [InlineData("Daily", 24 * 60)]
    public void InferWindowLength_MapsHumanLabels(string label, int expectedMinutes)
    {
        var len = QuotaWindowProjection.InferWindowLength(label);
        Assert.NotNull(len);
        Assert.Equal(expectedMinutes, (int)len!.Value.TotalMinutes);
    }

    [Fact]
    public void InferWindowLength_UnknownLabel_IsNull()
        => Assert.Null(QuotaWindowProjection.InferWindowLength("credits"));

    [Fact]
    public void Project_HalfwayThrough5HourAt60Pct_ProjectsBreach()
    {
        // 5-hour window, resets in 2.5h -> 2.5h elapsed -> 50% of window gone.
        // 60% used at the halfway point extrapolates to 120% by reset.
        var window = new QuotaWindow
        {
            Label = "5-hour",
            UsedPct = 60,
            ResetAt = Now.AddHours(2.5),
        };

        var proj = QuotaWindowProjection.Project(window, Now, capPct: 95);

        Assert.NotNull(proj);
        Assert.True(proj!.BreachesBeforeReset);
        Assert.Equal(120, proj.ProjectedUsedPct, precision: 0);
        Assert.Equal(24, proj.BurnRatePctPerHour, precision: 0); // 60% over 2.5h
        Assert.Equal(2.5, proj.HoursRemaining, precision: 1);
    }

    [Fact]
    public void Project_SlowBurn_DoesNotProjectBreach()
    {
        // 20% used at the halfway point -> projects to 40%, well under the cap.
        var window = new QuotaWindow { Label = "5-hour", UsedPct = 20, ResetAt = Now.AddHours(2.5) };
        var proj = QuotaWindowProjection.Project(window, Now, capPct: 95);
        Assert.NotNull(proj);
        Assert.False(proj!.BreachesBeforeReset);
    }

    [Fact]
    public void Project_TooEarlyInWindow_IsNull()
    {
        // Only 6 minutes into a 5-hour window (2% elapsed) -> refuse to project.
        var window = new QuotaWindow { Label = "5-hour", UsedPct = 5, ResetAt = Now.AddHours(4.9) };
        Assert.Null(QuotaWindowProjection.Project(window, Now, capPct: 95));
    }

    [Fact]
    public void Project_UnknownWindowLength_IsNull()
    {
        var window = new QuotaWindow { Label = "credits", UsedPct = 90, ResetAt = Now.AddHours(1) };
        Assert.Null(QuotaWindowProjection.Project(window, Now, capPct: 95));
    }

    [Fact]
    public void Project_AlreadyOverCap_NotFlaggedAsProjection()
    {
        // Over cap already -> that is the strict cap's job, not a projection.
        var window = new QuotaWindow { Label = "5-hour", UsedPct = 97, ResetAt = Now.AddHours(2.5) };
        var proj = QuotaWindowProjection.Project(window, Now, capPct: 95);
        Assert.NotNull(proj);
        Assert.False(proj!.BreachesBeforeReset);
    }

    [Fact]
    public void EvaluateProjectedBreach_ReturnsWorstProjectedWindow()
    {
        var snapshot = new QuotaSnapshot
        {
            CliType = "claude",
            Windows =
            {
                new QuotaWindow { Label = "5-hour", UsedPct = 60, ResetAt = Now.AddHours(2.5) }, // -> 120%
                new QuotaWindow { Label = "Weekly", UsedPct = 10, ResetAt = Now.AddDays(3) },     // fine
            },
        };

        var ev = QuotaWindowProjection.EvaluateProjectedBreach(snapshot, Caps(), Now);

        Assert.NotNull(ev);
        Assert.True(ev!.Blocked);
        Assert.True(ev.Projected);
        Assert.Equal("5-hour", ev.WindowLabel);
        Assert.Equal("claude", ev.CliType);
        Assert.Contains("projected", ev.DescribeReason());
    }

    [Fact]
    public void EvaluateProjectedBreach_HealthyWindows_ReturnsNull()
    {
        var snapshot = new QuotaSnapshot
        {
            CliType = "claude",
            Windows = { new QuotaWindow { Label = "5-hour", UsedPct = 10, ResetAt = Now.AddHours(2.5) } },
        };
        Assert.Null(QuotaWindowProjection.EvaluateProjectedBreach(snapshot, Caps(), Now));
    }
}
