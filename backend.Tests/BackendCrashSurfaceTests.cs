using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrchestratorApi.Services.Diagnostics;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the contract that defines the backend's "what just crashed?"
/// surface. Two layers are exercised:
///
/// <list type="number">
///   <item>The recorder + sink in isolation: a flattened
///   <see cref="AggregateException"/> produces both a structured log
///   line and a populated <c>last-crash.json</c> marker.</item>
///   <item>The full hosting path: a hosted service that fires off a
///   throwing fire-and-forget task surfaces through the wired
///   <c>TaskScheduler.UnobservedTaskException</c> handler in
///   Program.cs, and the resulting marker is served by
///   <c>/api/diagnostics/last-crash</c>.</item>
/// </list>
///
/// Together these are the proof asked for by the
/// backend-observability-real-logs task: an agent woken to "the dev
/// backend crashed" has a single, machine-readable place to look.
/// </summary>
public class BackendCrashSurfaceTests
{
    [Fact]
    public void Recorder_FlattensAggregate_AndWritesMarkerPlusStructuredLogLine()
    {
        using var temp = new TempDir();
        var options = new BackendFileLoggerOptions { LogDirectory = temp.Path, RetentionDays = 14 };
        using var sink = new BackendFileLogSink(options);
        var recorder = new CrashRecorder(options, sink);

        var inner = new InvalidOperationException("simulated hosted-service crash sk-ant-AAAAAAAAAAAAAAAAAAAA");
        var thrown = TryThrow(() => throw new AggregateException("outer", inner));

        var record = recorder.Record("HostedServiceTest", thrown!, isTerminating: false);

        Assert.Equal("System.InvalidOperationException", record.ExceptionType);
        Assert.Contains("simulated hosted-service crash", record.Message);
        Assert.Contains("[REDACTED]", record.Message); // anthropic key was scrubbed
        Assert.False(string.IsNullOrEmpty(record.TopFrame));

        var markerPath = Path.Combine(temp.Path, "last-crash.json");
        Assert.True(File.Exists(markerPath), "last-crash.json must be written");

        var json = JsonDocument.Parse(File.ReadAllText(markerPath));
        Assert.Equal("HostedServiceTest", json.RootElement.GetProperty("source").GetString());
        Assert.Equal("System.InvalidOperationException", json.RootElement.GetProperty("exceptionType").GetString());
        Assert.Contains("simulated hosted-service crash", json.RootElement.GetProperty("message").GetString());
        Assert.False(json.RootElement.GetProperty("isTerminating").GetBoolean());

        var logFile = Path.Combine(temp.Path, $"{DateTime.UtcNow:yyyy-MM-dd}.log");
        Assert.True(File.Exists(logFile), "daily log file must be written");
        var logContent = File.ReadAllText(logFile);
        Assert.Contains("Backend.Crash", logContent);
        Assert.Contains("[HostedServiceTest]", logContent);
        Assert.Contains("InvalidOperationException", logContent);
        Assert.DoesNotContain("sk-ant-AAAA", logContent); // redaction applied to log line too
    }

    [Fact]
    public void Sink_RotatesByDay_AndPrunesPastRetention()
    {
        using var temp = new TempDir();
        // Drop a 30-day-old log file; the sink must reap it on construction.
        var oldDay = DateTime.UtcNow.AddDays(-30);
        var oldFile = Path.Combine(temp.Path, $"{oldDay:yyyy-MM-dd}.log");
        Directory.CreateDirectory(temp.Path);
        File.WriteAllText(oldFile, "stale");

        using var sink = new BackendFileLogSink(new BackendFileLoggerOptions
        {
            LogDirectory = temp.Path,
            RetentionDays = 14,
            MinimumLevel = LogLevel.Information,
        });
        sink.Write(LogLevel.Information, "Demo", "hello world", null);

        Assert.False(File.Exists(oldFile), "files older than retention must be pruned");
        Assert.True(File.Exists(sink.CurrentLogPath));
        Assert.Contains("hello world", File.ReadAllText(sink.CurrentLogPath));
    }

