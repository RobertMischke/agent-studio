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
using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.ProjectChat;
using OrchestratorApi.Services.Projection;
using OrchestratorApi.Services.Projection.Sources;
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

// ProcessExit fires for any normal CLR teardown - Ctrl+C, parent shell exit,
// SIGTERM, OS-initiated polite shutdown. It does NOT fire for hard kills
// (Process.Kill from outside, TerminateProcess, native crash, OOM-killer).
// By writing a small "shutdown marker" here we close the diagnostic gap:
//   - marker present + last-crash.json absent  -> graceful shutdown
//   - marker absent + last-crash.json present  -> managed exception killed it
//   - both absent                              -> native / external kill (the
//                                                 silent-disappearance case
//                                                 we hit three times on
//                                                 2026-05-15; this is the
//                                                 signal that points us at
//                                                 OS-level or Process.Kill-
//                                                 from-parent investigations
//                                                 rather than wasted time
//                                                 grepping for managed throws).
// The marker file lives next to last-crash.json so a future operator finds
// both in one glance. Failure to write is intentionally swallowed - we are
// already in the host's last-gasp window.
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    try
    {
        var dir = Path.GetFullPath(fileLoggerOptions.LogDirectory);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "last-shutdown.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
        {
            capturedAt = DateTime.UtcNow.ToString("O"),
            pid = Environment.ProcessId,
            reason = "ProcessExit",
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    catch { /* last-gasp; nothing to do */ }
    try { fileLogSink.WriteRaw($"{DateTime.UtcNow:O} INFO  Program ProcessExit fired (pid={Environment.ProcessId})"); }
    catch { }
};

builder.Services.AddSingleton<ClientIdentityStore>();
builder.Services.AddSingleton<OrchestratorConfigService>();
builder.Services.AddSingleton<WorkspaceManagementService>();
builder.Services.AddSingleton<TaskScannerService>();
builder.Services.AddSingleton<OrchestratorApi.Services.Tasks.ITaskScanner>(sp => sp.GetRequiredService<TaskScannerService>());
// F45a: workspace / project registries + jobKey resolver. Additive layer;
// not yet load-bearing for the existing lane-folder code paths (F45c).
builder.Services.AddSingleton<OrchestratorApi.Services.Registry.WorkspaceRegistry>();
builder.Services.AddSingleton<OrchestratorApi.Services.Registry.ProjectRegistry>();
builder.Services.AddSingleton<OrchestratorApi.Services.Tasks.TaskKeyResolver>();
builder.Services.AddSingleton<ScreenshotIndexService>();
// F21: per-project write mutex for the lane tree. Must be registered
// before TaskStateMachine / TaskMutationService / TaskAccessService so
// every lane-mutating service can take it as a dependency. See
// docs/architecture-3-progress-lane-writers.md.
builder.Services.AddSingleton<LaneMutexRegistry>();
// SignalR fanout for fine-grained job mutation events (jobCreated /
// jobUpdated / jobMoved / jobDeleted / jobsReordered). Registered before
// the mutation services so each takes it as a constructor dependency.
// See backend/Services/Jobs/TaskChangeNotifier.cs.
builder.Services.AddSingleton<TaskChangeNotifier>();
builder.Services.AddSingleton<TaskStateMachine>();
builder.Services.AddSingleton<TaskMutationService>();
// Consolidation/merge API + completed-lane audit (Part 1+2 of the
// api-consolidationmerge-api task). All mutations route through
// MergeService / CompletedLaneAuditService; the audit log lives at
// <TaskRepository>/.audit/merges.jsonl. AuditRunStore is in-memory so
// in-flight audit runs are lost on restart (the persistent trail is the
// per-card quality_loop_reopened events in each timeline.jsonl).
builder.Services.AddSingleton<OrchestratorApi.Services.Tasks.Merge.MergeAuditLog>();
builder.Services.AddSingleton<OrchestratorApi.Services.Tasks.Merge.MergeCandidateFinder>();
builder.Services.AddSingleton<OrchestratorApi.Services.Tasks.Merge.MergeService>();
builder.Services.AddSingleton<OrchestratorApi.Services.Tasks.Audit.AcceptanceEvidenceDetector>();
builder.Services.AddSingleton<OrchestratorApi.Services.Tasks.Audit.AuditRunStore>();
builder.Services.AddSingleton<OrchestratorApi.Services.Tasks.Audit.CompletedLaneAuditService>();
builder.Services.AddSingleton<FixtureMigrationService>();
builder.Services.AddSingleton<TaskSessionLog>();
builder.Services.AddSingleton<TimelineLog>();
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
builder.Services.AddSingleton<TaskTransitionService>();
builder.Services.AddSingleton<TaskWatcherService>();
// Cycle 1: in-memory snapshot of all jobs across watch paths. Reads from
// TaskScannerService.ScanAllJobsRaw on miss, invalidated by TaskWatcherService
// events and by mutation services. Wired into TaskScannerService below via
// SetIndexCache so existing ScanAllJobs callers transparently benefit.
builder.Services.AddSingleton<TaskIndexCache>();
// TaskAccess layer (ADR-0024 phase 2-4): the typed façade in front of
// TaskScannerService / TaskMutationService / TaskStateMachine /
// TaskTransitionService. Outside callers (endpoints, runner, supervisor)
// resolve ITaskAccess so the lane-folder shape stays inside this layer.
builder.Services.AddSingleton<OrchestratorApi.Services.TaskAccess.TaskAccessService>();
builder.Services.AddSingleton<OrchestratorApi.Services.TaskAccess.ITaskAccess>(sp =>
    sp.GetRequiredService<OrchestratorApi.Services.TaskAccess.TaskAccessService>());
builder.Services.AddSingleton<OrchestratorApi.Services.TaskAccess.ITaskAccessHost>(sp =>
    sp.GetRequiredService<OrchestratorApi.Services.TaskAccess.TaskAccessService>());
builder.Services.AddSingleton<CopilotCliEnvironment>();
builder.Services.AddSingleton<CopilotModelDiscovery>();
builder.Services.AddSingleton<CodexModelDiscovery>();
builder.Services.AddSingleton<CopilotCliService>();
builder.Services.AddSingleton<ClaudeCliService>();
builder.Services.AddSingleton<CodexCliService>();
builder.Services.AddSingleton<GeminiCliService>();
builder.Services.AddSingleton<ClaudeSessionInspector>();
builder.Services.AddSingleton<CliRouter>();
builder.Services.AddSingleton<SessionToTaskIndex>();
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<ContextUsageParser>();
builder.Services.AddSingleton<SummaryGenerationService>();
builder.Services.AddSingleton<RuntimePromptService>();
builder.Services.AddSingleton<TitleGenerationService>();
builder.Services.AddSingleton<PromptEnhancementService>();
builder.Services.AddSingleton<OrchestratorApi.Services.AdHoc.AdHocUsageRecorder>();
builder.Services.AddSingleton<OrchestratorApi.Services.AdHoc.AdHocUsageService>();
builder.Services.AddSingleton<PickupLockFile>();
builder.Services.AddSingleton<TaskRunnerService>();
builder.Services.AddSingleton<CrashRecoveryService>();
builder.Services.AddSingleton<StaleProgressArchiver>();
builder.Services.AddSingleton<PickupFailureLog>();
builder.Services.AddSingleton<InfraHaltLog>();
builder.Services.AddSingleton<CrossSlugInfraCircuitBreaker>();
builder.Services.AddSingleton<HumanReviewEscalation>();
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
builder.Services.AddSingleton<OrchestratorApi.Services.RegressionRadar.RegressionRadarService>();
builder.Services.AddSingleton<OrchestratorApi.Services.Drift.DriftReportStore>();
builder.Services.AddSingleton<OrchestratorApi.Services.Drift.AdrCodeDriftAnalysisService>();
builder.Services.AddSingleton<OrchestratorApi.Services.Drift.DocsMarketingDriftAnalysisService>();
builder.Services.AddSingleton<OrchestratorApi.Services.Drift.SpecTaskDriftAnalysisService>();
builder.Services.AddSingleton<OrchestratorApi.Services.Drift.SoftwareArchitectureDriftAnalysisService>();
builder.Services.AddSingleton<OrchestratorApi.Services.Drift.ArchitectureElementStateStore>();
builder.Services.AddSingleton<OrchestratorApi.Services.Drift.DriftPostStepRunner>();
builder.Services.AddSingleton<OrchestratorApi.Services.Tags.TagRegistryService>();
builder.Services.AddSingleton<ProjectObservationService>();
builder.Services.AddSingleton<FilesystemLayerSnapshotService>();
builder.Services.AddSingleton<SupervisorInterventionService>();
builder.Services.AddHostedService<HardHealthCheckHostedService>();
builder.Services.AddHostedService<SoftReasoningHostedService>();
builder.Services.AddHostedService<AutoInterventionHostedService>();
builder.Services.AddHostedService<MetaCycleHostedService>();
builder.Services.AddHostedService<OrchestratorPrepHostedService>();
builder.Services.AddHostedService<ChatNoteHostedService>();
builder.Services.AddSingleton<OrchestratorApi.Services.Pipeline.PipelineExecutionLog>();
builder.Services.AddSingleton<OrchestratorApi.Services.Pipeline.ProjectPipelineCostService>();
builder.Services.AddSingleton<OrchestratorApi.Services.Pipeline.ILintScssRunner,
    OrchestratorApi.Services.Pipeline.LintScssRunner>();
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
// Completed-job auto-push runs off the request path: TaskTransitionService
// enqueues here on the move to 6-completed (instant), CompletedPushWorker
// drains and performs the git push, CompletedPushBackstopHostedService is the
// periodic safety net for missed / shutdown-dropped pushes.
builder.Services.AddSingleton<CompletedPushQueue>();
builder.Services.AddHostedService<CompletedPushWorker>();
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
builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskWatcherService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskRunnerService>());
// F22: server-rendered conversation projection. The projector serves the
// GET /api/tasks/{id}/conversation endpoint and (when the feature flag is
// on) broadcasts deltas over TaskHub. Sources are registered so the
// IEnumerable<IConversationEventSource> ctor gets a deterministic order.
builder.Services.AddSingleton<IMarkdownRenderer, MarkdigRenderer>();
var projectionCacheSize = int.TryParse(builder.Configuration["ConversationProjection:CacheSize"], out var pcs) ? pcs : 50;
builder.Services.AddSingleton(new ConversationCache(projectionCacheSize));
builder.Services.AddSingleton<IConversationEventSource, CliOutputSource>();
builder.Services.AddSingleton<IConversationEventSource, OrchestratorSource>();
builder.Services.AddSingleton<IConversationEventSource, AutoReviewSource>();
builder.Services.AddSingleton<IConversationEventSource, RunnerEventSource>();
builder.Services.AddSingleton<IConversationEventSource, SystemEventSource>();
builder.Services.AddSingleton<ConversationProjector>();
builder.Services.AddSingleton<SourceWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SourceWatcher>());
// Companion app sync (ADR-0018). Default-off; the HostedService loop exits
// immediately when Companion:Enabled is false. Bound from appsettings*.json.
builder.Services.Configure<CompanionSyncOptions>(builder.Configuration.GetSection(CompanionSyncOptions.SectionName));
builder.Services.AddSingleton<CompanionCommandDispatcher>();
builder.Services.AddHttpClient("companion-relay");
builder.Services.AddHostedService<CompanionSyncService>();
// Serialise enums as camelCase strings so the frontend can use string-literal
// unions (e.g. TaskSummaryStatus = 'none' | 'generating' | 'ready' | 'failed')
// instead of numeric values.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});
builder.Services.AddSignalR();
// Bridges TaskChangeNotifier + TaskTransitionService move events onto TaskHub
// (jobCreated / jobUpdated / jobMoved / jobDeleted / jobsReordered /
// jobsBulkChanged). Resolved + move-source-attached during startup wiring
// below so the notifier subscriptions are live before the first mutation.
builder.Services.AddSingleton<OrchestratorApi.Hubs.TaskHubBroadcaster>();
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
app.Services.GetRequiredService<TaskStateMachine>().EnsureStateFoldersAndMigrate();

