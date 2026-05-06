using System.Text;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Analysis;
using OrchestratorApi.Services.Drift;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Read + manual-trigger surface for the Drift project view (ROADMAP "Drift
/// Control"). Backed by <see cref="DriftReportStore"/>: disk is the source
/// of truth, the store keeps an in-memory projection per (workspace,
/// project) so list polls from the UI never block on a full scan.
/// </summary>
/// <remarks>
/// Drift is its own project dimension beside Architecture and Analysis
/// Reports (design-principles "Drift is a scored project dimension"). The
/// first manual action wired here is **ADR / Code Drift**, which compares
/// the architecture decisions archive and the architecture documentation
/// against the current source tree. Future producers (Software /
/// Architecture Drift, Spec / Task / Job Drift, Docs / Marketing Drift) plug
/// into the same store; this endpoint group is the project page's read
/// entry into that pile and the per-action manual-trigger entry.
/// </remarks>
public static class DriftReportEndpoints
{
    public static void MapDriftReportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/drift");

        group.MapGet("/{project}/reports", (
            string project,
            IConfiguration config,
            DriftReportStore store,
            string? trigger,
            string? scoreBand,
            int? limit,
            bool? refresh) =>
        {
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "project required" });
            var workspace = config["TaskRepository"];
            if (string.IsNullOrWhiteSpace(workspace))
                return Results.Ok(new DriftReportListResponse(Array.Empty<DriftReport>()));
            if (refresh == true)
                store.InvalidateProjection(workspace!, project);
            var snap = store.Snapshot(workspace!, project);
            IEnumerable<DriftReport> q = snap;
            if (!string.IsNullOrWhiteSpace(trigger) && Enum.TryParse<DriftReportTrigger>(trigger, ignoreCase: true, out var t))
                q = q.Where(r => r.Trigger == t);
            if (!string.IsNullOrWhiteSpace(scoreBand) && Enum.TryParse<DriftScoreBand>(scoreBand, ignoreCase: true, out var b))
                q = q.Where(r => r.ScoreBand == b);
            var cap = Math.Clamp(limit ?? 100, 1, 500);
            var ordered = q.OrderByDescending(r => r.CreatedAt).Take(cap).ToArray();
            return Results.Ok(new DriftReportListResponse(ordered));
        });

        group.MapGet("/{project}/reports/{reportId}", (
            string project,
            string reportId,
            IConfiguration config,
            DriftReportStore store) =>
        {
            if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(reportId))
                return Results.BadRequest(new { error = "project and reportId required" });
            var workspace = config["TaskRepository"];
            if (string.IsNullOrWhiteSpace(workspace)) return Results.NotFound();
            var report = store.GetById(workspace!, project, reportId);
            if (report is null) return Results.NotFound();
            var markdown = store.ReadMarkdown(workspace!, project, reportId);
            return Results.Ok(new DriftReportDetailResponse(report, markdown));
        });

        // -----------------------------------------------------------------
        // ADR / Code Drift action.
        // GET /prompt returns the assembled prompt + scope summary so an
        // operator (or future inline runner) can hand the prompt to a CLI
        // agent. POST runs the action: without `agentResponse` it produces
        // an Unstructured "evidence + prompt" report; with `agentResponse`
        // it parses the agent's reply and produces the typed verdict.
        // -----------------------------------------------------------------

        group.MapGet("/{project}/actions/adr-code-drift/prompt", (
            string project,
            IConfiguration config,
            DriftReportStore driftStore,
            AnalysisReportStore analysisStore,
            RuntimePromptService prompts,
            AdrCodeDriftAnalysisService action) =>
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
            var scope = action.SelectScope(project, projectRoot, repoRoot, driftStore, analysisStore, workspace);
            var template = prompts.Render(
                "adr-code-drift.md",
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var renderedPrompt = action.BuildPrompt(scope, template);
            return Results.Ok(new AdrCodeDriftPromptResponse(
                Project: project,
                CapturedAt: scope.CapturedAt,
                Docs: scope.Docs.Select(d => d.Path).ToArray(),
                SourceTree: scope.SourceTree.Select(d => d.Path).ToArray(),
                ModuleBoundaries: scope.ModuleBoundaries.Select(d => d.Path).ToArray(),
                Schemas: scope.Schemas.Select(d => d.Path).ToArray(),
                RecentTasks: scope.RecentTasks.Select(t => $"{t.Lane}/{t.JobId}").ToArray(),
                RecentDriftReports: scope.RecentDriftReports.Select(r => r.ReportId).ToArray(),
                RecentAnalysisReports: scope.RecentAnalysisReports.Select(r => r.ReportId).ToArray(),
                Prompt: renderedPrompt));
        });

        group.MapPost("/{project}/actions/adr-code-drift", async (
            string project,
            AdrCodeDriftRunRequest? body,
            IConfiguration config,
            DriftReportStore driftStore,
            AnalysisReportStore analysisStore,
            RuntimePromptService prompts,
            AdrCodeDriftAnalysisService action,
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
            var scope = action.SelectScope(project, projectRoot, repoRoot, driftStore, analysisStore, workspace);
            var template = prompts.Render(
                "adr-code-drift.md",
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var renderedPrompt = action.BuildPrompt(scope, template);

            var agentResponse = body?.AgentResponse;
            string markdown;
            AdrCodeDriftParseResult parse;
            if (string.IsNullOrWhiteSpace(agentResponse))
            {
                parse = new AdrCodeDriftParseResult(
                    Status: AdrCodeDriftParseStatus.Unstructured,
                    ScoreBand: DriftScoreBand.Unknown,
                    OverallScore: 0,
                    Summary: "Evidence assembled; no agent narrative supplied. Run the embedded prompt against a CLI agent to produce the verdict.",
                    Dimensions: null,
                    FollowUps: new[]
                    {
                        new DriftFollowUpSuggestion(
                            Title: "Run ADR / Code Drift against a CLI agent",
                            Summary: "Hand the embedded prompt to Claude / Codex / Copilot / Gemini and POST the reply back to /api/drift/{project}/actions/adr-code-drift to produce the structured verdict.",
                            Priority: DriftFollowUpPriority.Normal,
                            RelatedDimension: DriftDimensionType.Architecture),
                    },
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
                trigger: DriftReportTrigger.Manual);

            await driftStore.AppendAsync(workspace!, project, report, markdown, ct).ConfigureAwait(false);
            return Results.Ok(new DriftReportDetailResponse(report, markdown));
        });

        // -----------------------------------------------------------------
        // Docs / Marketing Drift action.
        // GET /prompt returns the assembled prompt + scope summary so an
        // operator (or future inline runner) can hand the prompt to a CLI
        // agent. POST runs the action: without `agentResponse` it produces
        // an Unstructured "evidence + prompt" report; with `agentResponse`
        // it parses the agent's reply and produces the typed verdict.
        //
        // Marketing repository path is never hardcoded: it is read from
        // configuration (`Drift:MarketingRepoPath`). When the path is
        // missing, the scope records "not configured" and the prompt
        // renders the absence explicitly so the agent does not invent
        // marketing findings.
        // -----------------------------------------------------------------

        group.MapGet("/{project}/actions/docs-marketing-drift/prompt", (
            string project,
            IConfiguration config,
            DriftReportStore driftStore,
            AnalysisReportStore analysisStore,
            RuntimePromptService prompts,
            DocsMarketingDriftAnalysisService action) =>
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
            var marketingRepo = config["Drift:MarketingRepoPath"];
            var scope = action.SelectScope(project, projectRoot, repoRoot, marketingRepo, driftStore, analysisStore, workspace);
            var template = prompts.Render(
                "docs-marketing-drift.md",
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var renderedPrompt = action.BuildPrompt(scope, template);
            return Results.Ok(new DocsMarketingDriftPromptResponse(
                Project: project,
                CapturedAt: scope.CapturedAt,
                CanonicalDocs: scope.CanonicalDocs.Select(d => d.Path).ToArray(),
                MockupDocs: scope.MockupDocs.Select(d => d.Path).ToArray(),
                QueueJobs: scope.QueueJobs.Select(t => $"{t.Lane}/{t.JobId}").ToArray(),
                RecentCompleted: scope.RecentCompleted.Select(t => $"{t.Lane}/{t.JobId}").ToArray(),
                MarketingConfigured: scope.Marketing.Configured,
                MarketingExists: scope.Marketing.Exists,
                MarketingRoot: scope.Marketing.Root,
                MarketingDocs: scope.Marketing.Docs.Select(d => d.Path).ToArray(),
                RecentDriftReports: scope.RecentDriftReports.Select(r => r.ReportId).ToArray(),
                RecentAnalysisReports: scope.RecentAnalysisReports.Select(r => r.ReportId).ToArray(),
                Prompt: renderedPrompt));
        });

        group.MapPost("/{project}/actions/docs-marketing-drift", async (
            string project,
            DocsMarketingDriftRunRequest? body,
            IConfiguration config,
            DriftReportStore driftStore,
            AnalysisReportStore analysisStore,
            RuntimePromptService prompts,
            DocsMarketingDriftAnalysisService action,
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
            var marketingRepo = config["Drift:MarketingRepoPath"];
            var scope = action.SelectScope(project, projectRoot, repoRoot, marketingRepo, driftStore, analysisStore, workspace);
            var template = prompts.Render(
                "docs-marketing-drift.md",
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var renderedPrompt = action.BuildPrompt(scope, template);

            var agentResponse = body?.AgentResponse;
            string markdown;
            DocsMarketingDriftParseResult parse;
            if (string.IsNullOrWhiteSpace(agentResponse))
            {
                parse = new DocsMarketingDriftParseResult(
                    Status: DocsMarketingDriftParseStatus.Unstructured,
                    ScoreBand: DriftScoreBand.Unknown,
                    OverallScore: 0,
                    Summary: "Evidence assembled; no agent narrative supplied. Run the embedded prompt against a CLI agent to produce the verdict.",
                    Dimensions: null,
                    FollowUps: new[]
                    {
                        new DriftFollowUpSuggestion(
                            Title: "Run Docs / Marketing Drift against a CLI agent",
                            Summary: "Hand the embedded prompt to Claude / Codex / Copilot / Gemini and POST the reply back to /api/drift/{project}/actions/docs-marketing-drift to produce the structured verdict.",
                            Priority: DriftFollowUpPriority.Normal,
                            RelatedDimension: DriftDimensionType.Documentation),
                    },
                    ParseError: null);
                markdown = BuildDocsMarketingEvidenceOnlyMarkdown(scope, renderedPrompt);
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
                trigger: DriftReportTrigger.Manual);

            await driftStore.AppendAsync(workspace!, project, report, markdown, ct).ConfigureAwait(false);
            return Results.Ok(new DriftReportDetailResponse(report, markdown));
        });

        // -----------------------------------------------------------------
        // Spec / Task / Job Drift action.
        // GET /prompt returns the assembled prompt + scope summary so an
        // operator (or future inline runner) can hand the prompt to a CLI
        // agent. POST runs the action: without `agentResponse` it produces
        // an Unstructured "evidence + prompt" report; with `agentResponse`
        // it parses the agent's reply and produces the typed verdict.
        // -----------------------------------------------------------------

        group.MapGet("/{project}/actions/spec-task-job-drift/prompt", (
            string project,
            IConfiguration config,
            DriftReportStore driftStore,
            AnalysisReportStore analysisStore,
            RuntimePromptService prompts,
            SpecTaskJobDriftAnalysisService action) =>
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
            var scope = action.SelectScope(project, projectRoot, repoRoot, driftStore, analysisStore, workspace);
            var template = prompts.Render(
                "spec-task-job-drift.md",
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var renderedPrompt = action.BuildPrompt(scope, template);
            return Results.Ok(new SpecTaskJobDriftPromptResponse(
                Project: project,
                CapturedAt: scope.CapturedAt,
                SpecDocs: scope.SpecDocs.Select(d => d.Path).ToArray(),
                ActiveJobs: scope.ActiveJobs.Select(t => $"{t.Lane}/{t.JobId}").ToArray(),
                RecentCompleted: scope.RecentCompleted.Select(t => $"{t.Lane}/{t.JobId}").ToArray(),
                DuplicateCandidates: scope.DuplicateCandidates
                    .Select(p => $"{p.LeftLane}/{p.LeftJobId} <> {p.RightLane}/{p.RightJobId} ({p.Overlap:0.00})")
                    .ToArray(),
                RecentDriftReports: scope.RecentDriftReports.Select(r => r.ReportId).ToArray(),
                RecentAnalysisReports: scope.RecentAnalysisReports.Select(r => r.ReportId).ToArray(),
                Prompt: renderedPrompt));
        });

        group.MapPost("/{project}/actions/spec-task-job-drift", async (
            string project,
            SpecTaskJobDriftRunRequest? body,
            IConfiguration config,
            DriftReportStore driftStore,
            AnalysisReportStore analysisStore,
            RuntimePromptService prompts,
            SpecTaskJobDriftAnalysisService action,
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
            var scope = action.SelectScope(project, projectRoot, repoRoot, driftStore, analysisStore, workspace);
            var template = prompts.Render(
                "spec-task-job-drift.md",
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
            var renderedPrompt = action.BuildPrompt(scope, template);

            var agentResponse = body?.AgentResponse;
            string markdown;
            SpecTaskJobDriftParseResult parse;
            if (string.IsNullOrWhiteSpace(agentResponse))
            {
                parse = new SpecTaskJobDriftParseResult(
                    Status: SpecTaskJobDriftParseStatus.Unstructured,
                    ScoreBand: DriftScoreBand.Unknown,
                    OverallScore: 0,
                    Summary: "Evidence assembled; no agent narrative supplied. Run the embedded prompt against a CLI agent to produce the verdict.",
                    Dimensions: null,
                    FollowUps: new[]
                    {
                        new DriftFollowUpSuggestion(
                            Title: "Run Spec / Task / Job Drift against a CLI agent",
                            Summary: "Hand the embedded prompt to Claude / Codex / Copilot / Gemini and POST the reply back to /api/drift/{project}/actions/spec-task-job-drift to produce the structured verdict.",
                            Priority: DriftFollowUpPriority.Normal,
                            RelatedDimension: DriftDimensionType.TaskJob),
                    },
                    ParseError: null);
                markdown = BuildSpecTaskJobEvidenceOnlyMarkdown(scope, renderedPrompt);
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
                trigger: DriftReportTrigger.Manual);

            await driftStore.AppendAsync(workspace!, project, report, markdown, ct).ConfigureAwait(false);
            return Results.Ok(new DriftReportDetailResponse(report, markdown));
        });

        // -----------------------------------------------------------------
        // Architecture marble surface.
        //
        // The Drift project view renders an architecture map (max ten
        // elements, per-element drift score and severity). The map reads
        // from the most recent drift report that carries an
        // `architectureModel`; element tracking-state overrides
        // (Tracked / Accepted / Ignored / Resolved) live in a sidecar
        // store so the user can mark drift without spawning a new
        // immutable report. Action buttons (Analyze / Create follow-up)
        // queue normal jobs through the existing job-create surface.
        // -----------------------------------------------------------------

        group.MapGet("/{project}/architecture", (
            string project,
            IConfiguration config,
            DriftReportStore driftStore,
            ArchitectureElementStateStore stateStore) =>
        {
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "project required" });
            var workspace = config["TaskRepository"];
            if (string.IsNullOrWhiteSpace(workspace))
                return Results.Ok(new DriftArchitectureSurfaceResponse(null, null, null, Array.Empty<ElementStateOverride>()));

            var snap = driftStore.Snapshot(workspace!, project);
            var latestWithModel = snap
                .Where(r => r.ArchitectureModel is not null)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefault();
            if (latestWithModel is null)
            {
                return Results.Ok(new DriftArchitectureSurfaceResponse(
                    Model: null,
                    SourceReportId: null,
                    SourceReportCreatedAt: null,
                    Overrides: Array.Empty<ElementStateOverride>()));
            }

            var overrides = stateStore.Snapshot(workspace!, project)
                .Values
                .Where(v => v.ModelId == latestWithModel.ArchitectureModel!.ModelId)
                .ToArray();
            return Results.Ok(new DriftArchitectureSurfaceResponse(
                Model: latestWithModel.ArchitectureModel,
                SourceReportId: latestWithModel.ReportId,
                SourceReportCreatedAt: latestWithModel.CreatedAt,
                Overrides: overrides));
        });

        group.MapPost("/{project}/architecture/{modelId}/elements/{elementId}/status", (
            string project,
            string modelId,
            string elementId,
            ElementStatusRequest? body,
            IConfiguration config,
            ArchitectureElementStateStore stateStore) =>
        {
            if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(elementId))
                return Results.BadRequest(new { error = "project, modelId and elementId required" });
            if (body is null || string.IsNullOrWhiteSpace(body.Status))
                return Results.BadRequest(new { error = "status required" });
            if (!Enum.TryParse<DriftFindingStatus>(body.Status, ignoreCase: true, out var parsed))
                return Results.BadRequest(new { error = $"unknown status '{body.Status}'" });
            var workspace = config["TaskRepository"];
            if (string.IsNullOrWhiteSpace(workspace))
                return Results.BadRequest(new { error = "TaskRepository not configured" });
            var saved = stateStore.Set(workspace!, project, modelId, elementId, parsed, body.Note);
            return Results.Ok(saved);
        });
    }

    /// <summary>
    /// Resolves the source repository root by walking up from
    /// <see cref="AppContext.BaseDirectory"/> until <c>AGENTS.md</c> is found.
    /// Falls back to the current working directory when no marker exists.
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

    private static string BuildDocsMarketingEvidenceOnlyMarkdown(
        DocsMarketingDriftScope scope,
        string renderedPrompt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Docs / Marketing Drift - evidence only");
        sb.AppendLine();
        sb.Append("**Verdict:** evidence assembled at ")
            .Append(scope.CapturedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))
            .AppendLine("; no agent narrative supplied. Run the embedded prompt against a CLI agent and POST the reply back to produce the structured verdict.");
        sb.AppendLine();
        sb.Append("**Project:** `").Append(scope.Project).AppendLine("`");
        sb.AppendLine();
        sb.AppendLine("## Snapshot");
        sb.AppendLine();
        sb.Append("- Canonical project docs found: ").Append(scope.CanonicalDocs.Count).AppendLine();
        sb.Append("- Mockup folders: ").Append(scope.MockupDocs.Count).AppendLine();
        sb.Append("- Queued jobs (active lanes): ").Append(scope.QueueJobs.Count).AppendLine();
        sb.Append("- Recent completed evidence entries: ").Append(scope.RecentCompleted.Count).AppendLine();
        sb.Append("- Marketing repository: ");
        if (!scope.Marketing.Configured) sb.AppendLine("not configured");
        else if (!scope.Marketing.Exists) sb.AppendLine($"configured but missing on disk (`{scope.Marketing.Root}`)");
        else sb.AppendLine($"available at `{scope.Marketing.Root}` ({scope.Marketing.Docs.Count} docs)");
        sb.AppendLine();
        sb.AppendLine("## Embedded prompt");
        sb.AppendLine();
        sb.AppendLine("Copy the block below into a Claude / Codex / Copilot / Gemini session, then POST the reply back to `/api/drift/{project}/actions/docs-marketing-drift` with `agentResponse` set to produce the structured verdict.");
        sb.AppendLine();
        sb.AppendLine("```markdown");
        sb.AppendLine(renderedPrompt);
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static string BuildSpecTaskJobEvidenceOnlyMarkdown(
        SpecTaskJobDriftScope scope,
        string renderedPrompt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Spec / Task / Job Drift - evidence only");
        sb.AppendLine();
        sb.Append("**Verdict:** evidence assembled at ")
            .Append(scope.CapturedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))
            .AppendLine("; no agent narrative supplied. Run the embedded prompt against a CLI agent and POST the reply back to produce the structured verdict.");
        sb.AppendLine();
        sb.Append("**Project:** `").Append(scope.Project).AppendLine("`");
        sb.AppendLine();
        sb.AppendLine("## Snapshot");
        sb.AppendLine();
        sb.Append("- Spec / planning docs: ").Append(scope.SpecDocs.Count).AppendLine();
        sb.Append("- Active queue jobs: ").Append(scope.ActiveJobs.Count).AppendLine();
        sb.Append("- Recent completed evidence entries: ").Append(scope.RecentCompleted.Count).AppendLine();
        sb.Append("- Duplicate candidate pairs (heuristic): ").Append(scope.DuplicateCandidates.Count).AppendLine();
        sb.AppendLine();
        sb.AppendLine("## Embedded prompt");
        sb.AppendLine();
        sb.AppendLine("Copy the block below into a Claude / Codex / Copilot / Gemini session, then POST the reply back to `/api/drift/{project}/actions/spec-task-job-drift` with `agentResponse` set to produce the structured verdict.");
        sb.AppendLine();
        sb.AppendLine("```markdown");
        sb.AppendLine(renderedPrompt);
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static string BuildEvidenceOnlyMarkdown(
        AdrCodeDriftScope scope,
        string renderedPrompt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ADR / Code Drift - evidence only");
        sb.AppendLine();
        sb.Append("**Verdict:** evidence assembled at ")
            .Append(scope.CapturedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))
            .AppendLine("; no agent narrative supplied. Run the embedded prompt against a CLI agent and POST the reply back to produce the structured verdict.");
        sb.AppendLine();
        sb.Append("**Project:** `").Append(scope.Project).AppendLine("`");
        sb.AppendLine();
        sb.AppendLine("## Snapshot");
        sb.AppendLine();
        sb.Append("- ADR / arch docs found: ").Append(scope.Docs.Count).AppendLine();
        sb.Append("- Top-level source folders: ").Append(scope.SourceTree.Count).AppendLine();
        sb.Append("- Backend module boundaries: ").Append(scope.ModuleBoundaries.Count).AppendLine();
        sb.Append("- Schemas in `docs/schemas/`: ").Append(scope.Schemas.Count).AppendLine();
        sb.Append("- Recent task evidence entries: ").Append(scope.RecentTasks.Count).AppendLine();
        sb.AppendLine();
        sb.AppendLine("## Embedded prompt");
        sb.AppendLine();
        sb.AppendLine("Copy the block below into a Claude / Codex / Copilot / Gemini session, then POST the reply back to `/api/drift/{project}/actions/adr-code-drift` with `agentResponse` set to produce the structured verdict.");
        sb.AppendLine();
        sb.AppendLine("```markdown");
        sb.AppendLine(renderedPrompt);
        sb.AppendLine("```");
        return sb.ToString();
    }
}

