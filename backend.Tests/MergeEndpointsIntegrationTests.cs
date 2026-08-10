using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// HTTP-level coverage for the consolidation API routes. The service tests
/// prove the folder/timeline/audit behavior; these tests pin the public
/// operator-facing <c>/api/tasks</c> contract required by the task spec.
/// </summary>
public sealed class MergeEndpointsIntegrationTests : IDisposable
{
    private const string ProjectName = "merge-endpoint-test";

    private readonly string _workspace;
    private readonly string _watchPath;

    public MergeEndpointsIntegrationTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "atp-merge-endpoints-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task JobsMergeRoutes_PreviewMergeAndUndo_UseJobsApiContract()
    {
        WriteJob(TaskStates.Ready, "primary", "Primary", "Implement primary change.");
        WriteJob(TaskStates.Backlog, "human-decision-needed-primary", "Wrapper",
            "Wrapper around primary. It references primary and carries old run history.");
        AppendTimelineEvent("human-decision-needed-primary", TimelineEventKinds.AgentRunStarted, "Wrapper run started");
        AppendTimelineEvent("human-decision-needed-primary", TimelineEventKinds.AgentRunFinished, "Wrapper run finished");

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        // Mutating routes cross the X-Client-Id write boundary
        // (ClientIdentityMiddleware); register as the default client like the
        // other write-endpoint integration tests do.
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_watchPath);

        using var candidates = await client.GetAsync($"/api/tasks/primary/merge/candidates?watchPath={watchPath}");
        candidates.EnsureSuccessStatusCode();
        var candidatesJson = await candidates.Content.ReadAsStringAsync();
        Assert.Contains("human-decision-needed-primary", candidatesJson);

        var mergeRequest = new MergeRequest
        {
            SecondaryId = "human-decision-needed-primary",
            Mode = MergeModes.Consolidate,
            Reason = "wrapper history belongs on primary",
        };

        using var preview = await client.PostAsJsonAsync($"/api/tasks/primary/merge/preview?watchPath={watchPath}", mergeRequest);
        preview.EnsureSuccessStatusCode();
        var previewJson = await preview.Content.ReadAsStringAsync();
        Assert.Contains(TimelineEventKinds.MergedIn, previewJson);
        Assert.Contains(TimelineEventKinds.AgentRunStarted, previewJson);

