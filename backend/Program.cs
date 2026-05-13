using OrchestratorApi.Endpoints;
using OrchestratorApi.Hubs;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Bus;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Drift;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Companion;
using OrchestratorApi.Services.Configuration;
using OrchestratorApi.Services.Diagnostics;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.ProjectChat;
using OrchestratorApi.Services.Pty;
using OrchestratorApi.Services.Quota;
using OrchestratorApi.Services.Runner;
using OrchestratorApi.Services.Security;
using OrchestratorApi.Services.Supervisor;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Default BackgroundServiceExceptionBehavior is StopHost: an unhandled
// exception escaping any HostedService.ExecuteAsync stops the entire host.
// That turned a single faulted runner tick or hosted-service iteration into
// a silent API-down event. Switch to Ignore so the offending service stops
// in isolation and the rest of the API keeps serving while the per-tick
// try/catch (TaskRunnerService) and the UnhandledException recorder above
// surface the cause.
builder.Services.Configure<HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// Local-only override file (gitignored) - sets per-checkout flags such as
// Environment:IsDev. Loaded after appsettings.Development.json so a developer
// can flip the dev banner / dev PWA icon on for their checkout without
// committing the toggle.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Rolling backend file logger + crash marker (see Services/Diagnostics).
// Built before WebApplication so the process-wide crash handlers below
// can capture the very first throw, even if it lands during DI build.
builder.Services.Configure<BackendFileLoggerOptions>(
    builder.Configuration.GetSection(BackendFileLoggerOptions.SectionName));
var fileLoggerOptions = new BackendFileLoggerOptions();
builder.Configuration.GetSection(BackendFileLoggerOptions.SectionName).Bind(fileLoggerOptions);
var fileLogSink = new BackendFileLogSink(fileLoggerOptions);
var crashRecorder = new CrashRecorder(fileLoggerOptions, fileLogSink);
builder.Services.AddSingleton(fileLogSink);
builder.Services.AddSingleton(crashRecorder);
builder.Logging.AddProvider(new BackendFileLoggerProvider(fileLogSink));

// Last-resort safety nets: an uncaught exception in a fire-and-forget Task
// (e.g. CLI output streaming, SignalR fan-out) used to take down the whole API
// silently with an empty stderr. Log them and persist a crash marker so the
// next operator (or Layer 3 review) can find the cause without re-attaching.
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    crashRecorder.Record("UnobservedTaskException", e.Exception, isTerminating: false);
    Console.Error.WriteLine($"[UnobservedTaskException] {e.Exception}");
    e.SetObserved();
};
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is Exception ex)
    {
        crashRecorder.Record("UnhandledException", ex, isTerminating: e.IsTerminating);
    }
    Console.Error.WriteLine($"[UnhandledException] terminating={e.IsTerminating} {e.ExceptionObject}");
};

