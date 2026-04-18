using OrchestratorApi.Endpoints;
using OrchestratorApi.Hubs;
using OrchestratorApi.Services;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<JobScannerService>();
builder.Services.AddSingleton<JobWatcherService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobWatcherService>());
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

app.UseCors();

// Ensure state folders exist and migrate legacy flat jobs
var scanner = app.Services.GetRequiredService<JobScannerService>();
scanner.EnsureStateFoldersAndMigrate();

// Wire up FileSystemWatcher → SignalR push
var watcher = app.Services.GetRequiredService<JobWatcherService>();
var hubContext = app.Services.GetRequiredService<IHubContext<JobHub>>();
watcher.OnJobChanged += _ => hubContext.Clients.All.SendAsync("jobsChanged");

app.MapJobEndpoints();
app.MapHub<JobHub>("/hubs/jobs");

app.Run();
