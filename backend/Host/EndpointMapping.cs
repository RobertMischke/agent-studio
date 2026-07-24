
using AgentStudio.Search;
using AgentStudio.Proposals;

namespace AgentStudio.Host;

/// <summary>
/// Composition root for all HTTP routes. <see cref="Program"/> calls
/// <see cref="MapAllEndpoints"/> once at startup; this method does
/// nothing on its own beyond delegating to the per-feature mappers
/// in <see cref="AgentStudio.Tasks"/> and the sibling
/// classes in this namespace. Splitting the routes by feature keeps
/// each file under ~150 lines and makes "where is the handler for
/// X" answerable in one grep.
/// </summary>
public static class EndpointMapping
{
    public static void MapAllEndpoints(this WebApplication app)
    {
        app.MapAccessSecurityEndpoints();
        var tasks = app.MapGroup("/api/tasks")
            .AddEndpointFilter<TaskOperationTimingFilter>();
        tasks.MapTaskCrudEndpoints();
        tasks.MapTaskFilesEndpoints();
        tasks.MapTaskRunnerEndpoints();
        tasks.MapTaskGitEndpoints();
        tasks.MapTaskClaudeEndpoints();
        tasks.MapTaskReviewEvidenceEndpoints();
        tasks.MapTaskExternalCompletionEndpoints();
        tasks.MapTaskCodeReviewEndpoints();
        tasks.MapTaskRegressionRadarEndpoints();
        tasks.MapTaskPipelineEndpoints();
        tasks.MapTaskMergeEndpoints();

        app.MapEpicEndpoints();
        app.MapCompletedLaneAuditEndpoints();
        app.MapRunnerEndpoints();
        app.MapOrchestratorSessionEndpoints();
        app.MapOrchestratorContextEndpoints();
        app.MapLeaseEndpoints();
        app.MapAttemptAuthorityEndpoints();
        app.MapIntegrationLeaseEndpoints();
        app.MapLogIngestionEndpoints();
        app.MapRunnerEventIngestionEndpoints();
        app.MapArtifactIngestionEndpoints();
        app.MapRegistryEndpoints();
        app.MapWorkspaceEndpoints();
        app.MapWorkspaceSettingsEndpoints();
        app.MapProjectSettingsEndpoints();
        app.MapCrashRecoveryEndpoints();
        app.MapProjectRegressionRadarEndpoints();
        app.MapProjectDocsEndpoints();
        app.MapProjectProposalEndpoints();
        app.MapWikiGradingEndpoints();
        app.MapProjectSteeringDocsEndpoints();
        app.MapSkillReadinessEndpoints();
        app.MapSecurityReviewEndpoints();
        app.MapDesignSurfaceEndpoints();
        app.MapProjectTokenUsageEndpoints();
        app.MapPipelineHealthEndpoints();
        app.MapTokenPricingEndpoints();
        app.MapReviewDecisionsEndpoints();
        app.MapProjectSnapshotEndpoints();
        app.MapProjectOperatorDashboardEndpoints();
        app.MapProjectGraphEndpoints();
        app.MapPublishEndpoints();
        if (!SecurityProfiles.IsNetworked(app.Configuration)) app.MapFilesystemLayerEndpoints();
        app.MapSystemEndpoints();
        app.MapCliEndpoints();
        if (!SecurityProfiles.IsNetworked(app.Configuration)) app.MapDevToolsEndpoints();
        app.MapAdminConfigEndpoints();
        app.MapSupervisorEndpoints();
        if (!SecurityProfiles.IsNetworked(app.Configuration)) app.MapDiagnosticsEndpoints();
        if (!SecurityProfiles.IsNetworked(app.Configuration)) app.MapInternalProbeEndpoints();
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
        app.MapGlobalSearchEndpoints();
        app.MapManagementEndpoints();
    }
}
