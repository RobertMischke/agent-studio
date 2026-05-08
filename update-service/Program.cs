using System.Text.Json;
using AgentTaskboard.UpdateService;

var builder = WebApplication.CreateBuilder(args);

// --- options binding ---------------------------------------------------------
var options = new UpdateServiceOptions();
builder.Configuration.GetSection("UpdateService").Bind(options);

options.ListenUrl       = Environment.GetEnvironmentVariable("ATP_UPDATE_LISTEN")      ?? options.ListenUrl;
options.StableCheckoutDir = Environment.GetEnvironmentVariable("ATP_STABLE_CHECKOUT")  ?? options.StableCheckoutDir;
options.DevspaceDir     = Environment.GetEnvironmentVariable("ATP_DEVSPACE_DIR")        ?? options.DevspaceDir;
options.UpdateScript    = Environment.GetEnvironmentVariable("ATP_UPDATE_SCRIPT")       ?? options.UpdateScript;
options.StopScript      = Environment.GetEnvironmentVariable("ATP_STOP_SCRIPT")         ?? options.StopScript;
options.StartScript     = Environment.GetEnvironmentVariable("ATP_START_SCRIPT")        ?? options.StartScript;
options.BackendUrl      = Environment.GetEnvironmentVariable("ATP_BACKEND_URL")         ?? options.BackendUrl;
options.HistoryFile     = Environment.GetEnvironmentVariable("ATP_UPDATE_HISTORY")      ?? options.HistoryFile;
options.RunsDirectory   = Environment.GetEnvironmentVariable("ATP_UPDATE_RUNS_DIR")     ?? options.RunsDirectory;
options.TriggerToken    = Environment.GetEnvironmentVariable("ATP_UPDATE_TOKEN")        ?? options.TriggerToken;
options.BashPath        = Environment.GetEnvironmentVariable("ATP_BASH_PATH")           ?? options.BashPath;
options.VersionFile     = Environment.GetEnvironmentVariable("ATP_VERSION_FILE")        ?? options.VersionFile;

// ADR-0031: opt-in auto-rollback. Only the env flag set to "1" / "true" turns
// it on; anything else (incl. unset) means failure stays loud and operator-
// driven. Default is OFF on purpose.
options.AutoRollback = string.Equals(
    Environment.GetEnvironmentVariable("ATP_UPDATE_AUTO_ROLLBACK"), "1", StringComparison.Ordinal)
    || string.Equals(
        Environment.GetEnvironmentVariable("ATP_UPDATE_AUTO_ROLLBACK"), "true", StringComparison.OrdinalIgnoreCase);

builder.Services.AddSingleton(options);
builder.Services.AddHttpClient();

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

builder.Services.AddSingleton<UpdateVerifier>();

builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<UpdateStatusStore>>();
    var git = sp.GetRequiredService<GitProbe>();
    Func<string> readVersion = () =>
    {
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

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.WebHost.UseUrls(options.ListenUrl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

var app = builder.Build();
app.UseCors();

// --- endpoints ---------------------------------------------------------------

app.MapGet("/healthz", () => Results.Text("\"ok\"", "application/json"));
app.MapGet("/update/health", () => Results.Text("\"ok\"", "application/json"));

var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

app.MapGet("/update/status", (UpdateStatusStore store) =>
    Results.Json(store.Get(), jsonOpts));

app.MapGet("/update/history", (UpdateStatusStore store, int? max) =>
    Results.Json(store.ReadHistory(max.GetValueOrDefault(20)), jsonOpts));

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
    return Results.Json(new TriggerResponse(runId, phase, message), statusCode: 202);
});

// ADR-0031 manual-rollback endpoint. Same auth/token gating as /update/trigger.
app.MapPost("/update/rollback", async (HttpContext ctx, UpdateOrchestrator orch, UpdateServiceOptions opt, IHostApplicationLifetime lifetime, CancellationToken ct) =>
{
    if (!string.IsNullOrEmpty(opt.TriggerToken))
    {
        var sent = ctx.Request.Headers["X-Update-Token"].FirstOrDefault();
        if (sent != opt.TriggerToken)
            return Results.Json(new { error = "unauthorized" }, statusCode: 401);
    }

    RollbackRequest? body = null;
    try { body = await ctx.Request.ReadFromJsonAsync<RollbackRequest>(ct); } catch { /* fall through */ }
    if (body == null || string.IsNullOrWhiteSpace(body.RunId))
        return Results.Json(new { error = "missing runId" }, statusCode: 400);

    var (runId, phase, message) = orch.StartManualRollback(body.RunId, lifetime.ApplicationStopping);
    return Results.Json(new RollbackResponse(runId, phase, message), statusCode: 202);
});

app.Run();