builder.Services.AddSingleton<ClientIdentityStore>();
builder.Services.AddSingleton<OrchestratorConfigService>();
builder.Services.AddSingleton<JobScannerService>();
builder.Services.AddSingleton<ScreenshotIndexService>();
builder.Services.AddSingleton<JobStateMachine>();
builder.Services.AddSingleton<JobMutationService>();
builder.Services.AddSingleton<FixtureMigrationService>();
builder.Services.AddSingleton<JobSessionLog>();
builder.Services.AddSingleton<OrchestratorChatLog>();
builder.Services.AddSingleton<OrchestratorLog>();
builder.Services.AddSingleton<OrchestratorChat>();
builder.Services.AddSingleton<OrchestratorChatService>();
builder.Services.AddSingleton<ProjectChatStore>();
builder.Services.AddSingleton<ProjectChatIndex>();
builder.Services.AddSingleton<ProjectChatMigration>();
builder.Services.AddSingleton<OrchestratorRunner>();
builder.Services.AddSingleton<OrchestratorSessionStore>();
builder.Services.AddSingleton<GlobalOrchestratorSessionStore>();
builder.Services.AddSingleton<GlobalOrchestratorBootstrap>();
builder.Services.AddSingleton<TokenSummaryCacheStore>();
builder.Services.AddSingleton<WorkspaceTokensCacheStore>();
builder.Services.AddSingleton<TokenSummaryService>();
builder.Services.AddSingleton<ProjectTokenUsageService>();
builder.Services.AddSingleton<WorkspaceTokensTimelineService>();
builder.Services.AddSingleton<WorkspaceSummaryService>();
builder.Services.AddSingleton<JobTransitionService>();
builder.Services.AddSingleton<JobWatcherService>();
// Cycle 1: in-memory snapshot of all jobs across watch paths. Reads from
// JobScannerService.ScanAllJobsRaw on miss, invalidated by JobWatcherService
// events and by mutation services. Wired into JobScannerService below via
// SetIndexCache so existing ScanAllJobs callers transparently benefit.
builder.Services.AddSingleton<JobIndexCache>();
builder.Services.AddSingleton<CopilotCliEnvironment>();
builder.Services.AddSingleton<CopilotModelDiscovery>();
builder.Services.AddSingleton<CodexModelDiscovery>();
builder.Services.AddSingleton<CopilotCliService>();
builder.Services.AddSingleton<ClaudeCliService>();
builder.Services.AddSingleton<CodexCliService>();
builder.Services.AddSingleton<GeminiCliService>();
builder.Services.AddSingleton<ClaudeSessionInspector>();
builder.Services.AddSingleton<CliRouter>();
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<ContextUsageParser>();
builder.Services.AddSingleton<SummaryGenerationService>();
builder.Services.AddSingleton<RuntimePromptService>();
builder.Services.AddSingleton<RoadmapIntakeService>();
builder.Services.AddSingleton<TitleGenerationService>();
builder.Services.AddSingleton<PromptEnhancementService>();
builder.Services.AddSingleton<OrchestratorApi.Services.AdHoc.AdHocUsageRecorder>();
builder.Services.AddSingleton<OrchestratorApi.Services.AdHoc.AdHocUsageService>();
builder.Services.AddSingleton<TaskRunnerService>();
builder.Services.AddSingleton<CrashRecoveryService>();
builder.Services.AddSingleton<StaleProgressArchiver>();
builder.Services.AddSingleton<PickupFailureLog>();
builder.Services.AddSingleton<InfraHaltLog>();
builder.Services.AddSingleton<CrossSlugInfraCircuitBreaker>();
builder.Services.AddSingleton<AgentMessageBusStore>();
builder.Services.AddSingleton<AgentMessageBusBridge>();
builder.Services.AddSingleton<ICliModelRegistry, CliModelRegistry>();
builder.Services.AddSingleton<ICliUsageParser, ClaudeUsageParser>();
builder.Services.AddSingleton<ICliUsageParser, CodexUsageParser>();
builder.Services.AddSingleton<CliUsageParserRegistry>();
builder.Services.AddSingleton<BusAggregationCache>();
builder.Services.AddSingleton<OrchestratorApi.Services.Tokens.BusBackedAdHocUsageReader>();
builder.Services.AddSingleton<OrchestratorApi.Services.Tokens.BusBackedTokenSummaryReader>();
builder.Services.AddSingleton<OrchestratorApi.Services.Tokens.BusBackedWorkspaceTimelineReader>();
builder.Services.AddSingleton<OrchestratorApi.Services.Tokens.BusBackedProjectTokenUsageReader>();
builder.Services.AddSingleton<OrchestratorApi.Services.Tokens.ITokenAggregator, OrchestratorApi.Services.Tokens.TokenAggregationService>();
builder.Services.AddSingleton<OrchestratorApi.Services.Cli.OneShot.ICliOneShot, OrchestratorApi.Services.Cli.OneShot.ClaudeOneShot>();
builder.Services.AddSingleton<OrchestratorApi.Services.Cli.OneShot.CliOneShotRegistry>();
builder.Services.AddSingleton<CodePatternDriftAnalysisService>();
builder.Services.AddSingleton<OrchestratorApi.Services.Persistence.IJsonlAppender, OrchestratorApi.Services.Persistence.JsonlAppender>();
builder.Services.AddSingleton<OrchestratorApi.Services.Runtime.ProductRuntimeEventStore>();
builder.Services.AddSingleton<OrchestratorApi.Services.State.SupervisorAdvisoryStore>();
builder.Services.AddSingleton<OrchestratorApi.Services.State.SupervisorInterventionStore>();
builder.Services.AddSingleton<OrchestratorApi.Services.Analysis.AnalysisReportStore>();
builder.Services.AddSingleton<OrchestratorApi.Services.Analysis.RoadmapAlignmentReviewService>();
builder.Services.AddSingleton<OrchestratorApi.Services.Analysis.SteeringDocsSummaryDriftService>();
builder.Services.AddSingleton<OrchestratorApi.Services.Drift.DriftReportStore>();
builder.Services.AddSingleton<OrchestratorApi.Services.Drift.AdrCodeDriftAnalysisService>();
builder.Services.AddSingleton<OrchestratorApi.Services.Drift.DocsMarketingDriftAnalysisService>();
builder.Services.AddSingleton<OrchestratorApi.Services.Drift.SpecTaskJobDriftAnalysisService>();
builder.Services.AddSingleton<OrchestratorApi.Services.Drift.ArchitectureElementStateStore>();
builder.Services.AddSingleton<OrchestratorApi.Services.Tags.TagRegistryService>();
builder.Services.AddSingleton<ProjectObservationService>();
builder.Services.AddSingleton<SupervisorInterventionService>();
builder.Services.AddHostedService<HardHealthCheckHostedService>();
builder.Services.AddHostedService<SoftReasoningHostedService>();
builder.Services.AddHostedService<AutoInterventionHostedService>();
builder.Services.AddHostedService<MetaCycleHostedService>();
builder.Services.AddHostedService<OrchestratorPrepHostedService>();
builder.Services.AddHostedService<ChatNoteHostedService>();
builder.Services.AddSingleton<AspectRunnerService>();
builder.Services.AddSingleton<OrchestratorApi.Services.Review.CodeReviewStepService>();
builder.Services.AddSingleton<AutoReviewStatusSnapshot>();
builder.Services.AddHostedService<ReviewDecisionOrchestrator>();
// Orchestrator-intake (ready-orchestrator-intake-lane). Off by default per
// project; see ProjectSettings.IntakeEnabled. The hosted service is cheap
// (heuristic only, no LLM) and skips projects that have not opted in.
builder.Services.AddSingleton<IntakeRunner>();
builder.Services.AddHostedService<IntakeHostedService>();
builder.Services.AddSingleton<GitService>();
builder.Services.AddSingleton<ProjectSettingsService>();
builder.Services.AddHostedService<CompletedPushBackstopHostedService>();
builder.Services.AddSingleton<ProjectDocsService>();
builder.Services.AddSingleton<ProjectSteeringDocsService>();
builder.Services.AddSingleton<SkillReadinessService>();
builder.Services.AddSingleton<ConceptDocsService>();
builder.Services.AddSingleton<SecurityReviewService>();
builder.Services.AddSingleton<OrchestratorApi.Services.Design.DesignEvidenceService>();
// Quota probes: each CLI gets its own probe instance, all surfaced through QuotaService.
builder.Services.AddSingleton<IQuotaProbe, CopilotQuotaProbe>();
builder.Services.AddSingleton<IQuotaProbe, ClaudeQuotaProbe>();
builder.Services.AddSingleton<IQuotaProbe, CodexQuotaProbe>();
builder.Services.AddSingleton<IQuotaProbe, GeminiQuotaProbe>();
builder.Services.AddSingleton<QuotaCacheStore>();
builder.Services.AddSingleton<QuotaService>();
builder.Services.AddSingleton<CliQuotaCapsService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobWatcherService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskRunnerService>());
// Companion app sync (ADR-0018). Default-off; the HostedService loop exits
// immediately when Companion:Enabled is false. Bound from appsettings*.json.
builder.Services.Configure<CompanionSyncOptions>(builder.Configuration.GetSection(CompanionSyncOptions.SectionName));
builder.Services.AddSingleton<CompanionCommandDispatcher>();
builder.Services.AddHttpClient("companion-relay");
builder.Services.AddHostedService<CompanionSyncService>();
// Serialise enums as camelCase strings so the frontend can use string-literal
// unions (e.g. JobSummaryStatus = 'none' | 'generating' | 'ready' | 'failed')
// instead of numeric values.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});
builder.Services.AddSignalR();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://localhost:4010", "http://localhost:4200")
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials());
});