/// <summary>List response wrapper so the JSON shape stays additive when more aggregates appear.</summary>
public sealed record DriftReportListResponse(IReadOnlyList<DriftReport> Reports);

/// <summary>One report plus its Markdown body for the drill-down.</summary>
public sealed record DriftReportDetailResponse(DriftReport Report, string? Markdown);

/// <summary>
/// Body for <c>POST /api/drift/{project}/actions/adr-code-drift</c>. When
/// <see cref="AgentResponse"/> is null or whitespace the endpoint emits an
/// Unstructured "evidence only" report carrying the rendered prompt; when
/// it is set, the endpoint parses the agent's reply (Markdown body plus an
/// optional fenced JSON sidecar) and emits the typed verdict.
/// </summary>
public sealed record AdrCodeDriftRunRequest(string? AgentResponse);

/// <summary>
/// Response for
/// <c>GET /api/drift/{project}/actions/adr-code-drift/prompt</c>. Returns
/// the assembled scope summary and the rendered prompt the operator (or
/// future inline runner) hands to a CLI agent.
/// </summary>
/// <summary>
/// Architecture marble surface payload. Carries the most recent drift
/// report's architecture model plus user-set element-state overrides.
/// </summary>
public sealed record DriftArchitectureSurfaceResponse(
    DriftArchitectureModel? Model,
    string? SourceReportId,
    DateTime? SourceReportCreatedAt,
    IReadOnlyList<ElementStateOverride> Overrides);

