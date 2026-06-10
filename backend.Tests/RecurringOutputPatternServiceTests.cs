using System.Text;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the contract of <see cref="RecurringOutputPatternService"/>: scope
/// extraction reads job evidence from active + recently-completed lanes and
/// groups repeated patterns; the rendered prompt carries the load-bearing
/// scope sections; the JSON parse fallback distinguishes Structured /
/// Unstructured / MalformedJson without ever hiding the Markdown body; and a
/// no-finding window produces a clean, valid report.
/// </summary>
public class RecurringOutputPatternServiceTests : IDisposable
{
    private readonly string _projectRoot;

    public RecurringOutputPatternServiceTests()
    {
        var stem = "recurring-pattern-tests-" + Guid.NewGuid().ToString("N");
        _projectRoot = Path.Combine(Path.GetTempPath(), stem, "project");
        Directory.CreateDirectory(_projectRoot);
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
    // Pattern grouping
    // ------------------------------------------------------------------

    [Fact]
    public void SelectScope_GroupsBlockedReasonsAcrossJobsAfterNormalisation()
    {
        // Three jobs, all blocked on a normalised "missing playwright spec"
        // reason that differs only by casing / extra whitespace / an absolute
        // path. The producer must group them as one finding.
        WriteJobWithLog("3-progress", "alpha", state: "3-progress",
            log: "[[TASK_BLOCKED:Missing Playwright spec for new flow]]");
        WriteJobWithLog("3-progress", "bravo", state: "3-progress",
            log: "thinking...\n[[TASK_BLOCKED: missing playwright spec for new flow]]\n");
        WriteJobWithLog("4-auto-review", "charlie", state: "4-auto-review",
            log: "[[TASK_BLOCKED:missing playwright spec for new flow at C:\\repo\\foo\\bar.ts]]");

        // A single unrelated blocked job should NOT form a group on its own
        // (minimum count is 2).
        WriteJobWithLog("3-progress", "delta", state: "3-progress",
            log: "[[TASK_BLOCKED:db migration unverified]]");

        var svc = new RecurringOutputPatternService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            windowFrom: DateTime.MinValue,
            windowTo: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(4, scope.Jobs.Count);
        Assert.Single(scope.Groups);
        var group = scope.Groups[0];
        Assert.Equal("blocked-reason", group.Kind);
        Assert.Equal(3, group.Members.Count);
        Assert.Contains(group.Members, m => m.JobId == "alpha");
        Assert.Contains(group.Members, m => m.JobId == "bravo");
        Assert.Contains(group.Members, m => m.JobId == "charlie");
    }

    [Fact]
    public void SelectScope_GroupsRepeatedRetriesAndMissingStatusEvidence()
    {
        // Two jobs each started >= 3 times (recovery / session-loss signal)
        // and two completed jobs missing status.md.
        var startedMarker = "[taskboard] Started run #";
        WriteJobWithLog("3-progress", "retry-a", state: "3-progress",
            log: $"{startedMarker}1\nfoo\n{startedMarker}2\nbar\n{startedMarker}3\n[[TASK_DONE]]");
        WriteJobWithLog("3-progress", "retry-b", state: "3-progress",
            log: $"{startedMarker}1\n{startedMarker}2\n{startedMarker}3\n{startedMarker}4\nrunning long\n");

        // Two completed jobs missing status.md.
        WriteJob(lane: "6-completed", id: "ship-x", title: "Ship X", writeStatus: false, log: "[[TASK_DONE]]");
        WriteJob(lane: "6-completed", id: "ship-y", title: "Ship Y", writeStatus: false, log: "[[TASK_DONE]]");

        var svc = new RecurringOutputPatternService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            windowFrom: DateTime.MinValue,
            windowTo: DateTime.UtcNow);

        var kinds = scope.Groups.Select(g => g.Kind).ToArray();
        Assert.Contains("repeated-retries", kinds);
        Assert.Contains("missing-status", kinds);
        var retries = scope.Groups.First(g => g.Kind == "repeated-retries");
        Assert.Equal(2, retries.Members.Count);
        var missing = scope.Groups.First(g => g.Kind == "missing-status");
        Assert.Equal(2, missing.Members.Count);
    }

