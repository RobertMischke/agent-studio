using System.Text.Json;
using Xunit;

namespace AgentStudio.Tests;

public sealed class UiTaskPipelineTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "ui-task-pipeline-tests-" + Guid.NewGuid().ToString("N"));

    public UiTaskPipelineTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Router_UsesSharedUiHeuristic_AndOptionalChangeSetConfirmation()
    {
        var task = Task("Restyle the board button", "feature", ["frontend"]);

        Assert.Equal(PipelineCatalogue.UiPipelineId,
            UiTaskPipelineRouter.Select(task, new ProjectSettings()).Id);
        Assert.Equal(PipelineCatalogue.UiPipelineId,
            UiTaskPipelineRouter.Select(task, new ProjectSettings(),
                ["frontend/src/app/components/board/board.component.html"]).Id);
        Assert.Equal(PipelineCatalogue.StandardPipelineId,
            UiTaskPipelineRouter.Select(task, new ProjectSettings(),
                ["backend/Features/Runner/ProjectRunner.cs"]).Id);
    }

    [Fact]
    public void Router_ProjectCanDisableNamedUiStepSet()
    {
        var settings = new ProjectSettings
        {
            PipelineSteps = new Dictionary<string, PipelineStepSetting>
            {
                [PipelineCatalogue.UiPipelineRoutingStepId] = new() { Enabled = false },
            },
        };

        Assert.Equal(PipelineCatalogue.StandardPipelineId,
            UiTaskPipelineRouter.Select(Task("UI panel", "feature", []), settings).Id);
    }

    [Fact]
    public void Catalogue_UiPipeline_HasMandatoryDependentIterationSteps()
    {
        var pipeline = PipelineCatalogue.UiIteration;
        var artifact = pipeline.Post.Single(step => step.Id == PipelineCatalogue.UiIterationArtifactStepId);
        var review = pipeline.Post.Single(step => step.Id == PipelineCatalogue.UiHumanReviewGateStepId);

        Assert.Equal(PipelineCatalogue.UiPipelineId, pipeline.Id);
        Assert.Contains(PipelineCatalogue.CoreAgentRunStepId, artifact.DependsOn);
        Assert.Contains(PipelineCatalogue.UiIterationArtifactStepId, review.DependsOn);
        Assert.False(PipelineStepConfigResolver.CanDisable(artifact));
        Assert.False(PipelineStepConfigResolver.CanDisable(review));
    }

    [Fact]
    public void Gate_RequiresVisualArtifactAndChangeDescriptionForThisIteration()
    {
        var iterationDir = UiIterationGate.IterationDirectory(_folder, 1);
        Directory.CreateDirectory(iterationDir);
        File.WriteAllBytes(Path.Combine(iterationDir, "screen.png"), [1, 2, 3]);

        var missingDescription = UiIterationGate.Evaluate(_folder, 1, 4);
        Assert.Equal(UiIterationGateAction.Incomplete, missingDescription.Action);
        Assert.Contains(missingDescription.Findings,
            finding => finding.Contains(UiIterationGate.ChangeDescriptionFileName, StringComparison.Ordinal));

        File.WriteAllText(Path.Combine(iterationDir, UiIterationGate.ChangeDescriptionFileName),
            "Adjusted the task card spacing and focus state.");
        var complete = UiIterationGate.Evaluate(_folder, 1, 4);

        Assert.Equal(UiIterationGateAction.ReadyForHumanReview, complete.Action);
        Assert.Single(complete.ArtifactPaths);
        Assert.Equal("ui-iteration-001/changes.md", complete.ChangeDescriptionPath);
    }

    [Fact]
    public void Gate_DoesNotReuseEvidenceFromPriorIteration()
    {
        var first = UiIterationGate.IterationDirectory(_folder, 1);
        Directory.CreateDirectory(first);
        File.WriteAllBytes(Path.Combine(first, "screen.png"), [1]);
        File.WriteAllText(Path.Combine(first, UiIterationGate.ChangeDescriptionFileName), "First pass");

        var second = UiIterationGate.Evaluate(_folder, 2, 4);

        Assert.Equal(UiIterationGateAction.Incomplete, second.Action);
        Assert.Equal(2, UiIterationGate.NextIteration(_folder));
    }

    [Fact]
    public void IterationResolver_RetriesEmptyCurrentDirectory_AndOnlyReviewAdvancesIt()
    {
        UiIterationGate.PrepareIterationDirectory(_folder, 2);

        Assert.Equal(2, UiIterationGate.ResolveRunIteration(_folder));
        Assert.Equal(3, UiIterationGate.ResolveRunIteration(_folder, new UiIterationReviewContract
        {
            Iteration = 2,
            MaxIterations = 4,
        }));
    }

    [Fact]
    public void Gate_EscalatesBeyondConfiguredCap_AndResolverClampsConfiguration()
    {
        var settings = new ProjectSettings
        {
            PipelineSteps = new Dictionary<string, PipelineStepSetting>
            {
                [PipelineCatalogue.UiPipelineRoutingStepId] = new() { MaxIterations = 3 },
            },
        };

        Assert.Equal(3, PipelineStepConfigResolver.ResolveUiMaxIterations(settings));
        var decision = UiIterationGate.Evaluate(_folder, iteration: 4, maxIterations: 3);
        Assert.Equal(UiIterationGateAction.EscalateCapReached, decision.Action);
        Assert.True(UiIterationGate.MustEscalateFeedbackContinuation(new UiIterationReviewContract
        {
            Iteration = 3,
            MaxIterations = 3,
            CapReached = false,
        }));
    }

    [Fact]
    public void CapGuard_RecognizesDirectAndQueuedHumanFeedbackContinuations()
    {
        Assert.True(UiIterationGate.IsFeedbackContinuation(
            RunIntent.UserContinue, hasPendingIntent: false));
        Assert.True(UiIterationGate.IsFeedbackContinuation(
            RunIntent.AutoPickup, hasPendingIntent: true));
        Assert.False(UiIterationGate.IsFeedbackContinuation(
            RunIntent.AutoPickup, hasPendingIntent: false));
        Assert.False(UiIterationGate.IsFeedbackContinuation(
            RunIntent.ManualStart, hasPendingIntent: true));
    }

    [Fact]
    public void Marker_RoundTripsPartTwoReviewContract()
    {
        SteerPendingMarker.Write(_folder, new SteerPendingRecord
        {
            Kind = SteerPendingKinds.UiIterationReview,
            UiIterationReview = new UiIterationReviewContract
            {
                Iteration = 2,
                MaxIterations = 4,
                ArtifactPaths = ["ui-iteration-002/after.png"],
                ChangeDescriptionPath = "ui-iteration-002/changes.md",
            },
        });

        var marker = SteerPendingMarker.TryRead(_folder);
        Assert.NotNull(marker);
        Assert.Equal(SteerPendingKinds.UiIterationReview, marker!.Kind);
        Assert.Equal(PipelineCatalogue.UiPipelineId, marker.UiIterationReview!.PipelineId);
        Assert.Equal(2, marker.UiIterationReview.Iteration);

        using var json = JsonDocument.Parse(File.ReadAllText(SteerPendingMarker.PathFor(_folder)));
        Assert.Equal(1, json.RootElement.GetProperty("uiIterationReview").GetProperty("contractVersion").GetInt32());
    }

    private TaskInfo Task(string title, string taskType, IReadOnlyList<string> tags) => new()
    {
        Id = "AGT-UI",
        Title = title,
        TaskType = taskType,
        Tags = tags.ToList(),
        Mode = TaskModes.Coding,
        FolderPath = _folder,
    };
}
