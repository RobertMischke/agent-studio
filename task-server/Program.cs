using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
if (string.Equals(Environment.GetEnvironmentVariable("TASK_SERVER_PROFILE"), "local-compatibility", StringComparison.OrdinalIgnoreCase))
    builder.Configuration.AddJsonFile("appsettings.LocalCompatibility.json", optional: false, reloadOnChange: false);
builder.Services.Configure<TaskServerOptions>(builder.Configuration.GetSection(TaskServerOptions.SectionName));
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<TaskServerStore>();
builder.Services.AddSingleton<LegacyMigrationService>();
builder.Services.AddHostedService<TaskServerInvariantReconciliationService>();

var configuredUrl = builder.Configuration[$"{TaskServerOptions.SectionName}:ListenUrl"];
if (!string.IsNullOrWhiteSpace(configuredUrl)
    && !builder.Environment.IsEnvironment("Testing")
    && string.IsNullOrWhiteSpace(builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey)))
    builder.WebHost.UseUrls(configuredUrl);

var app = builder.Build();
var store = app.Services.GetRequiredService<TaskServerStore>();
await store.InitializeAsync(app.Lifetime.ApplicationStopping);

var authenticationRequired = app.Configuration.GetValue<bool>(
    $"{TaskServerOptions.SectionName}:RequireAuthentication");
var studioBearer = app.Configuration[
    $"{TaskServerOptions.SectionName}:StudioBearerToken"];
var runnerBearer = app.Configuration[
    $"{TaskServerOptions.SectionName}:RunnerBearerToken"];
if (authenticationRequired
    && (string.IsNullOrWhiteSpace(studioBearer) || string.IsNullOrWhiteSpace(runnerBearer)))
{
    throw new InvalidOperationException(
        "Authenticated Task Server mode requires separate StudioBearerToken and RunnerBearerToken values.");
}
if (authenticationRequired && TokenMatches(studioBearer!, runnerBearer))
{
    throw new InvalidOperationException(
        "StudioBearerToken and RunnerBearerToken must be distinct credentials.");
}

app.Use(async (context, next) =>
{
    if (!authenticationRequired
        || !context.Request.Path.StartsWithSegments("/api/v1"))
    {
        await next();
        return;
    }

    var authorization = context.Request.Headers.Authorization.FirstOrDefault();
    var presented = authorization is not null
        && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : string.Empty;
    var runnerMutation = !HttpMethods.IsGet(context.Request.Method)
        && (context.Request.Path.StartsWithSegments("/api/v1/runners")
            || context.Request.Path.StartsWithSegments("/api/v1/runs"));
    var protocolNegotiation = context.Request.Path.StartsWithSegments("/api/v1/protocol");
    var accepted = protocolNegotiation
        ? TokenMatches(presented, studioBearer) || TokenMatches(presented, runnerBearer)
        : runnerMutation
            ? TokenMatches(presented, runnerBearer)
            : TokenMatches(presented, studioBearer);
    if (!accepted)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ApiError(
            "authentication-required",
            runnerMutation
                ? "A valid Runner service credential is required."
                : "A valid Agent Studio credential is required."));
        return;
    }

    await next();
});

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1/runners")
        || context.Request.Path.StartsWithSegments("/api/v1/runs")
        || context.Request.Path.StartsWithSegments("/api/v1/reviews"))
    {
        var raw = context.Request.Headers[TaskServerProtocol.HeaderName].FirstOrDefault();
        if (!int.TryParse(raw, out var version) || !TaskServerProtocol.Supports(version))
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            await context.Response.WriteAsJsonAsync(new ApiError(
                "protocol-unsupported",
                $"Runner protocol '{raw ?? "missing"}' is outside the supported range " +
                $"{TaskServerProtocol.MinimumSupported}-{TaskServerProtocol.MaximumSupported}."));
            return;
        }
    }

    await next();
});

app.MapTaskServerEndpoints();
await app.RunAsync();

static bool TokenMatches(string presented, string? expected)
{
    if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
        return false;
    var presentedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
    var expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
    return CryptographicOperations.FixedTimeEquals(presentedDigest, expectedDigest);
}

public partial class Program;
