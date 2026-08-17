using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Routing;
using System.Text.Json;

TaskServerCommandLine command;
try
{
    command = TaskServerCommandLine.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

if (command.Kind == TaskServerCommandKind.Version)
{
    Console.WriteLine($"task-server {TaskServerBuildIdentity.Current.DisplayVersion}");
    return 0;
}

var builder = WebApplication.CreateBuilder(command.HostArguments);
if (string.Equals(Environment.GetEnvironmentVariable("TASK_SERVER_PROFILE"), "local-compatibility", StringComparison.OrdinalIgnoreCase))
    builder.Configuration.AddJsonFile("appsettings.LocalCompatibility.json", optional: false, reloadOnChange: false);
var taskServerDeploymentProfile = Environment.GetEnvironmentVariable("TASK_SERVER_PROFILE")
                                  ?? builder.Configuration[$"{TaskServerOptions.SectionName}:DeploymentProfile"]
                                  ?? "task-server";
taskServerDeploymentProfile = ValidateTaskServerDeploymentProfile(taskServerDeploymentProfile);
var executionAdmissionPolicy = new ExecutionAdmissionPolicy(taskServerDeploymentProfile);
builder.Services.AddSingleton(executionAdmissionPolicy);
builder.Services.AddSingleton(serviceProvider =>
    TaskServerBootstrapOptions.Load(
        serviceProvider.GetRequiredService<IConfiguration>()));
builder.Services
    .AddOptions<TaskServerOptions>()
    .Bind(builder.Configuration.GetSection(TaskServerOptions.SectionName))
    .Configure<TaskServerBootstrapOptions>((options, bootstrap) =>
    {
        options.DataDirectory = bootstrap.StorePath;
        options.BackupDirectory = bootstrap.BackupPath;
        options.ListenUrl = bootstrap.ListenUrl;
    });
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IResultFinalizationSummaryGenerator, ApplicationResultFinalizationSummaryGenerator>();
builder.Services.AddSingleton<TaskServerStore>();
builder.Services.AddSingleton<RuntimeCapacitySettingsService>();
builder.Services.AddSingleton<HostProjectPolicyService>();
builder.Services.AddSingleton<LegacyMigrationService>();
builder.Services.AddSingleton<IResultRefDeleter, GitResultRefDeleter>();
builder.Services.AddHostedService<TaskServerInvariantReconciliationService>();
if (!executionAdmissionPolicy.IsPublicDemoLocked)
    builder.Services.AddHostedService<ResultRefGcHostedService>();

var configuredUrl = builder.Configuration["LISTEN_URL"]
                    ?? builder.Configuration[$"{TaskServerOptions.SectionName}:ListenUrl"];
if (!string.IsNullOrWhiteSpace(configuredUrl)
    && !builder.Environment.IsEnvironment("Testing")
    && string.IsNullOrWhiteSpace(builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey)))
    builder.WebHost.UseUrls(configuredUrl);

var app = builder.Build();
var effectiveTaskServerProfile = Environment.GetEnvironmentVariable("TASK_SERVER_PROFILE")
                                 ?? app.Configuration[$"{TaskServerOptions.SectionName}:DeploymentProfile"]
                                 ?? "task-server";
effectiveTaskServerProfile = ValidateTaskServerDeploymentProfile(effectiveTaskServerProfile);
if (!string.Equals(
        executionAdmissionPolicy.DeploymentProfile,
        new ExecutionAdmissionPolicy(effectiveTaskServerProfile).DeploymentProfile,
        StringComparison.Ordinal))
{
    throw new InvalidOperationException(
        "TaskServer:DeploymentProfile changed while the host was being constructed. " +
        "The execution admission profile is startup-only, so startup was refused.");
}
app.UseRouting();
app.UseMiddleware<PublicDemoExecutionAdmissionMiddleware>();
app.UseMiddleware<TaskServerAuthenticationMiddleware>();
app.UseMiddleware<TaskServerProtocolMiddleware>();
app.MapTaskServerEndpoints();
PublicDemoTaskServerRouteMatrix.ProveAtStartup(
    executionAdmissionPolicy,
    ((IEndpointRouteBuilder)app).DataSources);

var store = app.Services.GetRequiredService<TaskServerStore>();
if (command.Kind == TaskServerCommandKind.Backup)
{
    try
    {
        await store.InitializeForBackupAsync();
        var backup = await store.CreateBackupAsync(
            new BackupRequest(command.BackupName ?? "timer"),
            "task-server-backup-command",
            default);
        Console.WriteLine(JsonSerializer.Serialize(
            backup,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Task Server backup failed: {exception.Message}");
        return 1;
    }
}

await store.InitializeAsync(app.Lifetime.ApplicationStopping);
await app.RunAsync();
return 0;

static string ValidateTaskServerDeploymentProfile(string profile)
{
    var normalized = profile.Trim().ToLowerInvariant();
    return normalized switch
    {
        "task-server" or "local-compatibility" or DeploymentProfiles.PublicDemoReadonly => normalized,
        _ => throw new InvalidOperationException(
            $"Unknown Task Server deployment profile '{profile}'. Refusing to select an execution-enabled fallback."),
    };
}

public partial class Program;