// Backfill jobs that carry agent:"human" + cliType:null + model:null with owner
// client defaults. Idempotent; no-op once all jobs are already migrated.
{
    var backfillCount = app.Services.GetRequiredService<TaskMutationService>().BackfillAgentDefaults();
    if (backfillCount > 0)
        app.Services.GetRequiredService<ILogger<Program>>()
            .LogInformation("Backfilled agent defaults on {Count} job(s)", backfillCount);
}

// F45a: populate workspace + project registries from configured WatchPaths.
// Additive; does not move or rename anything on disk. Writes only to
// <TaskRepository>/.metadata/. Safe to run on every boot - idempotent.
try
{
    OrchestratorApi.Services.Registry.RegistryBootstrap.Run(
        app.Services.GetRequiredService<OrchestratorApi.Services.Registry.WorkspaceRegistry>(),
        app.Services.GetRequiredService<OrchestratorApi.Services.Registry.ProjectRegistry>(),
        app.Services.GetRequiredService<TaskScannerService>(),
        app.Services.GetRequiredService<ILogger<Program>>());
}
catch (Exception ex)
{
    crashRecorder.Record("RegistryBootstrap", ex);
}

// Backfill task keys (ATP-NNN) on jobs created before key generation was wired
// into CreateJob. Runs after RegistryBootstrap so project short codes exist.
// Idempotent; no-op once all jobs carry a key.
{
    var mutations = app.Services.GetRequiredService<TaskMutationService>();
    var keyCount = mutations.BackfillTaskKeys();
    if (keyCount > 0)
        app.Services.GetRequiredService<ILogger<Program>>()
            .LogInformation("Backfilled task keys on {Count} job(s)", keyCount);

    // Resolve any duplicate display keys (two tasks sharing one key). The
    // sweep is idempotent and a no-op once every key is unique; it keeps the
    // oldest task on the contested key and re-keys the namesakes. Runs after
    // the backfill so freshly stamped jobs are part of the uniqueness check.
    var dedupCount = mutations.DeduplicateTaskKeys();
    if (dedupCount > 0)
        app.Services.GetRequiredService<ILogger<Program>>()
            .LogWarning("Resolved duplicate task keys by re-keying {Count} task(s)", dedupCount);
}