var app = builder.Build();
var includeExceptionDetails = app.Configuration.GetValue<bool>("ErrorHandling:IncludeExceptionDetails");

app.UseExceptionHandler(exceptionApp =>
{
    exceptionApp.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerPathFeature>();
        var exception = feature?.Error;

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";

        var response = new Dictionary<string, object?>
        {
            ["error"] = includeExceptionDetails
                ? exception?.Message ?? "An unexpected server error occurred."
                : "An unexpected server error occurred.",
            ["path"] = feature?.Path,
            ["traceId"] = context.TraceIdentifier,
            ["timestamp"] = DateTimeOffset.UtcNow
        };

        if (includeExceptionDetails)
        {
            response["exceptionType"] = exception?.GetType().FullName;
            response["stackTrace"] = exception?.StackTrace;
            response["exception"] = exception?.ToString();
        }

        await context.Response.WriteAsJsonAsync(response);
    });
});

app.UseCors();

// X-Client-Id registration boundary: rejects mutations from unregistered
// identities, stamps lastSeenAt on known ones. Carve-outs for /api/clients/register,
// hubs, and health checks live in the middleware itself.
app.UseClientIdentity();

// Touch the identity store at boot so the bootstrap "local-default" identity
// is created before any caller looks at it.
app.Services.GetRequiredService<ClientIdentityStore>().EnsureLoaded();

