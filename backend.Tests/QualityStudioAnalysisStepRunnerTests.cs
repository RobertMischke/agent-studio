using AgentOrchestrator.CodeQuality;
using AgentStudio.Pipeline;
using AgentStudio.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace OrchestratorApi.Tests;

public sealed class QualityStudioAnalysisStepRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "agt-qs-analysis-" + Guid.NewGuid().ToString("N"));

    public QualityStudioAnalysisStepRunnerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Catalogue_DeclaresDistinctQualityStudioAnalysisSteps()
    {
        var steps = PipelineCatalogue.Standard.Post.Where(step => step.Kind == StepKind.Analysis).ToArray();

        Assert.Equal(PipelineCatalogue.QualityStudioAnalysisStepIds, steps.Select(step => step.Id));
        Assert.All(steps, step => Assert.True(step.DefaultEnabled));
        Assert.False(steps.Single(step =>
            step.Id == PipelineCatalogue.QualityStudioAngularRulesStepId).Stub);
        Assert.All(steps.Where(step =>
            step.Id != PipelineCatalogue.QualityStudioAngularRulesStepId), step => Assert.True(step.Stub));
        var buildIndex = PipelineCatalogue.Standard.Post.FindIndex(step =>
            step.Id == PipelineCatalogue.BuildTestGateStepId);
        var aspectIndex = PipelineCatalogue.Standard.Post.FindIndex(step => step.Kind == StepKind.Aspect);
        Assert.All(steps, step =>
        {
            var index = PipelineCatalogue.Standard.Post.FindIndex(candidate => candidate.Id == step.Id);
            Assert.True(index > buildIndex);
            Assert.True(index < aspectIndex);
        });
    }

    [Fact]
    public void Policy_UsesCardClassConventionsAndKeepsSecurityEvidenceOnly()
    {
        var frontend = QualityStudioAnalysisPolicy.Resolve(
            ["frontend/src/app/card.scss"],
            [PipelineStepStacks.Angular, PipelineStepStacks.DotNet],
            settings: null);
        Assert.True(Decision(frontend, PipelineCatalogue.QualityStudioAngularRulesStepId).Enabled);
        Assert.True(Decision(frontend, PipelineCatalogue.QualityStudioVisualStepId).Enabled);
        Assert.False(Decision(frontend, PipelineCatalogue.QualityStudioSecurityStepId).Enabled);

        var conventionalAngularLayout = QualityStudioAnalysisPolicy.Resolve(
            ["src/app/card.component.ts"],
            [PipelineStepStacks.Angular],
            settings: null);
        Assert.True(Decision(
            conventionalAngularLayout,
            PipelineCatalogue.QualityStudioAngularRulesStepId).Enabled);

        var backend = QualityStudioAnalysisPolicy.Resolve(
            ["backend/Features/Card.cs"],
            [PipelineStepStacks.Angular, PipelineStepStacks.DotNet],
            settings: null);
        Assert.True(Decision(backend, PipelineCatalogue.QualityStudioDotNetRulesStepId).Enabled);
        var security = Decision(backend, PipelineCatalogue.QualityStudioSecurityStepId);
        Assert.True(security.Enabled);
        Assert.False(security.BlocksOnFindings);
    }

    [Fact]
    public void Policy_ProjectPipelineSettingOverridesConvention()
    {
        var settings = new ProjectSettings
        {
            PipelineSteps = new Dictionary<string, PipelineStepSetting>
            {
                [PipelineCatalogue.QualityStudioVisualStepId] = new() { Enabled = false },
                [PipelineCatalogue.QualityStudioDotNetRulesStepId] = new() { Enabled = true },
            },
        };

        var decisions = QualityStudioAnalysisPolicy.Resolve(
            ["frontend/src/app/card.scss"],
            [PipelineStepStacks.Angular],
            settings);

        Assert.False(Decision(decisions, PipelineCatalogue.QualityStudioVisualStepId).Enabled);
        Assert.True(Decision(decisions, PipelineCatalogue.QualityStudioDotNetRulesStepId).Enabled);
    }

    [Fact]
    public async Task AngularRuleStep_RunsRealQualityStudioCoreAndWritesNamedEvidence()
    {
        Write("angular.json", "{}");
        Write("frontend/src/app/card.scss", ".card { margin: 12px; color: #abcdef; }");
        var jobFolder = Path.Combine(_root, "job");
        Directory.CreateDirectory(jobFolder);
        var runner = new QualityStudioAnalysisStepRunner(
            QualityAnalysisCore.CreateDefault(),
            NullLogger<QualityStudioAnalysisStepRunner>.Instance);

        var result = await runner.RunAngularRulesAsync(
            _root,
            jobFolder,
            ["frontend/src/app/card.scss"]);

        Assert.Equal(QualityStudioAnalysisRunStatus.Findings, result.Status);
        Assert.Contains(result.Findings, finding => finding.RuleId == "QS-NG-002");
        var artifactPath = Path.Combine(jobFolder, result.Artifact.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(artifactPath));
        var artifact = File.ReadAllText(artifactPath);
        using var document = JsonDocument.Parse(artifact);
        var packageVersion = document.RootElement.GetProperty("packageVersion").GetString();
        Assert.False(string.IsNullOrWhiteSpace(packageVersion));
        Assert.NotEqual("unknown", packageVersion);
        Assert.Contains("QS-NG-002", artifact);
        var reviewEvidence = File.ReadAllText(Path.Combine(jobFolder, "results", "review-evidence.jsonl"));
        Assert.Contains("QS-NG-002", reviewEvidence);
        var blockingVerdict = result.ToBlockingVerdict();
        Assert.Equal(AspectStatus.Block, blockingVerdict.Status);
        Assert.Contains("QS-NG-002", blockingVerdict.Body);
        Assert.Contains(result.Artifact, blockingVerdict.Summary);
    }

    [Fact]
    public async Task AngularRuleStep_LeavesRuleOverrideSemanticsToRepositoryConfig()
    {
        Write("angular.json", "{}");
        Write("frontend/src/app/card.scss", ".card { margin: 12px; }");
        Write(".quality/rules.json", """
        {
          "$schema": "https://quality.studio/schemas/rule-configuration.v1.schema.json",
          "schemaVersion": 1,
          "rules": {
            "QS-NG-002": { "enabled": false }
          }
        }
        """);
        var jobFolder = Path.Combine(_root, "job-config");
        Directory.CreateDirectory(jobFolder);
        var runner = new QualityStudioAnalysisStepRunner(
            QualityAnalysisCore.CreateDefault(),
            NullLogger<QualityStudioAnalysisStepRunner>.Instance);

        var result = await runner.RunAngularRulesAsync(
            _root,
            jobFolder,
            ["frontend/src/app/card.scss"]);

        Assert.Equal(QualityStudioAnalysisRunStatus.Passed, result.Status);
        Assert.Empty(result.Findings);
    }

    private static QualityStudioAnalysisDecision Decision(
        IEnumerable<QualityStudioAnalysisDecision> decisions,
        string stepId) => decisions.Single(decision => decision.Step.Id == stepId);

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "QualityStudioAnalysisStepRunnerTests cleanup"); }
    }
}
