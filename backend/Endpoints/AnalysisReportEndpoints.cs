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
}

/// <summary>List response wrapper so the JSON shape stays additive when more aggregates appear.</summary>
public sealed record AnalysisReportListResponse(IReadOnlyList<AnalysisReport> Reports);

/// <summary>One report plus its Markdown body for the drill-down.</summary>
public sealed record AnalysisReportDetailResponse(AnalysisReport Report, string? Markdown);

/// <summary>Body for <c>POST /api/analysis/{project}/reports</c>.</summary>
public sealed record ManualAnalysisReportRequest(string? Topic, string? Summary);

/// <summary>Body for <c>PUT /api/analysis/{project}/schedule</c>.</summary>
public sealed record AnalysisScheduleRequest(string? Topic, string? Cadence);
