using System.Text;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the contract of <see cref="AdrCodeDriftAnalysisService"/>: scope
/// selection picks up ADRs, architecture notes, the source-tree top level,
/// per-module boundaries under <c>backend/Services/</c>, the schema set, and
/// recent task evidence; the rendered prompt carries every load-bearing
/// section the agent needs; the JSON parse fallback distinguishes
/// Structured / Unstructured / MalformedJson without ever hiding the
/// Markdown body; and a "no findings" verdict still produces a schema-valid
/// drift report.
/// </summary>
public class AdrCodeDriftAnalysisServiceTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly string _repoRoot;

    public AdrCodeDriftAnalysisServiceTests()
    {
        var stem = "adr-code-drift-tests-" + Guid.NewGuid().ToString("N");
        _projectRoot = Path.Combine(Path.GetTempPath(), stem, "project");
        _repoRoot = Path.Combine(Path.GetTempPath(), stem, "repo");
        Directory.CreateDirectory(_projectRoot);
        Directory.CreateDirectory(_repoRoot);
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
    // SelectScope: ADR / source / module / schema / recent-task selection
    // ------------------------------------------------------------------

    [Fact]
    public void SelectScope_PicksUpAdrArchiveAndArchitectureNotesWhenPresentAndSkipsMissingFiles()
    {
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs"));
        File.WriteAllText(Path.Combine(_repoRoot, "docs", "architecture-decisions.md"), "# ADRs\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_repoRoot, "docs", "design-principles.md"), "# DP\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_repoRoot, "ROADMAP.md"), "# ROADMAP\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_repoRoot, "AGENTS.md"), "# AGENTS\n", Encoding.UTF8);
        // README intentionally absent so we prove missing files are skipped.

        var svc = new AdrCodeDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        var paths = scope.Docs.Select(d => d.Path.Replace('\\', '/')).ToArray();
        Assert.Contains("docs/architecture-decisions.md", paths);
        Assert.Contains("docs/design-principles.md", paths);
        Assert.Contains("ROADMAP.md", paths);
        Assert.Contains("AGENTS.md", paths);
        Assert.DoesNotContain(paths, p => p.EndsWith("README.md", StringComparison.Ordinal));
        // ADR file leads the doc list so the agent reads decisions first.
        Assert.Equal("docs/architecture-decisions.md", scope.Docs[0].Path.Replace('\\', '/'));
    }

    [Fact]
    public void SelectScope_ListsModuleBoundariesUnderBackendServicesAndSkipsBuildFolders()
    {
        var services = Path.Combine(_repoRoot, "backend", "Services");
        Directory.CreateDirectory(Path.Combine(services, "Analysis"));
        Directory.CreateDirectory(Path.Combine(services, "Drift"));
        Directory.CreateDirectory(Path.Combine(services, "Runner"));
        // Top-level build folders that must NOT be listed in the source tree.
        Directory.CreateDirectory(Path.Combine(_repoRoot, "node_modules"));
        Directory.CreateDirectory(Path.Combine(_repoRoot, "bin"));
        Directory.CreateDirectory(Path.Combine(_repoRoot, "obj"));
        Directory.CreateDirectory(Path.Combine(_repoRoot, "frontend"));

        var svc = new AdrCodeDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        var modulePaths = scope.ModuleBoundaries.Select(m => m.Path.Replace('\\', '/')).ToArray();
        Assert.Contains("backend/Services/Analysis/", modulePaths);
        Assert.Contains("backend/Services/Drift/", modulePaths);
        Assert.Contains("backend/Services/Runner/", modulePaths);

        var topLevel = scope.SourceTree.Select(t => t.Path).ToArray();
        Assert.Contains("backend/", topLevel);
        Assert.Contains("frontend/", topLevel);
        // Build folders are excluded from the top-level listing.
        Assert.DoesNotContain("node_modules/", topLevel);
        Assert.DoesNotContain("bin/", topLevel);
        Assert.DoesNotContain("obj/", topLevel);
    }

    [Fact]
    public void SelectScope_ListsSchemasFromDocsSchemasInSortedOrder()
    {
        var schemas = Path.Combine(_repoRoot, "docs", "schemas");
        Directory.CreateDirectory(schemas);
        File.WriteAllText(Path.Combine(schemas, "drift-report.schema.json"), "{}", Encoding.UTF8);
        File.WriteAllText(Path.Combine(schemas, "analysis-report.schema.json"), "{}", Encoding.UTF8);
        File.WriteAllText(Path.Combine(schemas, "agent-message.schema.json"), "{}", Encoding.UTF8);
        // A non-JSON file in the same folder is ignored.
        File.WriteAllText(Path.Combine(schemas, "README.md"), "schemas\n", Encoding.UTF8);

        var svc = new AdrCodeDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        var paths = scope.Schemas.Select(s => s.Path.Replace('\\', '/')).ToArray();
        Assert.Contains("docs/schemas/drift-report.schema.json", paths);
        Assert.Contains("docs/schemas/analysis-report.schema.json", paths);
        Assert.Contains("docs/schemas/agent-message.schema.json", paths);
        Assert.DoesNotContain(paths, p => p.EndsWith("README.md", StringComparison.Ordinal));
        // Sorted ordinally so prompts diff cleanly across runs.
        Assert.Equal(paths.OrderBy(p => p, StringComparer.Ordinal).ToArray(), paths);
    }

    [Fact]
    public void SelectScope_RecentTaskEvidenceIsLimitedToReviewedAndCompletedLanesAndIgnoresInFlightLanes()
    {
        WriteJob("3-progress", "in-flight-task", "In progress");
        WriteJob("4-auto-review", "auto-review-task", "Auto review");
        WriteJob("5-human-review", "human-review-task", "Human review");
        WriteJob("6-completed", "shipped-task-a", "Shipped A");
        WriteJob("6-completed", "shipped-task-b", "Shipped B");

        var svc = new AdrCodeDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        var ids = scope.RecentTasks.Select(t => t.JobId).ToArray();
        Assert.Contains("human-review-task", ids);
        Assert.Contains("shipped-task-a", ids);
        Assert.Contains("shipped-task-b", ids);
        // In-flight or auto-review work is not "recent task evidence" yet.
        Assert.DoesNotContain("in-flight-task", ids);
        Assert.DoesNotContain("auto-review-task", ids);
    }

    // ------------------------------------------------------------------
    // BuildPrompt: load-bearing placeholders are rendered
    // ------------------------------------------------------------------

    [Fact]
    public void BuildPrompt_RendersAllLoadBearingPlaceholdersWithoutLeavingUnrenderedBraces()
    {
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs"));
        File.WriteAllText(Path.Combine(_repoRoot, "docs", "architecture-decisions.md"), "# ADR\n", Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(_repoRoot, "backend", "Services", "Drift"));
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs", "schemas"));
        File.WriteAllText(
            Path.Combine(_repoRoot, "docs", "schemas", "drift-report.schema.json"), "{}", Encoding.UTF8);
        WriteJob("6-completed", "shipped-task", "Shipped");

        var svc = new AdrCodeDriftAnalysisService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            repoRoot: _repoRoot,
            now: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));

        const string template = """
            Project: {{project}}
            Captured: {{captured_at}}

            ## Docs
            {{doc_list}}

            ## Source tree
            {{source_tree}}

            ## Modules
            {{module_boundaries}}

            ## Schemas
            {{schema_list}}

            ## Tasks
            {{recent_tasks}}

            ## Drift reports
            {{recent_drift_reports}}

            ## Analysis reports
            {{recent_analysis_reports}}

            (do not modify any source file)
            """;
        var rendered = svc.BuildPrompt(scope, template);

        Assert.Contains("agent-taskboard", rendered);
        Assert.Contains("2026-05-05T12:00:00Z", rendered);
        Assert.Contains("docs/architecture-decisions.md", rendered);
        Assert.Contains("backend/Services/Drift/", rendered);
        Assert.Contains("docs/schemas/drift-report.schema.json", rendered);
        Assert.Contains("6-completed/shipped-task", rendered);
        // Hard-constraint wording from the template is preserved verbatim.
        Assert.Contains("do not modify any source file", rendered);
        // No unrendered placeholders remain.
        Assert.DoesNotContain("{{", rendered);
    }

    // ------------------------------------------------------------------
    // TryParseAgentResponse: Structured / Unstructured / MalformedJson
    // ------------------------------------------------------------------

    [Fact]
    public void TryParseAgentResponse_StructuredReplyExposesScoreBandDimensionsAndFollowUps()
    {
        const string raw = """
            # ADR / Code Drift

            Watch: two architecture mismatches need ADR updates.

            ```json
            {
              "verdict": "Watch: two architecture mismatches need ADR updates.",
              "scoreBand": "Watch",
              "overallScore": 70,
              "dimensions": [
                {
                  "type": "Architecture",
                  "score": 65,
                  "severity": "Warn",
                  "confidence": 0.8,
                  "sourceCoverage": 0.6,
                  "status": "New",
                  "summary": "Drift module landed without ADR.",
                  "evidenceRefs": ["backend/Services/Drift/", "docs/architecture-decisions.md"],
                  "recommendedActions": ["Add an ADR entry covering the Drift module"]
                },
                {
                  "type": "Schema",
                  "score": 80,
                  "severity": "Info",
                  "confidence": 0.7,
                  "sourceCoverage": 0.9,
                  "status": "New",
                  "summary": "Schema additions match producer code.",
                  "evidenceRefs": ["docs/schemas/drift-report.schema.json"],
                  "recommendedActions": []
                }
              ],
              "followUpTaskSuggestions": [
                {
                  "title": "Add ADR entry for Drift module",
                  "summary": "Drift module landed in backend/Services/Drift/ without an ADR; add a decision entry referencing the schema and the analysis vs drift split.",
                  "priority": "Normal",
                  "relatedDimension": "Architecture"
                }
              ]
            }
            ```
            """;

        var svc = new AdrCodeDriftAnalysisService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(AdrCodeDriftParseStatus.Structured, parse.Status);
        Assert.Equal(DriftScoreBand.Watch, parse.ScoreBand);
        Assert.Equal(70, parse.OverallScore);
        Assert.Null(parse.ParseError);
        Assert.NotNull(parse.Dimensions);
        Assert.Equal(2, parse.Dimensions!.Count);

        var arch = parse.Dimensions!.Single(d => d.Type == DriftDimensionType.Architecture);
        Assert.Equal(65, arch.Score);
        Assert.Equal(DriftSeverity.Warn, arch.Severity);
        Assert.Equal(DriftFindingStatus.New, arch.Status);
        Assert.Equal(0.8, arch.Confidence);
        Assert.Equal(0.6, arch.SourceCoverage);
        Assert.Contains("backend/Services/Drift/", arch.EvidenceRefs);
        Assert.Contains("docs/architecture-decisions.md", arch.EvidenceRefs);
        Assert.Single(arch.RecommendedActions);

        var schema = parse.Dimensions!.Single(d => d.Type == DriftDimensionType.Schema);
        Assert.Equal(80, schema.Score);
        Assert.Empty(schema.RecommendedActions);

        Assert.Single(parse.FollowUps!);
        Assert.Equal(DriftFollowUpPriority.Normal, parse.FollowUps![0].Priority);
        Assert.Equal(DriftDimensionType.Architecture, parse.FollowUps![0].RelatedDimension);
    }

    [Fact]
    public void TryParseAgentResponse_NoFencedJsonBlockProducesUnstructuredFallbackKeepingMarkdown()
    {
        const string raw = """
            # ADR / Code Drift

            Verdict: drifting.

            The Drift module exists without an ADR entry but I forgot the JSON sidecar.
            """;

        var svc = new AdrCodeDriftAnalysisService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(AdrCodeDriftParseStatus.Unstructured, parse.Status);
        Assert.Null(parse.ParseError);
        // Summary falls back to the first heading so the list view still has a useful one-liner.
        Assert.Equal("ADR / Code Drift", parse.Summary);
        Assert.Equal(DriftScoreBand.Unknown, parse.ScoreBand);
        Assert.Null(parse.Dimensions);
        Assert.Null(parse.FollowUps);
    }

    [Fact]
    public void TryParseAgentResponse_MalformedJsonSidecarSurfacesParseErrorWithoutHidingMarkdown()
    {
        const string raw = """
            # ADR / Code Drift

            Verdict: drifting.

            ```json
            { "verdict": "drifting", "scoreBand": "Watch", "overallScore": 70,
            ```
            """;

        var svc = new AdrCodeDriftAnalysisService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(AdrCodeDriftParseStatus.MalformedJson, parse.Status);
        Assert.False(string.IsNullOrWhiteSpace(parse.ParseError));
        Assert.Contains("JSON sidecar failed", parse.ParseError, StringComparison.OrdinalIgnoreCase);
        // The Markdown summary is still derived; the body itself stays visible at the call site.
        Assert.Equal("ADR / Code Drift", parse.Summary);
        Assert.Equal(DriftScoreBand.Unknown, parse.ScoreBand);
    }

    [Fact]
    public void TryParseAgentResponse_JsonWithBadScoreBandSurfacesAsMalformedJsonNotStructured()
    {
        const string raw = """
            # ADR / Code Drift

            ```json
            { "verdict": "drifting", "scoreBand": "Apocalyptic", "overallScore": 50 }
            ```
            """;

        var svc = new AdrCodeDriftAnalysisService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(AdrCodeDriftParseStatus.MalformedJson, parse.Status);
        Assert.Contains("scoreBand", parse.ParseError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseAgentResponse_JsonWithOutOfRangeOverallScoreSurfacesAsMalformedJson()
    {
        const string raw = """
            ```json
            { "verdict": "drifting", "scoreBand": "Watch", "overallScore": 150 }
            ```
            """;

        var svc = new AdrCodeDriftAnalysisService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(AdrCodeDriftParseStatus.MalformedJson, parse.Status);
        Assert.Contains("overallScore", parse.ParseError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseAgentResponse_EmptyInputProducesUnstructuredWithoutCrashing()
    {
        var svc = new AdrCodeDriftAnalysisService();
        var parse = svc.TryParseAgentResponse(null);
        Assert.Equal(AdrCodeDriftParseStatus.Unstructured, parse.Status);

        parse = svc.TryParseAgentResponse("");
        Assert.Equal(AdrCodeDriftParseStatus.Unstructured, parse.Status);
    }

    // ------------------------------------------------------------------
    // BuildReport: validation, evidence references, no-finding output
    // ------------------------------------------------------------------

    [Fact]
    public void BuildReport_StructuredHealthyVerdictWithoutFindings_StillProducesSchemaValidReportWithSyntheticDimension()
    {
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs"));
        File.WriteAllText(Path.Combine(_repoRoot, "docs", "architecture-decisions.md"), "# ADR\n", Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(_repoRoot, "backend", "Services", "Drift"));

        var svc = new AdrCodeDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);
        // Agent reports a healthy verdict and omits the dimensions array entirely.
        const string raw = """
            # ADR / Code Drift

            ```json
            {
              "verdict": "Healthy: ADRs match the source tree.",
              "scoreBand": "Healthy",
              "overallScore": 92
            }
            ```
            """;
        var parse = svc.TryParseAgentResponse(raw);
        Assert.Equal(AdrCodeDriftParseStatus.Structured, parse.Status);

        var report = svc.BuildReport(
            scope: scope,
            parse: parse,
            reportId: "01HX0000000000000000000DR1",
            createdAt: new DateTime(2026, 5, 5, 13, 0, 0, DateTimeKind.Utc));

        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
        Assert.Equal(DriftScoreBand.Healthy, report.ScoreBand);
        Assert.Equal(92, report.OverallScore);
        // Synthetic dimension keeps the record schema-valid (dimensions: minItems=1).
        Assert.Single(report.Dimensions);
        Assert.Equal(DriftDimensionType.Architecture, report.Dimensions[0].Type);
        Assert.Equal(DriftSeverity.Info, report.Dimensions[0].Severity);
        Assert.Equal(DriftFindingStatus.New, report.Dimensions[0].Status);
        Assert.NotEmpty(report.Dimensions[0].EvidenceRefs);
        Assert.Empty(report.FollowUpTaskSuggestions);
    }

    [Fact]
    public void BuildReport_StructuredVerdictWithFindings_PassesValidationAndCarriesEvidenceRefsOnEachDimension()
    {
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs"));
        File.WriteAllText(Path.Combine(_repoRoot, "docs", "architecture-decisions.md"), "# ADR\n", Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(_repoRoot, "backend", "Services", "Drift"));

        var svc = new AdrCodeDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);
        const string raw = """
            ```json
            {
              "verdict": "Watch: Drift module without ADR.",
              "scoreBand": "Watch",
              "overallScore": 70,
              "dimensions": [
                {
                  "type": "Architecture",
                  "score": 65,
                  "severity": "Warn",
                  "confidence": 0.8,
                  "sourceCoverage": 0.6,
                  "status": "New",
                  "summary": "Drift module landed without ADR.",
                  "evidenceRefs": ["backend/Services/Drift/", "docs/architecture-decisions.md"],
                  "recommendedActions": ["Add an ADR entry covering the Drift module"]
                }
              ]
            }
            ```
            """;
        var parse = svc.TryParseAgentResponse(raw);

        var report = svc.BuildReport(
            scope: scope,
            parse: parse,
            reportId: "01HX0000000000000000000DR2",
            createdAt: new DateTime(2026, 5, 5, 13, 30, 0, DateTimeKind.Utc));

        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
        Assert.Equal("agent-taskboard", report.Project);
        Assert.Equal(DriftReportTrigger.Manual, report.Trigger);
        Assert.Equal(DriftReportScopeKind.Project, report.Scope.Kind);
        Assert.Equal(DriftScoreBand.Watch, report.ScoreBand);
        // SourceRefs on the report scope cite the assembled evidence by stable id;
        // the doc list lands first so consumers can drill into ADRs immediately.
        Assert.NotNull(report.Scope.SourceRefs);
        Assert.Contains(
            report.Scope.SourceRefs!,
            r => r.Replace('\\', '/') == "docs/architecture-decisions.md");
        Assert.Contains(
            report.Scope.SourceRefs!,
            r => r.Replace('\\', '/') == "backend/Services/Drift/");
        // Per-dimension evidence refs survive the round-trip.
        Assert.Single(report.Dimensions);
        Assert.Contains("backend/Services/Drift/", report.Dimensions[0].EvidenceRefs);
        Assert.Contains("docs/architecture-decisions.md", report.Dimensions[0].EvidenceRefs);
    }

    [Fact]
    public void BuildReport_UnstructuredParse_EmitsEvidenceOnlyDimensionWithUnknownBandAndStillValidates()
    {
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs"));
        File.WriteAllText(Path.Combine(_repoRoot, "docs", "architecture-decisions.md"), "# ADR\n", Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(_repoRoot, "backend", "Services", "Drift"));

        var svc = new AdrCodeDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);
        var parse = svc.TryParseAgentResponse(null);

        var report = svc.BuildReport(
            scope: scope,
            parse: parse,
            reportId: "01HX0000000000000000000DR3",
            createdAt: DateTime.UtcNow);

        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
        Assert.Equal(DriftScoreBand.Unknown, report.ScoreBand);
        Assert.Equal(0, report.OverallScore);
        Assert.Single(report.Dimensions);
        Assert.Equal(DriftDimensionType.Architecture, report.Dimensions[0].Type);
        Assert.Contains("evidence-only", report.Dimensions[0].Summary, StringComparison.OrdinalIgnoreCase);
        // The synthesised dimension still cites concrete evidence so the user
        // can drill into the same paths the agent would have inspected.
        Assert.NotEmpty(report.Dimensions[0].EvidenceRefs);
    }

    [Fact]
    public void BuildReport_MalformedJsonParse_EmitsUnknownBandAndCarriesParseErrorInDimensionSummary()
    {
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs"));
        File.WriteAllText(Path.Combine(_repoRoot, "docs", "architecture-decisions.md"), "# ADR\n", Encoding.UTF8);

        var svc = new AdrCodeDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);
        var parse = svc.TryParseAgentResponse(
            "# Drift\n\n```json\n{ \"verdict\": \"x\", \"scoreBand\": \"Watch\",\n```\n");
        Assert.Equal(AdrCodeDriftParseStatus.MalformedJson, parse.Status);

        var report = svc.BuildReport(
            scope: scope,
            parse: parse,
            reportId: "01HX0000000000000000000DR4",
            createdAt: DateTime.UtcNow);

        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
        Assert.Equal(DriftScoreBand.Unknown, report.ScoreBand);
        Assert.Single(report.Dimensions);
        Assert.Contains("failed to parse", report.Dimensions[0].Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

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
