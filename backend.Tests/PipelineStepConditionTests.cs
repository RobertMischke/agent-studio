using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Pipeline;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Tests for the per-step run-condition feature: the pure
/// <see cref="PipelineStepConditionEvaluator"/>, the
/// <see cref="PipelineStepConfigResolver.ShouldRun(ProjectSettings?, PipelineStep, PipelineStepConditionContext)"/>
/// combination of enablement + condition, the shared
/// <see cref="PipelineStepConditions"/> vocabulary the endpoint validates
/// against, and the normalisation + persistence done by
/// <see cref="ProjectSettingsService.SetPipelineStep"/>.
/// </summary>
public class PipelineStepConditionTests
{
    private static PipelineStepCondition Cond(string when, string? value = null)
        => new() { When = when, Value = value };

    private static PipelineStepConditionContext Ctx(
        bool aborted = false, int? exitCode = null, bool anyAspectFailed = false,
        string? taskType = null, IReadOnlyCollection<string>? tags = null)
        => new()
        {
            Aborted = aborted,
            ExitCode = exitCode,
            AnyAspectFailed = anyAspectFailed,
            TaskType = taskType,
            Tags = tags,
        };

    // ---- Evaluator matrix -------------------------------------------------

    [Fact]
    public void Matches_NullOrAlwaysOrUnknown_AlwaysRuns()
    {
        var ctx = Ctx();
        Assert.True(PipelineStepConditionEvaluator.Matches(null, ctx));
        Assert.True(PipelineStepConditionEvaluator.Matches(Cond(PipelineStepConditions.Always), ctx));
        Assert.True(PipelineStepConditionEvaluator.Matches(Cond("   "), ctx));
        // An unknown token fails open (run) rather than silently skipping.
        Assert.True(PipelineStepConditionEvaluator.Matches(Cond("bogus-token"), ctx));
    }

    [Fact]
    public void Matches_Never_AlwaysSkips()
    {
        Assert.False(PipelineStepConditionEvaluator.Matches(
            Cond(PipelineStepConditions.Never), Ctx(aborted: true, exitCode: 1)));
    }

    [Fact]
    public void Matches_OnAbort_GatesOnAbortedFlag()
    {
        Assert.True(PipelineStepConditionEvaluator.Matches(Cond(PipelineStepConditions.OnAbort), Ctx(aborted: true)));
        Assert.False(PipelineStepConditionEvaluator.Matches(Cond(PipelineStepConditions.OnAbort), Ctx(aborted: false)));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(-1, true)]
    public void Matches_OnNonzeroExit_GatesOnExitCode(int? exitCode, bool expected)
    {
        Assert.Equal(expected, PipelineStepConditionEvaluator.Matches(
            Cond(PipelineStepConditions.OnNonzeroExit), Ctx(exitCode: exitCode)));
    }

    [Fact]
    public void Matches_OnAspectFail_GatesOnAspectFlag()
    {
        Assert.True(PipelineStepConditionEvaluator.Matches(Cond(PipelineStepConditions.OnAspectFail), Ctx(anyAspectFailed: true)));
        Assert.False(PipelineStepConditionEvaluator.Matches(Cond(PipelineStepConditions.OnAspectFail), Ctx(anyAspectFailed: false)));
    }

    [Fact]
    public void Matches_TaskType_IsCaseInsensitive_AndFailsClosedWithoutValue()
    {
        Assert.True(PipelineStepConditionEvaluator.Matches(
            Cond(PipelineStepConditions.TaskType, "Bug"), Ctx(taskType: "bug")));
        Assert.False(PipelineStepConditionEvaluator.Matches(
            Cond(PipelineStepConditions.TaskType, "feature"), Ctx(taskType: "bug")));
        // No value -> cannot match anything.
        Assert.False(PipelineStepConditionEvaluator.Matches(
            Cond(PipelineStepConditions.TaskType), Ctx(taskType: "bug")));
    }