    [Fact]
    public void SelectScope_DoesNotSurfaceSingletonPatternsAsRecurring()
    {
        // One blocked, one needs-input, one noop - three different shapes,
        // none repeated. With minimumPatternCount = 2 the producer must
        // emit zero groups.
        WriteJobWithLog("3-progress", "alpha", state: "3-progress",
            log: "[[TASK_BLOCKED:something]]");
        WriteJobWithLog("3-progress", "bravo", state: "3-progress",
            log: "[[TASK_NEEDS_INPUT:please clarify]]");
        WriteJobWithLog("3-progress", "charlie", state: "3-progress",
            log: "[[TASK_NOOP]]");

        var svc = new RecurringOutputPatternService();
        var scope = svc.SelectScope(
            "agent-taskboard", _projectRoot,
            DateTime.MinValue, DateTime.UtcNow);

        Assert.Empty(scope.Groups);
        Assert.Equal(3, scope.Jobs.Count);
    }

    [Fact]
    public void SelectScope_OnlyWalksTheInspectedLanesAndIgnoresOthers()
    {
        WriteJobWithLog("1-preparation", "draft", "1-preparation",
            log: "[[TASK_BLOCKED:foo]]");
        WriteJobWithLog("3-progress", "live", "3-progress",
            log: "[[TASK_BLOCKED:foo]]");
        WriteJobWithLog("7-archive", "old", "7-archive",
            log: "[[TASK_BLOCKED:foo]]");

        var svc = new RecurringOutputPatternService();
        var scope = svc.SelectScope(
            "agent-taskboard", _projectRoot,
            DateTime.MinValue, DateTime.UtcNow);

        Assert.Single(scope.Jobs);
        Assert.Equal("live", scope.Jobs[0].JobId);
    }

    // ------------------------------------------------------------------
    // BuildPrompt
    // ------------------------------------------------------------------

    [Fact]
    public void BuildPrompt_RendersAllPlaceholdersAndShowsDetectedGroupsTable()
    {
        WriteJobWithLog("3-progress", "alpha", "3-progress",
            log: "[[TASK_BLOCKED:missing playwright spec]]");
        WriteJobWithLog("3-progress", "bravo", "3-progress",
            log: "[[TASK_BLOCKED:missing playwright spec]]");

        var svc = new RecurringOutputPatternService();
        var scope = svc.SelectScope(
            project: "agent-taskboard",
            projectRoot: _projectRoot,
            windowFrom: DateTime.MinValue,
            windowTo: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));

        const string template = """
            project={{project}}
            captured_at={{captured_at}}
            job_count={{job_count}}
            findings={{has_findings_flag}}

            ## groups
            {{pattern_groups}}

            ## jobs
            {{job_evidence}}

            ## recent
            {{recent_reports}}

            (do not silently edit any source file)
            """;
        var rendered = svc.BuildPrompt(scope, template);

