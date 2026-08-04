using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Acceptance for the parked-card Wiedervorlage (AGT-2492, incident AGT-2220).
///
/// <para>AGT-2220 was parked in a human-decision lane on 2026-07-29 with
/// "4x ReviewInfra/BaselineUnavailable - parked for an operator decision, no auto
/// rerun". The documented remedy ran on 2026-08-02 and the precondition was gone,
/// yet no log, bus event, or timeline row mentions the card again until
/// 2026-08-03: four days of standstill on a card that was ready.</para>
///
/// <list type="number">
///   <item>A card whose blocker condition is fulfilled is REPORTED as
///   recallable, with the age it accumulated - the case that was silent.</item>
///   <item>A card whose condition still holds stays parked and is never
///   re-queued - "no auto rerun" survives the sweep.</item>
///   <item>Parking writes the machine-readable blocker; leaving the lane clears
///   it.</item>
/// </list>
/// </summary>
public sealed class ParkedCardRecallSweepTests : IDisposable
{
    private const string ProjectName = "demo";
    private readonly string _workspaceRoot;
    private readonly string _watchPath;
    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly TaskTransitionService _transitions;
    private readonly TimelineLog _timeline;
    private readonly FakeTimeProvider _clock;

    public ParkedCardRecallSweepTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "atp-parked-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspaceRoot, "projects", ProjectName);
        Directory.CreateDirectory(_workspaceRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["WatchPaths:0:RepositoryPath"] = _watchPath,
                ["TaskRepository"] = _workspaceRoot,
            })
            .Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config);
        _scanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance, summary);
        _timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        _states = new TaskStateMachine(_scanner, NullLogger<TaskStateMachine>.Instance, timeline: _timeline);

        var clients = new ClientIdentityStore(_config, NullLogger<ClientIdentityStore>.Instance);
        var mutations = new TaskMutationService(
            _scanner, clients, new ProjectRegistry(_config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var prompts = new RuntimePromptService(_config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, _config);
        var git = new GitService(NullLogger<GitService>.Instance, _scanner, _config, prompts);
        _transitions = new TaskTransitionService(
            _scanner, _states, mutations, git, settings, NullLogger<TaskTransitionService>.Instance);
        _scanner.SetIndexCache(new TaskIndexCache(_scanner, NullLogger<TaskIndexCache>.Instance, _config));

        _clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); } catch { /* best-effort */ }
    }

    // -- Acceptance 1 ------------------------------------------------------

    [Fact]
    public void Sweep_BlockerConditionFulfilled_ReportsCardAsRecallableWithItsAge()
    {
        var folder = ParkCard(
            "review-infra-baseline",
            parkedAt: new DateTime(2026, 7, 29, 22, 7, 0, DateTimeKind.Utc),
            blockerType: HumanReviewEscalationCategories.ReviewSubjectUnmaterializable);

        var recalls = BuildSweep(Probe(ParkedBlockerStatuses.Recallable, "'task/agt-2220' now contains 'develop'."))
            .Sweep();

        var recall = Assert.Single(recalls);
        Assert.True(recall.IsRecallable);
        Assert.Equal(TaskStates.HumanReview, recall.Lane);
        Assert.Equal(HumanReviewEscalationCategories.ReviewSubjectUnmaterializable, recall.BlockerType);
        Assert.Equal(ParkedBlockerConditionKinds.GitAncestor, recall.ConditionKind);

        // The aging that made the incident invisible is now a reported number:
        // parked 29.07. 22:07, swept 03.08. 12:00 -> just under five days.
        Assert.Equal(4, recall.ParkedForSeconds / 86400);

        // Board-visible: one ledger row an operator can see on the card.
        var announced = _timeline.ReadAll(folder)
            .Where(evt => evt.Kind == TimelineEventKinds.ParkedBlockerResolved)
            .ToList();
        Assert.Single(announced);
        Assert.Contains("ready for a human to re-queue", announced[0].Summary);
        Assert.Equal(TaskStates.HumanReview, announced[0].Details!["lane"]);

        // Reported, not requeued: the card is still exactly where it was parked.
        Assert.Equal(TaskStates.HumanReview, _scanner.ScanAllJobs().Single().State);
    }

    [Fact]
    public void Sweep_AlreadyReportedRecall_IsNotAnnouncedTwice()
    {
        // A 30-minute sweep would otherwise re-announce the same resolved
        // blocker ~48 times a day and bury the card's real history.
        var folder = ParkCard("repeat-report", _clock.GetUtcNow().UtcDateTime.AddDays(-2));
        var sweep = BuildSweep(Probe(ParkedBlockerStatuses.Recallable, "condition cleared"));

        sweep.Sweep();
        sweep.Sweep();

        Assert.Single(_timeline.ReadAll(folder).Where(evt => evt.Kind == TimelineEventKinds.ParkedBlockerResolved));
    }

    // -- Acceptance 2 ------------------------------------------------------

    [Fact]
    public void Sweep_ConditionStillHolds_LeavesCardParkedAndSilent()
    {
        var folder = ParkCard(
            "baseline-still-missing",
            parkedAt: _clock.GetUtcNow().UtcDateTime.AddDays(-5),
            blockerType: HumanReviewEscalationCategories.ReviewSubjectUnmaterializable);

        var recalls = BuildSweep(
                Probe(ParkedBlockerStatuses.Blocked, "'task/x' still does not contain 'develop'."))
            .Sweep();

        var recall = Assert.Single(recalls);
        Assert.False(recall.IsRecallable);
        Assert.Equal(ParkedBlockerStatuses.Blocked, recall.Status);

        // No lane move, no ready/progress requeue, no announcement.
        var task = Assert.Single(_scanner.ScanAllJobs());
        Assert.Equal(TaskStates.HumanReview, task.State);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.HumanReview, "baseline-still-missing")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Ready, "baseline-still-missing")));
        Assert.Empty(_timeline.ReadAll(folder).Where(evt => evt.Kind == TimelineEventKinds.ParkedBlockerResolved));

        // The card still says what it waits for, and how long it has waited.
        Assert.NotNull(task.ParkedBlocker);
        Assert.Equal(ParkedBlockerStatuses.Blocked, task.ParkedBlocker!.RecallStatus);
        Assert.Equal(
            HumanReviewEscalationCategories.ReviewSubjectUnmaterializable,
            task.ParkedBlocker.BlockerType);
    }

    [Fact]
    public void Sweep_UndeterminableCondition_IsReportedButNeverRecallable()
    {
        // A manual blocker, or a probe that cannot read the repository, must
        // never be optimistically reported as resolved.
        ParkCard("needs-a-person", _clock.GetUtcNow().UtcDateTime.AddDays(-9));

        var recall = Assert.Single(
            BuildSweep(Probe(ParkedBlockerStatuses.Undeterminable, "no automatic condition")).Sweep());

        Assert.False(recall.IsRecallable);
        Assert.Equal(ParkedBlockerStatuses.Undeterminable, recall.Status);
        Assert.Equal(9, recall.ParkedForSeconds / 86400);
    }

    // -- The marker itself -------------------------------------------------

    [Fact]
    public async Task MoveIntoParkedLane_RecordsMachineReadableBlocker_AndLeavingClearsIt()
    {
        const string jobId = "escalated-card";
        WriteJob(TaskStates.Progress, jobId);

        var escalated = await _transitions.MoveAsync(
            jobId, TaskStates.Escalated, _watchPath, CancellationToken.None,
            cause: TimelineActors.System,
            reason: HumanReviewEscalation.FormatReason(
                HumanReviewEscalationCategories.WorktreeBlocked,
                "Worktree preparation remained blocked after 5 attempts."));
        Assert.Equal(MoveJobStatus.Success, escalated.Status);

        var record = ParkedBlockerMarker.TryRead(escalated.NewFolderPath!);
        Assert.NotNull(record);
        Assert.Equal(HumanReviewEscalationCategories.WorktreeBlocked, record!.BlockerType);
        Assert.Equal(TaskStates.Escalated, record.Lane);
        // No category maps worktree-blocked to a checkable fact today, and the
        // catalog says so instead of inventing a condition nothing evaluates.
        Assert.Equal(ParkedBlockerConditionKinds.Manual, record.Condition.Kind);
        Assert.Contains("Worktree preparation remained blocked", record.Reason);

        var requeued = await _transitions.MoveAsync(
            jobId, TaskStates.Ready, _watchPath, CancellationToken.None,
            cause: TimelineActors.Human("ops@example.com"),
            reason: "Worktree released; try again.");
        Assert.Equal(MoveJobStatus.Success, requeued.Status);
        Assert.Null(ParkedBlockerMarker.TryRead(requeued.NewFolderPath!));
        Assert.Null(_scanner.ScanAllJobs().Single().ParkedBlocker);
    }

    [Fact]
    public void Sweep_CardParkedBeforeTheMarkerExisted_StillAges()
    {
        // The AGT-2220 card itself is in this class: parked long before any
        // blocker was recorded. It must not stay invisible just because it
        // predates the feature.
        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, "legacy-park");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            $"{{\"id\":\"legacy-park\",\"title\":\"legacy\",\"state\":\"{TaskStates.HumanReview}\"," +
            $"\"agent\":\"claude\",\"enteredLaneAt\":\"{_clock.GetUtcNow().UtcDateTime.AddDays(-6):o}\"}}");
        Assert.Null(ParkedBlockerMarker.TryRead(folder));

        var recall = Assert.Single(
            BuildSweep(Probe(ParkedBlockerStatuses.Undeterminable, "no condition recorded")).Sweep());

        Assert.Equal(ParkedBlockerCatalog.OperatorDecision, recall.BlockerType);
        Assert.Equal(6, recall.ParkedForSeconds / 86400);
        Assert.NotNull(ParkedBlockerMarker.TryRead(folder));
    }

    [Fact]
    public void Sweep_UnchangedVerdict_DoesNotTouchTheJobFolder()
    {
        // The job folder's mtime feeds TaskInfo.LastActivity. Re-writing an
        // unchanged marker every 30 minutes would make a card that has sat
        // untouched for days read as freshly active - the exact opposite of the
        // aging signal this feature adds.
        var folder = ParkCard("quietly-blocked", _clock.GetUtcNow().UtcDateTime.AddDays(-4));
        var sweep = BuildSweep(Probe(ParkedBlockerStatuses.Blocked, "still blocked"));

        sweep.Sweep();
        var afterFirstSweep = Directory.GetLastWriteTimeUtc(folder);

        _clock.Advance(TimeSpan.FromHours(1));
        sweep.Sweep();

        Assert.Equal(afterFirstSweep, Directory.GetLastWriteTimeUtc(folder));
    }

    [Fact]
    public void Sweep_UnparkedLanes_AreIgnored()
    {
        WriteJob(TaskStates.Ready, "waiting-to-run");
        WriteJob(TaskStates.Completed, "finished");

        Assert.Empty(BuildSweep(Probe(ParkedBlockerStatuses.Recallable, "would be wrong here")).Sweep());
    }

    // -- helpers -----------------------------------------------------------

    private ParkedCardRecallSweep BuildSweep(IParkedBlockerProbe probe)
        => new(_scanner, probe, NullLogger<ParkedCardRecallSweep>.Instance, _timeline, git: null, clock: _clock);

    private static IParkedBlockerProbe Probe(string status, string detail)
        => new StubProbe(status, detail);

    /// <summary>Creates a card sitting in <c>5-human-review</c> with a blocker
    /// marker, the shape the funnel leaves behind.</summary>
    private string ParkCard(
        string jobId,
        DateTime parkedAt,
        string blockerType = ParkedBlockerCatalog.OperatorDecision)
    {
        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, jobId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            $"{{\"id\":\"{jobId}\",\"title\":\"{jobId}\",\"state\":\"{TaskStates.HumanReview}\"," +
            $"\"agent\":\"claude\",\"enteredLaneAt\":\"{parkedAt:o}\"}}");

        ParkedBlockerMarker.Write(folder, new ParkedBlockerRecord
        {
            BlockerType = blockerType,
            Condition = ParkedBlockerCatalog.ConditionFor(blockerType),
            Lane = TaskStates.HumanReview,
            ParkedAt = parkedAt,
            Reason = "4x ReviewInfra/BaselineUnavailable - parked for an operator decision, no auto rerun",
        });
        _scanner.InvalidateCache();
        return folder;
    }

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\"," +
            "\"agent\":\"claude\",\"cliType\":\"claude\",\"ownerClientId\":\"local-default\"}");
        _scanner.InvalidateCache();
    }

    private sealed class StubProbe(string status, string detail) : IParkedBlockerProbe
    {
        public ParkedBlockerEvaluation Evaluate(
            ParkedBlockerCondition condition, ParkedBlockerContext context, DateTime now)
            => new() { Status = status, At = now, Detail = detail };
    }
}
