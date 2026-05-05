using System.Text;
using OrchestratorApi.Services.Analysis;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the contract of <see cref="RoadmapAlignmentReviewService"/>: scope
/// selection only walks the four inspected lanes, flags stray folders, the
/// rendered prompt carries every load-bearing section the agent needs, and
/// the JSON parse fallback distinguishes Structured / Unstructured /
/// MalformedJson without ever hiding the Markdown body.
/// </summary>
public class RoadmapAlignmentReviewServiceTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly string _repoRoot;

    public RoadmapAlignmentReviewServiceTests()
    {
        var stem = "roadmap-alignment-tests-" + Guid.NewGuid().ToString("N");
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
    public void SelectScope_OnlyWalksTheFourInspectedLanes_AndIgnoresCompletedAndArchive()
    {
        // Each of the inspected lanes plus the two excluded ones gets one
        // job. The action must report jobs only from 1-preparation,
        // 2-ready, 3-progress, 4-review; 5-completed and 6-archive are out of
        // scope by design ("are we on track" looks at the active queue).
        WriteJob("1-preparation", "draft-task", "Draft a task");
        WriteJob("2-ready", "ready-task", "Ready to start");
        WriteJob("3-progress", "in-flight-task", "In progress");
        WriteJob("4-review", "review-task", "Awaiting review");
        WriteJob("5-completed", "shipped-task", "Already shipped");
        WriteJob("6-archive", "old-task", "Archived");

        var svc = new RoadmapAlignmentReviewService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        Assert.Equal(new[] { "1-preparation", "2-ready", "3-progress", "4-review" },
            scope.JobsByLane.Keys.ToArray());
        Assert.Single(scope.JobsByLane["1-preparation"]);
        Assert.Single(scope.JobsByLane["2-ready"]);
        Assert.Single(scope.JobsByLane["3-progress"]);
        Assert.Single(scope.JobsByLane["4-review"]);
        // The two excluded lanes do not appear at all.
        Assert.False(scope.JobsByLane.ContainsKey("5-completed"));
        Assert.False(scope.JobsByLane.ContainsKey("6-archive"));
    }

    [Fact]
    public void SelectScope_FlagsStrayFoldersWithoutJobJsonAndMarksQueueDirty()
    {
        WriteJob("2-ready", "real-task", "Real");
        // A subfolder under 2-ready without job.json: a stray.
        Directory.CreateDirectory(Path.Combine(_projectRoot, "2-ready", "chip-1234567890"));
        // A subfolder under 3-progress with malformed JSON: also a stray.
        var bad = Path.Combine(_projectRoot, "3-progress", "broken-task");
        Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "job.json"), "{ this is not valid json", Encoding.UTF8);

        var svc = new RoadmapAlignmentReviewService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        Assert.False(scope.QueueIsClean);
        Assert.Equal(2, scope.StrayLaneFolders.Count);
        Assert.Contains(scope.StrayLaneFolders, s => s.Contains("chip-1234567890"));
        Assert.Contains(scope.StrayLaneFolders, s => s.Contains("broken-task") && s.Contains("malformed"));
        // The valid sibling job is still surfaced.
        Assert.Single(scope.JobsByLane["2-ready"]);
        Assert.Equal("real-task", scope.JobsByLane["2-ready"][0].JobId);
    }

    [Fact]
    public void SelectScope_PicksUpCanonicalDocsWhenTheyExistAndSkipsOnesThatDoNot()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "AGENTS.md"), "# AGENTS\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_repoRoot, "ROADMAP.md"), "# ROADMAP\n", Encoding.UTF8);
        // README intentionally absent for this run.
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs"));
        File.WriteAllText(
            Path.Combine(_repoRoot, "docs", "design-principles.md"), "# DP\n", Encoding.UTF8);
        // Two mockup folders so the action picks them up by directory walk.
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs", "mockups", "alpha-mockup"));
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs", "mockups", "beta-mockup"));

        var svc = new RoadmapAlignmentReviewService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        var paths = scope.Docs.Select(d => d.Path.Replace('\\', '/')).ToArray();
        Assert.Contains("AGENTS.md", paths);
        Assert.Contains("ROADMAP.md", paths);
        Assert.Contains("docs/design-principles.md", paths);
        Assert.DoesNotContain(paths, p => p.EndsWith("README.md", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("alpha-mockup", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("beta-mockup", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // BuildPrompt
    // ------------------------------------------------------------------

    [Fact]
    public void BuildPrompt_RendersAllLoadBearingPlaceholdersAndKeepsHardConstraintWording()
    {
        WriteJob("2-ready", "task-access-api-layer-extraction", "Task Access Layer phase 2");
        WriteJob("3-progress", "client-identity-and-task-attribution", "Client identity work");

        var svc = new RoadmapAlignmentReviewService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            repoRoot: _repoRoot,
            now: new DateTime(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc));

        const string template = """
            Project: {{project}}
            Captured: {{captured_at}}
            Queue clean: {{queue_clean_flag}}

            ## Snapshot
            {{queue_summary}}

            ## Jobs
            {{jobs_by_lane}}

            ## Docs
            {{doc_list}}

            ## Recent
            {{recent_reports}}

            ## Stray
            {{stray_folders}}

            (do not modify any source file)
            """;
        var rendered = svc.BuildPrompt(scope, template);

        Assert.Contains("agent-taskboard", rendered);
        Assert.Contains("2026-05-05T10:00:00Z", rendered);
        Assert.Contains("queue clean: yes", rendered, StringComparison.OrdinalIgnoreCase);
        // Lane counts table rendered.
        Assert.Contains("`1-preparation` | 0", rendered);
        Assert.Contains("`2-ready` | 1", rendered);
        Assert.Contains("`3-progress` | 1", rendered);
        Assert.Contains("`4-review` | 0", rendered);
        // Job ids land in the jobs-by-lane section.
        Assert.Contains("task-access-api-layer-extraction", rendered);
        Assert.Contains("client-identity-and-task-attribution", rendered);
        // Hard-constraint wording from the template is preserved verbatim.
        Assert.Contains("do not modify any source file", rendered);
        // No unrendered placeholders remain.
        Assert.DoesNotContain("{{", rendered);
    }

    [Fact]
    public void BuildPrompt_FlagsDirtyQueueAndListsStrayFoldersInline()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "2-ready", "chip-orphan-9999"));
        var svc = new RoadmapAlignmentReviewService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        var rendered = svc.BuildPrompt(scope,
            "queue-clean={{queue_clean_flag}};stray={{stray_folders}}");
        Assert.Contains("queue-clean=no", rendered);
        Assert.Contains("chip-orphan-9999", rendered);
    }

    // ------------------------------------------------------------------
    // TryParseAgentResponse
    // ------------------------------------------------------------------

    [Fact]
    public void TryParseAgentResponse_StructuredResponseExposesVerdictSeverityFindingsAndFollowUps()
    {
        const string raw = """
            # Roadmap alignment

            On track with two follow-ups.

            ```json
            {
              "verdict": "On track with two follow-ups.",
              "severity": "Warn",
              "findings": [
                {
                  "topic": "stale-review-backlog",
                  "severity": "Warn",
                  "message": "17 items in 4-review.",
                  "evidenceRefs": ["agent-taskboard/4-review/agent-message-bus-contract"]
                }
              ],
              "recommendedPriorityOrder": ["task-access-api-layer-extraction", "client-identity-and-task-attribution"],
              "followUpTaskSuggestions": [
                {
                  "title": "Drain review backlog before broad UI additions",
                  "summary": "Several review-lane items already produced docs and ADRs and now need acceptance.",
                  "priority": "High",
                  "relatedTopic": "QueueHealth",
                  "targetState": "2-ready"
                }
              ]
            }
            ```
            """;

        var svc = new RoadmapAlignmentReviewService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(AnalysisReportParseStatus.Structured, parse.Status);
        Assert.Equal(AnalysisReportSeverity.Warn, parse.Severity);
        Assert.Equal("On track with two follow-ups.", parse.Summary);
        Assert.Null(parse.ParseError);
        Assert.Single(parse.Findings!);
        Assert.Equal("stale-review-backlog", parse.Findings![0].Topic);
        Assert.Equal("17 items in 4-review.", parse.Findings![0].Message);
        Assert.Single(parse.FollowUps!);
        Assert.Equal(AnalysisReportFollowUpPriority.High, parse.FollowUps![0].Priority);
        Assert.Equal(AnalysisReportFollowUpRelatedTopic.QueueHealth, parse.FollowUps![0].RelatedTopic);
        // Constraint: agent-supplied targetState=2-ready is coerced to
        // 1-preparation. Open-ended producer must not bypass the user.
        Assert.Equal(AnalysisReportFollowUpTargetStates.OnePreparation, parse.FollowUps![0].TargetState);
        Assert.Equal(2, parse.PriorityOrder!.Count);
    }

    [Fact]
    public void TryParseAgentResponse_NoFencedJsonBlockProducesUnstructuredFallbackKeepingMarkdown()
    {
        const string raw = """
            # Roadmap alignment

            Verdict: drifting.

            The review backlog is too large but the agent forgot the JSON sidecar.
            """;

        var svc = new RoadmapAlignmentReviewService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(AnalysisReportParseStatus.Unstructured, parse.Status);
        Assert.Null(parse.ParseError);
        // Summary falls back to the first heading so the list view still has a
        // useful one-liner.
        Assert.Equal("Roadmap alignment", parse.Summary);
        Assert.Null(parse.Findings);
        Assert.Null(parse.FollowUps);
    }

    [Fact]
    public void TryParseAgentResponse_MalformedJsonSidecarSurfacesParseErrorWithoutHidingMarkdown()
    {
        const string raw = """
            # Roadmap alignment

            Verdict: drifting.

            ```json
            { "verdict": "drifting", "severity": "Warn",
            ```
            """;

        var svc = new RoadmapAlignmentReviewService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(AnalysisReportParseStatus.MalformedJson, parse.Status);
        Assert.False(string.IsNullOrWhiteSpace(parse.ParseError));
        Assert.Contains("JSON sidecar failed", parse.ParseError, StringComparison.OrdinalIgnoreCase);
        // The Markdown summary is still derived; the body itself stays
        // visible at the call site.
        Assert.Equal("Roadmap alignment", parse.Summary);
    }

    [Fact]
    public void TryParseAgentResponse_JsonWithBadSeveritySurfacesAsMalformedJsonNotStructured()
    {
        const string raw = """
            # Roadmap alignment

            ```json
            { "verdict": "drifting", "severity": "Catastrophic" }
            ```
            """;

        var svc = new RoadmapAlignmentReviewService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(AnalysisReportParseStatus.MalformedJson, parse.Status);
        Assert.Contains("severity", parse.ParseError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseAgentResponse_EmptyInputProducesUnstructuredWithoutCrashing()
    {
        var svc = new RoadmapAlignmentReviewService();
        var parse = svc.TryParseAgentResponse(null);
        Assert.Equal(AnalysisReportParseStatus.Unstructured, parse.Status);

        parse = svc.TryParseAgentResponse("");
        Assert.Equal(AnalysisReportParseStatus.Unstructured, parse.Status);
    }

    // ------------------------------------------------------------------
    // BuildReport
    // ------------------------------------------------------------------

    [Fact]
    public void BuildReport_PassesValidation_AndCarriesJobReferencesByStableId()
    {
        WriteJob("3-progress", "client-identity-and-task-attribution", "Client identity work");
        File.WriteAllText(Path.Combine(_repoRoot, "AGENTS.md"), "# AGENTS\n", Encoding.UTF8);

        var svc = new RoadmapAlignmentReviewService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);
        var parse = svc.TryParseAgentResponse(
            "# Verdict\n\n```json\n{ \"verdict\": \"on track\", \"severity\": \"Info\" }\n```\n");
        var report = svc.BuildReport(
            scope: scope,
            parse: parse,
            reportId: "01HX0000000000000000000RID",
            createdAt: new DateTime(2026, 5, 5, 11, 0, 0, DateTimeKind.Utc));

        Assert.True(AnalysisReportValidator.TryValidate(report, out var error), error);
        Assert.Equal("roadmap-alignment", report.Topic);
        Assert.Equal(AnalysisReportScopeKind.Project, report.Scope.Kind);
        Assert.Equal("agent-taskboard", report.Scope.Project);
        Assert.Contains(report.References, r =>
            r.Kind == AnalysisReportReferenceKind.Job
            && r.Ref == "agent-taskboard/3-progress/client-identity-and-task-attribution");
        Assert.Contains(report.References, r =>
            r.Kind == AnalysisReportReferenceKind.Doc && r.Ref == "AGENTS.md");
    }

    [Fact]
    public void BuildReport_DirtyQueueAddsTagSoConsumersCanFilter()
    {
        Directory.CreateDirectory(Path.Combine(_projectRoot, "2-ready", "chip-1"));
        var svc = new RoadmapAlignmentReviewService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);
        var parse = svc.TryParseAgentResponse("# verdict\n\nbody");
        var report = svc.BuildReport(
            scope, parse,
            reportId: "01HX0000000000000000000RIE",
            createdAt: DateTime.UtcNow);

        Assert.Contains("queue-dirty", report.Tags!);
        Assert.Contains("roadmap-alignment", report.Tags!);
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