        using var merge = await client.PostAsJsonAsync($"/api/tasks/primary/merge?watchPath={watchPath}", mergeRequest);
        merge.EnsureSuccessStatusCode();
        using var mergeBody = JsonDocument.Parse(await merge.Content.ReadAsStringAsync());
        var restoreToken = mergeBody.RootElement.GetProperty("restoreToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(restoreToken));
        Assert.True(mergeBody.RootElement.GetProperty("timelineEventsAppended").GetInt32() >= 3);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Backlog, "human-decision-needed-primary")));
        Assert.True(Directory.Exists(Path.Combine(_workspace, ".archive", "merged")));
        Assert.True(File.Exists(Path.Combine(_workspace, ".audit", "merges.jsonl")));

        using var undo = await client.PostAsJsonAsync(
            "/api/tasks/primary/merge/undo",
            new MergeUndoRequest { RestoreToken = restoreToken! });
        undo.EnsureSuccessStatusCode();
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Backlog, "human-decision-needed-primary")));
    }

    [Fact]
    public async Task JobsReEvaluateRoute_ReopensFalseCompletedCard()
    {
        WriteJob(TaskStates.Completed, "false-done", "False Done",
            "Fix the parser bug in TaskMergeEndpoints.",
            "Result: Success. Done.",
            commitSha: null,
            noBranchExpected: true);

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        // Mutating routes cross the X-Client-Id write boundary
        // (ClientIdentityMiddleware); register as the default client like the
        // other write-endpoint integration tests do.
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var watchPath = Uri.EscapeDataString(_watchPath);

        using var response = await client.PostAsync($"/api/tasks/false-done/re-evaluate?watchPath={watchPath}", content: null);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains(AuditVerdicts.NotReallyDone, json);
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Completed, "false-done")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "false-done")));
        Assert.Contains(
            TimelineEventKinds.QualityLoopReopened,
            File.ReadAllText(TaskPaths.TimelineLog(Path.Combine(_watchPath, TaskStates.Ready, "false-done"))));
    }

    // MachineBound 19.07.: flaky unter Parallellast im Karten-Gate (Audit-Poll-Timing).
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task CompletedLaneAuditRoutes_StartPollAndRenderReport()
    {
        WriteJob(TaskStates.Completed, "good-docs", "Good Docs",
            "Document the completed-lane API.",
            "Result: Success. Docs updated.");
        WriteJob(TaskStates.Completed, "false-code", "False Code",
            "Fix the completed-lane detector bug.",
            "Result: Success. Done.",
            commitSha: null);

        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        // Mutating routes cross the X-Client-Id write boundary
        // (ClientIdentityMiddleware); register as the default client like the
        // other write-endpoint integration tests do.
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        using var start = await client.PostAsync($"/api/projects/{ProjectName}/completed-lane/audit", content: null);

        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        using var startBody = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        var runId = startBody.RootElement.GetProperty("runId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(runId));

        JsonDocument? status = null;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                status?.Dispose();
                status = JsonDocument.Parse(await client.GetStringAsync($"/api/audits/{runId}"));
                if (status.RootElement.GetProperty("status").GetString() == "finished") break;
                await Task.Delay(50);
            }

            Assert.NotNull(status);
            Assert.Equal("finished", status!.RootElement.GetProperty("status").GetString());
            Assert.Equal(2, status.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(2, status.RootElement.GetProperty("processed").GetInt32());
            Assert.True(status.RootElement.GetProperty("notReallyDone").GetInt32() >= 1);
        }
        finally
        {
            status?.Dispose();
        }

        using var report = await client.GetAsync($"/api/projects/{ProjectName}/completed-lane/report");
        report.EnsureSuccessStatusCode();
        var reportJson = await report.Content.ReadAsStringAsync();
        Assert.Contains("Completed-lane audit", reportJson);
        Assert.Contains("Not really done", reportJson);
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Test");
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["WatchPaths:0:Name"] = ProjectName,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _watchPath,
                    });
                });
            });

    private void WriteJob(
        string state,
        string slug,
        string title,
        string promptBody,
        string statusBody = "Result: Success.",
        string? commitSha = "abc1234",
        bool noBranchExpected = false)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);

        var commitJson = commitSha == null
            ? ""
            : $",\"commits\":[{{\"sha\":\"{commitSha}\",\"message\":\"work\",\"authorEmail\":\"x@y\",\"at\":\"2026-05-29T12:00:00Z\",\"fileCount\":1}}]";
        var noBranchJson = noBranchExpected ? ",\"noBranchExpected\":true" : "";
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{title}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"{commitJson}{noBranchJson}}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), promptBody);
        File.WriteAllText(Path.Combine(dir, "status.md"), statusBody);
    }

    private void AppendTimelineEvent(string slug, string kind, string summary)
    {
        var dir = FindJobDir(slug);
        Directory.CreateDirectory(TaskPaths.LogsDir(dir));
        var line = JsonSerializer.Serialize(new TimelineEvent
        {
            Ts = DateTime.UtcNow,
            Kind = kind,
            Actor = TimelineActors.Agent,
            Summary = summary,
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        File.AppendAllText(TaskPaths.TimelineLog(dir), line + Environment.NewLine);
    }

    private string FindJobDir(string slug)
    {
        foreach (var state in TaskStates.All)
        {
            var dir = Path.Combine(_watchPath, state, slug);
            if (Directory.Exists(dir)) return dir;
        }
        throw new InvalidOperationException($"No folder for {slug}");
    }
}
