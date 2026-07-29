using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PromptEnrichmentServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "prompt-enrichment-" + Guid.NewGuid().ToString("N"));

    public PromptEnrichmentServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    [Fact]
    public void Prepare_RealUiCard_PersistsVisibleReportAndLedgerAttribution()
    {
        var folder = Path.Combine(_root, TaskStates.Ready, "ui-card");
        Directory.CreateDirectory(folder);
        var task = new TaskInfo
        {
            Id = "ui-card",
            TaskKey = "AGT-TEST",
            ProjectName = "test",
            FolderPath = folder,
            State = TaskStates.Ready,
            Mode = TaskModes.Coding,
            Title = "Polish the Angular task detail styling",
            Tags = ["frontend", "ui"],
        };
        var authored = "# Original task\n\nKeep this text byte-for-byte readable.";
        File.WriteAllText(Path.Combine(folder, "task.json"),
            """{"id":"ui-card","title":"Polish UI","state":"2-ready"}""");
        File.WriteAllText(Path.Combine(folder, "prompt.md"), authored);
        var guide = new ProjectStyleGuide(
            "frontend-styling",
            "Frontend styling context",
            "quality/frontend-styling.md",
            "Styling rules",
            "Use semantic tokens, calm surfaces, and no coloured left accent bars.",
            "7",
            new StyleGuideAppliesTo(["*"], ["angular"], ["frontend"]));
        var pipeline = new PipelineExecutionLog(
            NullLogger<PipelineExecutionLog>.Instance);
        var service = new PromptEnrichmentService(
            NullLogger<PromptEnrichmentService>.Instance,
            pipelineLog: pipeline);

        var result = service.Prepare(
            task,
            authored,
            downstreamModel: null,
            enabledOverride: true,
            guidesOverride: [guide],
            styleGuideSnapshotOverride: "style-snapshot-7");

        Assert.StartsWith(authored, result.LaunchPrompt, StringComparison.Ordinal);
        Assert.Contains("## Prompt enrichment", result.LaunchPrompt);
        Assert.Equal(PromptEnrichmentStatuses.Enriched, result.Report.Status);
        Assert.NotEqual(result.Report.OriginalPromptSha256, result.Report.EnrichedPromptSha256);
        Assert.Contains(result.Report.AppendedBlocks,
            block => block.Id == "style-guide:frontend-styling"
                     && block.Revision == "7"
                     && block.ExactContent.Contains("semantic tokens", StringComparison.Ordinal));
        Assert.InRange(result.Report.Tokens.Appended, 1, 1_500);
        Assert.Equal(0, result.Report.Tokens.PreprocessingInput);
        Assert.Equal(0, result.Report.Cost.SelectorUsd);

        var persisted = PromptEnrichmentService.ReadReport(folder);
        Assert.NotNull(persisted);
        Assert.Equal(result.Report.EnrichmentId, persisted!.EnrichmentId);
        Assert.True(File.Exists(Path.Combine(folder, PromptEnrichmentService.ReportFileName)));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _root,
            })
            .Build();
        var summary = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance,
            configuration);
        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            summary);
        var detail = scanner.GetJobDetail("ui-card");
        Assert.NotNull(detail);
        Assert.Equal(result.Report.EnrichmentId, detail!.EnrichmentReport?.EnrichmentId);
        Assert.Equal(authored, detail.PromptMarkdown);

        var execution = pipeline.Read(folder);
        Assert.NotNull(execution);
        var step = Assert.Single(execution!.Steps,
            entry => entry.StepId == PipelineCatalogue.PromptEnrichmentStepId);
        Assert.Equal(PipelineStepStatus.Passed, step.Status);
        Assert.Equal(0, step.InputTokens);
        Assert.Contains($"+{result.Report.Tokens.Appended} attributed prompt tokens",
            step.VerdictSummary);
        Assert.Equal("PROMPT ENRICHMENT REPORT / deterministic selector",
            step.TokenUsageSource);
    }

    [Fact]
    public void Prepare_ProjectDisabled_PreservesAuthoredPromptAndAuditsDecision()
    {
        var folder = Path.Combine(_root, "disabled-card");
        Directory.CreateDirectory(folder);
        var task = new TaskInfo
        {
            Id = "disabled-card",
            ProjectName = "test",
            FolderPath = folder,
            State = TaskStates.Ready,
            Mode = TaskModes.Coding,
            Title = "Change the frontend card styling",
        };
        var service = new PromptEnrichmentService(
            NullLogger<PromptEnrichmentService>.Instance);

        var result = service.Prepare(
            task,
            "Original only.",
            downstreamModel: null,
            enabledOverride: false);

        Assert.Equal("Original only.", result.LaunchPrompt);
        Assert.Equal(PromptEnrichmentStatuses.Unchanged, result.Report.Status);
        Assert.False(result.Report.Policy.ProjectEnabled);
        Assert.Empty(result.Report.AppendedBlocks);
        Assert.All(result.Report.Candidates,
            candidate => Assert.Equal("rejected-project-disabled", candidate.Decision));
        Assert.Equal(string.Empty, File.ReadAllText(Path.Combine(
            folder,
            IntakeRunner.EnrichedContextRelativePath.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public void Prepare_SelectorFailure_PersistsFallbackBeforeUsingAuthoredPrompt()
    {
        var folder = Path.Combine(_root, "fallback-card");
        Directory.CreateDirectory(folder);
        var task = new TaskInfo
        {
            Id = "fallback-card",
            ProjectName = "test",
            FolderPath = folder,
            State = TaskStates.Ready,
            Mode = TaskModes.Coding,
            Title = "Style the frontend card",
        };
        var invalidGuide = new ProjectStyleGuide(
            "invalid",
            "Invalid guide",
            "quality/invalid.md",
            "Invalid",
            "This guide deliberately exercises selector fallback.",
            "1",
            null!);
        var service = new PromptEnrichmentService(
            NullLogger<PromptEnrichmentService>.Instance);

        var result = service.Prepare(
            task,
            "Authored prompt survives.",
            downstreamModel: null,
            enabledOverride: true,
            guidesOverride: [invalidGuide]);

        Assert.Equal("Authored prompt survives.", result.LaunchPrompt);
        Assert.Equal(PromptEnrichmentStatuses.FallbackUnenriched, result.Report.Status);
        Assert.True(result.Report.Policy.ProjectEnabled);
        Assert.Empty(result.Report.AppendedBlocks);
        Assert.Contains(result.Report.Warnings,
            warning => warning.StartsWith("Selection failed open", StringComparison.Ordinal));
        Assert.Equal(
            PromptEnrichmentStatuses.FallbackUnenriched,
            PromptEnrichmentService.ReadReport(folder)?.Status);
    }

    [Fact]
    public void Prepare_ReportCannotBePersisted_BlocksDispatch()
    {
        var fileInsteadOfFolder = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(fileInsteadOfFolder, "occupied");
        var task = new TaskInfo
        {
            Id = "blocked-card",
            ProjectName = "test",
            FolderPath = fileInsteadOfFolder,
            State = TaskStates.Ready,
            Title = "Blocked persistence",
        };
        var service = new PromptEnrichmentService(
            NullLogger<PromptEnrichmentService>.Instance);

        var error = Assert.Throws<InvalidOperationException>(() =>
            service.Prepare(
                task,
                "A valid authored prompt.",
                downstreamModel: null,
                enabledOverride: true));

        Assert.Contains("Dispatch is blocked", error.Message);
    }
}
