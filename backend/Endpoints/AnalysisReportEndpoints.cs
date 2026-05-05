using System.Text;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Analysis;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Read + manual-trigger surface for the Analysis Reports project view
/// (ROADMAP "Analysis Reports and Meta-Actions"). Backed by
/// <see cref="AnalysisReportStore"/>: disk is the source of truth, the store
/// keeps an in-memory projection per (workspace, project) pair so list polls
/// from the UI never block on a full scan.
/// </summary>
/// <remarks>
/// <para>
/// The store carries reports written by every producer kind
/// (manual buttons, scheduled cadences, the orchestrator meta-cycle, supporting
/// agents, the Layer 3 external monitor). This endpoint group is the project
/// page's read entry into that pile and the manual-trigger button's write entry.
/// </para>
/// <para>
/// Manual triggers in this first cut produce a <em>placeholder</em> structured
/// report so the UI flow (list → detail → drill-down) is exercisable end to end.
/// The actual inspection logic for each topic (queue health, docs drift, roadmap
/// alignment, stale jobs, token-spend review, QA status) is owned by future
/// producer code paths; once those land, the manual trigger will run them
/// inline or enqueue a supporting-agent job. Until then the report serves as
/// the durable "the user asked for an inspection" record.
/// </para>
/// </remarks>
public static class AnalysisReportEndpoints
{
    public static void MapAnalysisReportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/analysis");

