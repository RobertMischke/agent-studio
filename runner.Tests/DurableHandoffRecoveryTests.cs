using System.Net;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DurableHandoffRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "runner-recovery-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Restart_replays_handoff_and_completion_without_starting_a_coding_process()
    {
        var authority = new RunOutboxAuthority(
            "run-recovery",
            "TASK-9",
            "runner-a",
            "old-host:41",
            "lease-a",
            9);
        var outbox = DurableRunOutbox.Open(
            Path.Combine(_root, "outbox"),
            authority);
        var manifest = RemoteTaskRunner.BuildArtifactManifest([]);
        var resultSha = new string('2', 40);
        var envelope = new ImmutableResultEnvelope(
            "repo-9",
            authority.RunId,
            new string('1', 40),
            resultSha,
            $"refs/heads/agent-studio/results/{authority.RunId}/{resultSha}",
            null,
            manifest.Digest);
        outbox.Enqueue("run-context", JsonSerializer.Serialize(
            new DurableRunContextPayload(
                "repo-9", null, "main", new string('1', 40)),
            WebJson));
        outbox.Enqueue("terminal", JsonSerializer.Serialize(
            new DurableTerminalPayload("Done", null),
            WebJson));
        outbox.Enqueue("artifact-manifest", manifest.Json);
        outbox.Enqueue("final-result", JsonSerializer.Serialize(envelope, WebJson));

        var handler = new RecordingHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost"),
        };
        using var client = new TaskServerClient(
            http,
            "runner-a",
            usesDurableTaskServer: true);
        var options = Options();

        await new DurableHandoffRecovery(options, client, _ => { })
            .RecoverAllAsync(default);

        var reopened = DurableRunOutbox.Open(
            Path.Combine(_root, "outbox"),
            authority);
        Assert.Equal("completed", reopened.Snapshot.FinalHandoffState);
        Assert.Empty(reopened.Pending);
        Assert.Equal(1, handler.HandoffCalls);
        Assert.Equal(1, handler.CompletionCalls);
        Assert.Equal(0, handler.CodingProcessCalls);
    }

    private RunnerOptions Options() => new()
    {
        ServerUrl = "http://localhost",
        RunnerId = "runner-a",
        RunnerName = "runner-a",
        Hostname = "new-host",
        BackendName = "test",
        WorkDir = _root,
        BaseBranch = "main",
        CliBin = "must-not-run",
        CliArgs = "",
    };

    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int HandoffCalls { get; private set; }
        public int CompletionCalls { get; private set; }
        public int CodingProcessCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/result-handoff", StringComparison.Ordinal))
            {
                HandoffCalls++;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var handoff = JsonSerializer.Deserialize<ResultHandoffRequest>(
                    body,
                    WebJson)!;
                return Json(HttpStatusCode.OK, new ResultHandoffAck(
                    "run-recovery",
                    handoff.Sequence,
                    handoff.EnvelopeDigest,
                    "acknowledged",
                    DateTime.UtcNow,
                    DateTime.UtcNow.AddDays(30),
                    false));
            }
            if (path.EndsWith("/completion", StringComparison.Ordinal))
            {
                CompletionCalls++;
                return Json(HttpStatusCode.OK, new RunDto(
                    "run-recovery",
                    "task-9",
                    "Done",
                    "runner-a",
                    9,
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    DateTime.UtcNow));
            }
            if (path.EndsWith("/outbox-status", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, new RunnerOutboxStatusDto(
                    "runner-a",
                    "new-host:1",
                    0,
                    0,
                    0,
                    null,
                    "completed",
                    "run-recovery",
                    null,
                    DateTime.UtcNow));
            }
            if (path.EndsWith("/events", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.Created, new EventDto(
                    1,
                    $"event-{Guid.NewGuid():N}",
                    "run-recovery",
                    "task-9",
                    "runner.event",
                    "{}",
                    $"key-{Guid.NewGuid():N}",
                    9,
                    DateTime.UtcNow));
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        }

        private static HttpResponseMessage Json<T>(HttpStatusCode status, T body)
            => new(status)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body, WebJson),
                    Encoding.UTF8,
                    "application/json"),
            };
    }
}
