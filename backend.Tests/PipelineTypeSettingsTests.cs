using Xunit;

namespace AgentStudio.Tests;

public sealed class PipelineTypeSettingsTests
{
    [Theory]
    [InlineData(TaskTypes.Chore, TaskModes.Coding, PipelineTypes.Task)]
    [InlineData(TaskTypes.Bug, TaskModes.Coding, PipelineTypes.Bug)]
    [InlineData(TaskTypes.Feature, TaskModes.Coding, PipelineTypes.Feature)]
    [InlineData(TaskTypes.Bug, TaskModes.Planning, PipelineTypes.Planning)]
    [InlineData(TaskTypes.Feature, TaskModes.Research, PipelineTypes.Planning)]
    public void Resolve_UsesCardType_WithReadOnlyModePrecedence(
        string taskType,
        string mode,
        string expected)
    {
        Assert.Equal(expected, PipelineTypes.Resolve(taskType, mode));
    }

    [Fact]
    public void ForTask_SelectsIndependentTypedOverridesAndOrder()
    {
        var settings = new ProjectSettings
        {
            PipelineStepsByType = new()
            {
                [PipelineTypes.Bug] = new()
                {
                    [PipelineCatalogue.LintScssStepId] = new() { Enabled = false },
                },
                [PipelineTypes.Feature] = new()
                {
                    [PipelineCatalogue.LintScssStepId] = new() { Enabled = true },
                },
            },
            PipelineStepOrderByType = new()
            {
                [PipelineTypes.Bug] = [PipelineCatalogue.LintScssStepId],
                [PipelineTypes.Feature] = [PipelineCatalogue.BuildTestGateStepId],
            },
        };

        var bug = PipelineTypeSettings.ForTask(settings, new TaskInfo
        {
            TaskType = TaskTypes.Bug,
            Mode = TaskModes.Coding,
        })!;
        var feature = PipelineTypeSettings.ForTask(settings, new TaskInfo
        {
            TaskType = TaskTypes.Feature,
            Mode = TaskModes.Coding,
        })!;

        Assert.False(PipelineStepConfigResolver.IsEnabled(
            bug,
            PipelineCatalogue.LintScssStepId));
        Assert.True(PipelineStepConfigResolver.IsEnabled(
            feature,
            PipelineCatalogue.LintScssStepId));
        Assert.Equal(PipelineCatalogue.LintScssStepId, bug.PipelineStepOrder!.Single());
        Assert.Equal(PipelineCatalogue.BuildTestGateStepId, feature.PipelineStepOrder!.Single());
    }

    [Fact]
    public void Planning_UsesReadOnlyCatalogue_AndNeverInheritsLegacyFlatOverrides()
    {
        var legacy = new ProjectSettings
        {
            PipelineSteps = new()
            {
                [PipelineCatalogue.CodeReviewGradeStepId] = new() { Enabled = false },
            },
        };
        var planning = new TaskInfo
        {
            TaskType = TaskTypes.Feature,
            Mode = TaskModes.Planning,
        };

        var effective = PipelineTypeSettings.ForTask(legacy, planning)!;

        Assert.Same(PipelineCatalogue.ReadOnly, PipelineCatalogue.ForTask(planning));
        Assert.Null(effective.PipelineSteps);
        Assert.True(PipelineStepConfigResolver.IsEnabled(
            effective,
            PipelineCatalogue.CodeReviewGradeStepId));
    }
}
