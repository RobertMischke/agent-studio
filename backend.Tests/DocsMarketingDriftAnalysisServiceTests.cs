using System.Text;
using OrchestratorApi.Services.Drift;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the contract of <see cref="DocsMarketingDriftAnalysisService"/>:
/// scope selection picks up canonical project docs, mockup folders, the
/// current queue, recent completed evidence, and the optional marketing
/// repository (treating "not configured" / "missing on disk" / "available"
/// as three distinct states); the rendered prompt carries every load-bearing
/// section and explicitly surfaces the absence of the marketing repo; the
/// JSON parse fallback distinguishes Structured / Unstructured /
/// MalformedJson without ever hiding the Markdown body; follow-up
/// suggestions round-trip through the parser; the resulting drift report's
/// evidence references cite the assembled scope; and a "no findings"
/// verdict still produces a schema-valid drift report.
/// </summary>
public class DocsMarketingDriftAnalysisServiceTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly string _repoRoot;
    private readonly string _stemRoot;

    public DocsMarketingDriftAnalysisServiceTests()
    {
        var stem = "docs-marketing-drift-tests-" + Guid.NewGuid().ToString("N");
        _stemRoot = Path.Combine(Path.GetTempPath(), stem);
        _projectRoot = Path.Combine(_stemRoot, "project");
        _repoRoot = Path.Combine(_stemRoot, "repo");
        Directory.CreateDirectory(_projectRoot);
        Directory.CreateDirectory(_repoRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_stemRoot)) Directory.Delete(_stemRoot, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // ------------------------------------------------------------------
    // SelectScope: canonical doc / mockup / queue / completed / marketing
    // ------------------------------------------------------------------

    [Fact]
    public void SelectScope_PicksUpCanonicalProjectDocsAndMockupFolders()
    {
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs"));
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "# README\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_repoRoot, "ROADMAP.md"), "# ROADMAP\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_repoRoot, "AGENTS.md"), "# AGENTS\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_repoRoot, "docs", "architecture-decisions.md"), "# ADR\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_repoRoot, "docs", "design-principles.md"), "# DP\n", Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs", "mockups", "quality-system"));
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs", "mockups", "next-gen-chat"));

        var svc = new DocsMarketingDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        var docPaths = scope.CanonicalDocs.Select(d => d.Path.Replace('\\', '/')).ToArray();
        Assert.Contains("README.md", docPaths);
        Assert.Contains("ROADMAP.md", docPaths);
        Assert.Contains("AGENTS.md", docPaths);
        Assert.Contains("docs/architecture-decisions.md", docPaths);
        Assert.Contains("docs/design-principles.md", docPaths);

        var mockupPaths = scope.MockupDocs.Select(d => d.Path.Replace('\\', '/')).ToArray();
        Assert.Contains("docs/mockups/quality-system/", mockupPaths);
        Assert.Contains("docs/mockups/next-gen-chat/", mockupPaths);
    }

    [Fact]
    public void SelectScope_QueueJobsCoverActiveLanesAndRecentCompletedComesFromCompletedLane()
    {
        WriteJob("1-preparation", "prep-task", "Prep task");
        WriteJob("2-ready", "ready-task", "Ready task");
        WriteJob("3-progress", "in-flight-task", "In progress");
        WriteJob("4-auto-review", "auto-review-task", "Auto review");
        WriteJob("5-human-review", "human-review-task", "Human review");
        WriteJob("6-completed", "shipped-task-a", "Shipped A");
        WriteJob("6-completed", "shipped-task-b", "Shipped B");
        // 7-archive must NOT show up as either queue or recent completed.
        WriteJob("7-archive", "archived-task", "Archived");

        var svc = new DocsMarketingDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        var queueIds = scope.QueueJobs.Select(t => t.JobId).ToArray();
        Assert.Contains("prep-task", queueIds);
        Assert.Contains("ready-task", queueIds);
        Assert.Contains("in-flight-task", queueIds);
        Assert.Contains("auto-review-task", queueIds);
        Assert.Contains("human-review-task", queueIds);
        Assert.DoesNotContain("shipped-task-a", queueIds);
        Assert.DoesNotContain("archived-task", queueIds);

        var completedIds = scope.RecentCompleted.Select(t => t.JobId).ToArray();
        Assert.Contains("shipped-task-a", completedIds);
        Assert.Contains("shipped-task-b", completedIds);
        Assert.DoesNotContain("archived-task", completedIds);
    }

    [Fact]
    public void SelectScope_MissingMarketingRepoPathProducesNotConfiguredScope()
    {
        var svc = new DocsMarketingDriftAnalysisService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            repoRoot: _repoRoot,
            marketingRepoRoot: null);

        Assert.False(scope.Marketing.Configured);
        Assert.False(scope.Marketing.Exists);
        Assert.Null(scope.Marketing.Root);
        Assert.Empty(scope.Marketing.Docs);
        Assert.False(string.IsNullOrWhiteSpace(scope.Marketing.Note));
    }

    [Fact]
    public void SelectScope_ConfiguredButMissingMarketingPathDistinguishesFromNotConfigured()
    {
        var svc = new DocsMarketingDriftAnalysisService();
        var pretendPath = Path.Combine(_stemRoot, "missing-marketing");

        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            repoRoot: _repoRoot,
            marketingRepoRoot: pretendPath);

        Assert.True(scope.Marketing.Configured);
        Assert.False(scope.Marketing.Exists);
        Assert.Equal(pretendPath, scope.Marketing.Root);
        Assert.Empty(scope.Marketing.Docs);
        Assert.Contains("missing", scope.Marketing.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectScope_AvailableMarketingRepoListsMarkdownFilesRelativeToRoot()
    {
        var marketingRoot = Path.Combine(_stemRoot, "marketing");
        Directory.CreateDirectory(Path.Combine(marketingRoot, "05-marketing-strategie"));
        Directory.CreateDirectory(Path.Combine(marketingRoot, "06-website-planung"));
        File.WriteAllText(Path.Combine(marketingRoot, "README.md"), "# Marketing\n", Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(marketingRoot, "05-marketing-strategie", "stars.md"),
            "# Stars\n", Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(marketingRoot, "06-website-planung", "website.md"),
            "# Website\n", Encoding.UTF8);
        // A non-Markdown file is ignored.
        File.WriteAllText(Path.Combine(marketingRoot, "ignore.txt"), "ignored\n", Encoding.UTF8);

        var svc = new DocsMarketingDriftAnalysisService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            repoRoot: _repoRoot,
            marketingRepoRoot: marketingRoot);

        Assert.True(scope.Marketing.Configured);
        Assert.True(scope.Marketing.Exists);
        Assert.Equal(marketingRoot, scope.Marketing.Root);
        var paths = scope.Marketing.Docs.Select(d => d.Path).ToArray();
        Assert.Contains("README.md", paths);
        Assert.Contains("05-marketing-strategie/stars.md", paths);
        Assert.Contains("06-website-planung/website.md", paths);
        Assert.DoesNotContain(paths, p => p.EndsWith("ignore.txt", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // BuildPrompt: load-bearing placeholders are rendered and
    // missing-marketing state surfaces explicitly
    // ------------------------------------------------------------------

    [Fact]
    public void BuildPrompt_RendersAllLoadBearingPlaceholdersWithoutLeavingUnrenderedBraces()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "# README\n", Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs", "mockups", "quality-system"));
        WriteJob("2-ready", "ready-task", "Ready task");
        WriteJob("6-completed", "shipped-task", "Shipped");

        var svc = new DocsMarketingDriftAnalysisService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            repoRoot: _repoRoot,
            marketingRepoRoot: null,
            now: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));

        const string template = """
            Project: {{project}}
            Captured: {{captured_at}}

            ## Canonical
            {{canonical_docs}}

            ## Mockups
            {{mockup_docs}}

            ## Queue
            {{queue_jobs}}

            ## Completed
            {{recent_completed}}

            ## Marketing status
            {{marketing_status}}

            ## Marketing docs
            {{marketing_docs}}

            ## Drift reports
            {{recent_drift_reports}}

            ## Analysis reports
            {{recent_analysis_reports}}

            (do not modify any source file)
            """;
        var rendered = svc.BuildPrompt(scope, template);

        Assert.Contains("agent-taskboard", rendered);
        Assert.Contains("2026-05-05T12:00:00Z", rendered);
        Assert.Contains("README.md", rendered);
        Assert.Contains("docs/mockups/quality-system/", rendered);
        Assert.Contains("2-ready/ready-task", rendered);
        Assert.Contains("6-completed/shipped-task", rendered);
        Assert.Contains("not configured", rendered);
        Assert.Contains("do not modify any source file", rendered);
        Assert.DoesNotContain("{{", rendered);
    }

    [Fact]
    public void BuildPrompt_MissingMarketingRepoIsCalledOutSoTheAgentDoesNotInventClaims()
    {
        var svc = new DocsMarketingDriftAnalysisService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            repoRoot: _repoRoot,
            marketingRepoRoot: null);

        const string template = "{{marketing_status}}\n\n{{marketing_docs}}";
        var rendered = svc.BuildPrompt(scope, template);

        Assert.Contains("not configured", rendered, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // TryParseAgentResponse: Structured / Unstructured / MalformedJson
    // ------------------------------------------------------------------

    [Fact]
    public void TryParseAgentResponse_StructuredReplyExposesScoreBandDimensionsAndFollowUps()
    {
        const string raw = """
            # Docs / Marketing Drift

            Watch: README claims a feature that has not shipped yet.

            ```json
            {
              "verdict": "Watch: README claims a feature that has not shipped yet.",
              "scoreBand": "Watch",
              "overallScore": 70,
              "dimensions": [
                {
                  "type": "Documentation",
                  "score": 65,
                  "severity": "Warn",
                  "confidence": 0.8,
                  "sourceCoverage": 0.6,
                  "status": "New",
                  "summary": "README mentions feature X but no queued or completed work backs it.",
                  "evidenceRefs": ["README.md", "agent-taskboard/2-ready/feature-x"],
                  "recommendedActions": ["Queue a docs-sync task to remove or qualify the claim"]
                },
                {
                  "type": "Marketing",
                  "score": 80,
                  "severity": "Info",
                  "confidence": 0.7,
                  "sourceCoverage": 0.5,
                  "status": "New",
                  "summary": "Marketing docs match current product behavior.",
                  "evidenceRefs": ["marketing:06-website-planung/website-strategie-und-anforderungen.md"],
                  "recommendedActions": []
                }
              ],
              "followUpTaskSuggestions": [
                {
                  "title": "Sync README with shipped capabilities",
                  "summary": "README mentions feature X without queue evidence; either queue the work or qualify the claim as a roadmap intent.",
                  "priority": "Normal",
                  "relatedDimension": "Documentation"
                },
                {
                  "title": "Update website strategy with the meta-cycle layer",
                  "summary": "Website plan does not yet describe the meta-cycle layer that recently shipped.",
                  "priority": "Low",
                  "relatedDimension": "Marketing"
                }
              ]
            }
            ```
            """;

        var svc = new DocsMarketingDriftAnalysisService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(DocsMarketingDriftParseStatus.Structured, parse.Status);
        Assert.Equal(DriftScoreBand.Watch, parse.ScoreBand);
        Assert.Equal(70, parse.OverallScore);
        Assert.Null(parse.ParseError);
        Assert.NotNull(parse.Dimensions);
        Assert.Equal(2, parse.Dimensions!.Count);

        var doc = parse.Dimensions!.Single(d => d.Type == DriftDimensionType.Documentation);
        Assert.Equal(65, doc.Score);
        Assert.Equal(DriftSeverity.Warn, doc.Severity);
        Assert.Contains("README.md", doc.EvidenceRefs);
        Assert.Single(doc.RecommendedActions);

        var marketing = parse.Dimensions!.Single(d => d.Type == DriftDimensionType.Marketing);
        Assert.Equal(80, marketing.Score);

        Assert.NotNull(parse.FollowUps);
        Assert.Equal(2, parse.FollowUps!.Count);
        var docFollowUp = parse.FollowUps.Single(f => f.RelatedDimension == DriftDimensionType.Documentation);
        Assert.Equal(DriftFollowUpPriority.Normal, docFollowUp.Priority);
        Assert.Equal("Sync README with shipped capabilities", docFollowUp.Title);
        var marketingFollowUp = parse.FollowUps.Single(f => f.RelatedDimension == DriftDimensionType.Marketing);
        Assert.Equal(DriftFollowUpPriority.Low, marketingFollowUp.Priority);
    }

    [Fact]
    public void TryParseAgentResponse_NoFencedJsonBlockProducesUnstructuredFallbackKeepingMarkdown()
    {
        const string raw = """
            # Docs / Marketing Drift

            Verdict: drifting.

            README mentions feature X but I forgot the JSON sidecar.
            """;

        var svc = new DocsMarketingDriftAnalysisService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(DocsMarketingDriftParseStatus.Unstructured, parse.Status);
        Assert.Null(parse.ParseError);
        Assert.Equal("Docs / Marketing Drift", parse.Summary);
        Assert.Equal(DriftScoreBand.Unknown, parse.ScoreBand);
        Assert.Null(parse.Dimensions);
        Assert.Null(parse.FollowUps);
    }

    [Fact]
    public void TryParseAgentResponse_MalformedJsonSurfacesParseErrorWithoutHidingMarkdown()
    {
        const string raw = """
            # Docs / Marketing Drift

            ```json
            { "verdict": "drifting", "scoreBand": "Watch", "overallScore": 70,
            ```
            """;

        var svc = new DocsMarketingDriftAnalysisService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(DocsMarketingDriftParseStatus.MalformedJson, parse.Status);
        Assert.False(string.IsNullOrWhiteSpace(parse.ParseError));
        Assert.Contains("JSON sidecar failed", parse.ParseError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Docs / Marketing Drift", parse.Summary);
        Assert.Equal(DriftScoreBand.Unknown, parse.ScoreBand);
    }

    [Fact]
    public void TryParseAgentResponse_EmptyInputProducesUnstructuredWithoutCrashing()
    {
        var svc = new DocsMarketingDriftAnalysisService();
        Assert.Equal(DocsMarketingDriftParseStatus.Unstructured,
            svc.TryParseAgentResponse(null).Status);
        Assert.Equal(DocsMarketingDriftParseStatus.Unstructured,
            svc.TryParseAgentResponse("").Status);
    }

    // ------------------------------------------------------------------
    // BuildReport: validation, evidence references, follow-up output
    // ------------------------------------------------------------------

    [Fact]
    public void BuildReport_StructuredHealthyVerdictWithoutFindings_StillProducesSchemaValidReportWithSyntheticDimension()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "# README\n", Encoding.UTF8);

        var svc = new DocsMarketingDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);
        const string raw = """
            # Docs / Marketing Drift

            ```json
            {
              "verdict": "Healthy: docs and marketing match shipped behavior.",
              "scoreBand": "Healthy",
              "overallScore": 92
            }
            ```
            """;
        var parse = svc.TryParseAgentResponse(raw);
        Assert.Equal(DocsMarketingDriftParseStatus.Structured, parse.Status);

        var report = svc.BuildReport(
            scope: scope,
            parse: parse,
            reportId: "01HX0000000000000000000DM1",
            createdAt: new DateTime(2026, 5, 5, 13, 0, 0, DateTimeKind.Utc));

        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
        Assert.Equal(DriftScoreBand.Healthy, report.ScoreBand);
        Assert.Equal(92, report.OverallScore);
        Assert.Single(report.Dimensions);
        Assert.Equal(DriftDimensionType.Documentation, report.Dimensions[0].Type);
        Assert.NotEmpty(report.Dimensions[0].EvidenceRefs);
        Assert.Empty(report.FollowUpTaskSuggestions);
    }

    [Fact]
    public void BuildReport_StructuredVerdictWithFindings_PassesValidationAndCarriesEvidenceRefsOnEachDimension()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "# README\n", Encoding.UTF8);
        WriteJob("6-completed", "shipped-task", "Shipped");

        var svc = new DocsMarketingDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);
        const string raw = """
            ```json
            {
              "verdict": "Watch: README claims a feature that has not shipped yet.",
              "scoreBand": "Watch",
              "overallScore": 70,
              "dimensions": [
                {
                  "type": "Documentation",
                  "score": 65,
                  "severity": "Warn",
                  "confidence": 0.8,
                  "sourceCoverage": 0.6,
                  "status": "New",
                  "summary": "README mentions feature X without backing queue.",
                  "evidenceRefs": ["README.md"],
                  "recommendedActions": ["Sync README with shipped capabilities"]
                }
              ],
              "followUpTaskSuggestions": [
                {
                  "title": "Sync README with shipped capabilities",
                  "summary": "Remove or qualify the unbacked README claim.",
                  "priority": "Normal",
                  "relatedDimension": "Documentation"
                }
              ]
            }
            ```
            """;
        var parse = svc.TryParseAgentResponse(raw);

        var report = svc.BuildReport(
            scope: scope,
            parse: parse,
            reportId: "01HX0000000000000000000DM2",
            createdAt: new DateTime(2026, 5, 5, 13, 30, 0, DateTimeKind.Utc));

        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
        Assert.Equal("agent-taskboard", report.Project);
        Assert.Equal(DriftReportTrigger.Manual, report.Trigger);
        Assert.Equal(DriftReportScopeKind.Project, report.Scope.Kind);
        Assert.Equal(DriftScoreBand.Watch, report.ScoreBand);

        // SourceRefs cite the assembled scope: canonical doc and the recent
        // completed job both round-trip into the report.
        Assert.NotNull(report.Scope.SourceRefs);
        Assert.Contains(
            report.Scope.SourceRefs!,
            r => r.Replace('\\', '/') == "README.md");
        Assert.Contains(
            report.Scope.SourceRefs!,
            r => r.Replace('\\', '/') == "agent-taskboard/6-completed/shipped-task");
        // Marketing absence is recorded in source refs even when no marketing
        // path was supplied so a downstream reviewer can tell the difference
        // between "no marketing claims to check" and "marketing missed".
        Assert.Contains(
            report.Scope.SourceRefs!,
            r => r.StartsWith("marketing:", StringComparison.Ordinal));

        // Per-dimension evidence refs survive the round-trip.
        Assert.Single(report.Dimensions);
        Assert.Contains("README.md", report.Dimensions[0].EvidenceRefs);

        // Follow-ups round-trip into the report intact.
        Assert.Single(report.FollowUpTaskSuggestions);
        var followUp = report.FollowUpTaskSuggestions[0];
        Assert.Equal("Sync README with shipped capabilities", followUp.Title);
        Assert.Equal(DriftFollowUpPriority.Normal, followUp.Priority);
        Assert.Equal(DriftDimensionType.Documentation, followUp.RelatedDimension);
    }

    [Fact]
    public void BuildReport_UnstructuredParse_EmitsEvidenceOnlyDimensionWithUnknownBandAndStillValidates()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "# README\n", Encoding.UTF8);

        var svc = new DocsMarketingDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);
        var parse = svc.TryParseAgentResponse(null);

        var report = svc.BuildReport(
            scope: scope,
            parse: parse,
            reportId: "01HX0000000000000000000DM3",
            createdAt: DateTime.UtcNow);

        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
        Assert.Equal(DriftScoreBand.Unknown, report.ScoreBand);
        Assert.Single(report.Dimensions);
        Assert.Equal(DriftDimensionType.Documentation, report.Dimensions[0].Type);
        Assert.Contains("evidence-only", report.Dimensions[0].Summary, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(report.Dimensions[0].EvidenceRefs);
    }

    [Fact]
    public void BuildReport_MalformedJsonParse_EmitsUnknownBandAndCarriesParseErrorInDimensionSummary()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "# README\n", Encoding.UTF8);

        var svc = new DocsMarketingDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);
        var parse = svc.TryParseAgentResponse(
            "# Drift\n\n```json\n{ \"verdict\": \"x\", \"scoreBand\": \"Watch\",\n```\n");
        Assert.Equal(DocsMarketingDriftParseStatus.MalformedJson, parse.Status);

        var report = svc.BuildReport(
            scope: scope,
            parse: parse,
            reportId: "01HX0000000000000000000DM4",
            createdAt: DateTime.UtcNow);

        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
        Assert.Equal(DriftScoreBand.Unknown, report.ScoreBand);
        Assert.Single(report.Dimensions);
        Assert.Contains("failed to parse", report.Dimensions[0].Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildReport_WithAvailableMarketingRepo_SourceRefsCarryMarketingDocCitations()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "# README\n", Encoding.UTF8);
        var marketingRoot = Path.Combine(_stemRoot, "marketing");
        Directory.CreateDirectory(Path.Combine(marketingRoot, "06-website-planung"));
        File.WriteAllText(
            Path.Combine(marketingRoot, "06-website-planung", "website.md"),
            "# Website\n", Encoding.UTF8);

        var svc = new DocsMarketingDriftAnalysisService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            repoRoot: _repoRoot,
            marketingRepoRoot: marketingRoot);

        var parse = svc.TryParseAgentResponse(
            """
            ```json
            {
              "verdict": "Healthy",
              "scoreBand": "Healthy",
              "overallScore": 90
            }
            ```
            """);

        var report = svc.BuildReport(
            scope: scope,
            parse: parse,
            reportId: "01HX0000000000000000000DM5",
            createdAt: DateTime.UtcNow);

        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
        Assert.NotNull(report.Scope.SourceRefs);
        Assert.Contains(
            report.Scope.SourceRefs!,
            r => r == "marketing:06-website-planung/website.md");
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
        File.WriteAllText(Path.Combine(dir, "job.json"), json, Encoding.UTF8);
    }
}
