using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectUrlDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "url-diagnostics-" + Guid.NewGuid().ToString("N"));

    public ProjectUrlDiagnosticsTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Redact_BoundsOutputAndRemovesCommonSecrets()
    {
        var raw = new string('x', 20_000) + "\ntoken=super-secret\nAuthorization: Bearer abc.def\nhttps://user:pass@example.test/\n";

        var result = ProjectUrlProcessService.Redact(raw);

        Assert.DoesNotContain("super-secret", result);
        Assert.DoesNotContain("abc.def", result);
        Assert.DoesNotContain("user:pass", result);
        Assert.Contains("[REDACTED]", result);
        Assert.True(result.Length <= ProjectUrlProcessService.OutputTailLimit + 1);
    }

    [Fact]
    public async Task StartAsync_InvalidCwdHasDistinctDiagnosisWithoutSpawning()
    {
        var service = new ProjectUrlProcessService(
            NullLogger<ProjectUrlProcessService>.Instance,
            new UnusedHttpClientFactory());
        var project = new ProjectRecord { Id = "PROJ-001", RepositoryPath = _root };
        var url = new ProjectUrlRecord
        {
            Id = "url-1", Url = "http://127.0.0.1:4184",
            StartRule = new ProjectUrlStartRule { Command = "npm start", Cwd = "missing-folder", Port = 4184 },
        };

        var result = await service.StartAsync(project, url, CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.InvalidCwd, result.Classification);
        Assert.False(result.ProcessCreated);
        Assert.Contains("does not exist", result.Summary);
    }

    [Fact]
    public async Task StartAsync_InvalidCwdRedactsSecretPathSegments()
    {
        var service = new ProjectUrlProcessService(
            NullLogger<ProjectUrlProcessService>.Instance,
            new UnusedHttpClientFactory());
        var url = new ProjectUrlRecord
        {
            Id = "url-1", Url = "http://127.0.0.1:4184",
            StartRule = new ProjectUrlStartRule { Command = "npm start", Cwd = "password=private", Port = 4184 },
        };

        var result = await service.StartAsync(Project(), url, CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.InvalidCwd, result.Classification);
        Assert.DoesNotContain("private", result.Cwd);
        Assert.Contains("[REDACTED]", result.Cwd);
    }

    [Fact]
    public async Task StartAsync_FailedCommandIsCommandUnavailableWithBoundedEvidence()
    {
        var service = Service();
        var result = await service.StartAsync(Project(), Url("agent-studio-command-that-does-not-exist"), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.CommandUnavailable, result.Classification);
        Assert.True(result.ProcessCreated);
        Assert.NotNull(result.ExitCode);
        Assert.True(result.StderrTail.Length <= ProjectUrlProcessService.OutputTailLimit);
    }

    [Fact]
    public async Task StartAsync_ProcessExitIsNotRunning()
    {
        var command = OperatingSystem.IsWindows() ? "exit /b 7" : "exit 7";
        var result = await Service().StartAsync(Project(), Url(command), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.ProcessExited, result.Classification);
        Assert.Equal(7, result.ExitCode);
        Assert.False(result.ContentReady);
    }

    [Fact]
    public async Task StartAsync_MissingCommandIsInvalidConfiguration()
    {
        var result = await Service().StartAsync(Project(), Url(""), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.InvalidConfiguration, result.Classification);
        Assert.False(result.ProcessCreated);
    }

    [Fact]
    public async Task TestAsync_LiveProcessWhosePortNeverOpensIsBoundedAndStopped()
    {
        var port = ReserveUnusedPort();
        var command = OperatingSystem.IsWindows() ? "ping 127.0.0.1 -n 5 >nul" : "sleep 4";
        var candidate = UrlAt(port) with
        {
            StartRule = new ProjectUrlStartRule { Command = command, Port = port, ReadinessTimeoutSeconds = 2 },
        };

        var result = await Service().TestAsync(Project(), candidate, CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.PortNeverOpened, result.Classification);
        Assert.True(result.ProcessCreated);
        Assert.True(result.TimedOut);
        Assert.False(result.PortReachable);
    }

    [Fact]
    public async Task StartAsync_PublishesStartingWhileReadinessIsInFlight()
    {
        var service = Service();
        var project = Project();
        var port = ReserveUnusedPort();
        var command = OperatingSystem.IsWindows() ? "ping 127.0.0.1 -n 5 >nul" : "sleep 4";
        var candidate = UrlAt(port) with
        {
            StartRule = new ProjectUrlStartRule { Command = command, Port = port, ReadinessTimeoutSeconds = 5 },
        };
        using var cancellation = new CancellationTokenSource();

        var run = service.TestAsync(project, candidate, cancellation.Token);
        for (var attempt = 0; attempt < 20 && service.Latest(project, candidate)?.Classification != ProjectUrlDiagnosisClasses.Starting; attempt++)
            await Task.Delay(20);

        Assert.Equal(ProjectUrlDiagnosisClasses.Starting, service.Latest(project, candidate)?.Classification);
        cancellation.Cancel();
        var result = await run;
        Assert.Equal(ProjectUrlDiagnosisClasses.Timeout, result.Classification);
    }

    [Fact]
    public async Task ProbeAsync_HttpErrorIsNotRunning()
    {
        using var listener = ListeningPort(out var port);
        var service = Service(() => HtmlResponse(HttpStatusCode.ServiceUnavailable, "<main>Unavailable</main>"));

        var result = await service.ProbeAsync(Project(), UrlAt(port), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.HttpError, result.Classification);
        Assert.Equal(503, result.HttpStatus);
        Assert.False(result.ContentReady);
    }

    [Fact]
    public async Task ProbeAsync_ExplicitFramePolicyIsNotRenderable()
    {
        using var listener = ListeningPort(out var port);
        var service = Service(() =>
        {
            var response = HtmlResponse(HttpStatusCode.OK, "<main>Ready</main>");
            response.Headers.TryAddWithoutValidation("X-Frame-Options", "DENY");
            return response;
        });

        var result = await service.ProbeAsync(Project(), UrlAt(port), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.ContentNotRenderable, result.Classification);
        Assert.True(result.ContentReady);
        Assert.False(result.IframeReady);
        Assert.Contains("X-Frame-Options", result.FramePolicy);
    }

    [Fact]
    public async Task ProbeAsync_BlankHtmlIsNotRenderable()
    {
        using var listener = ListeningPort(out var port);
        var service = Service(() => HtmlResponse(HttpStatusCode.OK, "<html><head><title>Blank</title></head><body></body></html>"));

        var result = await service.ProbeAsync(Project(), UrlAt(port), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.ContentNotRenderable, result.Classification);
        Assert.False(result.ContentReady);
        Assert.False(result.IframeReady);
    }

    [Fact]
    public async Task ProbeAsync_RenderableHtmlRequiresTcpAndHttpEvidence()
    {
        using var listener = ListeningPort(out var port);
        var service = Service(() => HtmlResponse(HttpStatusCode.OK, "<main>Ready</main>"));

        var result = await service.ProbeAsync(Project(), UrlAt(port), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.Running, result.Classification);
        Assert.True(result.PortReachable);
        Assert.Equal(200, result.HttpStatus);
        Assert.True(result.ContentReady);
    }

    [Fact]
    public async Task ProbeAsync_HttpTimeoutIsNotRunning()
    {
        using var listener = ListeningPort(out var port);
        var service = Service(() => throw new TaskCanceledException("bounded timeout"));

        var result = await service.ProbeAsync(Project(), UrlAt(port), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.Timeout, result.Classification);
        Assert.True(result.TimedOut);
        Assert.False(result.ContentReady);
    }

    [Fact]
    public async Task ProbeAsync_NoListenerIsNotStarted()
    {
        var port = ReserveUnusedPort();
        var result = await Service().ProbeAsync(Project(), UrlAt(port), CancellationToken.None);

        Assert.Equal(ProjectUrlDiagnosisClasses.NotStarted, result.Classification);
        Assert.False(result.PortReachable);
    }

    [Fact]
    public void Detection_FindsAgentStudioWebsiteReadmeRuleWithCwdAndPort()
    {
        File.WriteAllText(Path.Combine(_root, "README.md"), """
            ## Preview
            ```sh
            cd 04-angular-static-final
            npm start -- --host 127.0.0.1 --port 4184
            ```
            """);
        Directory.CreateDirectory(Path.Combine(_root, "04-angular-static-final"));
        var service = new ProjectUrlDetectionService(NullLogger<ProjectUrlDetectionService>.Instance);

        var result = service.Detect(new ProjectRecord { Id = "PROJ-012", RepositoryPath = _root });

        var suggestion = Assert.Single(result);
        Assert.Equal("npm start -- --host 127.0.0.1 --port 4184", suggestion.Command);
        // Suggestions carry absolute working directories (develop convention:
        // NormaliseStartRule validates cwd as an absolute, existing path).
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "04-angular-static-final")), suggestion.Cwd);
        Assert.Equal(4184, suggestion.Port);
        Assert.Equal("http://localhost:4184", suggestion.Url);
        Assert.Equal("readme", suggestion.Source);
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("HTTP should not be used for invalid cwd.");
    }

    private sealed class StubHttpClientFactory(Func<HttpResponseMessage> response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(response), disposeHandler: true);
    }

    private sealed class StubHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response());
    }

    private ProjectUrlProcessService Service() => new(
        NullLogger<ProjectUrlProcessService>.Instance,
        new UnusedHttpClientFactory());

    private ProjectUrlProcessService Service(Func<HttpResponseMessage> response) => new(
        NullLogger<ProjectUrlProcessService>.Instance,
        new StubHttpClientFactory(response));

    private ProjectRecord Project() => new() { Id = "PROJ-001", RepositoryPath = _root };

    private static ProjectUrlRecord Url(string command) => new()
    {
        Id = "url-1", Url = "http://127.0.0.1:4184",
        StartRule = new ProjectUrlStartRule { Command = command, Port = 4184, ReadinessTimeoutSeconds = 2 },
    };

    private static ProjectUrlRecord UrlAt(int port) => new()
    {
        Id = "url-1", Url = $"http://127.0.0.1:{port}",
        StartRule = new ProjectUrlStartRule { Command = "unused", Port = port, ReadinessTimeoutSeconds = 2 },
    };

    private static TcpListener ListeningPort(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static int ReserveUnusedPort()
    {
        using var listener = ListeningPort(out var port);
        return port;
    }

    private static HttpResponseMessage HtmlResponse(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html"),
    };
}
