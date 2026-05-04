using OrchestratorApi.Endpoints;
using OrchestratorApi.Hubs;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Pty;
using OrchestratorApi.Services.Quota;
using OrchestratorApi.Services.Runner;
using OrchestratorApi.Services.Supervisor;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Diagnostics;

// Last-resort safety nets: an uncaught exception in a fire-and-forget Task
// (e.g. CLI output streaming, SignalR fan-out) used to take down the whole API
// silently with an empty stderr. Log them instead of crashing.
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Console.Error.WriteLine($"[UnobservedTaskException] {e.Exception}");
    e.SetObserved();
};
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Console.Error.WriteLine($"[UnhandledException] terminating={e.IsTerminating} {e.ExceptionObject}");
};

var builder = WebApplication.CreateBuilder(args);

// Local-only override file (gitignored) - sets per-checkout flags such as
// Environment:IsDev. Loaded after appsettings.Development.json so a developer
// can flip the dev banner / dev PWA icon on for their checkout without
// committing the toggle.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddSingleton<JobScannerService>();
builder.Services.AddSingleton<JobStateMachine>();
builder.Services.AddSingleton<JobMutationService>();
builder.Services.AddSingleton<JobSessionLog>();
builder.Services.AddSingleton<OrchestratorChatLog>();
builder.Services.AddSingleton<OrchestratorLog>();
builder.Services.AddSingleton<OrchestratorChat>();
builder.Services.AddSingleton<OrchestratorChatService>();
builder.Services.AddSingleton<OrchestratorRunner>();
builder.Services.AddSingleton<OrchestratorSessionStore>();
builder.Services.AddSingleton<GlobalOrchestratorSessionStore>();
builder.Services.AddSingleton<GlobalOrchestratorBootstrap>();
builder.Services.AddSingleton<TokenSummaryService>();
builder.Services.AddSingleton<JobTransitionService>();
builder.Services.AddSingleton<JobWatcherService>();
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
builder.Services.AddSingleton<TaskRunnerService>();
builder.Services.AddSingleton<ProjectObservationService>();
builder.Services.AddSingleton<SupervisorInterventionService>();
builder.Services.AddHostedService<HardHealthCheckHostedService>();
builder.Services.AddHostedService<SoftReasoningHostedService>();
builder.Services.AddHostedService<AutoInterventionHostedService>();
builder.Services.AddSingleton<GitService>();
builder.Services.AddSingleton<ProjectSettingsService>();
builder.Services.AddSingleton<ProjectDocsService>();
// Quota probes: each CLI gets its own probe instance, all surfaced through QuotaService.
builder.Services.AddSingleton<IQuotaProbe, CopilotQuotaProbe>();
builder.Services.AddSingleton<IQuotaProbe, ClaudeQuotaProbe>();
builder.Services.AddSingleton<IQuotaProbe, CodexQuotaProbe>();
builder.Services.AddSingleton<IQuotaProbe, GeminiQuotaProbe>();
builder.Services.AddSingleton<QuotaCacheStore>();
builder.Services.AddSingleton<QuotaService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobWatcherService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskRunnerService>());
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

// Ensure state folders exist and migrate legacy flat jobs
app.Services.GetRequiredService<JobStateMachine>().EnsureStateFoldersAndMigrate();

// Wire up FileSystemWatcher → SignalR push
var watcher = app.Services.GetRequiredService<JobWatcherService>();
var hubContext = app.Services.GetRequiredService<IHubContext<JobHub>>();
watcher.OnJobChanged += _ => hubContext.Clients.All.SendAsync("jobsChanged");

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
