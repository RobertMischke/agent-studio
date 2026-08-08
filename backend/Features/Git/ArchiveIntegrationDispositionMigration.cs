using AgentStudio.Pipeline;

namespace AgentStudio.Git;

public sealed record ArchiveIntegrationDispositionMigrationReport(
    int Candidates,
    int Written,
    int AlreadyClassified);

internal sealed record ArchiveIntegrationDispositionSeed(
    string Status,
    string Reason,
    string EvidenceCommit);

/// <summary>
/// Idempotent AGT-2508 migration for the archived Agent Studio deliveries that
/// predate attempt authority or were evaluated by card-scoped salvage review.
/// Existing dispositions always win so a later operator decision is never
/// overwritten by a restart.
/// </summary>
public sealed class ArchiveIntegrationDispositionMigration
{
    private const string ProjectName = "Agent Studio";
    private const string Classifier = "migration:AGT-2508";

    internal static readonly IReadOnlyDictionary<string, ArchiveIntegrationDispositionSeed> AssessedConflicts =
        new Dictionary<string, ArchiveIntegrationDispositionSeed>(StringComparer.OrdinalIgnoreCase)
        {
            ["AGT-2478"] = Superseded(
                "Analysis-only archive audit; operator acceptance recorded that its reviewed deliveries were superseded and no integration was applicable.",
                "c9ce1342e3a8048ea53a4c405ec85d0a2442ebbc"),
            ["AGT-2473"] = Superseded(
                "The connectivity monitor was re-delivered with AGT-2471 by the later combined integration.",
                "0b0b31de5bdcc5f68fae0c009eb99f0535bf5f3a"),
            ["AGT-2471"] = Superseded(
                "The slot-adoption change was re-delivered with AGT-2473 by the later combined integration.",
                "0b0b31de5bdcc5f68fae0c009eb99f0535bf5f3a"),
            ["AGT-2446"] = Superseded(
                "The exact-primary-report rule is present in the later research pipeline and documentation contract; replaying the stale branch would regress that contract.",
                "2129f9998327e327b894690817a9b15c3c23cb4e"),
            ["AGT-2257"] = Recovery(
                "Later health handling narrows lock-retention risk, but no equivalent hard end-to-end post-acquisition watchdog was found.",
                "248d996ef21e3a324807a97e9e672069cc681328"),
            ["AGT-2283"] = Recovery(
                "The archived delivery did not complete the requested Wiki UI wording and provenance presentation, and those gaps remain in the current view.",
                "5048a8178e6b6755b71f712b8c332be10fd7493b"),
            ["AGT-2239"] = Recovery(
                "The current chat collapse contract still uses the older threshold and short-summary behavior; recovery belongs in the Coding Agent Chat owner repository.",
                "c42df99016216ae809abf7d066d94010fd398599"),
            ["AGT-2221"] = Superseded(
                "Later runner preflight and capacity hardening implements the delivery gate on the current execution path.",
                "afb83f6d456299394c81c7354ed4c673e4ec40b9"),
            ["AGT-2187"] = Superseded(
                "The stale omnibus stabilization delivery was replaced by later authority, gate, and recovery slices; replaying it would cross those newer boundaries.",
                "3f5d1ba5d363b489b2451d24e9b7368dc0e1af0c"),
            ["AGT-2328"] = Superseded(
                "The task was re-delivered and integrated by a later card-scoped commit.",
                "8a077f0a1b5d449950d9661fc3de06048a8761f4"),
            ["AGT-2256"] = Superseded(
                "The task was re-delivered and integrated by a later card-scoped commit.",
                "3282a2ef7b9c8b0a519c3bda9f5c6f74afb87e13"),
            ["AGT-2241"] = Superseded(
                "The task was re-delivered and integrated by a later card-scoped commit.",
                "ce9e0921c0b8314e1c3e0134fb08077c37d7fa84"),
            ["AGT-2229"] = Superseded(
                "The task was re-delivered and integrated by a later card-scoped commit.",
                "59540aff1203781f72f8b077a937817de2897562"),
            ["AGT-2209"] = Superseded(
                "The task was re-delivered and integrated by a later card-scoped commit.",
                "ae7d081e2699ca1349cef05042099cf216916a49"),
            ["AGT-2275"] = Superseded(
                "The distributed hardening review gaps were closed by the later integration.",
                "4339df7e805fcc6c73e3b59b95ee1c2fda16998b"),
            ["AGT-2255"] = Superseded(
                "The later fail-closed lane-move fix implemented postcondition verification, bounded retry, and partial-target handling.",
                "ee3fda37971aa185c833f7b3b0289a8b81159232"),
            ["AGT-2226"] = Superseded(
                "Later host-capacity work centralized runner slots and deliberately made post-processing capacity derived rather than independently configured.",
                "d3b54d191d02085825462a253a03eaf5e4d5288a"),
            ["AGT-2164"] = Superseded(
                "The task was re-delivered through the later rebase integration.",
                "e4ad06d2e3ce02ca30eb17af7cb665fa6aa6eaef"),
            ["AGT-2198"] = Superseded(
                "Later Wiki theme-token work replaced the archived color patch on the current component path.",
                "4d22f729bda725b012fc49fc72a7ac780217bf92"),
            ["AGT-2106"] = Recovery(
                "The archived payload changed an unrelated Explorer context-menu path; the requested task-card pipeline summary remains incomplete.",
                "05705f9ab0bb2bd8b1e095383c9e0d3df20f3a16"),
            ["AGT-2172"] = Superseded(
                "The later repair delivered the required behavior and was already accepted by the AGT-2478 archive audit.",
                "a2afa568b43dbd4ebafb12102684b244f19b0a12"),
        };