    [Fact]
    public async Task UnobservedTaskException_FromHostedService_SurfacesViaDiagnosticsEndpoint()
    {
        using var temp = new TempDir();
        // The marker file is keyed on the resolved log directory at
        // process startup, so we point the WebApplicationFactory at the
        // temp dir before it boots.
        Environment.SetEnvironmentVariable("Logging__BackendFile__LogDirectory", temp.Path);
        try
        {
            await using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b =>
                {
                    b.UseEnvironment("Test");
                    b.ConfigureAppConfiguration((_, cfg) =>
                    {
                        cfg.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Logging:BackendFile:LogDirectory"] = temp.Path,
                            ["Logging:BackendFile:RetentionDays"] = "14",
                        });
                    });
                    b.ConfigureServices(s =>
                    {
                        s.AddHostedService<ThrowingHostedService>();
                    });
                });

            using var client = factory.CreateClient();

            // Give the hosted service time to fire the throwing task and
            // for the GC to surface the UnobservedTaskException.
            CrashRecord? crash = null;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                var resp = await client.GetAsync("/api/diagnostics/last-crash");
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    crash = JsonSerializer.Deserialize<CrashRecord>(body, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                    });
                    if (crash != null && crash.Source == "UnobservedTaskException") break;
                }
                await Task.Delay(150);
            }

            Assert.NotNull(crash);
            Assert.Equal("UnobservedTaskException", crash!.Source);
            Assert.Contains("synthetic-hosted-service-crash", crash.Message);

            // The full stack must also have landed in the daily log file.
            var logFile = Path.Combine(temp.Path, $"{DateTime.UtcNow:yyyy-MM-dd}.log");
            Assert.True(File.Exists(logFile));
            Assert.Contains("UnobservedTaskException", File.ReadAllText(logFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Logging__BackendFile__LogDirectory", null);
        }
    }

    /// <summary>
    /// Pins the host-survival contract: a HostedService whose
    /// <c>ExecuteAsync</c> faults must not stop the rest of the host. .NET's
    /// default <see cref="BackgroundServiceExceptionBehavior"/> is
    /// <see cref="BackgroundServiceExceptionBehavior.StopHost"/>; the dev
    /// backend silently went unreachable on 2026-05-04 because a tick
    /// somewhere inside <c>TaskRunnerService.ExecuteAsync</c> threw and
    /// took the whole API down with it.
    ///
    /// <para>Regression for that incident: <c>Program.cs</c> sets
    /// <see cref="BackgroundServiceExceptionBehavior.Ignore"/> so the
    /// offending service stops in isolation and other endpoints keep
    /// serving. This test boots the real <c>Program</c> with an extra
    /// hosted service that throws inside its first tick, then asserts the
    /// HTTP surface (here <c>/healthz</c>) stays reachable.</para>
    /// </summary>
    [Fact]
    public async Task HostStaysUp_WhenHostedServiceExecuteAsyncThrows()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Test");
                b.ConfigureServices(s =>
                {
                    s.AddHostedService<ExecuteAsyncThrowingHostedService>();
                });
            });

        using var client = factory.CreateClient();

        // Give the throwing hosted service time to fault.
        await Task.Delay(200);

        var resp = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private static Exception? TryThrow(Action action)
    {
        try { action(); }
        catch (Exception ex) { return ex; }
        return null;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "atp-crash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Mimics a real bug-class hosted service that fires off a Task it
    /// never awaits. The thrown exception becomes
    /// <see cref="TaskScheduler.UnobservedTaskException"/> when the GC
    /// finalises the orphaned task - which is exactly the silent-crash
    /// path this task is here to make visible.
    /// </summary>
    private sealed class ThrowingHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = Task.Run(() => throw new InvalidOperationException("synthetic-hosted-service-crash"));
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Mimics the May 4 dev-backend crash class: a
    /// <see cref="BackgroundService"/> whose <c>ExecuteAsync</c> escapes a
    /// throw. With the default <see cref="BackgroundServiceExceptionBehavior.StopHost"/>
    /// the whole host shuts down. Program.cs flips this to Ignore so the
    /// rest of the API stays reachable.
    /// </summary>
    private sealed class ExecuteAsyncThrowingHostedService : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
            throw new InvalidOperationException("synthetic-execute-async-crash");
    }
}