        group.MapGet("/{project}/reports", (
            string project,
            IConfiguration config,
            AnalysisReportStore store,
            string? trigger,
            string? severity,
            string? topic,
            int? limit,
            bool? refresh) =>
        {
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "project required" });
            var workspace = config["TaskRepository"];
            if (string.IsNullOrWhiteSpace(workspace))
                return Results.Ok(new AnalysisReportListResponse(Array.Empty<AnalysisReport>()));
            // Optional out-of-band reload: discard the cached projection so an
            // external writer (the Layer 3 monitor, a manual edit on disk, an
            // E2E spec planting a fixture file) is visible without a backend
            // restart. Read paths use the projection so this stays cheap.
            if (refresh == true)
                store.InvalidateProjection(workspace!, project);
            var snap = store.Snapshot(workspace!, project);
            IEnumerable<AnalysisReport> q = snap;
            if (!string.IsNullOrWhiteSpace(trigger) && Enum.TryParse<AnalysisReportTrigger>(trigger, ignoreCase: true, out var t))
                q = q.Where(r => r.Trigger == t);
            if (!string.IsNullOrWhiteSpace(severity) && Enum.TryParse<AnalysisReportSeverity>(severity, ignoreCase: true, out var s))
                q = q.Where(r => r.Severity == s);
            if (!string.IsNullOrWhiteSpace(topic))
                q = q.Where(r => string.Equals(r.Topic, topic, StringComparison.OrdinalIgnoreCase));
            // Newest first; cap at a sane page size.
            var cap = Math.Clamp(limit ?? 100, 1, 500);
            var ordered = q.OrderByDescending(r => r.CreatedAt).Take(cap).ToArray();
            return Results.Ok(new AnalysisReportListResponse(ordered));
        });

        group.MapGet("/{project}/reports/{reportId}", (
            string project,
            string reportId,
            IConfiguration config,
            AnalysisReportStore store) =>
        {
            if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(reportId))
                return Results.BadRequest(new { error = "project and reportId required" });
            var workspace = config["TaskRepository"];
            if (string.IsNullOrWhiteSpace(workspace)) return Results.NotFound();
            var report = store.GetById(workspace!, project, reportId);
            if (report is null) return Results.NotFound();
            // The Markdown is the durable artifact; surface it next to the
            // structured record so the UI drill-down does not need a second
            // round-trip. Falls back to null when the file is missing on disk
            // (e.g. partial write); the UI shows the structured summary.
            var markdown = store.ReadMarkdown(workspace!, project, reportId);
            return Results.Ok(new AnalysisReportDetailResponse(report, markdown));
        });

        group.MapPost("/{project}/reports", async (
            string project,
            ManualAnalysisReportRequest body,
            IConfiguration config,
            AnalysisReportStore store,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "project required" });
            if (string.IsNullOrWhiteSpace(body.Topic))
                return Results.BadRequest(new { error = "topic required" });
            var workspace = config["TaskRepository"];
            if (string.IsNullOrWhiteSpace(workspace))
                return Results.BadRequest(new { error = "TaskRepository not configured" });

            // Placeholder report: the UI flow is fully exercised, but the
            // narrative explicitly names this as a manual-trigger placeholder
            // so a reader does not mistake it for a real inspection. Real
            // producers replace `markdownBody` and the structured fields
            // when they land.
            var topic = body.Topic.Trim();
            var summary = string.IsNullOrWhiteSpace(body.Summary)
                ? $"Manual {topic} inspection placeholder. Real producer not yet wired; record exists so the surface is reviewable end to end."
                : body.Summary.Trim();
            var reportId = "01" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + Guid.NewGuid().ToString("N").Substring(0, 8);
            var report = new AnalysisReport(
                ReportId: reportId,
                CreatedAt: DateTime.UtcNow,
                Scope: new AnalysisReportScope(AnalysisReportScopeKind.Project, Project: project),
                Producer: new AnalysisReportProducer(AnalysisReportProducerKind.Manual, ParticipantId: "user", Agent: null),
                Trigger: AnalysisReportTrigger.Manual,
                Topic: topic,
                Summary: summary,
                Severity: AnalysisReportSeverity.Info,
                ParseStatus: AnalysisReportParseStatus.Structured,
                References: Array.Empty<AnalysisReportReference>(),
                FollowUpTaskSuggestions: Array.Empty<AnalysisReportFollowUpTaskSuggestion>(),
                Tags: new[] { "manual", "placeholder" });

            var markdownBody =
                $"# {topic}\n\n" +
                $"**Verdict:** info — placeholder run, real producer not wired yet.\n\n" +
                $"{summary}\n\n" +
                $"## Trigger\n\nManual button on the project Analysis Reports surface.\n\n" +
                $"## Follow-up\n\nNone. When the topic-specific producer ships it will replace this body and add suggestions.\n";

            await store.AppendAsync(workspace!, project, report, markdownBody, ct).ConfigureAwait(false);
            return Results.Ok(new AnalysisReportDetailResponse(report, markdownBody));
        });

        // -----------------------------------------------------------------
        // Roadmap Alignment Review (named producer behind "are we on track?").
        // GET /prompt returns the assembled prompt + scope summary so an
        // operator (or future inline runner) can hand the prompt to a CLI
        // agent. POST runs the action: without `agentResponse` it produces
        // an Unstructured "evidence + prompt" report; with `agentResponse` it
        // parses the agent's reply and produces the typed report.
        // -----------------------------------------------------------------

        group.MapGet("/{project}/actions/roadmap-alignment/prompt", (
            string project,
            IConfiguration config,
            AnalysisReportStore store,
            RuntimePromptService prompts,
            RoadmapAlignmentReviewService action) =>
        {
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "project required" });
            var workspace = config["TaskRepository"];
            if (string.IsNullOrWhiteSpace(workspace))
                return Results.BadRequest(new { error = "TaskRepository not configured" });

            var projectRoot = Path.Combine(workspace!, "projects", project);
            if (!Directory.Exists(projectRoot))
                return Results.NotFound(new { error = $"project root not found: {projectRoot}" });

            var repoRoot = ResolveRepoRoot();
            var scope = action.SelectScope(project, projectRoot, repoRoot, store, workspace);
            var template = prompts.Render(
                "roadmap-alignment-review.md",
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var renderedPrompt = action.BuildPrompt(scope, template);
            return Results.Ok(new RoadmapAlignmentPromptResponse(
                Project: project,
                CapturedAt: scope.CapturedAt,
                QueueIsClean: scope.QueueIsClean,
                JobsByLane: scope.JobsByLane.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<string>)kv.Value.Select(j => j.JobId).ToArray()),
                StrayLaneFolders: scope.StrayLaneFolders,
                Docs: scope.Docs.Select(d => d.Path).ToArray(),
                RecentReports: scope.RecentReports.Select(r => r.ReportId).ToArray(),
                Prompt: renderedPrompt));
        });

        group.MapPost("/{project}/actions/roadmap-alignment", async (
            string project,
            RoadmapAlignmentRunRequest? body,
            IConfiguration config,
            AnalysisReportStore store,
            RuntimePromptService prompts,
            RoadmapAlignmentReviewService action,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "project required" });
            var workspace = config["TaskRepository"];
            if (string.IsNullOrWhiteSpace(workspace))
                return Results.BadRequest(new { error = "TaskRepository not configured" });

            var projectRoot = Path.Combine(workspace!, "projects", project);
            if (!Directory.Exists(projectRoot))
                return Results.NotFound(new { error = $"project root not found: {projectRoot}" });

            var repoRoot = ResolveRepoRoot();
            var scope = action.SelectScope(project, projectRoot, repoRoot, store, workspace);
            var template = prompts.Render(
                "roadmap-alignment-review.md",
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var renderedPrompt = action.BuildPrompt(scope, template);

            var agentResponse = body?.AgentResponse;
            string markdown;
            RoadmapAlignmentParseResult parse;
            if (string.IsNullOrWhiteSpace(agentResponse))
            {
                // No agent run was supplied. Persist the assembled scope and
                // the rendered prompt as an Unstructured report so the user
                // has a durable artifact recording that the inspection was
                // requested. The Markdown body embeds the prompt itself so an
                // operator can copy it into a CLI session.
                parse = new RoadmapAlignmentParseResult(
                    Status: AnalysisReportParseStatus.Unstructured,
                    Severity: AnalysisReportSeverity.Info,
                    Summary: scope.QueueIsClean
                        ? "Evidence assembled; no agent narrative supplied. Run the embedded prompt against a CLI to produce a verdict."
                        : "Evidence assembled; queue has stray lane folders, narrative deferred to agent run.",
                    Findings: null,
                    FollowUps: new[]
                    {
                        new AnalysisReportFollowUpTaskSuggestion(
                            Title: "Run roadmap alignment review against a CLI agent",
                            Summary: "Hand the embedded prompt to Claude / Codex / Copilot / Gemini and POST the reply back to /api/analysis/{project}/actions/roadmap-alignment to produce the structured verdict.",
                            Priority: AnalysisReportFollowUpPriority.Normal,
                            RelatedTopic: AnalysisReportFollowUpRelatedTopic.RoadmapAlignment,
                            TargetState: AnalysisReportFollowUpTargetStates.OnePreparation),
                    },
                    PriorityOrder: null,
                    ParseError: null);
                markdown = BuildEvidenceOnlyMarkdown(scope, renderedPrompt);
            }
            else
            {
                parse = action.TryParseAgentResponse(agentResponse);
                markdown = agentResponse;
            }

            var reportId = "01" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + Guid.NewGuid().ToString("N").Substring(0, 8);
            var report = action.BuildReport(
                scope: scope,
                parse: parse,
                reportId: reportId,
                createdAt: DateTime.UtcNow,
                producerKind: AnalysisReportProducerKind.Manual,
                trigger: AnalysisReportTrigger.Manual,
                participantId: "user");

            await store.AppendAsync(workspace!, project, report, markdown, ct).ConfigureAwait(false);
            return Results.Ok(new AnalysisReportDetailResponse(report, markdown));
        });

        group.MapGet("/{project}/schedule", (
            string project,
            ProjectSettingsService settings) =>
        {
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "project required" });
            var s = settings.Get(project);
            return Results.Ok(s.AnalysisSchedules ?? new Dictionary<string, string>());
        });

        group.MapPut("/{project}/schedule", (
            string project,
            AnalysisScheduleRequest req,
            ProjectSettingsService settings) =>
        {
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "project required" });
            if (string.IsNullOrWhiteSpace(req.Topic))
                return Results.BadRequest(new { error = "topic required" });
            if (!IsValidCadence(req.Cadence))
                return Results.BadRequest(new { error = "cadence must be one of: disabled, fewHours, daily, manualOnly" });
            settings.SetAnalysisSchedule(project, req.Topic.Trim(), req.Cadence);
            var s = settings.Get(project);
            return Results.Ok(s.AnalysisSchedules ?? new Dictionary<string, string>());
        });
    }

    private static bool IsValidCadence(string? cadence) => cadence switch
    {
        "disabled" or "fewHours" or "daily" or "manualOnly" => true,
        _ => false,
    };

    /// <summary>
    /// Resolves the source repository root by walking up from
    /// <see cref="AppContext.BaseDirectory"/> until <c>AGENTS.md</c> is found.
    /// Falls back to the current working directory when no marker exists; the
    /// roadmap-alignment action degrades gracefully (the doc list will be
    /// shorter, the report still records the queue evidence).
    /// </summary>
    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        var cwd = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (cwd is not null)
        {
            if (File.Exists(Path.Combine(cwd.FullName, "AGENTS.md")))
                return cwd.FullName;
            cwd = cwd.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private static string BuildEvidenceOnlyMarkdown(
        RoadmapAlignmentReviewScope scope,
        string renderedPrompt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Roadmap alignment review - evidence only");
        sb.AppendLine();
        sb.Append("**Verdict:** evidence assembled at ")
            .Append(scope.CapturedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))
            .AppendLine("; no agent narrative supplied. Run the embedded prompt against a CLI agent and POST the reply back to produce the structured verdict.");
        sb.AppendLine();
        sb.Append("**Project:** `").Append(scope.Project).AppendLine("`");
        sb.Append("**Queue clean (no stray lane folders):** ").AppendLine(scope.QueueIsClean ? "yes" : "no");
        sb.AppendLine();
        sb.AppendLine("## Queue snapshot");
        sb.AppendLine();
        sb.AppendLine("| Lane | Count |");
        sb.AppendLine("|------|------:|");
        foreach (var lane in RoadmapAlignmentReviewService.InspectedLanes)
        {
            var count = scope.JobsByLane.TryGetValue(lane, out var jobs) ? jobs.Count : 0;
            sb.Append("| `").Append(lane).Append("` | ").Append(count).AppendLine(" |");
        }
        sb.AppendLine();
        if (scope.StrayLaneFolders.Count > 0)
        {
            sb.AppendLine("## Stray lane folders");
            sb.AppendLine();
            foreach (var f in scope.StrayLaneFolders)
                sb.Append("- ").AppendLine(f);
            sb.AppendLine();
        }
        sb.AppendLine("## Embedded prompt");
        sb.AppendLine();
        sb.AppendLine("Copy the block below into a Claude / Codex / Copilot / Gemini session, then POST the reply back to `/api/analysis/{project}/actions/roadmap-alignment` with `agentResponse` set to produce the structured verdict.");
        sb.AppendLine();
        sb.AppendLine("```markdown");
        sb.AppendLine(renderedPrompt);
        sb.AppendLine("```");
        return sb.ToString();
    }
}

