using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class QualityStudioAnalysisPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "quality-studio-analysis-tests-" + Guid.NewGuid().ToString("N"));

    public QualityStudioAnalysisPolicyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task StepRunner_RunsAngularDefaultsInProcessAndRecordsPipelineEvidence()
    {
        var repository = Path.Combine(_root, "frontend-repository");
        var jobFolder = Path.Combine(_root, "runner-job");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(jobFolder);
        var core = new RecordingCore();
        var log = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        log.Begin(jobFolder, PipelineCatalogue.Standard, "Agent Studio", "AGT-2655");
        var runner = new QualityStudioAnalysisStepRunner(
            core, log, NullLogger<QualityStudioAnalysisStepRunner>.Instance);
        var task = new TaskInfo
        {
            Id = "agt-2655",
            Key = "AGT-2655",
            Commits =
            [
                new TaskCommitInfo
                {
                    Sha = "abc",
                    Files = ["frontend/src/app/project-card.scss"],
                },
            ],
        };

        var outcome = await runner.RunAsync(repository, jobFolder, task);

        Assert.True(outcome.RequiresSteeredRetry);
        Assert.False(outcome.DependencyUnavailable);
        Assert.Equal(2, core.Requests.Count);
        Assert.All(core.Requests, request => Assert.Equal(["angular"], request.RuleProfiles));
        Assert.Equal(PipelineCatalogue.QualityStaticRulesStepId, core.Requests[0].StepId);
        Assert.Equal(PipelineCatalogue.QualityVisualStepId, core.Requests[1].StepId);
        var recorded = log.Read(jobFolder)!;
        Assert.Equal(PipelineStepStatus.Passed,
            recorded.Steps.Single(step => step.StepId == PipelineCatalogue.QualityStaticRulesStepId).Status);
        Assert.Equal("findings",
            recorded.Steps.Single(step => step.StepId == PipelineCatalogue.QualityStaticRulesStepId).Verdict);
        Assert.Equal(PipelineStepStatus.NotApplicable,
            recorded.Steps.Single(step => step.StepId == PipelineCatalogue.QualitySecurityStepId).Status);
    }

    [Fact]
    public void Catalogue_ExposesNamedQualityStudioAxesInStandardAndUiPipelines()
    {
        var standard = PipelineCatalogue.Standard.Post
            .Where(step => step.Kind == StepKind.Analysis)
            .ToArray();
        var ui = PipelineCatalogue.UiIteration.Post
            .Where(step => step.Kind == StepKind.Analysis)
            .ToArray();

        Assert.Equal(PipelineCatalogue.QualityAnalysisStepIds, standard.Select(step => step.Id));
        Assert.Equal(PipelineCatalogue.QualityAnalysisStepIds, ui.Select(step => step.Id));
        Assert.All(standard, step => Assert.Contains(PipelineCatalogue.BuildTestGateStepId, step.DependsOn));
        Assert.All(PipelineCatalogue.Standard.Post.Where(step => step.Kind == StepKind.Aspect),
            step => Assert.All(PipelineCatalogue.QualityAnalysisStepIds,
                dependency => Assert.Contains(dependency, step.DependsOn)));
    }

    [Fact]
    public void FrontendCard_DefaultsToAngularRulesAndVisualAnalysis()
    {
        var selection = QualityStudioAnalysisPolicy.Resolve(_root,
            ["frontend/src/app/project-card.ts", "frontend/src/app/project-card.html"]);

        Assert.Equal(QualityStudioCardClass.Frontend, selection.CardClass);
        Assert.Equal(
            [PipelineCatalogue.QualityStaticRulesStepId, PipelineCatalogue.QualityVisualStepId],
            selection.StepIds);
        Assert.Equal(["angular"], selection.RuleProfiles);
        Assert.Null(selection.OverridePath);
    }

    [Fact]
    public void BackendCard_DefaultsToDotNetRulesAndNonBlockingSecurityAnalysis()
    {
        var selection = QualityStudioAnalysisPolicy.Resolve(_root,
            ["backend/Features/Pipeline/Runner.cs"]);

        Assert.Equal(QualityStudioCardClass.Backend, selection.CardClass);
        Assert.Equal(
            [PipelineCatalogue.QualityStaticRulesStepId, PipelineCatalogue.QualitySecurityStepId],
            selection.StepIds);
        Assert.Equal(["dotnet"], selection.RuleProfiles);
        Assert.False(QualityStudioAnalysisPolicy.FindingsBlock(PipelineCatalogue.QualitySecurityStepId));
        Assert.True(QualityStudioAnalysisPolicy.FindingsBlock(PipelineCatalogue.QualityStaticRulesStepId));
    }

    [Fact]
    public void RepositoryOverride_ChangesNamedStepsWithoutEnvironmentOrCentralSettings()
    {
        var qualityDirectory = Path.Combine(_root, ".quality");
        Directory.CreateDirectory(qualityDirectory);
        File.WriteAllText(Path.Combine(qualityDirectory, "agent-studio.json"),
            """
            {
              "schemaVersion": 1,
              "analysisSteps": {
                "analysis-qs-visual": false,
                "analysis-qs-consistency": true
              }
            }
            """);

        var selection = QualityStudioAnalysisPolicy.Resolve(_root,
            ["frontend/src/app/project-card.scss"]);

        Assert.Equal(
            [PipelineCatalogue.QualityStaticRulesStepId, PipelineCatalogue.QualityConsistencyStepId],
            selection.StepIds);
        Assert.Equal(QualityStudioProjectPolicy.RelativePath, selection.OverridePath);
        Assert.Equal(QualityStudioProjectPolicy.RuleConfigurationRelativePath,
            selection.RuleConfigurationPath);
    }

    [Fact]
    public void RepositoryOverride_RejectsUnknownStepInsteadOfSilentlyDrifting()
    {
        var qualityDirectory = Path.Combine(_root, ".quality");
        Directory.CreateDirectory(qualityDirectory);
        File.WriteAllText(Path.Combine(qualityDirectory, "agent-studio.json"),
            """{"schemaVersion":1,"analysisSteps":{"analysis-qs-invented":true}}""");

        var error = Assert.Throws<InvalidDataException>(() =>
            QualityStudioAnalysisPolicy.Resolve(_root, ["frontend/src/app/app.ts"]));

        Assert.Contains("analysis-qs-invented", error.Message);
    }

    [Fact]
    public void AngularFinding_PersistsNamedRuleEvidenceAndRequestsSteeredRetry()
    {
        var jobFolder = Path.Combine(_root, "job");
        Directory.CreateDirectory(jobFolder);
        var selection = QualityStudioAnalysisPolicy.Resolve(_root,
            ["frontend/src/app/project-card.scss"]);
        var result = new QualityStudioAnalysisResult(
            true,
            "AgentOrchestrator.CodeQuality",
            "0.1.0",
            [Finding("QS-NG-002")]);

        var outcome = QualityStudioAnalysisEvidence.Persist(
            jobFolder,
            "AGT-2655",
            PipelineCatalogue.QualityStaticRulesStepId,
            selection,
            result,
            runIndex: 1,
            generatedAt: DateTimeOffset.Parse("2026-08-15T10:00:00Z"));

        Assert.True(outcome.RequiresSteeredRetry);
        Assert.Equal(1, outcome.FindingCount);
        var evidence = Assert.Single(ReviewEvidenceLog.ReadLatestPerId(jobFolder));
        Assert.Equal(ReviewEvidenceSources.QualityStudio, evidence.Source);
        Assert.Equal("QS-NG-002: Quality Studio test finding", evidence.Title);
        Assert.Equal(["frontend/src/app/project-card.scss:12"], evidence.FileRefs);
        Assert.Equal([outcome.ArtifactPath], evidence.Artifacts);

        var artifact = Path.Combine(jobFolder, outcome.ArtifactPath.Replace('/', Path.DirectorySeparatorChar));
        using var document = JsonDocument.Parse(File.ReadAllText(artifact));
        Assert.Equal("QS-NG-002",
            document.RootElement.GetProperty("findings")[0].GetProperty("ruleId").GetString());
        Assert.True(document.RootElement.GetProperty("findingsBlockPipeline").GetBoolean());
    }

    [Fact]
    public void SecurityFinding_RemainsVisibleWithoutBlockingThePipeline()
    {
        var jobFolder = Path.Combine(_root, "security-job");
        Directory.CreateDirectory(jobFolder);
        var selection = QualityStudioAnalysisPolicy.Resolve(_root, ["backend/Auth.cs"]);
        var result = new QualityStudioAnalysisResult(
            true,
            "AgentOrchestrator.CodeQuality",
            "0.1.0",
            [Finding("QS-CS-004")]);

        var outcome = QualityStudioAnalysisEvidence.Persist(
            jobFolder, "AGT-2655", PipelineCatalogue.QualitySecurityStepId, selection, result);

        Assert.False(outcome.RequiresSteeredRetry);
        Assert.Single(ReviewEvidenceLog.ReadLatestPerId(jobFolder));
    }

    private static QualityStudioFinding Finding(string ruleId) => new(
        "finding-1",
        ruleId,
        "maintainability",
        "medium",
        "Quality Studio test finding",
        "A synthetic finding used to verify the consumer contract.",
        "Consult the named Quality Studio rule.",
        "sha256:0123456789abcdef",
        [new QualityStudioFindingLocation("frontend/src/app/project-card.scss", 12, 4)],
        "Synthetic package response.");

    private sealed class RecordingCore : IQualityStudioAnalysisCore
    {
        public List<QualityStudioAnalysisRequest> Requests { get; } = [];

        public Task<QualityStudioAnalysisResult> RunAsync(
            QualityStudioAnalysisRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            IReadOnlyList<QualityStudioFinding> findings =
                request.StepId == PipelineCatalogue.QualityStaticRulesStepId
                    ? [Finding("QS-NG-002")]
                    : [];
            return Task.FromResult(new QualityStudioAnalysisResult(
                true, "AgentOrchestrator.CodeQuality", "0.1.0", findings));
        }
    }
}
