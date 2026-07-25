using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;

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

public partial class Program;
