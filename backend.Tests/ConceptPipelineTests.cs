using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ConceptPipelineTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "concept-pipeline-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Scaffold_ProducesReviewableWorkbenchDocument()
    {
        Directory.CreateDirectory(_root);

        var directory = ConceptWorkbenchContract.CreateScaffold(
            _root,
            "concept-pipeline",
            "Concept pipeline",
            "A document-first task pipeline.",
            "AGT-2358");
        var review = ConceptWorkbenchContract.ReviewChangedFiles(
            _root,
            [
                "docs/operations/concept-pipeline/workbench.json",
                "docs/operations/concept-pipeline/index.html",
            ]);

        Assert.True(review.IsComplete, review.Summary);
        Assert.Equal("concept-pipeline", review.Topic);
        Assert.Equal("concept", review.Descriptor!.Pattern);
        Assert.True(File.Exists(Path.Combine(directory, "workbench.json")));
        Assert.True(File.Exists(Path.Combine(directory, "index.html")));
        Assert.Contains(
            "data-document-section=\"evidence\"",
            File.ReadAllText(Path.Combine(directory, "index.html")));
    }

    [Fact]
    public void Scaffold_RendersTheUiVariantFromTheCanonicalArticleTemplate()
    {
        Directory.CreateDirectory(_root);

        var directory = ConceptWorkbenchContract.CreateScaffold(
            _root,
            "visual-options",
            "Visual options",
            "Compare two interface directions.",
            "AGT-2536",
            "ui");
        var descriptor = JsonSerializer.Deserialize<ConceptWorkbenchDescriptor>(
            File.ReadAllText(Path.Combine(directory, "workbench.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var html = File.ReadAllText(Path.Combine(directory, "index.html"));

        Assert.Equal("ui", descriptor.Pattern);
        Assert.Contains("data-document-pattern=\"ui\"", html);
        Assert.Contains("data-article-template=\"v2\"", html);
        Assert.Contains("width: min(70ch", html);
        Assert.Contains("[data-document-pattern=\"ui\"] .variant-grid", html);
        Assert.Contains("[data-document-pattern=\"concept\"] .evidence-class", html);
    }

    [Fact]
    public void Review_RejectsAWorkbenchWithoutRequiredEvidenceSection()
    {
        var directory = ConceptWorkbenchContract.CreateScaffold(
            _root, "incomplete", "Incomplete", "Missing evidence.", "AGT-2358");
        var entrypoint = Path.Combine(directory, "index.html");
        File.WriteAllText(
            entrypoint,
            File.ReadAllText(entrypoint).Replace(
                "data-document-section=\"evidence\"",
                "data-removed-section=\"evidence\"",
                StringComparison.Ordinal));

        var review = ConceptWorkbenchContract.ReviewDirectory(
            _root, "docs/operations/incomplete");

        Assert.False(review.IsComplete);
        Assert.Contains(review.Findings, finding => finding.Contains("evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Promotion_CreatesCodingCardsFromPublishedDocument_Idempotently()
    {
        var repository = Path.Combine(_root, "repository");
        var taskStore = Path.Combine(_root, "tasks");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(taskStore);
        var (_, scanner, _, mutations) = BuildServices(repository, taskStore);
        var sourceId = mutations.CreateJob(new CreateTaskRequest
        {
            Id = "concept-source",
            Title = "Concept source",
            Mode = TaskModes.Concept,
            WatchPath = taskStore,
            TargetState = TaskStates.HumanReview,
        });
        Assert.NotNull(sourceId);
        var source = scanner.FindJob(sourceId!, taskStore);
        Assert.NotNull(source);

        var workbenchDirectory = ConceptWorkbenchContract.CreateScaffold(
            repository,
            "delivery-flow",
            "Delivery flow",
            "Approved delivery design.",
            source!.Key ?? source.Id);
        var descriptorPath = Path.Combine(workbenchDirectory, "workbench.json");
        var descriptor = JsonSerializer.Deserialize<ConceptWorkbenchDescriptor>(
            File.ReadAllText(descriptorPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        descriptor = descriptor with
        {
            ImplementationTasks =
            [
                new ConceptImplementationTask
                {
                    Title = "Implement delivery API",
                    PromptMarkdown = "Add the approved delivery endpoint.",
                },
                new ConceptImplementationTask
                {
                    Title = "Implement delivery UI",
                    PromptMarkdown = "Add the approved delivery controls.",
                },
            ],
        };
        File.WriteAllText(
            descriptorPath,
            JsonSerializer.Serialize(
                descriptor,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Assert.True(ConceptWorkbenchStore.Write(source.FolderPath, new ConceptWorkbenchRecord
        {
            RepoRelativeDirectory = "docs/operations/delivery-flow",
            RepoRelativeEntrypoint = "docs/operations/delivery-flow/index.html",
            Title = descriptor.Title,
            PublishedAt = DateTime.UtcNow,
        }));

        var plan = scanner.BuildPromoteConceptPlan(source.Id, taskStore);
        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Items.Count);
        var service = new ConceptPromotionService(
            scanner, mutations, NullLogger<ConceptPromotionService>.Instance);

        var first = service.Promote(source, plan, new PromoteConceptRequest());
        var repeated = service.Promote(source, plan, new PromoteConceptRequest());

        Assert.Equal(2, first.Created.Count);
        Assert.Equal(
            first.Created.Select(item => item.JobId),
            repeated.Created.Select(item => item.JobId));
        foreach (var promoted in first.Created)
        {
            var created = scanner.FindJob(promoted.JobId, taskStore);
            Assert.NotNull(created);
            Assert.Equal(TaskModes.Coding, created!.Mode);
            Assert.Equal(TaskStates.Preparation, created.State);
            Assert.Contains(
                "docs/operations/delivery-flow/index.html",
                File.ReadAllText(Path.Combine(created.FolderPath, "prompt.md")));
        }
    }

    [Fact]
    public async Task CompletingSightReview_IsSuccessfulAndClearsPendingGate()
    {
        var repository = Path.Combine(_root, "repository");
        var taskStore = Path.Combine(_root, "tasks");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(taskStore);
        var (config, scanner, states, mutations) = BuildServices(repository, taskStore);
        var sourceId = mutations.CreateJob(new CreateTaskRequest
        {
            Id = "sight-review",
            Title = "Sight review",
            Mode = TaskModes.Concept,
            WatchPath = taskStore,
            TargetState = TaskStates.HumanReview,
        });
        var source = scanner.FindJob(sourceId!, taskStore)!;
        var pipelineLog = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        pipelineLog.Begin(
            source.FolderPath,
            PipelineCatalogue.Concept,
            source.ProjectName,
            source.Id);
        var now = DateTime.UtcNow;
        pipelineLog.RecordStep(source.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.ConceptSightReviewGateStepId,
            Kind = StepKind.Orchestrator,
            Status = PipelineStepStatus.Running,
            StartedAt = now,
            Verdict = "awaiting-sight-review",
        });
        SteerPendingMarker.Write(source.FolderPath, new SteerPendingRecord
        {
            Kind = SteerPendingKinds.ConceptSightReview,
            WaitStartedAt = now,
            Question = "Approve the concept.",
        });
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var transitions = new TaskTransitionService(
            scanner,
            states,
            mutations,
            new GitService(NullLogger<GitService>.Instance, scanner, config, prompts),
            new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config),
            NullLogger<TaskTransitionService>.Instance,
            pipelineLog: pipelineLog);

        var outcome = await transitions.MoveAsync(
            source.Id, TaskStates.Completed, taskStore);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var completed = scanner.FindJob(source.Id, taskStore)!;
        Assert.Equal(TaskStates.Completed, completed.State);
        Assert.False(SteerPendingMarker.Exists(completed.FolderPath));
        var execution = JsonSerializer.Deserialize<PipelineExecutionRecord>(
            File.ReadAllText(Path.Combine(completed.FolderPath, PipelineExecutionLog.FileName)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(execution.IsComplete);
        Assert.Equal(
            PipelineStepStatus.Passed,
            execution.Steps.Single(step =>
                step.StepId == PipelineCatalogue.ConceptSightReviewGateStepId).Status);
        Assert.Equal(
            PipelineStepStatus.Skipped,
            execution.Steps.Single(step =>
                step.StepId == PipelineCatalogue.ConceptPromotionStepId).Status);
    }

    private (
        IConfiguration Config,
        TaskScannerService Scanner,
        TaskStateMachine States,
        TaskMutationService Mutations) BuildServices(
        string repository,
        string taskStore)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = "Concept project",
                ["WatchPaths:0:Path"] = taskStore,
                ["WatchPaths:0:RootPath"] = repository,
                ["WatchPaths:0:RepositoryPath"] = repository,
            })
            .Build();
        var summary = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(
            config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        states.EnsureStateFoldersAndMigrate();
        var clients = new ClientIdentityStore(
            config, NullLogger<ClientIdentityStore>.Instance);
        clients.EnsureLoaded();
        var mutations = new TaskMutationService(
            scanner,
            clients,
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        return (config, scanner, states, mutations);
    }
}