    private readonly TaskScannerService _scanner;
    private readonly IntegrationQueueDispositionStore _store;
    private readonly TimeProvider _timeProvider;

    public ArchiveIntegrationDispositionMigration(
        TaskScannerService scanner,
        IntegrationQueueDispositionStore store,
        TimeProvider? timeProvider = null)
    {
        _scanner = scanner;
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ArchiveIntegrationDispositionMigrationReport Migrate()
    {
        var candidates = 0;
        var written = 0;
        var alreadyClassified = 0;

        foreach (var task in _scanner.ScanAllJobsWithArchive()
                     .Where(IsEligibleTask))
        {
            var key = string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key!;
            var seed = ResolveSeed(task, key);
            if (seed is null) continue;
            candidates++;

            if (_store.Read(task.FolderPath, key) is not null)
            {
                alreadyClassified++;
                continue;
            }

            _store.Write(task.FolderPath, new IntegrationQueueDisposition
            {
                TaskKey = key,
                Status = seed.Status,
                Reason = seed.Reason,
                EvidenceCommit = seed.EvidenceCommit,
                ClassifiedAtUtc = _timeProvider.GetUtcNow(),
                ClassifiedBy = Classifier,
            });
            written++;
        }

        return new ArchiveIntegrationDispositionMigrationReport(candidates, written, alreadyClassified);
    }

    private static bool IsEligibleTask(TaskInfo task)
        => string.Equals(task.ProjectName, ProjectName, StringComparison.OrdinalIgnoreCase)
           && (task.State == TaskStates.Archive
               || task.State == TaskStates.Completed
               && string.Equals(task.TaskKey, "AGT-2478", StringComparison.OrdinalIgnoreCase));

    private static ArchiveIntegrationDispositionSeed? ResolveSeed(TaskInfo task, string key)
    {
        if (AssessedConflicts.TryGetValue(key, out var assessed)) return assessed;

        var subject = ReviewSubjectStore.Read(task.FolderPath);
        if (subject is null || !string.IsNullOrWhiteSpace(subject.RunAttemptId)) return null;

        return new ArchiveIntegrationDispositionSeed(
            IntegrationQueueStates.LegacyUnverifiable,
            $"Archived review subject for '{key}' predates attempt authority and has no RunAttemptId. Its delivery cannot be verified or retried, so it is terminal rather than an active conflict.",
            subject.ResultSha);
    }

    private static ArchiveIntegrationDispositionSeed Superseded(string reason, string evidenceCommit)
        => new(IntegrationQueueStates.Superseded, reason, evidenceCommit);

    private static ArchiveIntegrationDispositionSeed Recovery(string reason, string evidenceCommit)
        => new(IntegrationQueueStates.RecoveryRecommended, reason, evidenceCommit);
}
