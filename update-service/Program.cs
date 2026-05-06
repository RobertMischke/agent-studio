using System.Text.Json;
using AgentTaskboard.UpdateService;

var builder = WebApplication.CreateBuilder(args);

// --- options binding ---------------------------------------------------------
var options = new UpdateServiceOptions();
builder.Configuration.GetSection("UpdateService").Bind(options);

// Env-var overrides (so the launcher script can pin paths without writing JSON).
options.ListenUrl       = Environment.GetEnvironmentVariable("ATP_UPDATE_LISTEN")      ?? options.ListenUrl;
options.StableCheckoutDir = Environment.GetEnvironmentVariable("ATP_STABLE_CHECKOUT")  ?? options.StableCheckoutDir;
options.DevspaceDir     = Environment.GetEnvironmentVariable("ATP_DEVSPACE_DIR")        ?? options.DevspaceDir;
options.UpdateScript    = Environment.GetEnvironmentVariable("ATP_UPDATE_SCRIPT")       ?? options.UpdateScript;
options.BackendUrl      = Environment.GetEnvironmentVariable("ATP_BACKEND_URL")         ?? options.BackendUrl;
options.HistoryFile     = Environment.GetEnvironmentVariable("ATP_UPDATE_HISTORY")      ?? options.HistoryFile;
options.TriggerToken    = Environment.GetEnvironmentVariable("ATP_UPDATE_TOKEN")        ?? options.TriggerToken;
options.BashPath        = Environment.GetEnvironmentVariable("ATP_BASH_PATH")           ?? options.BashPath;
options.VersionFile     = Environment.GetEnvironmentVariable("ATP_VERSION_FILE")        ?? options.VersionFile;

builder.Services.AddSingleton(options);
builder.Services.AddHttpClient();

// --- service wiring ----------------------------------------------------------
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<GitProbe>>();
    return new GitProbe(options.StableCheckoutDir, logger);
});

builder.Services.AddSingleton(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
    var logger = sp.GetRequiredService<ILogger<BackendProbe>>();
    return new BackendProbe(http, options.BackendUrl, options.BackendClientId, logger);
});

builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<UpdateStatusStore>>();
    var git = sp.GetRequiredService<GitProbe>();
    Func<string> readVersion = () =>
    {
        // First non-empty trimmed line of the VERSION file. The function
        // is invoked from inside the store every time the snapshot moves,
        // so we accept the file IO cost (it's <100 bytes).
        var path = options.VersionFile;
        if (!File.Exists(path)) return "unknown";
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith("#")) return trimmed;
        }
        return "unknown";
    };
    return new UpdateStatusStore(options.HistoryFile, git.HeadShort(), readVersion, logger);
});

builder.Services.AddSingleton<UpdateOrchestrator>();
builder.Services.AddHostedService<PeriodicProbeService>();

// CORS: the UpdateService must be reachable from the FE during a stable
// restart, when the main backend's /api proxy is dark. We are running on
// localhost only, so a wide-open CORS policy is fine.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.WebHost.UseUrls(options.ListenUrl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

var app = builder.Build();
app.UseCors();

// --- endpoints ---------------------------------------------------------------

// Plain liveness probe: the main backend's /healthz semantics. Always 200
// while the process is alive — that is the whole reason this service exists.
app.MapGet("/healthz", () => Results.Text("\"ok\"", "application/json"));
app.MapGet("/update/health", () => Results.Text("\"ok\"", "application/json"));

// Full snapshot. Cheap, lock-protected read; safe to poll every 1-30 s.
app.MapGet("/update/status", (UpdateStatusStore store) =>
{
    return Results.Json(store.Get(), new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });
});

// Tail of the append-only history (latest N entries).
app.MapGet("/update/history", (UpdateStatusStore store, int? max) =>
{
    var n = max.GetValueOrDefault(20);
    return Results.Json(store.ReadHistory(n), new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });
});

// Manual trigger.
//   - Honours optional ATP_UPDATE_TOKEN: send as X-Update-Token header.
//   - Body { reason?, force? } is recorded in history.
//   - The orchestration is intentionally decoupled from the HTTP request's
//     cancellation: a client that gives up (e.g. Playwright's 30 s default)
//     must not abort an in-flight stable restart. We tie cancellation to
//     the application's stopping token so the only thing that can cancel
//     the run is a process shutdown.
app.MapPost("/update/trigger", async (HttpContext ctx, UpdateOrchestrator orch, UpdateServiceOptions opt, IHostApplicationLifetime lifetime, CancellationToken ct) =>
{
    if (!string.IsNullOrEmpty(opt.TriggerToken))
    {
        var sent = ctx.Request.Headers["X-Update-Token"].FirstOrDefault();
        if (sent != opt.TriggerToken)
            return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    }

    TriggerRequest? body = null;
    try { body = await ctx.Request.ReadFromJsonAsync<TriggerRequest>(ct); } catch { /* empty body OK */ }

    var force = body?.Force ?? false;
    var (runId, phase, message) = orch.StartTrigger(trigger: "manual", force: force, lifetime.ApplicationStopping);
    // 202 Accepted: orchestration is running in the background. Clients poll
    // /update/status to watch phase transitions.
    return Results.Json(new TriggerResponse(runId, phase, message), statusCode: 202);
});

app.Run();