// Ensure state folders exist and migrate legacy flat jobs
app.Services.GetRequiredService<JobStateMachine>().EnsureStateFoldersAndMigrate();

// ADR-0020: run the crash-recovery sweep BEFORE the first runner tick. Any
// surviving completion-marker.json finishes its 3-progress -> 4-review move
// here, and any orphan working-tree changes get committed under a
// crash-recovery author tag so a second crash mid-recovery is itself
// recoverable on the next boot. Sync wait is intentional: we want the
// runner to see the recovered state on its first scan.
try
{
    app.Services.GetRequiredService<CrashRecoveryService>().RecoverAsync().GetAwaiter().GetResult();
}
catch (Exception ex)
{
    // Recovery never blocks boot; a failure here is logged and surfaced
    // through the crash recorder so the operator can find it.
    crashRecorder.Record("CrashRecoveryService", ex);
}

// After file-level crash recovery, sweep the 3-progress lane for folders that
// have been wedged past the resume window. Pairs with crash recovery: that
// rescues changes, this rescues the lane (one running job per project, ADR-0001).
try
{
    app.Services.GetRequiredService<StaleProgressArchiver>().SweepAsync().GetAwaiter().GetResult();
}
catch (Exception ex)
{
    crashRecorder.Record("StaleProgressArchiver", ex);
}

// Seed the Agent Message Bus participant registry. Workspace-scoped, idempotent
// across boots; safe to fire-and-forget. See docs/agent-message-bus.md section 2.
try
{
    var bus = app.Services.GetRequiredService<AgentMessageBusBridge>();
    _ = bus.SeedBuiltInParticipantsAsync();
}
catch (Exception ex) { crashRecorder.Record("AgentMessageBusBridge.Seed", ex); }

// Wire the aggregation cache onto the bus store. Every successful append
// updates the per-project tallies in O(1), so /token-aggregate requests
// do not scan messages. The cache backfills lazily on first use.
//
// SignalR push: also broadcast every appended message as `busMessageAdded`
// so the Observability panel can drop its polling loop. The push is
// fire-and-forget; subscribers reconnect their own snapshot if they fall
// behind.
try
{
    var store = app.Services.GetRequiredService<AgentMessageBusStore>();
    var cache = app.Services.GetRequiredService<BusAggregationCache>();
    var pushHub = app.Services.GetRequiredService<IHubContext<JobHub>>();
    store.OnAppended = (workspace, msg) =>
    {
        try { cache.OnAppended(workspace, msg); } catch { /* best-effort */ }
        try { _ = pushHub.Clients.All.SendAsync("busMessageAdded", msg); } catch { /* best-effort */ }
    };
}
catch (Exception ex) { crashRecorder.Record("BusAggregationCache.Wire", ex); }