/// <summary>List response wrapper so the JSON shape stays additive when more aggregates appear.</summary>
public sealed record AnalysisReportListResponse(IReadOnlyList<AnalysisReport> Reports);

/// <summary>One report plus its Markdown body for the drill-down.</summary>
public sealed record AnalysisReportDetailResponse(AnalysisReport Report, string? Markdown);

/// <summary>Body for <c>POST /api/analysis/{project}/reports</c>.</summary>
public sealed record ManualAnalysisReportRequest(string? Topic, string? Summary);

/// <summary>Body for <c>PUT /api/analysis/{project}/schedule</c>.</summary>
public sealed record AnalysisScheduleRequest(string? Topic, string? Cadence);

/// <summary>
/// Body for <c>POST /api/analysis/{project}/actions/roadmap-alignment</c>.
/// When <see cref="AgentResponse"/> is null or whitespace the endpoint emits
/// an Unstructured "evidence only" report carrying the rendered prompt; when
/// it is set, the endpoint parses the agent's reply (Markdown body plus an
/// optional fenced JSON sidecar) and emits the typed verdict.
/// </summary>
public sealed record RoadmapAlignmentRunRequest(string? AgentResponse);

/// <summary>
/// Response for <c>GET /api/analysis/{project}/actions/roadmap-alignment/prompt</c>.
/// Returns the assembled scope summary and the rendered prompt the operator
/// (or future inline runner) hands to a CLI agent.
/// </summary>
public sealed record RoadmapAlignmentPromptResponse(
    string Project,
    DateTime CapturedAt,
    bool QueueIsClean,
    IReadOnlyDictionary<string, IReadOnlyList<string>> JobsByLane,
    IReadOnlyList<string> StrayLaneFolders,
    IReadOnlyList<string> Docs,
    IReadOnlyList<string> RecentReports,
    string Prompt);
