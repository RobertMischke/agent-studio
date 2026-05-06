using System.Text;
using OrchestratorApi.Services.Analysis;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the contract of <see cref="SteeringDocsSummaryDriftService"/>: scope
/// selection inventories the canonical steering surface (and surfaces missing
/// sources rather than swallowing them), the rendered prompt carries every
/// load-bearing section, the JSON parse fallback distinguishes
/// Structured / Unstructured / MalformedJson without ever hiding the Markdown
/// body, and follow-up suggestions are coerced to the safe default lane.
/// </summary>
public class SteeringDocsSummaryDriftServiceTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly string _repoRoot;

    public SteeringDocsSummaryDriftServiceTests()
    {
        var stem = "steering-docs-drift-tests-" + Guid.NewGuid().ToString("N");
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
    // SelectScope
    // ------------------------------------------------------------------

    [Fact]
    public void SelectScope_InventoriesEveryCanonicalSourceAndMarksMissingFiles()
    {
        // Only AGENTS, README, and the task contract exist - the rest of the
        // canonical surface should still be inventoried, just marked missing.
        File.WriteAllText(Path.Combine(_repoRoot, "AGENTS.md"), "# AGENTS\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "# README\n", Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs"));
        File.WriteAllText(
            Path.Combine(_repoRoot, "docs", "agent-task-contract.md"), "# Task contract\n", Encoding.UTF8);

        var svc = new SteeringDocsSummaryDriftService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        // Every canonical source appears, even when missing.
        Assert.Contains(scope.Sources, s => s.Id == "agents" && s.Exists);
        Assert.Contains(scope.Sources, s => s.Id == "readme" && s.Exists);
        Assert.Contains(scope.Sources, s => s.Id == "task-contract" && s.Exists);
        Assert.Contains(scope.Sources, s => s.Id == "roadmap" && !s.Exists);
        Assert.Contains(scope.Sources, s => s.Id == "adr" && !s.Exists);
        Assert.Contains(scope.Sources, s => s.Id == "skills-architecture" && !s.Exists);
        Assert.Contains(scope.Sources, s => s.Id == "runtime-prompts");

        // ROADMAP missing is not a critical source for warnings, but
        // missing AGENTS / README / task contract would be. Here all three
        // critical sources exist, so the inventory is "clean enough" for
        // those critical-warning rules.
        Assert.DoesNotContain(scope.Warnings, w => w.SourceId == "agents");
        Assert.DoesNotContain(scope.Warnings, w => w.SourceId == "readme");
        Assert.DoesNotContain(scope.Warnings, w => w.SourceId == "task-contract");
        // The other missing sources surface as info-level missing warnings.
        Assert.Contains(scope.Warnings, w => w.SourceId == "roadmap");
    }

    [Fact]
    public void SelectScope_FlagsCriticalMissingSourcesAtHighSeverity()
    {
        // No AGENTS, no README, no task contract.
        var svc = new SteeringDocsSummaryDriftService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        Assert.False(scope.InventoryClean);
        Assert.Contains(scope.Warnings, w =>
            w.SourceId == "agents" && w.Severity == AnalysisReportSeverity.High);
        Assert.Contains(scope.Warnings, w =>
            w.SourceId == "readme" && w.Severity == AnalysisReportSeverity.High);
        Assert.Contains(scope.Warnings, w =>
            w.SourceId == "task-contract" && w.Severity == AnalysisReportSeverity.High);
    }

    [Fact]
    public void SelectScope_FlagsShimDriftWhenCompatibilityShimGrowsBeyondTinyThreshold()
    {
        // CLAUDE.md is a compatibility shim; >1 KB triggers the warning.
        File.WriteAllText(Path.Combine(_repoRoot, "AGENTS.md"), "# AGENTS\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "# README\n", Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs"));
        File.WriteAllText(
            Path.Combine(_repoRoot, "docs", "agent-task-contract.md"), "# Task contract\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_repoRoot, "CLAUDE.md"), new string('x', 4096), Encoding.UTF8);

        var svc = new SteeringDocsSummaryDriftService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        Assert.Contains(scope.Warnings, w =>
            w.SourceId == "claude-shim"
            && w.Kind == SteeringInventoryWarningKind.PossibleConflict
            && w.Severity == AnalysisReportSeverity.Warn);
    }

    [Fact]
    public void SelectScope_SamplesRecentJobsAcrossInspectedLanesAndSkipsExcludedLanes()
    {
        // 1-preparation and 7-archive are excluded by design.
        WriteJob("1-preparation", "draft-task", "Draft");
        WriteJob("2-ready", "ready-task", "Ready");
        WriteJob("3-progress", "in-flight-task", "In flight");
        WriteJob("4-auto-review", "auto-review-task", "Auto review");
        WriteJob("5-human-review", "human-review-task", "Human review");
        WriteJob("6-completed", "shipped-task", "Shipped");
        WriteJob("7-archive", "old-task", "Archived");

        var svc = new SteeringDocsSummaryDriftService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        Assert.Contains("2-ready", scope.JobsByLane.Keys);
        Assert.Contains("3-progress", scope.JobsByLane.Keys);
        Assert.Contains("4-auto-review", scope.JobsByLane.Keys);
        Assert.Contains("5-human-review", scope.JobsByLane.Keys);
        Assert.Contains("6-completed", scope.JobsByLane.Keys);
        Assert.DoesNotContain("1-preparation", scope.JobsByLane.Keys);
        Assert.DoesNotContain("7-archive", scope.JobsByLane.Keys);
        Assert.Single(scope.JobsByLane["2-ready"]);
        Assert.Equal("ready-task", scope.JobsByLane["2-ready"][0].JobId);
    }

    // ------------------------------------------------------------------
    // BuildPrompt
    // ------------------------------------------------------------------

    [Fact]
    public void BuildPrompt_RendersAllLoadBearingPlaceholdersAndKeepsHardConstraintWording()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "AGENTS.md"), "# AGENTS\n", Encoding.UTF8);
        WriteJob("3-progress", "client-identity-and-task-attribution", "Client identity work");

        var svc = new SteeringDocsSummaryDriftService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            repoRoot: _repoRoot,
            now: new DateTime(2026, 5, 6, 9, 0, 0, DateTimeKind.Utc));

        const string template = """
            Project: {{project}}
            Captured: {{captured_at}}
            Inventory clean: {{inventory_clean_flag}}

            ## Sources
            {{source_inventory}}

            ## Warnings
            {{inventory_warnings}}

            ## Recent reports
            {{recent_analysis_reports}}

            ## Recent jobs
            {{recent_job_evidence}}

            (do not modify any source file)
            """;
        var rendered = svc.BuildPrompt(scope, template);

        Assert.Contains("agent-taskboard", rendered);
        Assert.Contains("2026-05-06T09:00:00Z", rendered);
        Assert.Contains("AGENTS.md", rendered);
        Assert.Contains("client-identity-and-task-attribution", rendered);
        Assert.Contains("do not modify any source file", rendered);
        Assert.DoesNotContain("{{", rendered);
    }

    // ------------------------------------------------------------------
    // TryParseAgentResponse
    // ------------------------------------------------------------------

    [Fact]
    public void TryParseAgentResponse_StructuredReplyExposesVerdictSeverityFindingsProposalsAndFollowUps()
    {
        const string raw = """
            # Steering docs summary

            On track but two areas drifting.

            ```json
            {
              "kind": "steering-docs-summary-and-drift",
              "schemaVersion": 1,
              "scope": { "project": "agent-taskboard" },
              "summary": "On track but two areas drifting.",
              "severity": "Warn",
              "sources": ["AGENTS.md", "ROADMAP.md"],
              "driftFindings": [
                {
                  "topic": "shim-drift",
                  "severity": "Warn",
                  "message": "CLAUDE.md has grown beyond the three-line shim contract.",
                  "evidenceRefs": ["CLAUDE.md"]
                }
              ],
              "proposalRefs": [
                { "path": "CLAUDE.md", "label": "Trim shim back to pointer-only contract" }
              ],
              "followUpTaskSuggestions": [
                {
                  "title": "Trim CLAUDE.md back to a 3-line pointer",
                  "summary": "Restore the compatibility-shim contract documented in AGENTS.md.",
                  "priority": "High",
                  "relatedTopic": "DocsDrift",
                  "targetState": "2-ready"
                }
              ],
              "parseStatus": "Structured"
            }
            ```
            """;

        var svc = new SteeringDocsSummaryDriftService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(AnalysisReportParseStatus.Structured, parse.Status);
        Assert.Equal(AnalysisReportSeverity.Warn, parse.Severity);
        Assert.Equal("On track but two areas drifting.", parse.Summary);
        Assert.Null(parse.ParseError);
        Assert.Single(parse.Findings!);
        Assert.Equal("shim-drift", parse.Findings![0].Topic);
        Assert.Single(parse.ProposalRefs!);
        Assert.Equal("CLAUDE.md", parse.ProposalRefs![0].Path);
        Assert.Single(parse.FollowUps!);
        Assert.Equal(AnalysisReportFollowUpRelatedTopic.DocsDrift, parse.FollowUps![0].RelatedTopic);
        // Constraint: agent-supplied targetState=2-ready is coerced to
        // 1-preparation. Open-ended producer must not bypass the user.
        Assert.Equal(AnalysisReportFollowUpTargetStates.OnePreparation, parse.FollowUps![0].TargetState);
        Assert.Equal(2, parse.Sources!.Count);
    }

    [Fact]
    public void TryParseAgentResponse_NoFencedJsonFallsBackToUnstructuredKeepingMarkdownSummary()
    {
        const string raw = """
            # Steering docs summary

            Verdict: drifting.

            The agent forgot the JSON sidecar. The Markdown body is still durable.
            """;

        var svc = new SteeringDocsSummaryDriftService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(AnalysisReportParseStatus.Unstructured, parse.Status);
        Assert.Null(parse.ParseError);
        Assert.Equal("Steering docs summary", parse.Summary);
        Assert.Null(parse.Findings);
        Assert.Null(parse.ProposalRefs);
    }

    [Fact]
    public void TryParseAgentResponse_MalformedJsonSurfacesParseErrorAndStaysAtMalformedJsonStatus()
    {
        const string raw = """
            # Steering docs summary

            ```json
            { "summary": "drifting", "severity": "Warn",
            ```
            """;

        var svc = new SteeringDocsSummaryDriftService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(AnalysisReportParseStatus.MalformedJson, parse.Status);
        Assert.False(string.IsNullOrWhiteSpace(parse.ParseError));
    }

    [Fact]
    public void TryParseAgentResponse_BadSeveritySurfacesAsMalformedJsonNotStructured()
    {
        const string raw = """
            # Steering docs summary

            ```json
            { "summary": "drifting", "severity": "Catastrophic" }
            ```
            """;

        var svc = new SteeringDocsSummaryDriftService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(AnalysisReportParseStatus.MalformedJson, parse.Status);
        Assert.Contains("severity", parse.ParseError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseAgentResponse_KindMismatchIsRejectedNotSilentlyAccepted()
    {
        const string raw = """
            # Steering docs summary

            ```json
            { "kind": "roadmap-alignment", "summary": "x", "severity": "Info" }
            ```
            """;

        var svc = new SteeringDocsSummaryDriftService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(AnalysisReportParseStatus.MalformedJson, parse.Status);
        Assert.Contains("kind", parse.ParseError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseAgentResponse_EmptyInputProducesUnstructuredWithoutCrashing()
    {
        var svc = new SteeringDocsSummaryDriftService();
        Assert.Equal(AnalysisReportParseStatus.Unstructured, svc.TryParseAgentResponse(null).Status);
        Assert.Equal(AnalysisReportParseStatus.Unstructured, svc.TryParseAgentResponse("").Status);
    }

    // ------------------------------------------------------------------
    // BuildReport
    // ------------------------------------------------------------------

    [Fact]
    public void BuildReport_PassesValidationCarriesDocAndJobReferencesByStableId()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "AGENTS.md"), "# AGENTS\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "# README\n", Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs"));
        File.WriteAllText(
            Path.Combine(_repoRoot, "docs", "agent-task-contract.md"), "# Contract\n", Encoding.UTF8);
        WriteJob("3-progress", "client-identity-and-task-attribution", "Client identity work");

        var svc = new SteeringDocsSummaryDriftService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);
        var parse = svc.TryParseAgentResponse(
            "# Verdict\n\n```json\n{ \"kind\": \"steering-docs-summary-and-drift\", \"summary\": \"on track\", \"severity\": \"Info\" }\n```\n");
        var report = svc.BuildReport(
            scope: scope,
            parse: parse,
            reportId: "01HX0000000000000000000RID",
            createdAt: new DateTime(2026, 5, 6, 11, 0, 0, DateTimeKind.Utc));

        Assert.True(AnalysisReportValidator.TryValidate(report, out var error), error);
        Assert.Equal("steering-docs-summary-and-drift", report.Topic);
        Assert.Equal(AnalysisReportScopeKind.Project, report.Scope.Kind);
        Assert.Equal("agent-taskboard", report.Scope.Project);
        Assert.Contains(report.References, r =>
            r.Kind == AnalysisReportReferenceKind.Doc && r.Ref == "AGENTS.md");
        Assert.Contains(report.References, r =>
            r.Kind == AnalysisReportReferenceKind.Job
            && r.Ref == "agent-taskboard/3-progress/client-identity-and-task-attribution");
    }

    [Fact]
    public void BuildReport_ProposalRefsFromAgentBecomeDocReferencesIncludingPlannedNewFiles()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "AGENTS.md"), "# AGENTS\n", Encoding.UTF8);
        var svc = new SteeringDocsSummaryDriftService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        const string raw = """
            # Steering docs summary

            ```json
            {
              "kind": "steering-docs-summary-and-drift",
              "summary": "two proposals",
              "severity": "Info",
              "proposalRefs": [
                { "path": "AGENTS.md", "label": "Add note about analysis reports" },
                { "path": "new:docs/onboarding.md", "label": "Document onboarding flow" }
              ]
            }
            ```
            """;
        var parse = svc.TryParseAgentResponse(raw);
        var report = svc.BuildReport(scope, parse,
            reportId: "01HX0000000000000000000PRP",
            createdAt: DateTime.UtcNow);

        Assert.True(AnalysisReportValidator.TryValidate(report, out var error), error);
        Assert.Contains(report.References, r => r.Ref == "AGENTS.md" && (r.Label ?? string.Empty).StartsWith("proposal", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.References, r => r.Ref == "new:docs/onboarding.md");
    }

    [Fact]
    public void BuildReport_DirtyInventoryAddsTagSoConsumersCanFilter()
    {
        // No AGENTS, no README, no task contract -> high-severity warnings.
        var svc = new SteeringDocsSummaryDriftService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);
        var parse = svc.TryParseAgentResponse("# verdict\n\nbody");
        var report = svc.BuildReport(scope, parse,
            reportId: "01HX0000000000000000000RIE",
            createdAt: DateTime.UtcNow);

        Assert.Contains("inventory-dirty", report.Tags!);
        Assert.Contains("steering-docs", report.Tags!);
        Assert.Contains("unstructured", report.Tags!);
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