// Slice D project-chat: migrate the legacy `orchestrator-chat.jsonl`
// per-project file into the new per-month markdown tree, then ensure
// the per-project FTS5 index is fresh. Idempotent; cheap when the
// migration has already run. Fire-and-forget so a slow disk does not
// hold up boot.
_ = Task.Run(() =>
{
    try
    {
        var migration = app.Services.GetRequiredService<ProjectChatMigration>();
        migration.MigrateAll();

        var scanner = app.Services.GetRequiredService<JobScannerService>();
        var index = app.Services.GetRequiredService<ProjectChatIndex>();
        foreach (var entry in scanner.GetWatchPaths())
        {
            try { index.EnsureFresh(entry.Path); }
            catch (Exception ex) { crashRecorder.Record($"ProjectChatIndex.EnsureFresh:{entry.Name}", ex); }
        }
    }
    catch (Exception ex)
    {
        crashRecorder.Record("ProjectChatMigration", ex);
    }
});

// Wire up FileSystemWatcher → SignalR push
var watcher = app.Services.GetRequiredService<JobWatcherService>();
var hubContext = app.Services.GetRequiredService<IHubContext<JobHub>>();
watcher.OnJobChanged += _ => hubContext.Clients.All.SendAsync("jobsChanged");

// Cycle 1: bind the in-memory snapshot cache. JobScannerService.ScanAllJobs
// now serves from cache; JobWatcherService.OnJobChanged invalidates it on
// external file changes, mutation services invalidate it on API writes.
// Without this two-line bridge the cache exists but nothing fills or
// invalidates it, and ScanAllJobs falls back to per-call disk walks.
var jobIndexCache = app.Services.GetRequiredService<JobIndexCache>();
app.Services.GetRequiredService<JobScannerService>().SetIndexCache(jobIndexCache);
watcher.OnJobChanged += _ => jobIndexCache.Invalidate(JobIndexCache.InvalidationSource.External);

// Wire JobTransitionService move events to atomically clear the per-project
// runner's _activeJobId when the active job is moved out of 3-progress.
// Without this, an external move (API or otherwise) leaves the runner pinned
// to a slug whose folder has left the lane, every pickup tick short-circuits
// on `active != null`, and the project wedges until backend restart.
var transitionsForRunner = app.Services.GetRequiredService<JobTransitionService>();
var runnerForTransitions = app.Services.GetRequiredService<TaskRunnerService>();
transitionsForRunner.OnJobMoved += (projectName, jobId, fromState, toState) =>
{
    if (fromState != JobStates.Progress) return;
    runnerForTransitions.ClearActiveJobForProject(
        projectName, jobId,
        $"job moved out of 3-progress externally ({fromState} -> {toState})");
};

// Defensive: when a non-API folder change touches the watch tree (external
// script, manual edit, boot-time stuck-folder sweep), sweep every runner so
// a stale active-job latch is cleared before the next pickup tick.
watcher.OnJobChanged += _ => runnerForTransitions.ReconcileAllRunners();

// Wire up CLI events → SignalR push (across all CLI backends via the router)
var cliRouter = app.Services.GetRequiredService<CliRouter>();
cliRouter.OnOutput += (cliType, jobId, line) =>
    hubContext.Clients.All.SendAsync("cliOutput", jobId, line.Text, line.Stream, line.Timestamp, cliType);
cliRouter.OnStarted += (cliType, jobId, exec) =>
    hubContext.Clients.All.SendAsync("cliStarted", jobId, exec.ProcessId, exec.StartedAt, cliType);
cliRouter.OnFinished += (cliType, jobId, exec) =>
    hubContext.Clients.All.SendAsync("cliFinished", jobId, exec.ExitCode, exec.DurationSeconds, exec.Status, cliType);

// Per-CLI startup hook. Copilot re-attaches to surviving processes (its own
// implementation); Claude / Codex / Gemini reap orphans - see
// CliExecutionServiceBase.ReattachOnStartup. Must run before any new CLI run
// is started so we never have two processes editing the same repo.
cliRouter.ReattachAll();

// Wire up Runner status → SignalR push
var taskRunner = app.Services.GetRequiredService<TaskRunnerService>();
taskRunner.OnRunnerStatusChanged += (projectName, status) =>
    hubContext.Clients.All.SendAsync("runnerStatusChanged", projectName, status.Mode, status.ActiveJobId);

app.MapAllEndpoints();
app.MapHub<JobHub>("/hubs/jobs");

app.Run();
