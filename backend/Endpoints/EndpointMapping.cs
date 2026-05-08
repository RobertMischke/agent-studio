using OrchestratorApi.Endpoints.Jobs;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Composition root for all HTTP routes. <see cref="Program"/> calls
/// <see cref="MapAllEndpoints"/> once at startup; this method does
/// nothing on its own beyond delegating to the per-feature mappers
/// in <see cref="OrchestratorApi.Endpoints.Jobs"/> and the sibling
/// classes in this namespace. Splitting the routes by feature keeps
/// each file under ~150 lines and makes "where is the handler for
/// X" answerable in one grep.
/// </summary>
public static class EndpointMapping
{
    public static void MapAllEndpoints(this WebApplication app)
    {
        var jobs = app.MapGroup("/api/jobs");
        jobs.MapJobCrudEndpoints();
        jobs.MapJobFilesEndpoints();
        jobs.MapJobRunnerEndpoints();
        jobs.MapJobGitEndpoints();
        jobs.MapJobClaudeEndpoints();
        jobs.MapJobReviewEvidenceEndpoints();

        app.MapRunnerEndpoints();
        app.MapWorkspaceEndpoints();
        app.MapProjectSettingsEndpoints();
        app.MapProjectDocsEndpoints();
        app.MapProjectSteeringDocsEndpoints();
        app.MapSkillReadinessEndpoints();
        app.MapSecurityReviewEndpoints();
        app.MapDesignSurfaceEndpoints();
        app.MapProjectTokenUsageEndpoints();
        app.MapReviewDecisionsEndpoints();
        app.MapSystemEndpoints();
        app.MapCliEndpoints();
        app.MapDevToolsEndpoints();
        app.MapAdminConfigEndpoints();
        app.MapSupervisorEndpoints();
        app.MapDiagnosticsEndpoints();
        app.MapInternalProbeEndpoints();
        app.MapRoadmapIntakeEndpoints();
        app.MapTitleGenerationEndpoints();
        app.MapPromptEnhancementEndpoints();
        app.MapAdHocUsageEndpoints();
        app.MapBusEndpoints();
        app.MapRuntimeEventEndpoints();
        app.MapClientEndpoints();
        app.MapAnalysisReportEndpoints();
        app.MapDriftReportEndpoints();
        app.MapTagEndpoints();
        app.MapProjectChatEndpoints();
        app.MapConceptDocsEndpoints();
    }
}
