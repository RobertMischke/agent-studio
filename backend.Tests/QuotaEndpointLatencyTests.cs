using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

public sealed class QuotaEndpointLatencyTests
{
    private readonly ITestOutputHelper _output;

    public QuotaEndpointLatencyTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task GetQuota_ReturnsCachedReportWhileProbeSynchronousPrefixIsBlocked()
    {
        var taskRepository = Path.Combine(Path.GetTempPath(), "quota-endpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(taskRepository);
        using var release = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        using var completed = new ManualResetEventSlim(false);
        var probe = new BlockingProbe(started, release, completed);

        try
        {
            await using (var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = taskRepository,
                        ["Quota:ProbeTimeoutSeconds"] = "5"
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.RemoveAll<IQuotaProbe>();
                    services.AddSingleton<IQuotaProbe>(probe);
                });
            }))
            {
                using var client = factory.CreateClient();
                using var warmup = await client.GetAsync("/healthz");
                warmup.EnsureSuccessStatusCode();

                var stopwatch = Stopwatch.StartNew();
                using var response = await client.GetAsync("/api/cli/quota").WaitAsync(TimeSpan.FromSeconds(2));
                stopwatch.Stop();

                response.EnsureSuccessStatusCode();
                Assert.True(started.Wait(TimeSpan.FromSeconds(2)), "background probe did not start");
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                    $"cache-only quota GET took {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
                _output.WriteLine($"GET /api/cli/quota latency: {stopwatch.Elapsed.TotalMilliseconds:F1} ms while probe remained blocked");
                release.Set();
                Assert.True(completed.Wait(TimeSpan.FromSeconds(2)), "background probe did not finish after release");
                var cachePath = Path.Combine(taskRepository, ".runtime", "quota-cache.json");
                Assert.True(SpinWait.SpinUntil(() => File.Exists(cachePath), TimeSpan.FromSeconds(2)),
                    "background probe did not persist its result");
            }
        }
        finally
        {
            release.Set();
            try { Directory.Delete(taskRepository, recursive: true); } catch { }
        }
    }

    private sealed class BlockingProbe : IQuotaProbe
    {
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;
        private readonly ManualResetEventSlim _completed;

        public BlockingProbe(
            ManualResetEventSlim started,
            ManualResetEventSlim release,
            ManualResetEventSlim completed)
        {
            _started = started;
            _release = release;
            _completed = completed;
        }

        public string CliType => "codex";

        public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
        {
            _started.Set();
            _release.Wait(ct);
            _completed.Set();
            return Task.FromResult(new QuotaSnapshot { CliType = CliType });
        }
    }
}
