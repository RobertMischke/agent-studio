using AgentStudio.Pipeline;

using AgentStudio.Runner;
using AgentStudio.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class QualityAnalysisStepRunnerTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "quality-analysis-step-" + Guid.NewGuid().ToString("N"));
    private string Repository => Path.Combine(root, "repo");
    private string TaskFolder => Path.Combine(root, "task");

    public QualityAnalysisStepRunnerTests()
    {
        Directory.CreateDirectory(Path.Combine(Repository, "frontend", "src", "app"));
        File.WriteAllText(Path.Combine(Repository, "frontend", "angular.json"), "{}");
        Directory.CreateDirectory(TaskFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    [Fact]
    public void Policy_selects_conventional_axes_from_changed_files()
    {
        var frontend = QualityAnalysisPolicy.Resolve(Repository,
            ["frontend/src/app/example.component.scss"]);
        Assert.Contains(PipelineCatalogue.QualityAngularRulesStepId, frontend.EnabledSteps);
        Assert.Contains(PipelineCatalogue.QualityVisualStepId, frontend.EnabledSteps);
        Assert.DoesNotContain(PipelineCatalogue.QualitySecurityStepId, frontend.EnabledSteps);

        var backend = QualityAnalysisPolicy.Resolve(Repository,
            ["backend/Features/Tasks/TaskService.cs"]);
        Assert.Contains(PipelineCatalogue.QualityDotNetRulesStepId, backend.EnabledSteps);
        Assert.Contains(PipelineCatalogue.QualitySecurityStepId, backend.EnabledSteps);
        Assert.DoesNotContain(PipelineCatalogue.QualityVisualStepId, backend.EnabledSteps);
    }

    [Fact]
    public void Policy_reads_only_the_versioned_repository_override()
    {
        var quality = Path.Combine(Repository, ".quality");
        Directory.CreateDirectory(quality);
        File.WriteAllText(Path.Combine(quality, "agent-studio.json"), $$"""
        {
          "$schema": "{{QualityAnalysisPolicyFiles.SchemaId}}",
          "schemaVersion": 1,
          "steps": {
            "{{PipelineCatalogue.QualityAngularRulesStepId}}": { "enabled": false },
            "{{PipelineCatalogue.QualityConsistencyStepId}}": { "enabled": true }
          }
        }
        """);

        var result = QualityAnalysisPolicy.Resolve(Repository,
            ["frontend/src/app/example.component.scss"]);

        Assert.DoesNotContain(PipelineCatalogue.QualityAngularRulesStepId, result.EnabledSteps);
        Assert.Contains(PipelineCatalogue.QualityVisualStepId, result.EnabledSteps);
        Assert.Contains(PipelineCatalogue.QualityConsistencyStepId, result.EnabledSteps);
        Assert.Equal(QualityAnalysisPolicyFiles.RelativePath, result.ConfigurationPath);
    }

    [Fact]
    public async Task Angular_slice_runs_named_QS_analysis_and_writes_rule_evidence()
    {
        var source = "frontend/src/app/example.component.scss";
        File.WriteAllText(Path.Combine(Repository, source.Replace('/', Path.DirectorySeparatorChar)),
            ".sample { margin: 13px; }");
        var finding = new QualityStudioFinding(
            "qs-ng-002-sample",
            "QS-NG-002",
            "maintainability",
            "medium",
            "Angular quality rule matched",
            "The named Quality Studio rule matched this source location.",
            "Apply the linked Quality Studio rule guidance.",
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "Deterministic rule pre-check matched source text.",
            [new QualityStudioLocation(source, 1, 19)]);
        var core = new RecordingCore(new QualityStudioCoreResult(
            true, null, "AgentOrchestrator.CodeQuality", "0.1.0", [finding]));
        var runner = new QualityAnalysisStepRunner(
            core, NullLogger<QualityAnalysisStepRunner>.Instance);

        var result = await runner.RunAngularRulesAsync(
            Repository, TaskFolder, [source], 3, CancellationToken.None);

        Assert.Equal(QualityAnalysisStepVerdict.Findings, result.Verdict);
        Assert.Single(result.BlockingFindings);
        Assert.Equal(QualityStudioAnalysisCoreAdapter.RulesAnalysisName, core.AnalysisName);
        Assert.Equal("code", core.Configuration!["reviewKind"]);
        Assert.Equal([source], core.Paths);
        Assert.True(File.Exists(Path.Combine(TaskFolder,
            "results", "quality-analysis", PipelineCatalogue.QualityAngularRulesStepId + ".json")));
        var evidence = Assert.Single(ReviewEvidenceLog.ReadLatestPerId(TaskFolder));
        Assert.Equal("QS-NG-002", evidence.RuleId);
        Assert.Equal(3, evidence.RunIndex);
        Assert.Contains("frontend/src/app/example.component.scss:1", evidence.FileRefs);
        Assert.Contains(result.EvidencePath!, evidence.Artifacts);
    }

    [Fact]
    public void Security_findings_are_visible_but_non_blocking()
    {
        var finding = new QualityStudioFinding(
            "security-finding", "QS-SEC-001", "security", "critical", "Secret",
            "Secret detected.", "Rotate it.", "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            null, [new QualityStudioLocation("backend/App.cs", 1, 1)]);

        Assert.False(QualityAnalysisGatePolicy.Blocks(PipelineCatalogue.QualitySecurityStepId, finding));
        Assert.True(QualityAnalysisGatePolicy.Blocks(PipelineCatalogue.QualityAngularRulesStepId, finding));
    }

    [Fact]
    public void Named_rule_findings_feed_the_bounded_steered_retry_gate()
    {
        var finding = new QualityStudioFinding(
            "angular-finding", "QS-NG-002", "maintainability", "medium", "Angular quality rule matched",
            "The named rule matched.", "Apply the linked Quality Studio guidance.",
            "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            null, [new QualityStudioLocation("frontend/src/app/example.component.scss", 7, 4)]);
        var result = new QualityAnalysisStepResult(
            PipelineCatalogue.QualityAngularRulesStepId,
            QualityAnalysisStepVerdict.Findings,
            10,
            "one finding",
            "results/quality-analysis/post-analysis-angular-rules.json",
            [finding],
            [finding]);

        var retry = ReviewDecisionOrchestrator.BuildQualityAnalysisGateDecision(result, 0, 1);
        var exhausted = ReviewDecisionOrchestrator.BuildQualityAnalysisGateDecision(result, 1, 1);

        Assert.NotNull(retry);
        Assert.Equal(CompletionGate.CompletionGateAction.Reissue, retry.Action);
        Assert.Equal(CompletionGate.CompletionGateAction.Escalate, exhausted!.Action);
        Assert.Contains("QS-NG-002 frontend/src/app/example.component.scss:7", retry.Findings[0]);
    }

    private sealed class RecordingCore(QualityStudioCoreResult result) : IQualityStudioAnalysisCore
    {
        public string? AnalysisName { get; private set; }
        public IReadOnlyDictionary<string, string>? Configuration { get; private set; }
        public IReadOnlyList<string>? Paths { get; private set; }

        public Task<QualityStudioCoreResult> RunAsync(
            string repositoryPath,
            string analysisName,
            IReadOnlyDictionary<string, string> configuration,
            IReadOnlyList<string> relativePaths,
            CancellationToken cancellationToken)
        {
            AnalysisName = analysisName;
            Configuration = configuration;
            Paths = relativePaths;
            return Task.FromResult(result);
        }
    }
}
