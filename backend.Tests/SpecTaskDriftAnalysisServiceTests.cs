using System.Text;
using OrchestratorApi.Services.Drift;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the contract of <see cref="SpecTaskDriftAnalysisService"/>:
/// scope selection picks up specs / planning docs and the full active queue
/// (1-preparation through 5-human-review) plus recent completed evidence;
/// the duplicate-detection hook flags suspect pairs without acting on them;
/// missing source folders never crash; the JSON parse fallback distinguishes
/// Structured / Unstructured / MalformedJson without ever hiding the
/// Markdown body; and an empty / no-drift verdict still produces a
/// schema-valid drift report.
/// </summary>
public class SpecTaskDriftAnalysisServiceTests : IDisposable
{
    private readonly string _projectRoot;
    private readonly string _repoRoot;

    public SpecTaskDriftAnalysisServiceTests()
    {
        var stem = "spec-task-job-drift-tests-" + Guid.NewGuid().ToString("N");
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
    // SelectScope: spec / queue / recent-completed source selection
    // ------------------------------------------------------------------

    [Fact]
    public void SelectScope_PicksUpSpecsAndPlanningDocsAndSkipsMissingFiles()
    {
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs"));
        File.WriteAllText(Path.Combine(_repoRoot, "ROADMAP.md"), "# ROADMAP\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_repoRoot, "AGENTS.md"), "# AGENTS\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_repoRoot, "docs", "design-principles.md"), "# DP\n", Encoding.UTF8);
        // README intentionally absent so we prove missing files are skipped.

        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs", "mockups", "drift-control"));
        Directory.CreateDirectory(Path.Combine(_repoRoot, "docs", "mockups", "orchestrator-meta-cycle"));

        var svc = new SpecTaskDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        var paths = scope.SpecDocs.Select(d => d.Path.Replace('\\', '/')).ToArray();
        Assert.Contains("ROADMAP.md", paths);
        Assert.Contains("AGENTS.md", paths);
        Assert.Contains("docs/design-principles.md", paths);
        Assert.DoesNotContain(paths, p => p.EndsWith("README.md", StringComparison.Ordinal));
        // Mockup folders surface as separate spec entries.
        Assert.Contains("docs/mockups/drift-control/", paths);
        Assert.Contains("docs/mockups/orchestrator-meta-cycle/", paths);
        // ROADMAP leads the list so the agent reads stated intent first.
        Assert.Equal("ROADMAP.md", scope.SpecDocs[0].Path.Replace('\\', '/'));
    }

    [Fact]
    public void SelectScope_MissingProjectRootSubfoldersDoNotCrashAndYieldEmptyLists()
    {
        // Bare project root - no lane folders at all. The action must still
        // produce a usable scope; empty-lane handling lets the prompt render
        // "(no active queue jobs)" rather than failing.
        File.WriteAllText(Path.Combine(_repoRoot, "ROADMAP.md"), "# R\n", Encoding.UTF8);

        var svc = new SpecTaskDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        Assert.Empty(scope.ActiveJobs);
        Assert.Empty(scope.RecentCompleted);
        Assert.Empty(scope.DuplicateCandidates);
        Assert.Single(scope.SpecDocs);
    }

    [Fact]
    public void SelectScope_PicksUpAllActiveLanesIncludingPreparationAndProgressAndIgnoresArchive()
    {
        WriteJob("1-preparation", "draft-task", "Draft task");
        WriteJob("2-ready", "ready-task", "Ready task");
        WriteJob("3-progress", "in-flight-task", "In progress");
        WriteJob("4-auto-review", "auto-review-task", "Auto review");
        WriteJob("5-human-review", "human-review-task", "Human review");
        WriteJob("6-completed", "shipped-task", "Shipped");
        WriteJob("7-archive", "archived-task", "Archived");

        var svc = new SpecTaskDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        var activeIds = scope.ActiveJobs.Select(t => t.JobId).ToArray();
        Assert.Contains("draft-task", activeIds);
        Assert.Contains("ready-task", activeIds);
        Assert.Contains("in-flight-task", activeIds);
        Assert.Contains("auto-review-task", activeIds);
        Assert.Contains("human-review-task", activeIds);
        // Completed and archive belong to RecentCompleted (or nowhere).
        Assert.DoesNotContain("shipped-task", activeIds);
        Assert.DoesNotContain("archived-task", activeIds);

        var completedIds = scope.RecentCompleted.Select(t => t.JobId).ToArray();
        Assert.Contains("shipped-task", completedIds);
        Assert.DoesNotContain("archived-task", completedIds);
    }

    [Fact]
    public void SelectScope_ActiveJobIncludesPromptExcerptAndStatusLogMarkers()
    {
        WriteJob("2-ready", "task-with-evidence", "Task with evidence",
            promptBody: "# Title\n\nThis prompt explains the task in enough detail.",
            withStatus: true,
            withLogs: true);
        WriteJob("2-ready", "thin-task", "Thin task",
            promptBody: "tbd",
            withStatus: false,
            withLogs: false);

        var svc = new SpecTaskDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        var rich = scope.ActiveJobs.Single(j => j.JobId == "task-with-evidence");
        Assert.True(rich.HasStatus);
        Assert.True(rich.HasLogs);
        Assert.NotNull(rich.PromptExcerpt);
        Assert.Contains("explains the task", rich.PromptExcerpt!);

        var thin = scope.ActiveJobs.Single(j => j.JobId == "thin-task");
        Assert.False(thin.HasStatus);
        Assert.False(thin.HasLogs);
        Assert.Equal("tbd", thin.PromptExcerpt);
    }

    // ------------------------------------------------------------------
    // Duplicate detection hook
    // ------------------------------------------------------------------

    [Fact]
    public void DetectDuplicateTaskCandidates_FlagsPairsWithHighTokenOverlapAndSkipsUnrelatedJobs()
    {
        var jobs = new[]
        {
            new SpecTaskDriftAnalysisService.ActiveJobRef(
                JobId: "drift-report-schema-and-scoring",
                Title: "Drift report schema and scoring",
                Lane: "2-ready",
                LastWriteUtc: null,
                PromptExcerpt: null,
                HasStatus: false,
                HasLogs: false),
            new SpecTaskDriftAnalysisService.ActiveJobRef(
                JobId: "drift-report-schema-scoring",
                Title: "Drift report schema scoring",
                Lane: "1-preparation",
                LastWriteUtc: null,
                PromptExcerpt: null,
                HasStatus: false,
                HasLogs: false),
            new SpecTaskDriftAnalysisService.ActiveJobRef(
                JobId: "frontend-styling-pass",
                Title: "Frontend styling pass",
                Lane: "2-ready",
                LastWriteUtc: null,
                PromptExcerpt: null,
                HasStatus: false,
                HasLogs: false),
        };

        var pairs = SpecTaskDriftAnalysisService.DetectDuplicateTaskCandidates(jobs);

        Assert.Single(pairs);
        var pair = pairs[0];
        Assert.True(
            (pair.LeftJobId == "drift-report-schema-and-scoring" && pair.RightJobId == "drift-report-schema-scoring") ||
            (pair.LeftJobId == "drift-report-schema-scoring" && pair.RightJobId == "drift-report-schema-and-scoring"),
            $"unexpected pair: {pair.LeftJobId} vs {pair.RightJobId}");
        Assert.True(pair.Overlap >= 0.6, $"overlap should clear default threshold but was {pair.Overlap}");
    }

    [Fact]
    public void DetectDuplicateTaskCandidates_EmptyInputProducesEmptyResultWithoutCrashing()
    {
        var none = SpecTaskDriftAnalysisService.DetectDuplicateTaskCandidates(
            Array.Empty<SpecTaskDriftAnalysisService.ActiveJobRef>());
        Assert.Empty(none);
    }

    [Fact]
    public void SelectScope_DuplicateCandidatesAreFlaggedFromActiveQueue()
    {
        WriteJob("2-ready", "drift-report-schema-and-scoring", "Drift report schema and scoring");
        WriteJob("1-preparation", "drift-report-schema-scoring", "Drift report schema scoring");
        WriteJob("2-ready", "unrelated-task", "Frontend styling pass");

        var svc = new SpecTaskDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);

        Assert.Single(scope.DuplicateCandidates);
        Assert.Contains(
            scope.DuplicateCandidates,
            p => (p.LeftJobId == "drift-report-schema-and-scoring" && p.RightJobId == "drift-report-schema-scoring") ||
                 (p.LeftJobId == "drift-report-schema-scoring" && p.RightJobId == "drift-report-schema-and-scoring"));
    }

    // ------------------------------------------------------------------
    // BuildPrompt: load-bearing placeholders are rendered
    // ------------------------------------------------------------------

    [Fact]
    public void BuildPrompt_RendersAllLoadBearingPlaceholdersWithoutLeavingUnrenderedBraces()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "ROADMAP.md"), "# R\n", Encoding.UTF8);
        WriteJob("2-ready", "drift-report-schema-and-scoring", "Drift report schema and scoring");
        WriteJob("1-preparation", "drift-report-schema-scoring", "Drift report schema scoring");
        WriteJob("6-completed", "shipped-task", "Shipped");

        var svc = new SpecTaskDriftAnalysisService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            repoRoot: _repoRoot,
            now: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));

        const string template = """
            Project: {{project}}
            Captured: {{captured_at}}

            ## Specs
            {{spec_docs}}

            ## Active queue
            {{active_jobs}}

            ## Recent completed
            {{recent_completed}}

            ## Duplicates
            {{duplicate_candidates}}

            ## Drift reports
            {{recent_drift_reports}}

            ## Analysis reports
            {{recent_analysis_reports}}

            (do not edit any task prompt)
            """;
        var rendered = svc.BuildPrompt(scope, template);

        Assert.Contains("agent-taskboard", rendered);
        Assert.Contains("2026-05-05T12:00:00Z", rendered);
        Assert.Contains("ROADMAP.md", rendered);
        Assert.Contains("2-ready/drift-report-schema-and-scoring", rendered);
        Assert.Contains("1-preparation/drift-report-schema-scoring", rendered);
        Assert.Contains("6-completed/shipped-task", rendered);
        // Duplicate heuristic surfaces in the rendered prompt.
        Assert.Contains("drift-report-schema-and-scoring", rendered);
        Assert.Contains("token overlap", rendered);
        // Hard-constraint wording from the template is preserved verbatim.
        Assert.Contains("do not edit any task prompt", rendered);
        Assert.DoesNotContain("{{", rendered);
    }

    // ------------------------------------------------------------------
    // TryParseAgentResponse: Structured / Unstructured / MalformedJson
    // ------------------------------------------------------------------

    [Fact]
    public void TryParseAgentResponse_StructuredReplyExposesScoreBandDimensionsAndFollowUps()
    {
        const string raw = """
            # Spec / Task / Job Drift

            Watch: two duplicates and one thin prompt found.

            ```json
            {
              "verdict": "Watch: two duplicates and one thin prompt.",
              "scoreBand": "Watch",
              "overallScore": 68,
              "dimensions": [
                {
                  "type": "TaskJob",
                  "score": 60,
                  "severity": "Warn",
                  "confidence": 0.8,
                  "sourceCoverage": 0.7,
                  "status": "New",
                  "summary": "Two queued tasks duplicate prior shipped work.",
                  "evidenceRefs": ["agent-taskboard/2-ready/drift-report-schema-and-scoring"],
                  "recommendedActions": ["Merge the two drift-report-schema tasks"]
                },
                {
                  "type": "Spec",
                  "score": 75,
                  "severity": "Info",
                  "confidence": 0.6,
                  "sourceCoverage": 0.6,
                  "status": "New",
                  "summary": "ROADMAP still matches the active queue.",
                  "evidenceRefs": ["ROADMAP.md"],
                  "recommendedActions": []
                }
              ],
              "followUpTaskSuggestions": [
                {
                  "title": "Merge duplicate drift-report-schema tasks",
                  "summary": "Two queued tasks describe the same drift schema work; consolidate into one.",
                  "priority": "Normal",
                  "relatedDimension": "TaskJob"
                }
              ]
            }
            ```
            """;

        var svc = new SpecTaskDriftAnalysisService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(SpecTaskJobDriftParseStatus.Structured, parse.Status);
        Assert.Equal(DriftScoreBand.Watch, parse.ScoreBand);
        Assert.Equal(68, parse.OverallScore);
        Assert.Null(parse.ParseError);
        Assert.NotNull(parse.Dimensions);
        Assert.Equal(2, parse.Dimensions!.Count);

        var taskJob = parse.Dimensions!.Single(d => d.Type == DriftDimensionType.TaskJob);
        Assert.Equal(DriftSeverity.Warn, taskJob.Severity);
        Assert.Single(taskJob.RecommendedActions);

        Assert.Single(parse.FollowUps!);
        Assert.Equal(DriftFollowUpPriority.Normal, parse.FollowUps![0].Priority);
        Assert.Equal(DriftDimensionType.TaskJob, parse.FollowUps![0].RelatedDimension);
    }

    [Fact]
    public void TryParseAgentResponse_NoFencedJsonBlockProducesUnstructuredFallbackKeepingMarkdown()
    {
        const string raw = """
            # Spec / Task / Job Drift

            Verdict: drifting.

            I forgot the JSON sidecar.
            """;

        var svc = new SpecTaskDriftAnalysisService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(SpecTaskJobDriftParseStatus.Unstructured, parse.Status);
        Assert.Null(parse.ParseError);
        Assert.Equal("Spec / Task / Job Drift", parse.Summary);
        Assert.Equal(DriftScoreBand.Unknown, parse.ScoreBand);
        Assert.Null(parse.Dimensions);
        Assert.Null(parse.FollowUps);
    }

    [Fact]
    public void TryParseAgentResponse_MalformedJsonSidecarSurfacesParseErrorWithoutHidingMarkdown()
    {
        const string raw = """
            # Spec / Task / Job Drift

            ```json
            { "verdict": "drifting", "scoreBand": "Watch", "overallScore": 70,
            ```
            """;

        var svc = new SpecTaskDriftAnalysisService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(SpecTaskJobDriftParseStatus.MalformedJson, parse.Status);
        Assert.False(string.IsNullOrWhiteSpace(parse.ParseError));
        Assert.Contains("JSON sidecar failed", parse.ParseError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Spec / Task / Job Drift", parse.Summary);
        Assert.Equal(DriftScoreBand.Unknown, parse.ScoreBand);
    }

    [Fact]
    public void TryParseAgentResponse_EmptyInputProducesUnstructuredWithoutCrashing()
    {
        var svc = new SpecTaskDriftAnalysisService();
        var parse = svc.TryParseAgentResponse(null);
        Assert.Equal(SpecTaskJobDriftParseStatus.Unstructured, parse.Status);

        parse = svc.TryParseAgentResponse("");
        Assert.Equal(SpecTaskJobDriftParseStatus.Unstructured, parse.Status);
    }

    // ------------------------------------------------------------------
    // BuildReport: empty / no-drift output and parse-failure passthrough
    // ------------------------------------------------------------------

    [Fact]
    public void BuildReport_StructuredHealthyVerdictWithoutFindings_StillProducesSchemaValidReport()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "ROADMAP.md"), "# R\n", Encoding.UTF8);
        WriteJob("2-ready", "task-a", "Task A");

        var svc = new SpecTaskDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);
        const string raw = """
            # Spec / Task / Job Drift

            ```json
            {
              "verdict": "Healthy: queued tasks align with ROADMAP and have full context.",
              "scoreBand": "Healthy",
              "overallScore": 92
            }
            ```
            """;
        var parse = svc.TryParseAgentResponse(raw);
        Assert.Equal(SpecTaskJobDriftParseStatus.Structured, parse.Status);

        var report = svc.BuildReport(
            scope: scope,
            parse: parse,
            reportId: "01HX0000000000000000000ST1",
            createdAt: new DateTime(2026, 5, 5, 13, 0, 0, DateTimeKind.Utc));

        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
        Assert.Equal(DriftScoreBand.Healthy, report.ScoreBand);
        Assert.Equal(92, report.OverallScore);
        // Synthetic dimension keeps the record schema-valid (dimensions: minItems=1).
        Assert.Single(report.Dimensions);
        Assert.Equal(DriftDimensionType.TaskJob, report.Dimensions[0].Type);
        Assert.NotEmpty(report.Dimensions[0].EvidenceRefs);
        Assert.Empty(report.FollowUpTaskSuggestions);
    }

    [Fact]
    public void BuildReport_UnstructuredParse_EmitsEvidenceOnlyDimensionWithUnknownBandAndStillValidates()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "ROADMAP.md"), "# R\n", Encoding.UTF8);

        var svc = new SpecTaskDriftAnalysisService();
        var scope = svc.SelectScope("agent-taskboard", _projectRoot, _repoRoot);
        var parse = svc.TryParseAgentResponse(null);

        var report = svc.BuildReport(
            scope: scope,
            parse: parse,
            reportId: "01HX0000000000000000000ST2",
            createdAt: DateTime.UtcNow);

        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
        Assert.Equal(DriftScoreBand.Unknown, report.ScoreBand);
        Assert.Equal(0, report.OverallScore);
        Assert.Single(report.Dimensions);
        Assert.Equal(DriftDimensionType.TaskJob, report.Dimensions[0].Type);
        Assert.Contains("evidence-only", report.Dimensions[0].Summary, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(report.Dimensions[0].EvidenceRefs);
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private void WriteJob(
        string lane,
        string jobId,
        string title,
        string? promptBody = null,
        bool withStatus = false,
        bool withLogs = false)
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
        if (promptBody is not null)
            File.WriteAllText(Path.Combine(dir, "prompt.md"), promptBody, Encoding.UTF8);
        if (withStatus)
            File.WriteAllText(Path.Combine(dir, "status.md"), "# status\n", Encoding.UTF8);
        if (withLogs)
            Directory.CreateDirectory(Path.Combine(dir, "logs"));
    }
}
