using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class QualityAnalysisPolicyTests
{
    [Fact]
    public void FrontendChange_DefaultsToAngularRulesAndVisualAxis()
    {
        var decision = QualityAnalysisPolicy.Decide(
            ["frontend/src/app/card.component.ts", "frontend/src/app/card.component.scss"]);

        Assert.True(decision.RunsAngularRules);
        Assert.True(decision.RunsVisual);
        Assert.False(decision.RunsDotNetRules);
        Assert.False(decision.RunsSecurity);
        Assert.Equal(
            [QualityAnalysisPolicy.AngularRuleAxis, QualityAnalysisPolicy.VisualAxis],
            decision.DefaultAxes);
    }

    [Fact]
    public void BackendChange_DefaultsToDotNetRulesAndNonBlockingSecurityAxis()
    {
        var decision = QualityAnalysisPolicy.Decide(["backend/Features/Tasks/Task.cs"]);

        Assert.True(decision.RunsDotNetRules);
        Assert.True(decision.RunsSecurity);
        Assert.False(decision.RunsAngularRules);
        Assert.False(decision.RunsVisual);
    }

    [Fact]
    public void DocumentationChange_DoesNotScheduleCodeAxes()
    {
        var decision = QualityAnalysisPolicy.Decide(["docs/system/domains/pipeline.md"]);

        Assert.Empty(decision.DefaultAxes);
    }

    [Fact]
    public void AngularFindings_ProduceOneNamedSteeredRetry()
    {
        var report = ReviewReport("QS-NG-002", "frontend/src/app/card.scss", 12);

        var decision = QualityAnalysisSteeredRetryPolicy.Decide(report, new PipelineExecutionRecord());

        Assert.True(decision.ShouldRetry);
        Assert.False(decision.BudgetExhausted);
        Assert.Contains("`QS-NG-002`", decision.FollowUp);
        Assert.Contains("frontend/src/app/card.scss:12", decision.FollowUp);
    }

    [Fact]
    public void PriorFailedRuleAttempt_ExhaustsDurableRetryBudget()
    {
        var report = ReviewReport("QS-NG-004", "frontend/src/app/card.html", 8);
        var pipeline = new PipelineExecutionRecord
        {
            PreviousAttempts =
            [
                new PipelineExecutionRecord
                {
                    Steps =
                    [
                        new PipelineStepExecution
                        {
                            StepId = PipelineCatalogue.QualityStaticRulesStepId,
                            Kind = StepKind.Analysis,
                            Status = PipelineStepStatus.Failed,
                        },
                    ],
                },
            ],
        };

        var decision = QualityAnalysisSteeredRetryPolicy.Decide(report, pipeline);

        Assert.False(decision.ShouldRetry);
        Assert.True(decision.BudgetExhausted);
        Assert.Single(decision.Findings);
    }

    private static ReviewReportRequest ReviewReport(string ruleId, string path, int line)
    {
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            findings = new[]
            {
                new
                {
                    ruleId,
                    title = "Use design tokens",
                    path,
                    line,
                    recommendation = "Use the shared token.",
                },
            },
        });
        var bytes = Encoding.UTF8.GetBytes(json);
        var digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        return new ReviewReportRequest(
            "executor",
            "instance",
            "lease",
            1,
            "key",
            "ProductFailure",
            null,
            "findings",
            new ReviewWorkspaceProofDto("repo", "sha", "sha", "tree", false, false, "workspace", "namespace"),
            new ReviewEnvironmentDto("host", "executor", "instance", "os", "arch", "runtime", new Dictionary<string, string>(), new Dictionary<string, string>()),
            [
                new ReviewCommandEvidenceDto(
                    PipelineCatalogue.QualityStaticRulesStepId,
                    QualityAnalysisPolicy.AngularRuleAxis,
                    QualityAnalysisPolicy.AngularRuleAnalysis,
                    [path],
                    "sha",
                    "sha",
                    "tree",
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    1,
                    null,
                    digest,
                    "stderr",
                    ExecutionKind: ReviewCommandKinds.QualityAnalysis),
            ],
            [new ReviewArtifactEvidenceDto("quality.json", "application/json", digest, bytes.Length, Convert.ToBase64String(bytes))],
            [new ReviewVerdictDto(QualityAnalysisPolicy.AngularRuleAxis, "block", "QualityAnalysisFindings", "finding")]);
    }
}