/// <summary>Body for setting an architecture element's tracking-state override.</summary>
public sealed record ElementStatusRequest(string Status, string? Note);

/// <summary>
/// Body for <c>POST /api/drift/{project}/actions/docs-marketing-drift</c>.
/// Same envelope as the ADR / Code Drift action: an empty
/// <see cref="AgentResponse"/> emits an Unstructured "evidence only" report
/// carrying the rendered prompt; a populated value parses the agent's reply
/// and emits the typed verdict.
/// </summary>
public sealed record DocsMarketingDriftRunRequest(string? AgentResponse);

/// <summary>Response for
/// <c>GET /api/drift/{project}/actions/docs-marketing-drift/prompt</c>.</summary>
public sealed record DocsMarketingDriftPromptResponse(
    string Project,
    DateTime CapturedAt,
    IReadOnlyList<string> CanonicalDocs,
    IReadOnlyList<string> MockupDocs,
    IReadOnlyList<string> QueueJobs,
    IReadOnlyList<string> RecentCompleted,
    bool MarketingConfigured,
    bool MarketingExists,
    string? MarketingRoot,
    IReadOnlyList<string> MarketingDocs,
    IReadOnlyList<string> RecentDriftReports,
    IReadOnlyList<string> RecentAnalysisReports,
    string Prompt);