// ADR-0020: run the crash-recovery sweep BEFORE the first runner tick. Any
// surviving completion-marker.json finishes its 3-progress -> 4-review move
// here, and any orphan working-tree changes get committed under a
// crash-recovery author tag so a second crash mid-recovery is itself
// recoverable on the next boot. Sync wait is intentional: we want the
// runner to see the recovered state on its first scan.
//
// F21 boot order: CrashRecoveryService runs before StaleProgressArchiver
// because crash-recovery may complete a half-finished transition
// (3-progress -> 4-auto-review) that the archiver would otherwise mistake
// for a stuck orphan. The two services also share the per-project
// LaneMutexRegistry, so even an accidental reorder cannot produce a
// concurrent rename against the same slug - the sequential ordering is
// belt-and-braces.
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
    var archiver = app.Services.GetRequiredService<StaleProgressArchiver>();
    archiver.SweepAsync().GetAwaiter().GetResult();
    // Failed-pickup-elimination (supersedes ADR-0028/0029): drain any folders
    // that linger in the retired 3a-failed-pickup lane from before this change
    // - real tasks back to 2-ready, debris to 7-archive - after the sweep so a
    // folder requeued from 3-progress is never also drained. Idempotent once
    // the lane is empty.
    archiver.DrainFailedPickupLaneAsync().GetAwaiter().GetResult();
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
    var pushHub = app.Services.GetRequiredService<IHubContext<TaskHub>>();
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

        var scanner = app.Services.GetRequiredService<TaskScannerService>();
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
var watcher = app.Services.GetRequiredService<TaskWatcherService>();
var hubContext = app.Services.GetRequiredService<IHubContext<TaskHub>>();
watcher.OnJobChanged += _ => hubContext.Clients.All.SendAsync("jobsChanged");

