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
    return new UpdateStatusStore(options.HistoryFile, git.HeadShort(), logger);
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
app.MapPost("/update/trigger", async (HttpContext ctx, UpdateOrchestrator orch, UpdateServiceOptions opt, CancellationToken ct) =>
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
    var (runId, phase, message) = await orch.TriggerAsync(trigger: "manual", force: force, ct);
    var status = (phase == "failed") ? 500 : 200;
    return Results.Json(new TriggerResponse(runId, phase, message), statusCode: status);
});

app.Run();