    [Fact]
    public void Matches_Tag_MatchesAnyTag_CaseInsensitive_AndFailsClosedWithoutValue()
    {
        var tags = new[] { "urgent", "frontend" };
        Assert.True(PipelineStepConditionEvaluator.Matches(
            Cond(PipelineStepConditions.Tag, "Frontend"), Ctx(tags: tags)));
        Assert.False(PipelineStepConditionEvaluator.Matches(
            Cond(PipelineStepConditions.Tag, "backend"), Ctx(tags: tags)));
        Assert.False(PipelineStepConditionEvaluator.Matches(
            Cond(PipelineStepConditions.Tag, "urgent"), Ctx(tags: null)));
        Assert.False(PipelineStepConditionEvaluator.Matches(
            Cond(PipelineStepConditions.Tag), Ctx(tags: tags)));
    }

    // ---- Resolver: enablement + condition combined ------------------------

    private static ProjectSettings SettingsWith(string stepId, PipelineStepSetting setting)
        => new()
        {
            PipelineSteps = new Dictionary<string, PipelineStepSetting>(StringComparer.OrdinalIgnoreCase)
            {
                [stepId] = setting,
            },
        };

    [Fact]
    public void ShouldRun_DisabledByDefault_AbortReviewDoesNotRun()
    {
        // Abort review defaults off; with no override ShouldRun is false even
        // when the condition (none) would otherwise pass.
        Assert.False(PipelineStepConfigResolver.ShouldRun(
            null, PipelineCatalogue.AbortReviewStep, Ctx(aborted: true)));
    }

    [Fact]
    public void ShouldRun_EnabledWithMatchingCondition_Runs()
    {
        var settings = SettingsWith(PipelineCatalogue.PostAbortReviewStepId, new PipelineStepSetting
        {
            Enabled = true,
            Condition = Cond(PipelineStepConditions.OnNonzeroExit),
        });
        Assert.True(PipelineStepConfigResolver.ShouldRun(
            settings, PipelineCatalogue.AbortReviewStep, Ctx(aborted: true, exitCode: 2)));
    }

    [Fact]
    public void ShouldRun_EnabledWithNonMatchingCondition_DoesNotRun()
    {
        var settings = SettingsWith(PipelineCatalogue.PostAbortReviewStepId, new PipelineStepSetting
        {
            Enabled = true,
            Condition = Cond(PipelineStepConditions.TaskType, "feature"),
        });
        Assert.False(PipelineStepConfigResolver.ShouldRun(
            settings, PipelineCatalogue.AbortReviewStep, Ctx(aborted: true, taskType: "bug")));
    }

    [Fact]
    public void ShouldRun_EnabledWithNoCondition_RunsWheneverEnabled()
    {
        var settings = SettingsWith(PipelineCatalogue.PostAbortReviewStepId, new PipelineStepSetting { Enabled = true });
        Assert.True(PipelineStepConfigResolver.ShouldRun(
            settings, PipelineCatalogue.AbortReviewStep, Ctx(aborted: true)));
    }

    [Fact]
    public void ShouldRun_PreOrchestratorPrep_HonoursTaskScopedCondition()
    {
        var step = PipelineCatalogue.Standard.Pre.First(s => s.Id == PipelineCatalogue.PreOrchestratorPrepStepId);
        var settings = SettingsWith(step.Id, new PipelineStepSetting
        {
            Enabled = true,
            Condition = Cond(PipelineStepConditions.Tag, "frontend"),
        });

        Assert.True(PipelineStepConfigResolver.ShouldRun(settings, step, Ctx(tags: ["frontend", "bug"])));
        Assert.False(PipelineStepConfigResolver.ShouldRun(settings, step, Ctx(tags: ["backend"])));
    }

    [Fact]
    public void ResolvePrompt_ReturnsTrimmedOverride_AndNullForCatalogueDefault()
    {
        var settings = SettingsWith("aspect-code-quality", new PipelineStepSetting { Prompt = "  custom review prompt  " });

        Assert.Equal("custom review prompt", PipelineStepConfigResolver.ResolvePrompt(settings, "aspect-code-quality"));
        Assert.Null(PipelineStepConfigResolver.ResolvePrompt(new ProjectSettings(), "aspect-code-quality"));
    }

    // ---- Vocabulary the endpoint validation reuses ------------------------

