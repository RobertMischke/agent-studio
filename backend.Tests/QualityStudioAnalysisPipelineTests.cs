using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class QualityStudioAnalysisPipelineTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "agent-studio-quality-analysis-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Catalogue_DeclaresDistinctDefaultQualityStudioAxes()
    {
        var steps = PipelineCatalogue.Standard.Post
            .Where(step => step.Kind == StepKind.Analysis)
            .ToArray();

        Assert.Equal(PipelineCatalogue.QualityAnalysisStepIds, steps.Select(step => step.Id));
        Assert.All(steps, step =>
        {
            Assert.Equal("quality-studio", step.AnalysisProvider);
            Assert.True(step.DefaultEnabled);
        });
        Assert.False(steps.Single(step =>
            step.Id == PipelineCatalogue.QualitySecurityStepId).BlockingFindings);
        Assert.False(steps.Single(step =>
            step.Id == PipelineCatalogue.QualityAngularRulesStepId).Stub);
        Assert.All(steps.Where(step =>
            step.Id != PipelineCatalogue.QualityAngularRulesStepId),
            step => Assert.True(step.Stub));
    }

    [Fact]
    public void Policy_SelectsFrontendAndBackendConventionsFromCardFiles()
    {
        Directory.CreateDirectory(root);

        var frontend = QualityStudioAnalysisPolicy.Resolve(root,
            ["frontend/src/app/card.component.ts", "frontend/src/app/card.component.scss"]);
        Assert.True(frontend.FrontendTouching);
        Assert.False(frontend.BackendTouching);
        Assert.Contains(PipelineCatalogue.QualityAngularRulesStepId, frontend.DefaultStepIds);
        Assert.Contains(PipelineCatalogue.QualityVisualStepId, frontend.DefaultStepIds);
        Assert.DoesNotContain(PipelineCatalogue.QualitySecurityStepId, frontend.DefaultStepIds);

        var backend = QualityStudioAnalysisPolicy.Resolve(root,
            ["backend/Features/Tasks/TaskService.cs"]);
        Assert.False(backend.FrontendTouching);
        Assert.True(backend.BackendTouching);
        Assert.Contains(PipelineCatalogue.QualityDotNetRulesStepId, backend.DefaultStepIds);
        Assert.Contains(PipelineCatalogue.QualitySecurityStepId, backend.DefaultStepIds);
        Assert.Contains(PipelineCatalogue.QualityConsistencyStepId, backend.DefaultStepIds);
    }

    [Fact]
    public void ProjectSettings_CanDisableDefaultAnalysisStep()
    {
        var step = PipelineCatalogue.Standard.Post.Single(item =>
            item.Id == PipelineCatalogue.QualityAngularRulesStepId);
        var settings = new ProjectSettings
        {
            PipelineSteps = new Dictionary<string, PipelineStepSetting>(StringComparer.OrdinalIgnoreCase)
            {
                [step.Id] = new() { Enabled = false },
            },
        };

        Assert.True(PipelineStepConfigResolver.IsEnabled(null, step));
        Assert.False(PipelineStepConfigResolver.IsEnabled(settings, step));
    }

    [Fact]
    public async Task AngularRulePass_UsesQsRuleIdsAndWritesReviewEvidence()
    {
        var (repository, job, stylePath) = CreateAngularCard(".card { padding: 12px; }\n");
        var runner = new QualityStudioAnalysisStepRunner(
            NullLogger<QualityStudioAnalysisStepRunner>.Instance);

        var result = await runner.RunAngularRulesAsync(new QualityStudioAnalysisStepRequest(
            repository,
            job,
            [stylePath],
            RunIndex: 2));

        Assert.Equal(QualityStudioAnalysisVerdict.Findings, result.Verdict);
        var finding = Assert.Single(result.Findings);
        Assert.Equal("QS-NG-002", finding.RuleId);
        Assert.Equal(QualityStudioAnalysisStepRunner.ArtifactRelativePath, result.ArtifactPath);

        var artifact = Path.Combine(job, "results", "quality-studio", "angular-rules.json");
        Assert.True(File.Exists(artifact));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(artifact));
        Assert.Equal("quality-rules", document.RootElement.GetProperty("analysisName").GetString());
        Assert.Equal("QS-NG-002", document.RootElement
            .GetProperty("findings")[0]
            .GetProperty("ruleId")
            .GetString());
        Assert.False(document.RootElement.GetProperty("persistedRepositoryArtifacts").GetBoolean());

        var evidence = Assert.Single(ReviewEvidenceLog.ReadLatestPerId(job));
        Assert.Equal("QS-NG-002", evidence.RuleId);
        Assert.Equal(ReviewEvidenceSources.QualityStudio, evidence.Source);
        Assert.Equal(2, evidence.RunIndex);
        Assert.Contains(QualityStudioAnalysisStepRunner.ArtifactRelativePath, evidence.Artifacts);
        Assert.Contains(evidence.FileRefs, path => path.EndsWith(":1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AngularRulePass_ReadsRepositoryOwnedOverride()
    {
        var (repository, job, stylePath) = CreateAngularCard(".card { padding: 12px; }\n");
        var quality = Path.Combine(repository, ".quality");
        Directory.CreateDirectory(quality);
        await File.WriteAllTextAsync(Path.Combine(quality, "rules.json"), """
            {
              "$schema": "https://quality.studio/schemas/rule-configuration.v1.schema.json",
              "schemaVersion": 1,
              "rules": {
                "QS-NG-002": { "enabled": false }
              }
            }
            """);
        var runner = new QualityStudioAnalysisStepRunner(
            NullLogger<QualityStudioAnalysisStepRunner>.Instance);

        var result = await runner.RunAngularRulesAsync(new QualityStudioAnalysisStepRequest(
            repository,
            job,
            [stylePath]));

        Assert.Equal(QualityStudioAnalysisVerdict.Pass, result.Verdict);
        Assert.Empty(result.Findings);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(job, "results", "quality-studio", "angular-rules.json")));
        Assert.Equal(".quality/rules.json",
            document.RootElement.GetProperty("ruleConfiguration").GetString());
    }

    private (string Repository, string Job, string StylePath) CreateAngularCard(string style)
    {
        var repository = Path.Combine(root, "repository");
        var job = Path.Combine(root, "job");
        var component = Path.Combine(repository, "frontend", "src", "app");
        Directory.CreateDirectory(component);
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(repository, "frontend", "angular.json"), "{}\n");
        const string relativeStyle = "frontend/src/app/card.component.scss";
        File.WriteAllText(Path.Combine(repository, relativeStyle.Replace('/', Path.DirectorySeparatorChar)), style);
        return (repository, job, relativeStyle);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