// Fine-grained job-mutation push (jobCreated / jobUpdated / jobMoved /
// jobDeleted / jobsReordered / jobsBulkChanged). Resolving the singleton
// attaches its TaskChangeNotifier subscriptions; AttachMoveSource hooks the
// transition service's move event. See backend/Hubs/TaskHubBroadcaster.cs.
var jobHubBroadcaster = app.Services.GetRequiredService<OrchestratorApi.Hubs.TaskHubBroadcaster>();
jobHubBroadcaster.AttachMoveSource(app.Services.GetRequiredService<TaskTransitionService>());

// Cycle 1: bind the in-memory snapshot cache. TaskScannerService.ScanAllJobs
// now serves from cache; TaskWatcherService.OnJobChanged invalidates it on
// external file changes, mutation services invalidate it on API writes.
// Without this two-line bridge the cache exists but nothing fills or
// invalidates it, and ScanAllJobs falls back to per-call disk walks.
var jobIndexCache = app.Services.GetRequiredService<TaskIndexCache>();
app.Services.GetRequiredService<TaskScannerService>().SetIndexCache(jobIndexCache);
watcher.OnJobChanged += _ => jobIndexCache.Invalidate(TaskIndexCache.InvalidationSource.External);

// TaskAccess layer (ADR-0024): force a synchronous first index read so
// boot-time disk problems surface here rather than on the first HTTP
// request. The host's other lifecycle calls (ReloadProjectAsync,
// ShutdownAsync) are wired through the typed interface and used by
// callers, not at startup.
_ = app.Services.GetRequiredService<OrchestratorApi.Services.TaskAccess.ITaskAccessHost>()
    .BootAsync();