        Assert.Contains("project=agent-taskboard", rendered);
        Assert.Contains("2026-05-05T12:00:00Z", rendered);
        Assert.Contains("job_count=2", rendered);
        Assert.Contains("findings=yes", rendered);
        Assert.Contains("blocked-reason", rendered);
        Assert.Contains("`alpha`", rendered);
        Assert.Contains("`bravo`", rendered);
        Assert.Contains("do not silently edit any source file", rendered);
        Assert.DoesNotContain("{{", rendered);
    }

    [Fact]
    public void BuildPrompt_NoFindingsRendersExplicitEmptyMarkerNotAFakeRow()
    {
        WriteJobWithLog("3-progress", "alpha", "3-progress",
            log: "[[TASK_DONE]]");

        var svc = new RecurringOutputPatternService();
        var scope = svc.SelectScope(
            "agent-taskboard", _projectRoot,
            DateTime.MinValue, DateTime.UtcNow);

        var rendered = svc.BuildPrompt(scope,
            "findings={{has_findings_flag}};groups={{pattern_groups}}");
        Assert.Contains("findings=no", rendered);
        Assert.Contains("(no recurring patterns detected", rendered);
    }

    // ------------------------------------------------------------------
    // TryParseAgentResponse
    // ------------------------------------------------------------------

    [Fact]
    public void TryParseAgentResponse_StructuredResponseExposesVerdictSeverityFindingsFollowUpsConfidence()
    {
        const string raw = """
            # Recurring output pattern review

            Three blocked-on-missing-playwright-spec hits in the last week.

            ```json
            {
              "verdict": "Three blocked-on-missing-playwright-spec hits in the last week.",
              "severity": "High",
              "confidence": 0.85,
              "findings": [
                {
                  "topic": "blocked-reason",
                  "severity": "High",
                  "message": "Three jobs blocked on missing Playwright spec; AGENTS.md mentions specs but no example for this flow.",
                  "evidenceRefs": ["agent-taskboard/3-progress/alpha", "agent-taskboard/3-progress/bravo"]
                }
              ],
              "followUpTaskSuggestions": [
                {
                  "title": "Document Playwright spec authoring expectations in AGENTS.md",
                  "summary": "Three recent jobs blocked on the same missing-spec reason. AGENTS.md has the rule but no worked example.",
                  "priority": "High",
                  "relatedTopic": "DocsDrift",
                  "targetState": "2-ready"
                }
              ]
            }
            ```
            """;

        var svc = new RecurringOutputPatternService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(AnalysisReportParseStatus.Structured, parse.Status);
        Assert.Equal(AnalysisReportSeverity.High, parse.Severity);
        Assert.Equal(0.85, parse.Confidence);
        Assert.Single(parse.Findings!);
        Assert.Equal("blocked-reason", parse.Findings![0].Topic);
        Assert.Single(parse.FollowUps!);
        Assert.Equal(AnalysisReportFollowUpPriority.High, parse.FollowUps![0].Priority);
        Assert.Equal(AnalysisReportFollowUpRelatedTopic.DocsDrift, parse.FollowUps![0].RelatedTopic);
        // Constraint: agent-supplied targetState=2-ready is coerced to
        // 1-preparation. Producer must not bypass user review.
        Assert.Equal(AnalysisReportFollowUpTargetStates.OnePreparation, parse.FollowUps![0].TargetState);
        Assert.Null(parse.ParseError);
    }

    [Fact]
    public void TryParseAgentResponse_NoFencedJsonBlockProducesUnstructuredFallbackKeepingMarkdown()
    {
        const string raw = """
            # Recurring output pattern review

            One pattern: blocked-on-spec.

            The agent forgot the JSON sidecar.
            """;

        var svc = new RecurringOutputPatternService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(AnalysisReportParseStatus.Unstructured, parse.Status);
        Assert.Null(parse.ParseError);
        Assert.Equal("Recurring output pattern review", parse.Summary);
        Assert.Null(parse.Findings);
        Assert.Null(parse.FollowUps);
    }

    [Fact]
    public void TryParseAgentResponse_MalformedJsonSidecarSurfacesParseErrorWithoutHidingMarkdown()
    {
        const string raw = """
            # Recurring output pattern review

            ```json
            { "verdict": "drifting", "severity": "High",
            ```
            """;

        var svc = new RecurringOutputPatternService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(AnalysisReportParseStatus.MalformedJson, parse.Status);
        Assert.False(string.IsNullOrWhiteSpace(parse.ParseError));
        Assert.Equal("Recurring output pattern review", parse.Summary);
    }

    [Fact]
    public void TryParseAgentResponse_BadSeveritySurfacesAsMalformedJsonNotStructured()
    {
        const string raw = """
            # Recurring output pattern review
            ```json
            { "verdict": "x", "severity": "Catastrophic" }
            ```
            """;

        var svc = new RecurringOutputPatternService();
        var parse = svc.TryParseAgentResponse(raw);

        Assert.Equal(AnalysisReportParseStatus.MalformedJson, parse.Status);
        Assert.Contains("severity", parse.ParseError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseAgentResponse_EmptyInputProducesUnstructuredWithoutCrashing()
    {
        var svc = new RecurringOutputPatternService();
        var parse = svc.TryParseAgentResponse(null);
        Assert.Equal(AnalysisReportParseStatus.Unstructured, parse.Status);

        parse = svc.TryParseAgentResponse("");
        Assert.Equal(AnalysisReportParseStatus.Unstructured, parse.Status);
    }

    // ------------------------------------------------------------------
    // BuildReport
    // ------------------------------------------------------------------

    [Fact]
    public void BuildReport_PassesValidation_AndCarriesJobAndLogReferencesByStableId()
    {
        WriteJobWithLog("3-progress", "alpha", "3-progress",
            log: "[[TASK_BLOCKED:missing playwright spec]]");
        WriteJobWithLog("3-progress", "bravo", "3-progress",
            log: "[[TASK_BLOCKED:missing playwright spec]]");

        var svc = new RecurringOutputPatternService();
        var scope = svc.SelectScope(
            "agent-taskboard", _projectRoot,
            DateTime.MinValue, new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));
        var parse = svc.TryParseAgentResponse(
            "# Verdict\n\n```json\n{ \"verdict\": \"recurring blocked\", \"severity\": \"Warn\" }\n```\n");

        var report = svc.BuildReport(
            scope: scope,
            parse: parse,
            reportId: "01HX0000000000000000000RPL",
            createdAt: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));

        Assert.True(AnalysisReportValidator.TryValidate(report, out var error), error);
        Assert.Equal("recurring-output-pattern", report.Topic);
        Assert.Equal(AnalysisReportScopeKind.Project, report.Scope.Kind);
        Assert.Equal("agent-taskboard", report.Scope.Project);

        // Evidence-link retention: every inspected job + its cli-output.log is
        // cited by stable id.
        Assert.Contains(report.References, r =>
            r.Kind == AnalysisReportReferenceKind.Job
            && r.Ref == "agent-taskboard/3-progress/alpha");
        Assert.Contains(report.References, r =>
            r.Kind == AnalysisReportReferenceKind.LogSlice
            && r.Ref.EndsWith("/alpha/logs/cli-output.log", StringComparison.Ordinal));

        // Pattern-kind tag is exposed so consumers can filter.
        Assert.Contains(report.Tags!, t => t == "pattern:blocked-reason");
        Assert.Contains(report.Tags!, t => t == "recurring-output-pattern");
    }

    [Fact]
    public void BuildReport_NoFindingWindowProducesValidNoFindingReportWithoutFollowUps()
    {
        // Single done job - nothing to group, nothing to recommend.
        WriteJobWithLog("3-progress", "solo", "3-progress",
            log: "[[TASK_DONE]]");

        var svc = new RecurringOutputPatternService();
        var scope = svc.SelectScope(
            "agent-taskboard", _projectRoot,
            DateTime.MinValue, DateTime.UtcNow);
        // Even if the agent emits a fake follow-up, the no-finding path drops
        // it so the report stays honest.
        var parse = svc.TryParseAgentResponse(
            "# verdict\n\n```json\n{ \"verdict\": \"making things up\", \"severity\": \"High\", "
            + "\"followUpTaskSuggestions\": [ { \"title\": \"x\", \"summary\": \"y\" } ] }\n```\n");

        var report = svc.BuildReport(scope, parse,
            reportId: "01HX0000000000000000000RPM",
            createdAt: DateTime.UtcNow);

        Assert.True(AnalysisReportValidator.TryValidate(report, out var error), error);
        Assert.Equal(AnalysisReportSeverity.Info, report.Severity);
        Assert.Empty(report.FollowUpTaskSuggestions);
        Assert.NotNull(report.Findings);
        Assert.Empty(report.Findings!);
        Assert.Contains(report.Tags!, t => t == "no-finding");
        Assert.Contains("No recurring pattern detected", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormaliseReason_CollapsesPathsHashesAndCasingSoSimilarReasonsGroup()
    {
        Assert.Equal(
            RecurringOutputPatternService.NormaliseReason("MISSING playwright spec"),
            RecurringOutputPatternService.NormaliseReason("missing  playwright   spec"));
        Assert.Equal(
            RecurringOutputPatternService.NormaliseReason("foo at C:\\repo\\bar.ts"),
            RecurringOutputPatternService.NormaliseReason("foo at C:\\repo\\baz.ts"));
        Assert.Equal(
            RecurringOutputPatternService.NormaliseReason("commit abcdef1234567 missing"),
            RecurringOutputPatternService.NormaliseReason("commit ff00112233445 missing"));
        Assert.Equal("(no reason)", RecurringOutputPatternService.NormaliseReason(null));
        Assert.Equal("(no reason)", RecurringOutputPatternService.NormaliseReason("   "));
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private void WriteJobWithLog(string lane, string id, string state, string log)
    {
        WriteJob(lane, id, title: id, writeStatus: true, log: log, laneState: state);
    }

    private void WriteJob(string lane, string id, string title, bool writeStatus, string log, string? laneState = null)
    {
        var dir = Path.Combine(_projectRoot, lane, id);
        Directory.CreateDirectory(dir);
        var stateValue = laneState ?? lane;
        var json =
            "{\n" +
            $"  \"id\": \"{id}\",\n" +
            $"  \"title\": \"{title}\",\n" +
            $"  \"state\": \"{stateValue}\",\n" +
            "  \"agent\": \"claude\",\n" +
            "  \"cliType\": \"claude\"\n" +
            "}\n";
        File.WriteAllText(Path.Combine(dir, "task.json"), json, Encoding.UTF8);
        File.WriteAllText(Path.Combine(dir, "prompt.md"), $"# {title}\n", Encoding.UTF8);
        if (writeStatus)
            File.WriteAllText(Path.Combine(dir, "status.md"), $"# {title} status\n", Encoding.UTF8);

        var logsDir = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logsDir);
        File.WriteAllText(Path.Combine(logsDir, "cli-output.log"), log, Encoding.UTF8);
    }
}
