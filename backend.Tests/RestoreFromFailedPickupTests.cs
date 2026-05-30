using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Acceptance contract for the restore-from-failed-pickup endpoint. Pairs
/// with <see cref="StaleProgressArchiverTests"/> (which produces the
/// dead-letter rows) and pins the inverse path: a dead-letter folder
/// lifts back into 2-ready under its original slug, the pickup-attempt
/// state is clear (in-memory counter was already cleared at dead-letter
/// time), and a forensics row is appended so the dead-letter -> restore
/// lifecycle is reviewable per-slug.
///
/// <para>Closes the gap that motivated the 2026-05-08 manual <c>mv</c>
/// incident: without this endpoint an operator who wanted the original
/// slug back had to reach for shell <c>mv</c>, exactly the bypass the
/// architecture test + AGENTS.md "API first" rule forbid.</para>
/// </summary>
public sealed class RestoreFromFailedPickupTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _watchPath;
    private readonly string _workspaceRoot;
    private const string ProjectName = "demo";

    public RestoreFromFailedPickupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "atp-restore-failed-pickup-" + Guid.NewGuid().ToString("N"));
        _workspaceRoot = Path.Combine(_tempDir, "workspace");
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Restore_DeadLetterSlug_LandsInReadyUnderOriginalSlug_AndAppendsForensicsRow()
    {
        // Arrange: simulate a finished dead-letter. The folder lives under
        // 3a-failed-pickup/foo-pickup-failed-2026-05-08 with a job.json
        // whose state field still reads "3a-failed-pickup" (set by the
        // dead-letter move). A supervisor chat-log note is also present
        // so we can confirm logs survive the restore.
        const string original = "foo";
        const string deadLetterSlug = "foo-pickup-failed-2026-05-08";
        var deadLetterFolder = Path.Combine(_watchPath, TaskStates.FailedPickup, deadLetterSlug);
        Directory.CreateDirectory(deadLetterFolder);
        File.WriteAllText(Path.Combine(deadLetterFolder, "job.json"),
            $"{{\"id\":\"{deadLetterSlug}\",\"title\":\"{original}\",\"state\":\"{TaskStates.FailedPickup}\",\"order\":1,\"agent\":\"copilot\"}}");
        var logsDir = Path.Combine(deadLetterFolder, "logs");
        Directory.CreateDirectory(logsDir);
        File.WriteAllText(Path.Combine(logsDir, "cli-output.log"),
            "[12:00:00.000] [supervisor] [pickup-failed] gave up after 3 silent attempts\n");

        var (states, scanner, pickupLog) = Build();

        // Act
        var outcome = states.RestoreFromFailedPickup(deadLetterSlug, _watchPath, keepDeadLetterSlug: false);

        // Assert: outcome reports the rename and points back to the original slug.
        Assert.Equal(RestoreFromFailedPickupStatus.Success, outcome.Status);
        Assert.Equal(original, outcome.RestoredSlug);
        Assert.Equal(original, outcome.OriginalSlug);
        Assert.Equal(deadLetterSlug, outcome.SourceSlug);

        // Folder structure: source gone, target present, archive empty.
        Assert.False(Directory.Exists(deadLetterFolder), "dead-letter source folder must be moved, not copied");
        var restored = Path.Combine(_watchPath, TaskStates.Ready, original);
        Assert.True(Directory.Exists(restored), "folder must land in 2-ready under the original slug");

        // The cli-output log (and its supervisor note) survived the move
        // intact, so the protocol pane still has the historical evidence.
        var preservedLog = File.ReadAllText(Path.Combine(restored, "logs", "cli-output.log"));
        Assert.Contains("[supervisor]", preservedLog);

        // job.json state is now 2-ready. The id field is left to the
        // scanner's self-heal pass: a fresh scan rewrites it to match
        // the new folder name (line 201-208 of TaskScannerService).
        var jobJsonRaw = File.ReadAllText(Path.Combine(restored, "job.json"));
        Assert.Contains($"\"state\": \"{TaskStates.Ready}\"", jobJsonRaw);

        // Forensics: the endpoint appends a pickup-restored row. The
        // service method does not append on its own (the endpoint layer
        // owns log writes so the state-machine stays Filesystem-only),
        // so simulate the same call the endpoint makes.
        pickupLog.AppendRestore(new PickupRestoreRecord
        {
            At = DateTime.UtcNow,
            ProjectName = ProjectName,
            Slug = outcome.OriginalSlug!,
            SourceSlug = outcome.SourceSlug!,
            RestoredAs = outcome.RestoredSlug!,
            TargetState = TaskStates.Ready,
            Reason = "Operator restore via API; original slug recovered."
        });

        var jsonlPath = Path.Combine(_workspaceRoot, "logs", "pickup-failures.jsonl");
        Assert.True(File.Exists(jsonlPath));
        var jsonl = File.ReadAllText(jsonlPath);
        Assert.Contains("\"kind\":\"pickup-restored\"", jsonl);
        Assert.Contains($"\"slug\":\"{original}\"", jsonl);
        Assert.Contains($"\"sourceSlug\":\"{deadLetterSlug}\"", jsonl);
        Assert.Contains($"\"restoredAs\":\"{original}\"", jsonl);

        // Self-heal of the divergent id field happens on the next scan.
        var rescannedId = scanner.FindJob(original, _watchPath);
        Assert.NotNull(rescannedId);
        Assert.Equal(TaskStates.Ready, rescannedId!.State);
        Assert.Equal(original, rescannedId.Id);
    }

    [Fact]
    public void Restore_WithKeepDeadLetterSlug_RetainsTheSuffix()
    {
        const string deadLetterSlug = "foo-pickup-failed-2026-05-08";
        var deadLetterFolder = Path.Combine(_watchPath, TaskStates.FailedPickup, deadLetterSlug);
        Directory.CreateDirectory(deadLetterFolder);
        File.WriteAllText(Path.Combine(deadLetterFolder, "job.json"),
            $"{{\"id\":\"{deadLetterSlug}\",\"title\":\"foo\",\"state\":\"{TaskStates.FailedPickup}\",\"order\":1,\"agent\":\"copilot\"}}");

        var (states, _, _) = Build();

        var outcome = states.RestoreFromFailedPickup(deadLetterSlug, _watchPath, keepDeadLetterSlug: true);

        Assert.Equal(RestoreFromFailedPickupStatus.Success, outcome.Status);
        Assert.Equal(deadLetterSlug, outcome.RestoredSlug);
        Assert.Equal("foo", outcome.OriginalSlug);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, deadLetterSlug)));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "foo")));
    }

    [Fact]
    public void Restore_UnknownSlug_Returns404Equivalent()
    {
        var (states, _, _) = Build();

        var outcome = states.RestoreFromFailedPickup("never-existed-pickup-failed-2026-05-08", _watchPath, keepDeadLetterSlug: false);

        Assert.Equal(RestoreFromFailedPickupStatus.NotFound, outcome.Status);
    }

    [Fact]
    public void Restore_SlugAlreadyInReady_IsIdempotentNoOp()
    {
        // The expected "already restored, called twice" case. Folder is in
        // 2-ready under the original slug; the caller passes the dead-letter
        // slug. The endpoint should return a 200 no-op instead of 404 so
        // retries do not produce spurious failures.
        const string original = "foo";
        const string deadLetterSlug = "foo-pickup-failed-2026-05-08";

        // Seed 2-ready with the restored folder.
        var restored = Path.Combine(_watchPath, TaskStates.Ready, original);
        Directory.CreateDirectory(restored);
        File.WriteAllText(Path.Combine(restored, "job.json"),
            $"{{\"id\":\"{original}\",\"title\":\"{original}\",\"state\":\"{TaskStates.Ready}\",\"order\":1,\"agent\":\"copilot\"}}");
        // 3a-failed-pickup is empty: nothing to restore.

        var (states, _, _) = Build();

        var outcome = states.RestoreFromFailedPickup(deadLetterSlug, _watchPath, keepDeadLetterSlug: false);

        Assert.Equal(RestoreFromFailedPickupStatus.NoOp, outcome.Status);
        Assert.Equal(original, outcome.RestoredSlug);
        Assert.Equal(original, outcome.OriginalSlug);
        Assert.True(Directory.Exists(restored), "no-op must not touch the existing 2-ready folder");
    }

    [Fact]
    public void Restore_TargetLaneCollision_ReportsConflictWithoutTouchingSource()
    {
        // Stale duplicate already exists in 2-ready under the original
        // slug, and a real dead-letter folder still sits in
        // 3a-failed-pickup. The state machine must refuse rather than
        // overwrite or silently rename.
        const string original = "foo";
        const string deadLetterSlug = "foo-pickup-failed-2026-05-08";

        var stale = Path.Combine(_watchPath, TaskStates.Ready, original);
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "job.json"),
            $"{{\"id\":\"{original}\",\"title\":\"stale\",\"state\":\"{TaskStates.Ready}\",\"order\":1,\"agent\":\"copilot\"}}");

        var deadLetterFolder = Path.Combine(_watchPath, TaskStates.FailedPickup, deadLetterSlug);
        Directory.CreateDirectory(deadLetterFolder);
        File.WriteAllText(Path.Combine(deadLetterFolder, "job.json"),
            $"{{\"id\":\"{deadLetterSlug}\",\"title\":\"{original}\",\"state\":\"{TaskStates.FailedPickup}\",\"order\":1,\"agent\":\"copilot\"}}");

        var (states, _, _) = Build();

        var outcome = states.RestoreFromFailedPickup(deadLetterSlug, _watchPath, keepDeadLetterSlug: false);

        Assert.Equal(RestoreFromFailedPickupStatus.TargetFolderExists, outcome.Status);
        Assert.True(Directory.Exists(deadLetterFolder), "source must stay put when the target lane is occupied");
        Assert.True(Directory.Exists(stale), "stale duplicate must not be overwritten");
    }

    [Fact]
    public void TryParseFailedPickupSlug_HandlesPlainAndCollisionSuffix()
    {
        Assert.True(PickupFailureLog.TryParseFailedPickupSlug("foo-pickup-failed-2026-05-08", out var p1));
        Assert.Equal("foo", p1);

        Assert.True(PickupFailureLog.TryParseFailedPickupSlug("multi-word-task-pickup-failed-2026-05-08", out var p2));
        Assert.Equal("multi-word-task", p2);

        Assert.True(PickupFailureLog.TryParseFailedPickupSlug("foo-pickup-failed-2026-05-08-2", out var p3));
        Assert.Equal("foo", p3);

        Assert.False(PickupFailureLog.TryParseFailedPickupSlug("just-a-normal-slug", out _));
        Assert.False(PickupFailureLog.TryParseFailedPickupSlug("", out _));
    }

    private (TaskStateMachine States, TaskScannerService Scanner, PickupFailureLog PickupLog) Build()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = ProjectName,
            ["WatchPaths:0:Path"] = _watchPath,
            ["WatchPaths:0:RootPath"] = _workspaceRoot,
            ["TaskRepository"] = _workspaceRoot
        }).Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var pickupLog = new PickupFailureLog(config, NullLogger<PickupFailureLog>.Instance);
        return (states, scanner, pickupLog);
    }
}