    [Fact]
    public void Conditions_Normalize_CanonicalisesKnown_AndRejectsUnknown()
    {
        Assert.Equal(PipelineStepConditions.OnAbort, PipelineStepConditions.Normalize("ON-ABORT"));
        Assert.Equal(PipelineStepConditions.TaskType, PipelineStepConditions.Normalize("  task-type "));
        Assert.Null(PipelineStepConditions.Normalize(""));
        Assert.Null(PipelineStepConditions.Normalize(null));
        Assert.Null(PipelineStepConditions.Normalize("nope"));
    }

    [Fact]
    public void Conditions_RequiresValue_OnlyForValueBearingTokens()
    {
        Assert.True(PipelineStepConditions.RequiresValue(PipelineStepConditions.TaskType));
        Assert.True(PipelineStepConditions.RequiresValue(PipelineStepConditions.Tag));
        Assert.False(PipelineStepConditions.RequiresValue(PipelineStepConditions.OnAbort));
        Assert.False(PipelineStepConditions.RequiresValue(PipelineStepConditions.Always));
    }

    // ---- Service: normalisation + persistence round-trip ------------------

    private static ProjectSettingsService NewService(string taskRepo)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = taskRepo })
            .Build();
        return new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
    }

    [Fact]
    public void SetPipelineStep_PersistsCondition_AndSurvivesReload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atp-cond-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var svc = NewService(dir);
            svc.SetPipelineStep("Proj", PipelineCatalogue.PostAbortReviewStepId, new PipelineStepSetting
            {
                Enabled = true,
                Condition = Cond(PipelineStepConditions.TaskType, "  Bug  "),
            });

            var stored = svc.Get("Proj").PipelineSteps![PipelineCatalogue.PostAbortReviewStepId];
            Assert.Equal(PipelineStepConditions.TaskType, stored.Condition!.When);
            Assert.Equal("Bug", stored.Condition.Value); // trimmed

            // A fresh instance reads it back off disk.
            var reloaded = NewService(dir).Get("Proj").PipelineSteps![PipelineCatalogue.PostAbortReviewStepId];
            Assert.Equal(PipelineStepConditions.TaskType, reloaded.Condition!.When);
            Assert.Equal("Bug", reloaded.Condition.Value);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SetPipelineStep_AlwaysOrValuelessValueBearing_CollapsesConditionToNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atp-cond-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var svc = NewService(dir);

            // "always" condition is a no-op: with an enabled flag the entry
            // stays but carries no condition.
            svc.SetPipelineStep("Proj", PipelineCatalogue.PostAbortReviewStepId, new PipelineStepSetting
            {
                Enabled = true,
                Condition = Cond(PipelineStepConditions.Always),
            });
            var s1 = svc.Get("Proj").PipelineSteps![PipelineCatalogue.PostAbortReviewStepId];
            Assert.Null(s1.Condition);
            Assert.True(s1.Enabled);

            // task-type with no value cannot match, so it collapses to null too.
            svc.SetPipelineStep("Proj", PipelineCatalogue.PostAbortReviewStepId, new PipelineStepSetting
            {
                Enabled = true,
                Condition = Cond(PipelineStepConditions.TaskType),
            });
            var s2 = svc.Get("Proj").PipelineSteps![PipelineCatalogue.PostAbortReviewStepId];
            Assert.Null(s2.Condition);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SetPipelineStep_ConditionOnly_MakesEntryNonEmpty_AndClearsBackToDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atp-cond-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var svc = NewService(dir);

            // Condition is the only set dimension: the entry must persist.
            svc.SetPipelineStep("Proj", PipelineCatalogue.PostAbortReviewStepId, new PipelineStepSetting
            {
                Condition = Cond(PipelineStepConditions.OnAbort),
            });
            var stored = svc.Get("Proj").PipelineSteps![PipelineCatalogue.PostAbortReviewStepId];
            Assert.Equal(PipelineStepConditions.OnAbort, stored.Condition!.When);

            // Clearing every dimension removes the override entirely.
            svc.SetPipelineStep("Proj", PipelineCatalogue.PostAbortReviewStepId, new PipelineStepSetting());
            Assert.Null(svc.Get("Proj").PipelineSteps);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
