using System.Text;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the contract of <see cref="SoftwareArchitectureDriftAnalysisService"/>.
/// Covers the deliverables called out in the task prompt: missing-model
/// surfacing, more-than-ten-elements rejection, per-element scoring round
/// trip, evidence-ref carry-through, JSON parse fallback (Structured /
/// Unstructured / MalformedJson), and the no-drift output that still
/// produces a schema-valid report.
/// </summary>
public class SoftwareArchitectureDriftAnalysisServiceTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly string _repoRoot;
    private readonly string _watchedProjectRoot;

    public SoftwareArchitectureDriftAnalysisServiceTests()
    {
        var stem = "software-architecture-drift-tests-" + Guid.NewGuid().ToString("N");
        _projectRoot = Path.Combine(Path.GetTempPath(), stem, "project");
        _repoRoot = Path.Combine(Path.GetTempPath(), stem, "repo");
        _watchedProjectRoot = Path.Combine(Path.GetTempPath(), stem, "watched");
        Directory.CreateDirectory(_projectRoot);
        Directory.CreateDirectory(_repoRoot);
        Directory.CreateDirectory(_watchedProjectRoot);
    }

    public void Dispose()
    {
        try
        {
            var parent = Directory.GetParent(_projectRoot)?.FullName;
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                Directory.Delete(parent, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ------------------------------------------------------------------
    // Missing / rejected architecture model
    // ------------------------------------------------------------------

    [Fact]
    public void SelectScope_NoArchitectureModel_RecordsAttemptedPathsAndKeepsModelNull()
    {
        var svc = new SoftwareArchitectureDriftAnalysisService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            repoRoot: _repoRoot,
            watchedProjectRoot: _watchedProjectRoot);

        Assert.Null(scope.ArchitectureModel);
        Assert.NotEmpty(scope.ArchitectureModelLookup.AttemptedPaths);
        Assert.Null(scope.ArchitectureModelLookup.RejectionReason);
    }

    [Fact]
    public void BuildReport_MissingModel_EmitsHighArchitectureFindingAndStillValidates()
    {
        var svc = new SoftwareArchitectureDriftAnalysisService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            repoRoot: _repoRoot,
            watchedProjectRoot: _watchedProjectRoot);

        // Even when the agent reported a structured Healthy verdict, a
        // missing model overrides the band with a finding.
        const string raw = """
            ```json
            { "verdict": "Healthy.", "scoreBand": "Healthy", "overallScore": 90 }
            ```
            """;
        var parse = svc.TryParseAgentResponse(raw);
        Assert.Equal(SoftwareArchitectureDriftParseStatus.Structured, parse.Status);

        var report = svc.BuildReport(
            scope: scope,
            parse: parse,
            reportId: "01HX0000000000000000000SAR1",
            createdAt: new DateTime(2026, 5, 5, 13, 0, 0, DateTimeKind.Utc));

        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
        Assert.Single(report.Dimensions);
        var dim = report.Dimensions[0];
        Assert.Equal(DriftDimensionType.Architecture, dim.Type);
        Assert.Equal(DriftSeverity.High, dim.Severity);
        Assert.Contains("not yet defined", dim.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Null(report.ArchitectureModel);
    }

    // ------------------------------------------------------------------
    // More-than-ten elements rejection (source model)
    // ------------------------------------------------------------------

    [Fact]
    public void SelectScope_ModelWithElevenElements_IsRejectedAndSurfacedAsLookupRejection()
    {
        WriteArchitectureModel(buildElevenElements: true);

        var svc = new SoftwareArchitectureDriftAnalysisService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            repoRoot: _repoRoot,
            watchedProjectRoot: _watchedProjectRoot);

        Assert.Null(scope.ArchitectureModel);
        Assert.False(string.IsNullOrWhiteSpace(scope.ArchitectureModelLookup.RejectionReason));
        Assert.Contains("at most 10", scope.ArchitectureModelLookup.RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // More-than-ten elements rejection (agent sidecar)
    // ------------------------------------------------------------------

    [Fact]
    public void TryParseAgentResponse_AgentEmittedElevenElements_SurfacesAsMalformedJson()
    {
        var sb = new StringBuilder();
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"verdict\": \"x\",");
        sb.AppendLine("  \"scoreBand\": \"Watch\",");
        sb.AppendLine("  \"overallScore\": 70,");
        sb.AppendLine("  \"architectureModel\": { \"elements\": [");
        for (int i = 1; i <= 11; i++)
        {
            sb.Append("    {\"elementId\":\"el-").Append(i).Append("\",\"score\":50,\"severity\":\"Warn\",\"sourceCoverage\":0.5,\"status\":\"New\"}");
            sb.AppendLine(i == 11 ? string.Empty : ",");
        }
        sb.AppendLine("  ]}");
        sb.AppendLine("}");
        sb.AppendLine("```");

        var svc = new SoftwareArchitectureDriftAnalysisService();
        var parse = svc.TryParseAgentResponse(sb.ToString());

        Assert.Equal(SoftwareArchitectureDriftParseStatus.MalformedJson, parse.Status);
        Assert.Contains("at most 10", parse.ParseError, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Per-element scoring round trip + evidence refs carry through
    // ------------------------------------------------------------------

    [Fact]
    public void BuildReport_PerElementScores_RoundTripIntoArchitectureModelProjectionWithEvidenceRefs()
    {
        WriteArchitectureModel(buildElevenElements: false);

        var svc = new SoftwareArchitectureDriftAnalysisService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            repoRoot: _repoRoot,
            watchedProjectRoot: _watchedProjectRoot);

        Assert.NotNull(scope.ArchitectureModel);
        Assert.Equal(2, scope.ArchitectureModel!.Elements.Count);

        const string raw = """
            ```json
            {
              "verdict": "Watch: backend-api drifted.",
              "scoreBand": "Watch",
              "overallScore": 72,
              "dimensions": [
                {
                  "type": "Architecture",
                  "score": 60,
                  "severity": "Warn",
                  "confidence": 0.8,
                  "sourceCoverage": 0.7,
                  "status": "New",
                  "summary": "backend-api owns more than its documented surface.",
                  "evidenceRefs": ["backend/Endpoints/", "backend/Services/Runner/"],
                  "recommendedActions": ["Trim backend-api ownership boundary"]
                }
              ],
              "architectureModel": {
                "elements": [
                  {
                    "elementId": "frontend-shell",
                    "score": 90,
                    "severity": "Info",
                    "sourceCoverage": 0.85,
                    "status": "New",
                    "summary": "Frontend shell still owns the kanban + project surfaces.",
                    "evidenceRefs": ["frontend/src/app/", "frontend/AGENTS.md"],
                    "followUpTaskSuggestions": []
                  },
                  {
                    "elementId": "backend-api",
                    "score": 55,
                    "severity": "Warn",
                    "sourceCoverage": 0.7,
                    "status": "New",
                    "summary": "Endpoints layer started reaching into runner internals.",
                    "evidenceRefs": ["backend/Endpoints/TaskEndpoints.cs", "backend/Services/Runner/"],
                    "followUpTaskSuggestions": ["Audit TaskEndpoints for runner-internal calls"]
                  }
                ]
              },
              "followUpTaskSuggestions": [
                {
                  "title": "Audit backend-api boundary",
                  "summary": "Endpoints have started calling into runner-internal services. Add an ADR or split the endpoints under a new element.",
                  "priority": "High",
                  "relatedDimension": "Architecture"
                }
              ]
            }
            ```
            """;
        var parse = svc.TryParseAgentResponse(raw);
        Assert.Equal(SoftwareArchitectureDriftParseStatus.Structured, parse.Status);

        var report = svc.BuildReport(
            scope: scope,
            parse: parse,
            reportId: "01HX0000000000000000000SAR2",
            createdAt: new DateTime(2026, 5, 5, 13, 30, 0, DateTimeKind.Utc));

        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
        Assert.NotNull(report.ArchitectureModel);
        Assert.Equal("agent-taskboard-test", report.ArchitectureModel!.ModelId);
        Assert.Equal(2, report.ArchitectureModel!.Elements.Count);

        var backend = report.ArchitectureModel!.Elements.Single(e => e.ElementId == "backend-api");
        Assert.Equal(55, backend.Score);
        Assert.Equal(DriftSeverity.Warn, backend.Severity);
        Assert.Equal(0.7, backend.SourceCoverage);
        Assert.Equal(DriftFindingStatus.New, backend.Status);
        Assert.Contains("backend/Endpoints/TaskEndpoints.cs", backend.EvidenceRefs);
        Assert.Contains("backend/Services/Runner/", backend.EvidenceRefs);
        Assert.NotNull(backend.FollowUpTaskSuggestions);
        Assert.Contains("Audit TaskEndpoints for runner-internal calls", backend.FollowUpTaskSuggestions!);
        // Source model fields (expectedRole, allowedDependencies, sourceRefs)
        // are denormalized into the projection so reviewers do not need the
        // source open.
        Assert.False(string.IsNullOrWhiteSpace(backend.ExpectedRole));
        Assert.NotNull(backend.AllowedDependencies);
        Assert.Contains("task-access", backend.AllowedDependencies!);
        Assert.Contains("runner", backend.AllowedDependencies!);
        Assert.NotNull(backend.SourceRefs);
        Assert.Contains("backend/AGENTS.md", backend.SourceRefs!);
        Assert.Single(report.FollowUpTaskSuggestions);
        Assert.Equal(DriftFollowUpPriority.High, report.FollowUpTaskSuggestions[0].Priority);
    }

    // ------------------------------------------------------------------
    // JSON parse fallback (Unstructured + MalformedJson)
    // ------------------------------------------------------------------

    [Fact]
    public void TryParseAgentResponse_NoFencedJsonBlock_ProducesUnstructuredFallbackKeepingMarkdown()
    {
        const string raw = """
            # Software / Architecture Drift

            Verdict: drifting.

            The backend API element has overgrown but I forgot the JSON sidecar.
            """;

        var svc = new SoftwareArchitectureDriftAnalysisService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(SoftwareArchitectureDriftParseStatus.Unstructured, parse.Status);
        Assert.Null(parse.ParseError);
        Assert.Equal("Software / Architecture Drift", parse.Summary);
        Assert.Equal(DriftScoreBand.Unknown, parse.ScoreBand);
        Assert.Null(parse.Dimensions);
        Assert.Null(parse.ArchitectureElements);
    }

    [Fact]
    public void TryParseAgentResponse_MalformedSidecar_SurfacesParseErrorWithoutHidingMarkdown()
    {
        const string raw = """
            # Software / Architecture Drift

            ```json
            { "verdict": "drifting", "scoreBand": "Watch", "overallScore": 70,
            ```
            """;

        var svc = new SoftwareArchitectureDriftAnalysisService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(SoftwareArchitectureDriftParseStatus.MalformedJson, parse.Status);
        Assert.False(string.IsNullOrWhiteSpace(parse.ParseError));
        Assert.Contains("JSON sidecar failed", parse.ParseError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildReport_MalformedJson_StillProducesSchemaValidReportWithUnknownBand()
    {
        WriteArchitectureModel(buildElevenElements: false);
        var svc = new SoftwareArchitectureDriftAnalysisService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            repoRoot: _repoRoot,
            watchedProjectRoot: _watchedProjectRoot);

        var parse = svc.TryParseAgentResponse(
            "# Drift\n\n```json\n{ \"verdict\": \"x\", \"scoreBand\": \"Watch\",\n```\n");
        Assert.Equal(SoftwareArchitectureDriftParseStatus.MalformedJson, parse.Status);

        var report = svc.BuildReport(
            scope: scope,
            parse: parse,
            reportId: "01HX0000000000000000000SAR3",
            createdAt: DateTime.UtcNow);

        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
        Assert.Equal(DriftScoreBand.Unknown, report.ScoreBand);
        Assert.NotNull(report.ArchitectureModel);
        Assert.Equal(2, report.ArchitectureModel!.Elements.Count);
        // All element scores fall back to 0 / Info / New on MalformedJson.
        Assert.All(report.ArchitectureModel!.Elements, e => Assert.Equal(0, e.Score));
        // Each element still carries at least one evidence ref (the
        // ownershipBoundary fallback so the marble surface points somewhere).
        Assert.All(report.ArchitectureModel!.Elements, e => Assert.NotEmpty(e.EvidenceRefs));
    }

    // ------------------------------------------------------------------
    // No-drift (Healthy) output round trip
    // ------------------------------------------------------------------

    [Fact]
    public void BuildReport_HealthyNoDriftVerdict_ProducesSchemaValidReportWithSyntheticDimension()
    {
        WriteArchitectureModel(buildElevenElements: false);
        var svc = new SoftwareArchitectureDriftAnalysisService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            repoRoot: _repoRoot,
            watchedProjectRoot: _watchedProjectRoot);

        const string raw = """
            ```json
            {
              "verdict": "Healthy: source still matches the architecture model.",
              "scoreBand": "Healthy",
              "overallScore": 92,
              "architectureModel": { "elements": [
                {"elementId":"frontend-shell","score":95,"severity":"Info","sourceCoverage":0.9,"status":"New","summary":"Frontend shell on plan.","evidenceRefs":["frontend/src/app/"],"followUpTaskSuggestions":[]},
                {"elementId":"backend-api","score":90,"severity":"Info","sourceCoverage":0.85,"status":"New","summary":"Backend API on plan.","evidenceRefs":["backend/Endpoints/TaskEndpoints.cs"],"followUpTaskSuggestions":[]}
              ]}
            }
            ```
            """;
        var parse = svc.TryParseAgentResponse(raw);
        Assert.Equal(SoftwareArchitectureDriftParseStatus.Structured, parse.Status);

        var report = svc.BuildReport(
            scope: scope,
            parse: parse,
            reportId: "01HX0000000000000000000SAR4",
            createdAt: DateTime.UtcNow);

        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
        Assert.Equal(DriftScoreBand.Healthy, report.ScoreBand);
        Assert.Equal(92, report.OverallScore);
        Assert.Single(report.Dimensions);
        Assert.Equal(DriftDimensionType.Architecture, report.Dimensions[0].Type);
        Assert.Equal(DriftSeverity.Info, report.Dimensions[0].Severity);
        Assert.NotNull(report.ArchitectureModel);
        Assert.All(report.ArchitectureModel!.Elements, e => Assert.True(e.Score >= 80));
        Assert.Empty(report.FollowUpTaskSuggestions);
    }

    // ------------------------------------------------------------------
    // Prompt rendering keeps every load-bearing placeholder
    // ------------------------------------------------------------------

    [Fact]
    public void BuildPrompt_RendersArchitectureModelSectionAndAllLoadBearingPlaceholders()
    {
        WriteArchitectureModel(buildElevenElements: false);
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs"));
        WriteRepoFile("docs/system/architecture/decisions/adr-archive.md", "# ADR\n");
        Directory.CreateDirectory(Path.Combine(_repoRoot, "backend", "Services", "Drift"));
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs", "app", "schemas"));
        File.WriteAllText(
            Path.Combine(_repoRoot, "docs", "app", "schemas", "drift-report.schema.json"), "{}", Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(_repoRoot, "backend.Tests"));
        Directory.CreateDirectory(Path.Combine(_repoRoot, "frontend", "e2e"));
        WriteJob("6-completed", "shipped-task", "Shipped");

        var svc = new SoftwareArchitectureDriftAnalysisService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            repoRoot: _repoRoot,
            watchedProjectRoot: _watchedProjectRoot,
            now: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));

        const string template = """
            Project: {{project}}
            Captured: {{captured_at}}

            ## Architecture
            {{architecture_model}}

            ## Docs
            {{doc_list}}

            ## Source tree
            {{source_tree}}

            ## Modules
            {{module_boundaries}}

            ## Schemas
            {{schema_list}}

            ## Tests
            {{test_dirs}}

            ## Tasks
            {{recent_tasks}}

            ## Drift
            {{recent_drift_reports}}

            ## Analysis
            {{recent_analysis_reports}}

            (do not modify any source file)
            """;
        var rendered = svc.BuildPrompt(scope, template);

        Assert.Contains("agent-taskboard", rendered);
        Assert.Contains("2026-05-05T12:00:00Z", rendered);
        Assert.Contains("agent-taskboard-test", rendered);
        Assert.Contains("frontend-shell", rendered);
        Assert.Contains("backend-api", rendered);
        Assert.Contains("docs/system/architecture/decisions/adr-archive.md", rendered);
        Assert.Contains("backend/Services/Drift/", rendered);
        Assert.Contains("docs/app/schemas/drift-report.schema.json", rendered);
        Assert.Contains("backend.Tests/", rendered);
        Assert.Contains("6-completed/shipped-task", rendered);
        Assert.Contains("do not modify any source file", rendered);
        Assert.DoesNotContain("{{", rendered);
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private void WriteRepoFile(string relPath, string content)
    {
        var full = Path.Combine(_repoRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.UTF8);
    }

    private void WriteArchitectureModel(bool buildElevenElements)
    {
        var dir = Path.Combine(_watchedProjectRoot, "architecture");
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("modelId: agent-taskboard-test");
        sb.AppendLine("title: Agent Taskboard - Test Model");
        sb.AppendLine("project: agent-taskboard");
        sb.AppendLine("updatedAt: 2026-05-05T12:00:00Z");
        sb.AppendLine("schemaVersion: 1");
        sb.AppendLine("elements:");
        if (buildElevenElements)
        {
            for (int i = 1; i <= 11; i++)
            {
                sb.AppendLine($"  - elementId: el-{i}");
                sb.AppendLine($"    label: Element {i}");
                sb.AppendLine($"    expectedRole: Owns part {i} of the system.");
                sb.AppendLine($"    ownershipBoundary:");
                sb.AppendLine($"      - src/part-{i}/**");
            }
        }
        else
        {
            sb.AppendLine("  - elementId: frontend-shell");
            sb.AppendLine("    label: Frontend App Shell");
            sb.AppendLine("    expectedRole: Hosts the Angular PWA, routing, and the kanban + project surfaces.");
            sb.AppendLine("    ownershipBoundary:");
            sb.AppendLine("      - frontend/src/app/**");
            sb.AppendLine("    guidelines:");
            sb.AppendLine("      - Standalone components only");
            sb.AppendLine("      - Signals for state");
            sb.AppendLine("    allowedDependencies:");
            sb.AppendLine("      - backend-api");
            sb.AppendLine("    sourceRefs:");
            sb.AppendLine("      - frontend/AGENTS.md");
            sb.AppendLine("  - elementId: backend-api");
            sb.AppendLine("    label: Backend API");
            sb.AppendLine("    expectedRole: ASP.NET Core API + SignalR hub. Owns REST endpoints and live push.");
            sb.AppendLine("    ownershipBoundary:");
            sb.AppendLine("      - backend/Endpoints/**");
            sb.AppendLine("      - backend/Program.cs");
            sb.AppendLine("    allowedDependencies:");
            sb.AppendLine("      - task-access");
            sb.AppendLine("      - runner");
            sb.AppendLine("    sourceRefs:");
            sb.AppendLine("      - backend/AGENTS.md");
        }
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("# Test architecture model");
        File.WriteAllText(Path.Combine(dir, "agent-taskboard-test.md"), sb.ToString(), Encoding.UTF8);
    }

    private void WriteJob(string lane, string jobId, string title)
    {
        var dir = Path.Combine(_projectRoot, lane, jobId);
        Directory.CreateDirectory(dir);
        var json =
            "{\n" +
            $"  \"id\": \"{jobId}\",\n" +
            $"  \"title\": \"{title}\",\n" +
            $"  \"state\": \"{lane}\",\n" +
            "  \"agent\": \"claude\",\n" +
            "  \"cliType\": \"claude\"\n" +
            "}\n";
        File.WriteAllText(Path.Combine(dir, "task.json"), json, Encoding.UTF8);
    }
}
