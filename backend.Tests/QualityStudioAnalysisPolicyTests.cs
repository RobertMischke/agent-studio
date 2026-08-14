using AgentStudio.Persistence;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using Xunit;

namespace AgentStudio.Tests;

public sealed class QualityStudioAnalysisPolicyTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("agt-qs-policy-").FullName;

    [Fact]
    public void Frontend_card_selects_rules_visual_and_cross_cutting_axes()
    {
        var selection = QualityStudioAnalysisPolicy.Resolve(new QualityStudioCardFacts(
            "task", [], "Update the workspace", ["frontend/src/app/demo.component.ts"]));

        Assert.True(selection.FrontendTouching);
        Assert.False(selection.BackendTouching);
        Assert.True(selection.Runs(PipelineCatalogue.QualityStudioRuleAnalysisStepId));
        Assert.True(selection.Runs(PipelineCatalogue.QualityStudioVisualQualityStepId));
        Assert.True(selection.Runs(PipelineCatalogue.QualityStudioModelReviewStepId));
        Assert.True(selection.Runs(PipelineCatalogue.QualityStudioRedundancyStepId));
        Assert.True(selection.Runs(PipelineCatalogue.QualityStudioConsistencyStepId));
        Assert.False(selection.Runs(PipelineCatalogue.QualityStudioSecurityStepId));
    }

    [Fact]
    public void Angular_project_root_selects_frontend_without_agent_studio_folder_convention()
    {
        var repository = Path.Combine(root, "angular-root");
        Directory.CreateDirectory(Path.Combine(repository, "src", "app"));
        File.WriteAllText(Path.Combine(repository, "angular.json"), "{}");

        var selection = QualityStudioAnalysisPolicy.Resolve(new QualityStudioCardFacts(
            "task", [], "Update workspace", ["src/app/demo.component.ts"], repository));

        Assert.True(selection.FrontendTouching);
        Assert.True(selection.Runs(PipelineCatalogue.QualityStudioVisualQualityStepId));
    }

    [Theory]
    [InlineData("../outside.ts")]
    [InlineData("/tmp/outside.ts")]
    [InlineData("frontend/../outside.ts")]
    public void Analysis_paths_must_be_repository_relative_and_contained(string path)
    {
        Assert.Null(QualityStudioAnalysisPolicy.NormalizeRuleSourcePath(path));
    }

    [Fact]
    public void Backend_card_selects_csharp_rules_and_non_blocking_security_axis()
    {
        var selection = QualityStudioAnalysisPolicy.Resolve(new QualityStudioCardFacts(
            "bug", [], "Repair task projection", ["backend/Features/Tasks/Projection.cs"]));

        Assert.False(selection.FrontendTouching);
        Assert.True(selection.BackendTouching);
        Assert.True(selection.Runs(PipelineCatalogue.QualityStudioRuleAnalysisStepId));
        Assert.True(selection.Runs(PipelineCatalogue.QualityStudioSecurityStepId));
        Assert.False(selection.Runs(PipelineCatalogue.QualityStudioVisualQualityStepId));
        Assert.False(QualityStudioAnalysisPolicy.BlocksPipeline(
            PipelineCatalogue.QualityStudioSecurityStepId, "critical"));
        Assert.True(QualityStudioAnalysisPolicy.BlocksPipeline(
            PipelineCatalogue.QualityStudioRuleAnalysisStepId, "high"));
    }

    [Fact]
    public void Repository_json_rejects_unknown_analysis_steps()
    {
        Directory.CreateDirectory(Path.Combine(root, ".quality"));
        File.WriteAllText(Path.Combine(root, ".quality", "agent-studio-pipeline.json"), """
        {
          "$schema": "https://agent-taskboard.local/schemas/quality-analysis-policy.v1.schema.json",
          "schemaVersion": 1,
          "steps": {
            "post-qs-invented-axis": { "enabled": true }
          }
        }
        """);

        var exception = Assert.Throws<InvalidDataException>(() =>
            QualityStudioAnalysisPolicy.LoadOverrides(root));

        Assert.Contains("unknown analysis step", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Repository_json_is_the_only_step_override_and_is_strict()
    {
        Directory.CreateDirectory(Path.Combine(root, ".quality"));
        File.WriteAllText(Path.Combine(root, ".quality", "agent-studio-pipeline.json"), """
        {
          "$schema": "https://agent-taskboard.local/schemas/quality-analysis-policy.v1.schema.json",
          "schemaVersion": 1,
          "steps": {
            "post-qs-visual-quality": { "enabled": false },
            "post-qs-security": { "enabled": true }
          }
        }
        """);

        var overrides = QualityStudioAnalysisPolicy.LoadOverrides(root);
        var selection = QualityStudioAnalysisPolicy.Resolve(new QualityStudioCardFacts(
            "task", [], "Frontend work", ["frontend/src/app/demo.component.html"]), overrides);

        Assert.False(selection.Runs(PipelineCatalogue.QualityStudioVisualQualityStepId));
        Assert.True(selection.Runs(PipelineCatalogue.QualityStudioSecurityStepId));
    }

    [Fact]
    public async Task Real_quality_studio_package_emits_named_rule_evidence_for_frontend_card()
    {
        var repository = Path.Combine(root, "repository");
        var job = Path.Combine(root, "job");
        var componentDirectory = Path.Combine(repository, "frontend", "src", "app");
        Directory.CreateDirectory(componentDirectory);
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(repository, "frontend", "angular.json"), "{}");
        File.WriteAllText(Path.Combine(componentDirectory, "demo.component.scss"),
            ".demo { padding: 12px; color: #fff; }");

        var runner = new QualityStudioRuleAnalysisRunner(
            new QualityStudioPackageAnalysisCore(),
            new AtomicJsonFileWriter());
        var outcome = await runner.RunAsync(new QualityStudioRuleAnalysisRequest(
            repository,
            job,
            "task",
            ["frontend"],
            "Add frontend demo",
            ["frontend/src/app/demo.component.scss"],
            1), CancellationToken.None);

        Assert.Equal(QualityStudioRuleAnalysisVerdict.Findings, outcome.Verdict);
        Assert.True(outcome.RequiresRetry);
        Assert.Contains(outcome.Findings, finding => finding.RuleId == "QS-NG-002");
        var artifact = Path.Combine(job,
            QualityStudioRuleAnalysisRunner.ArtifactRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(artifact));
        var json = File.ReadAllText(artifact);
        Assert.Contains("AgentOrchestrator.CodeQuality", json, StringComparison.Ordinal);
        Assert.Contains("QS-NG-002", json, StringComparison.Ordinal);
        Assert.Contains("quality-rules", json, StringComparison.Ordinal);
        var evidence = Assert.Single(ReviewEvidenceLog.ReadLatestPerId(job));
        Assert.StartsWith("[QS-NG-002]", evidence.Title, StringComparison.Ordinal);
        Assert.Equal("frontend/src/app/demo.component.scss:1", Assert.Single(evidence.FileRefs));
        Assert.False(Directory.Exists(Path.Combine(repository, ".quality")),
            "The pipeline package call must keep repository artifact writes disabled.");
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
