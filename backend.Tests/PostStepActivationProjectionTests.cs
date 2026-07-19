using Xunit;

namespace AgentStudio.Tests;

public sealed class PostStepActivationProjectionTests
{
    private static readonly PipelineStep Step = new()
    {
        Id = "post-example",
        DisplayName = "Example post-step",
        DefaultEnabled = true,
    };

    [Fact]
    public void Build_DefaultEnabledStep_AttributesGlobalSource()
    {
        var activation = PostStepActivationProjection.Build(
            Step, configured: null, execution: null, record: null, new TaskInfo());

        Assert.Equal(PostStepActivationProjection.Active, activation.State);
        Assert.Equal(PostStepActivationProjection.GlobalSource, activation.Source);
        Assert.Equal("Enabled by the global catalogue default.", activation.Reason);
    }

    [Fact]
    public void Build_DisabledOverride_AttributesProjectSource()
    {
        var activation = PostStepActivationProjection.Build(
            Step,
            new PipelineStepSetting { Enabled = false },
            execution: null,
            record: null,
            new TaskInfo());

        Assert.Equal(PostStepActivationProjection.Inactive, activation.State);
        Assert.Equal(PostStepActivationProjection.ProjectSource, activation.Source);
        Assert.Equal("Disabled by the project override.", activation.Reason);
    }

    [Fact]
    public void Build_UnmatchedTagCondition_ExplainsConditionWithoutFrontendGuessing()
    {
        var activation = PostStepActivationProjection.Build(
            Step,
            new PipelineStepSetting
            {
                Enabled = true,
                Condition = new PipelineStepCondition
                {
                    When = PipelineStepConditions.Tag,
                    Value = "security",
                },
            },
            execution: null,
            record: null,
            new TaskInfo { Tags = ["frontend"] });

        Assert.Equal(PostStepActivationProjection.Skipped, activation.State);
        Assert.Equal(PostStepActivationProjection.ConditionSource, activation.Source);
        Assert.Equal(
            "Condition \"task has tag 'security'\" does not match this task run.",
            activation.Reason);
    }

    [Fact]
    public void Build_RuntimeSkip_PrefersPreciseRecordedReason()
    {
        var activation = PostStepActivationProjection.Build(
            Step,
            new PipelineStepSetting
            {
                Condition = new PipelineStepCondition { When = PipelineStepConditions.OnAbort },
            },
            new PipelineStepExecution
            {
                StepId = Step.Id,
                Status = PipelineStepStatus.Skipped,
                Reason = "Run completed normally; abort cleanup was not needed.",
            },
            record: null,
            new TaskInfo());

        Assert.Equal(PostStepActivationProjection.Skipped, activation.State);
        Assert.Equal(PostStepActivationProjection.ConditionSource, activation.Source);
        Assert.Equal("Run completed normally; abort cleanup was not needed.", activation.Reason);
    }
}
