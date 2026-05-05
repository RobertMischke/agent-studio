using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;
using OrchestratorApi.Services.Supervisor;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the cadence and dedup contract for the supervisor chat-note ticker:
/// the first tick bootstraps the cursor (no message), a quiet window stays
/// quiet, a populated window emits exactly one message, and two consecutive
/// windows produce at most one message each. The pure summariser is covered
/// in <see cref="ChatNoteSummaryTests"/>; here we only exercise
/// <see cref="ChatNoteHostedService.EvaluateProject"/> against a real disk
/// layout so the file readers see the same shapes the production code does.
/// </summary>
public class ChatNoteHostedServiceTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _project = "test-project";

    public ChatNoteHostedServiceTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "chatnote-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void EvaluateProject_QuietWindow_WritesNoMessage()
    {
        var time = new FakeTimeProvider(new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));
        var (svc, jobs, jobLog) = BuildService(time);

        // Bootstrap: first call seeds the cursor and writes nothing.
        Assert.Null(svc.EvaluateProject(_workspace, _project, jobs, TimeSpan.FromMinutes(30)));

        time.Advance(TimeSpan.FromMinutes(31));

        // Window is quiet: no advisories, no cycles, no review-lane arrivals
        // since the cursor seeded above.
        var msg = svc.EvaluateProject(_workspace, _project, jobs, TimeSpan.FromMinutes(30));
        Assert.Null(msg);
        Assert.False(File.Exists(jobLog), "no chat-note should have been written for a quiet window");
    }

    [Fact]
    public void EvaluateProject_OneWarnAdvisory_WritesOneMessageContainingTopic()
    {
        var t0 = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);
        var time = new FakeTimeProvider(t0);
        var (svc, jobs, jobLog) = BuildService(time);

        Assert.Null(svc.EvaluateProject(_workspace, _project, jobs, TimeSpan.FromMinutes(30)));

        // Seed an observation that lands inside the next window.
        AppendAdvisory(t0.AddMinutes(15), severity: SupervisorSeverity.Warn, topic: "no-progress");

        time.Advance(TimeSpan.FromMinutes(30));
        var msg = svc.EvaluateProject(_workspace, _project, jobs, TimeSpan.FromMinutes(30));

        Assert.NotNull(msg);
        Assert.Contains("no-progress", msg!);
        Assert.True(File.Exists(jobLog));
        var contents = File.ReadAllText(jobLog);
        Assert.Contains("[supervisor]", contents);
        Assert.Contains("[chat-note]", contents);
        Assert.Contains("no-progress", contents);
    }

    [Fact]
    public void EvaluateProject_TwoWindowsBackToBack_AtMostOneMessagePerWindow()
    {
        var t0 = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);
        var time = new FakeTimeProvider(t0);
        var (svc, jobs, jobLog) = BuildService(time);

        // Bootstrap.
        Assert.Null(svc.EvaluateProject(_workspace, _project, jobs, TimeSpan.FromMinutes(30)));

        // Window 1: two advisories arrive.
        AppendAdvisory(t0.AddMinutes(5), SupervisorSeverity.Warn, "no-progress");
        AppendAdvisory(t0.AddMinutes(10), SupervisorSeverity.Warn, "tool-call-repeat");

        // Mid-window evaluation: cadence has not elapsed, must stay silent.
        time.Advance(TimeSpan.FromMinutes(15));
        Assert.Null(svc.EvaluateProject(_workspace, _project, jobs, TimeSpan.FromMinutes(30)));

        // Window 1 boundary: emits exactly one message.
        time.Advance(TimeSpan.FromMinutes(15));
        var first = svc.EvaluateProject(_workspace, _project, jobs, TimeSpan.FromMinutes(30));
        Assert.NotNull(first);

        var afterFirst = CountChatNoteLines(jobLog);
        Assert.Equal(1, afterFirst);

        // Window 2: one new advisory arrives, then we cross the boundary.
        AppendAdvisory(t0.AddMinutes(45), SupervisorSeverity.Warn, "error-burst");
        time.Advance(TimeSpan.FromMinutes(30));
        var second = svc.EvaluateProject(_workspace, _project, jobs, TimeSpan.FromMinutes(30));
        Assert.NotNull(second);

        var afterSecond = CountChatNoteLines(jobLog);
        Assert.Equal(2, afterSecond);

        // A third evaluation immediately after window 2 is below the period
        // and must not double-write.
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(svc.EvaluateProject(_workspace, _project, jobs, TimeSpan.FromMinutes(30)));
        Assert.Equal(2, CountChatNoteLines(jobLog));
    }

    private (ChatNoteHostedService svc, List<JobInfo> jobs, string jobLog) BuildService(FakeTimeProvider time)
    {
        var jobFolder = Path.Combine(_workspace, "jobs", "j-1");
        Directory.CreateDirectory(jobFolder);
        Directory.CreateDirectory(Path.Combine(jobFolder, "logs"));

        var job = new JobInfo
        {
            Id = "j-1",
            JobKey = $"{_workspace}::j-1",
            Title = "test job",
            State = JobStates.Progress,
            ProjectName = _project,
            FolderPath = jobFolder,
            WatchPath = _workspace,
            LastActivity = time.GetUtcNow().UtcDateTime,
        };

        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
            })
            .Build();

        // EvaluateProject does not call _taskRunner or _scanner, so passing
        // null! is safe for the cadence-focused tests. TickOnceAsync would
        // need both wired up; we deliberately exercise the inner method
        // here.
        var svc = new ChatNoteHostedService(
            taskRunner: null!,
            scanner: null!,
            chatLog: chatLog,
            configuration: config,
            logger: NullLogger<ChatNoteHostedService>.Instance,
            time: time);

        var jobLog = Path.Combine(jobFolder, "logs", "cli-output.log");
        return (svc, new List<JobInfo> { job }, jobLog);
    }

    private void AppendAdvisory(DateTime at, SupervisorSeverity severity, string topic)
    {
        var dir = SupervisorLogPaths.ProjectLogDir(_workspace, _project);
        Directory.CreateDirectory(dir);
        var path = SupervisorLogPaths.ObservationsFile(_workspace, _project);
        var advisory = new SupervisorAdvisory(
            CreatedAt: at,
            Project: _project,
            Severity: severity,
            Source: SupervisorSource.HardCheck,
            Topic: topic,
            Message: $"synthetic {topic}",
            JobId: "j-1");
        var line = JsonSerializer.Serialize(advisory, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        File.AppendAllText(path, line + Environment.NewLine);
    }

    private static int CountChatNoteLines(string jobLog)
    {
        if (!File.Exists(jobLog)) return 0;
        return File.ReadAllLines(jobLog).Count(l => l.Contains("[supervisor] [chat-note]"));
    }
}
