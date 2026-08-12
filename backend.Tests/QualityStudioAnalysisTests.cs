using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace AgentStudio.Tests;

public sealed class QualityStudioAnalysisTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "agent-studio-qs-analysis-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("frontend/src/app/example.component.ts", true, false)]
    [InlineData("frontend/src/app/example.component.html", true, false)]
    [InlineData("src/app/example.component.ts", true, false)]
    [InlineData("apps/portal/src/app/example.component.scss", true, false)]
    [InlineData("backend/Features/Example.cs", false, true)]
    [InlineData("docs/system/domains/pipeline.md", false, false)]
    public void CardClass_IsDerivedOnlyFromChangedFiles(
        string path,
        bool frontend,
        bool backend)
    {
        var result = QualityStudioAnalysisPolicy.Classify([path]);

        Assert.Equal(frontend, result.Frontend);
        Assert.Equal(backend, result.Backend);
    }

    [Fact]
    public void FrontendDefaults_SelectAngularVisualAndSharedAxes()
    {
        var selected = QualityStudioAnalysisPolicy.SelectDefaultStepIds(
            ["frontend/src/app/example.component.ts"]);

        Assert.Contains(PipelineCatalogue.QualityStudioAngularRulesStepId, selected);
        Assert.Contains(PipelineCatalogue.QualityStudioVisualStepId, selected);
        Assert.Contains(PipelineCatalogue.QualityStudioModelReviewStepId, selected);
        Assert.Contains(PipelineCatalogue.QualityStudioRedundancyStepId, selected);
        Assert.Contains(PipelineCatalogue.QualityStudioConsistencyStepId, selected);
        Assert.DoesNotContain(PipelineCatalogue.QualityStudioDotNetRulesStepId, selected);
        Assert.DoesNotContain(PipelineCatalogue.QualityStudioSecurityStepId, selected);
    }

    [Fact]
    public void BackendDefaults_SelectDotNetSecurityAndSharedAxes()
    {
        var selected = QualityStudioAnalysisPolicy.SelectDefaultStepIds(
            ["backend/Features/Example.cs"]);

        Assert.Contains(PipelineCatalogue.QualityStudioDotNetRulesStepId, selected);
        Assert.Contains(PipelineCatalogue.QualityStudioSecurityStepId, selected);
        Assert.Contains(PipelineCatalogue.QualityStudioModelReviewStepId, selected);
        Assert.Contains(PipelineCatalogue.QualityStudioRedundancyStepId, selected);
        Assert.Contains(PipelineCatalogue.QualityStudioConsistencyStepId, selected);
        Assert.DoesNotContain(PipelineCatalogue.QualityStudioAngularRulesStepId, selected);
        Assert.DoesNotContain(PipelineCatalogue.QualityStudioVisualStepId, selected);
    }

    [Fact]
    public void ActionableFinding_SteersOnceThenEscalates()
    {
        var finding = Finding("medium");

        Assert.Equal(
            QualityStudioFindingDisposition.SteerOnce,
            QualityStudioAnalysisPolicy.Decide([finding], priorSteeredRetries: 0));
        Assert.Equal(
            QualityStudioFindingDisposition.Escalate,
            QualityStudioAnalysisPolicy.Decide([finding], priorSteeredRetries: 1));
        Assert.Equal(
            QualityStudioFindingDisposition.Continue,
            QualityStudioAnalysisPolicy.Decide([Finding("low")], priorSteeredRetries: 0));
    }

    [Fact]
    public void PortableEvidence_ParsesOnlyQsAngularNewAndPersistingFindings()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "evidence.json");
        var angular = FindingEnvelope("finding-angular", "QS-NG-002", "medium");
        var other = FindingEnvelope("finding-dotnet", "QS-CS-001", "high");
        var resolved = FindingEnvelope("finding-resolved", "QS-NG-004", "medium");
        var artifact = new Dictionary<string, object?>
        {
            ["$schema"] = QualityStudioAnalysisRunner.EvidenceSchema,
            ["schemaVersion"] = 1,
            ["generatedAt"] = "2026-08-12T08:00:00Z",
            ["repository"] = "PROJ-002",
            ["policy"] = new
            {
                id = "quality-studio-change-review",
                version = "1",
                contentHash = Hash('a'),
            },
            ["reviews"] = new[]
            {
                new
                {
                    subject = new { },
                    review = new { },
                    agentEvidence = new { },
                    findings = new
                    {
                        @new = new[] { angular, other },
                        resolved = new[] { resolved },
                        persisting = new[] { angular },
                    },
                },
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(artifact));

        var findings = QualityStudioEvidenceParser.ParseAngularFindings(path);

        var finding = Assert.Single(findings);
        Assert.Equal("QS-NG-002", finding.RuleId);
        Assert.Equal("frontend/src/app/example.component.scss", finding.Locations.Single().Path);
        Assert.Equal(12, finding.Locations.Single().Line);
    }

    [Fact]
    public void CliArguments_UsePortableNoWriteSeamAndStableRepositoryIdentity()
    {
        var startInfo = new ProcessStartInfo();
        var request = new QualityStudioAnalysisRequest(
            "/repo", "Agent Studio", "AGT-1", "/task", new string('a', 40),
            new string('b', 40), ["frontend/src/app/example.component.ts"])
        {
            RepositoryId = "PROJ-002",
            ReviewPolicyHash = Hash('c'),
        };

        QualityStudioAnalysisRunner.AddArguments(startInfo, request, "/task/results/quality-studio/angular-rules.json");

        Assert.Equal("diff", startInfo.ArgumentList[0]);
        Assert.Contains("--no-write", startInfo.ArgumentList);
        Assert.Contains("--fail-on-regression", startInfo.ArgumentList);
        Assert.Equal("PROJ-002", ValueAfter(startInfo.ArgumentList, "--repository"));
        Assert.Equal(Hash('c'), ValueAfter(startInfo.ArgumentList, "--review-policy-hash"));
        Assert.Equal("/task/results/quality-studio/angular-rules.json", ValueAfter(startInfo.ArgumentList, "--output"));
    }

    [Fact]
    public void ReviewEvidence_PreservesQualityStudioRuleIdAndArtifact()
    {
        var finding = Finding("medium");
        var result = new QualityStudioAnalysisResult(
            QualityStudioAnalysisVerdict.Findings,
            1,
            12,
            "one finding",
            "",
            "results/quality-studio/angular-rules.json",
            [finding]);

        QualityStudioReviewEvidence.Append(_root, 3, result);

        var entry = Assert.Single(ReviewEvidenceLog.ReadLatestPerId(_root));
        Assert.Equal(ReviewEvidenceSources.QualityStudio, entry.Source);
        Assert.Equal("QS-NG-002", entry.RuleId);
        Assert.Equal(3, entry.RunIndex);
        Assert.Equal(["results/quality-studio/angular-rules.json"], entry.Artifacts);
        Assert.Contains("QS-NG-002", QualityStudioReviewEvidence.BuildFollowUp([finding]));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort test cleanup.
        }
    }

    private static QualityStudioFinding Finding(string severity) => new(
        "finding", "QS-NG-002", severity, "Named finding", "Description",
        "Recommendation", Hash('d'), [], null);

    private static Dictionary<string, object?> FindingEnvelope(
        string id,
        string ruleId,
        string severity) => new()
    {
        ["$schema"] = QualityStudioAnalysisRunner.FindingSchema,
        ["schemaVersion"] = 1,
        ["subject"] = new { },
        ["id"] = id,
        ["aspect"] = "maintainability",
        ["severity"] = severity,
        ["title"] = "Named Angular finding",
        ["description"] = "Finding description from Quality Studio.",
        ["recommendation"] = "Finding recommendation from Quality Studio.",
        ["locations"] = new[]
        {
            new
            {
                path = "frontend/src/app/example.component.scss",
                range = new
                {
                    start = new { line = 12, column = 3 },
                    end = new { line = 12, column = 10 },
                },
            },
        },
        ["fingerprint"] = Hash(ruleId[^1]),
        ["fingerprintCanonicalization"] = "quality-studio-finding-v1",
        ["ruleId"] = ruleId,
        ["producer"] = new { kind = "deterministic", id = "quality-rules" },
    };

    private static string Hash(char value) => "sha256:" + new string(value, 64);

    private static string? ValueAfter(IList<string> arguments, string key)
    {
        var index = arguments.IndexOf(key);
        return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
    }
}