public sealed record AdrCodeDriftPromptResponse(
    string Project,
    DateTime CapturedAt,
    IReadOnlyList<string> Docs,
    IReadOnlyList<string> SourceTree,
    IReadOnlyList<string> ModuleBoundaries,
    IReadOnlyList<string> Schemas,
    IReadOnlyList<string> RecentTasks,
    IReadOnlyList<string> RecentDriftReports,
    IReadOnlyList<string> RecentAnalysisReports,
    string Prompt);

/// <summary>
/// Body for <c>POST /api/drift/{project}/actions/spec-task-job-drift</c>.
/// Same envelope as the other Drift actions: an empty
/// <see cref="AgentResponse"/> emits an Unstructured "evidence only" report
/// carrying the rendered prompt; a populated value parses the agent's reply
/// and emits the typed verdict.
/// </summary>
public sealed record SpecTaskJobDriftRunRequest(string? AgentResponse);

/// <summary>Response for
/// <c>GET /api/drift/{project}/actions/spec-task-job-drift/prompt</c>.</summary>
public sealed record SpecTaskJobDriftPromptResponse(
    string Project,
    DateTime CapturedAt,
    IReadOnlyList<string> SpecDocs,
    IReadOnlyList<string> ActiveJobs,
    IReadOnlyList<string> RecentCompleted,
    IReadOnlyList<string> DuplicateCandidates,
    IReadOnlyList<string> RecentDriftReports,
    IReadOnlyList<string> RecentAnalysisReports,
    string Prompt);