// Pre-warm AgentMessageBusStore projections for every watched project
// BEFORE the HTTP listener starts. The grouped-jobs endpoint folds in
// per-project token totals (BuildTokenLookup -> SummarizePerJob ->
// BusTokenEntryConverter.LoadOrchestratorEntries -> Store.Query ->
// GetOrLoad), so a cold projection forces the first /api/jobs/grouped
// caller to wait for tens of seconds while a multi-megabyte JSONL tree
// is parsed. On real workspaces (Runbook ~ 100MB / >100k lines) that
// lazy-load wedges the post-restart UpdateVerifier window — the verifier
// sees /healthz=200 but /api/jobs/grouped never drains. Paying the
// parse cost here moves the cost out of the first request. Per-project
// warmups run in parallel so total boot time is bounded by the slowest
// project rather than the sum.
{
    var warmupSw = System.Diagnostics.Stopwatch.StartNew();
    var workspaceRoot = app.Configuration["TaskRepository"];
    var scannerForWarmup = app.Services.GetRequiredService<TaskScannerService>();
    var busStore = app.Services.GetRequiredService<AgentMessageBusStore>();
    var warmupLogger = app.Services.GetRequiredService<ILogger<Program>>();
    if (string.IsNullOrWhiteSpace(workspaceRoot))
    {
        warmupLogger.LogInformation("bus-warmup-skipped reason=TaskRepository-unset");
    }
    else
    {
        var projects = scannerForWarmup.GetWatchPaths()
            .Select(e => e.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var failures = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            Parallel.ForEach(projects, project =>
            {
                try
                {
                    var n = busStore.WarmProject(workspaceRoot!, project);
                    counts[project] = n;
                }
                catch (Exception ex)
                {
                    failures[project] = ex.GetType().Name + ": " + ex.Message;
                }
            });
        }
        catch (Exception ex)
        {
            crashRecorder.Record("BusProjectionWarmup", ex);
        }
        warmupSw.Stop();
        var totalMessages = counts.Values.Sum();
        warmupLogger.LogInformation(
            "bus-warmup-complete projects={ProjectCount} messages={MessageCount} failures={FailureCount} elapsedMs={ElapsedMs}",
            counts.Count, totalMessages, failures.Count, warmupSw.ElapsedMilliseconds);
        foreach (var kv in failures)
        {
            warmupLogger.LogWarning("bus-warmup-failure project={Project} error={Error}", kv.Key, kv.Value);
        }
    }
}

// Wire TaskTransitionService move events to atomically clear the per-project
// runner's _activeJobId when the active job is moved out of 3-progress.
// Without this, an external move (API or otherwise) leaves the runner pinned
// to a slug whose folder has left the lane, every pickup tick short-circuits
// on `active != null`, and the project wedges until backend restart.
var transitionsForRunner = app.Services.GetRequiredService<TaskTransitionService>();
var runnerForTransitions = app.Services.GetRequiredService<TaskRunnerService>();
transitionsForRunner.OnJobMoved += (projectName, jobId, fromState, toState) =>
{
    if (fromState != TaskStates.Progress) return;
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
// Plan strip live push: when the agent emits a TodoWrite / update_plan frame the
// runner persists a snapshot; tell the open detail view to re-fetch /plan. Uses
// the same job identifier as cliOutput so the frontend correlates identically.
cliRouter.OnRunEvent += (cliType, jobId, evt) =>
{
    if (evt is OrchestratorApi.Services.Cli.CliRunEvent.PlanUpdated)
        hubContext.Clients.All.SendAsync("planUpdated", jobId, cliType);
};

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
app.MapConversationEndpoints();
app.MapHub<TaskHub>("/hubs/jobs");

app.Run();
