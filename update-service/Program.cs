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

builder.WebHost.UseUrls(options.ListenUrl);

var app = builder.Build();

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
