using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class QualityStudioAnalysisCommandRunnerTests
{
    [Fact]
    public async Task MissingPackage_IsAnExplicitInfrastructureFailureWithoutHttpFallback()
    {
        var runner = new QualityStudioAnalysisCommandRunner(
            () => throw new FileNotFoundException("package missing"));
        var command = new ReviewCommandDto(
            "analysis-qs-static-rules",
            "angular-rules",
            "quality-rules",
            ["frontend/src/app/card.scss"],
            ExecutionKind: ReviewCommandKinds.QualityAnalysis);

        var execution = await runner.RunAsync(command, Path.GetTempPath(), CancellationToken.None);

        Assert.Equal(127, execution.Process.ExitCode);
        Assert.Contains("in-process analysis is unavailable", execution.Process.StdErr);
        Assert.DoesNotContain("http", execution.Process.StdErr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StructuredEvidence_SummarizesNamedRuleIdsForRetry()
    {
        var json = """
        {
          "schemaVersion": 1,
          "stepId": "analysis-qs-static-rules",
          "analysis": "quality-rules",
          "axis": "angular-rules",
          "provider": "AgentOrchestrator.CodeQuality",
          "providerVersion": "0.1.0",
          "configurationPath": ".quality/rules.json",
          "available": true,
          "unavailableReason": null,
          "securityFindingsBlockPipeline": false,
          "findings": [
            {
              "id": "finding-1",
              "ruleId": "QS-NG-002",
              "severity": "medium",
              "title": "Use design tokens",
              "description": "Raw value",
              "recommendation": "Use the token",
              "fingerprint": "sha256:1",
              "path": "frontend/src/app/card.scss",
              "line": 12,
              "column": 4
            }
          ]
        }
        """;

        Assert.True(QualityStudioAnalysisEvidence.TryParse(json, out var report));
        Assert.NotNull(report);
        Assert.Contains("QS-NG-002 frontend/src/app/card.scss:12", report.VerdictSummary());
        Assert.False(report.SecurityFindingsBlockPipeline);
    }

    [Fact]
    public async Task PublicSensorWithOptionalLibraryConstructor_ProducesStructuredFindingEvidence()
    {
        var runner = new QualityStudioAnalysisCommandRunner(
            () => typeof(AgentOrchestrator.CodeQuality.RulePrecheckSensor).Assembly);
        var command = new ReviewCommandDto(
            "analysis-qs-static-rules",
            "angular-rules",
            "quality-rules",
            ["frontend/src/app/card.scss"],
            ExecutionKind: ReviewCommandKinds.QualityAnalysis);

        var execution = await runner.RunAsync(command, Path.GetTempPath(), CancellationToken.None);

        Assert.Equal(1, execution.Process.ExitCode);
        Assert.True(QualityStudioAnalysisEvidence.TryParse(execution.Process.StdOut, out var report));
        var finding = Assert.Single(Assert.IsType<QualityStudioAnalysisEvidence>(report).Findings);
        Assert.Equal("QS-NG-002", finding.RuleId);
        Assert.Equal("frontend/src/app/card.scss", finding.Path);
        Assert.Equal(7, finding.Line);
    }
}
