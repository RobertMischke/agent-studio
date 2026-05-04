using System.Reflection;
using CompanionRelay;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<RelayStore>();
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

builder.Services.AddCors(options =>
{
    var origins = (builder.Configuration["Companion:PwaOrigins"] ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    options.AddDefaultPolicy(p =>
    {
        if (origins.Length == 0) p.AllowAnyOrigin(); else p.WithOrigins(origins);
        p.AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();
app.UseCors();

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "dev";
var token = builder.Configuration["Companion:Token"]
            ?? Environment.GetEnvironmentVariable("COMPANION_TOKEN")
            ?? "";

bool Authorized(HttpContext ctx)
{
    if (string.IsNullOrEmpty(token)) return false;
    var header = ctx.Request.Headers.Authorization.ToString();
    if (string.IsNullOrEmpty(header)) return false;
    const string prefix = "Bearer ";
    if (!header.StartsWith(prefix, StringComparison.Ordinal)) return false;
    var presented = header[prefix.Length..].Trim();
    // Constant-time compare so a leak through timing analysis is harder.
    var a = System.Text.Encoding.UTF8.GetBytes(presented);
    var b = System.Text.Encoding.UTF8.GetBytes(token);
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
}

app.MapGet("/healthz", (RelayStore store) => Results.Ok(new HealthResponse
{
    Status = "ok",
    Version = version,
    LastSyncAt = store.GetLastSyncAt(),
}));

app.MapPost("/sync", async (HttpContext ctx, RelayStore store) =>
{
    if (!Authorized(ctx)) return Results.Unauthorized();
    var req = await ctx.Request.ReadFromJsonAsync<SyncRequest>();
    if (req is null) return Results.BadRequest(new { error = "invalid body" });
    store.StoreSnapshot(req.Snapshot);
    if (req.AckIds.Count > 0) store.Drop(req.AckIds);
    return Results.Ok(new SyncResponse { Commands = store.Drain().ToList() });
});

app.MapGet("/state", (HttpContext ctx, RelayStore store) =>
{
    if (!Authorized(ctx)) return Results.Unauthorized();
    return Results.Ok(new StateResponse
    {
        Snapshot = store.GetSnapshot(),
        LastSyncAt = store.GetLastSyncAt(),
        PendingCommandCount = store.PendingCount,
    });
});

app.MapPost("/commands", async (HttpContext ctx, RelayStore store) =>
{
    if (!Authorized(ctx)) return Results.Unauthorized();
    var req = await ctx.Request.ReadFromJsonAsync<EnqueueCommandRequest>();
    if (req is null || string.IsNullOrWhiteSpace(req.Kind))
        return Results.BadRequest(new { error = "kind is required" });
    var cmd = store.Enqueue(req.Kind, req.Payload);
    return Results.Ok(new EnqueueCommandResponse { Id = cmd.Id });
});

app.Run();

// Visible to the relay test project.
public partial class Program { }
