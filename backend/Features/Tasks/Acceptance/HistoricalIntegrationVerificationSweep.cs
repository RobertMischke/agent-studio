using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>Pure classification inputs for one historical accepted card.</summary>
public sealed record HistoricalIntegrationVerificationFacts(
    bool HasEffectiveCommits,
    bool AllEffectiveCommitsIntegrated,
    bool AcceptedBeforeRecordingEra,
    bool NoCodeExpected,
    bool HasDeliverables,
    bool HasFenceContent);

/// <summary>
/// Conservative six-way policy. Positive Git ancestry is the only code
/// integration proof; no-code classification requires both intent and an
/// artifact; a surviving recovery ref outranks legacy attribution gaps and the
/// missing bucket.
/// </summary>
public static class HistoricalIntegrationVerificationPolicy
{
    public static string Classify(HistoricalIntegrationVerificationFacts facts)
    {
        if (facts.HasEffectiveCommits && facts.AllEffectiveCommitsIntegrated)
        {
            return facts.AcceptedBeforeRecordingEra
                ? IntegrationRecordClasses.IntegratedHistorical
                : IntegrationRecordClasses.IntegratedVerified;
        }

        if (!facts.HasEffectiveCommits && facts.NoCodeExpected && facts.HasDeliverables)
            return IntegrationRecordClasses.NoCodeExpected;

        if (facts.HasFenceContent)
            return IntegrationRecordClasses.ContentOnFence;

        return !facts.HasEffectiveCommits && facts.AcceptedBeforeRecordingEra
            ? IntegrationRecordClasses.NoAttributionLegacy
            : IntegrationRecordClasses.GenuinelyMissing;
    }

    /// <summary>
    /// Preserves the v1 population and adds the same unverified accepted-card
    /// population used by the acute alert.
    /// </summary>
    public static bool IsSweepCandidate(
        TaskInfo task,
        bool hasNativeRecord,
        bool hasAcceptanceRecord,
        bool hasHistoricalVerification)
    {
        if (task.State is not (TaskStates.Completed or TaskStates.Archive)) return false;
        if (hasHistoricalVerification) return false;
        if (!hasNativeRecord) return true;
        return IsAlertPopulationCandidate(task, hasAcceptanceRecord, hasHistoricalVerification);
    }

    /// <summary>The terminal acceptance population before age and Git-state filtering.</summary>
    public static bool IsAlertPopulationCandidate(
        TaskInfo task,
        bool hasAcceptanceRecord,
        bool hasHistoricalVerification)
        => (task.State is TaskStates.Completed or TaskStates.Archive)
           && AcceptanceIntegrationPolicy.IsIntegrationRequired(task)
           && hasAcceptanceRecord
           && !hasHistoricalVerification;
}

/// <summary>
/// Shared definition of a durable integration record. Live records are the
/// merge pipeline step or one of the integration timeline events;
/// historical verification rows are the append-only compatibility record.
/// </summary>
public static class TaskIntegrationRecordDetector
{
    private static readonly HashSet<string> IntegrationTimelineKinds = new(StringComparer.Ordinal)
    {
        TimelineEventKinds.IntegrationStarted,
        TimelineEventKinds.IntegrationSucceeded,
        TimelineEventKinds.IntegrationFailed,
        TimelineEventKinds.IntegrationOverridden,
    };

    public static bool HasNativeRecord(
        PipelineStepExecution? mergeStep,
        IReadOnlyCollection<TimelineEvent> timeline)
        => mergeStep is not null || timeline.Any(row => IntegrationTimelineKinds.Contains(row.Kind));

    public static TimelineEvent? LatestAcceptanceStarted(
        IReadOnlyCollection<TimelineEvent> timeline)
        => timeline
            .Where(row => string.Equals(
                row.Kind,
                TimelineEventKinds.IntegrationStarted,
                StringComparison.Ordinal))
            .Where(row => !string.Equals(
                row.Details?.GetValueOrDefault("stage"),
                RemoteDeliveryIntegrationCoordinator.PreHumanReviewStage,
                StringComparison.Ordinal))
            .OrderByDescending(row => row.Ts)
            .FirstOrDefault();

    public static bool HasAcceptanceRecord(
        PipelineStepExecution? mergeStep,
        IReadOnlyCollection<TimelineEvent> timeline)
        => mergeStep is not null || LatestAcceptanceStarted(timeline) is not null;

    public static TaskIntegrationRecord? LatestVerification(TaskInfo task)
        => task.IntegrationRecords
            .Where(record => IntegrationRecordClasses.All.Contains(
                record.Classification,
                StringComparer.Ordinal))
            .OrderByDescending(record => record.RecordedAtUtc)
            .FirstOrDefault();

    public static TaskIntegrationRecord? LatestOperatorVisibleVerification(TaskInfo task)
    {
        var record = LatestVerification(task);
        return record is not null
               && IntegrationRecordClasses.IsOperatorVisible(record.Classification)
            ? record
            : null;
    }
}

/// <summary>
/// One-time, Git-read-only verification of terminal cards that lack historical
/// verification. V2 extends the original population to terminal cards with an
/// acceptance record. Card writes are limited to append-only task.json rows,
/// processed in bounded batches after the host starts. No branch, worktree, or
/// lane mutation is performed.
/// </summary>
public sealed class HistoricalIntegrationVerificationSweep
{
    public const string LegacyRecordId = "historical-integration-verification-v1";
    public const string RecordId = "historical-integration-verification-v2";
    public const string ReportFileName = "historical-integration-verification-v2.json";

    // AGT-2543 entered develop at 2026-08-10T00:22:56Z. Cards accepted before
    // this boundary could not have emitted the transactional integration facts.
    internal static readonly DateTime RecordingEraStartedAtUtc =
        new(2026, 8, 10, 0, 22, 56, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly string[] RefNamespaces =
    [
        "refs/heads/agent-studio",
        "refs/remotes/origin/agent-studio",
        "refs/heads/runner",
        "refs/remotes/origin/runner",
        "refs/heads/task",
        "refs/remotes/origin/task",
    ];

    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly PipelineExecutionLog _pipeline;
    private readonly TimelineLog _timeline;
    private readonly ILogger<HistoricalIntegrationVerificationSweep> _logger;
    private readonly string _reportPath;
    private readonly int _batchSize;
    private readonly TimeProvider _timeProvider;

    public HistoricalIntegrationVerificationSweep(
        TaskScannerService scanner,
        TaskMutationService mutations,
        GitService git,
        ProjectSettingsService settings,
        PipelineExecutionLog pipeline,
        TimelineLog timeline,
        IConfiguration configuration,
        ILogger<HistoricalIntegrationVerificationSweep> logger)
        : this(
            scanner,
            mutations,
            git,
            settings,
            pipeline,
            timeline,
            Path.Combine(
                Path.GetFullPath(configuration["TaskRepository"]
                    ?? Path.Combine(AppContext.BaseDirectory, "workspace")),
                ".metadata",
                "migrations",
                ReportFileName),
            Math.Clamp(
                configuration.GetValue<int?>("Integration:HistoricalVerificationBatchSize") ?? 50,
                10,
                100),
            TimeProvider.System,
            logger)
    {
    }

    internal HistoricalIntegrationVerificationSweep(
        TaskScannerService scanner,
        TaskMutationService mutations,
        GitService git,
        ProjectSettingsService settings,
        PipelineExecutionLog pipeline,
        TimelineLog timeline,
        string reportPath,
        int batchSize,
        TimeProvider timeProvider,
        ILogger<HistoricalIntegrationVerificationSweep> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _git = git;
        _settings = settings;
        _pipeline = pipeline;
        _timeline = timeline;
        _reportPath = reportPath;
        _batchSize = Math.Clamp(batchSize, 1, 100);
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<HistoricalIntegrationVerificationReport> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        if (TryReadCompletedReport() is { } completed)
            return completed with { AlreadyCompleted = true, RecordsWritten = 0 };

        var terminal = _scanner.ScanAllAutomationJobsWithArchive()
            .Where(task => task.State is TaskStates.Completed or TaskStates.Archive)
            .OrderBy(task => task.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(task => task.Key ?? task.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var candidates = new List<VerificationCandidate>();
        var alreadyClassified = new List<(TaskInfo Task, TaskIntegrationRecord Record)>();
        foreach (var task in terminal)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TaskIntegrationRecordDetector.LatestVerification(task) is not null)
            {
                var sweepRecord = task.IntegrationRecords
                    .Where(IsSweepRecord)
                    .OrderByDescending(record => record.RecordedAtUtc)
                    .FirstOrDefault();
                if (sweepRecord is not null)
                    alreadyClassified.Add((task, sweepRecord));
                continue;
            }
            var mergeStep = ReadLatestMergeStep(task.FolderPath);
            var timeline = _timeline.ReadAll(task.FolderPath);
            var hasNativeRecord = TaskIntegrationRecordDetector.HasNativeRecord(mergeStep, timeline);
            var hasAcceptanceRecord = TaskIntegrationRecordDetector.HasAcceptanceRecord(mergeStep, timeline);
            if (!HistoricalIntegrationVerificationPolicy.IsSweepCandidate(
                    task,
                    hasNativeRecord,
                    hasAcceptanceRecord,
                    hasHistoricalVerification: false))
            {
                continue;
            }
            candidates.Add(new VerificationCandidate(task, timeline));
        }

        var repoEvidence = BuildRepoEvidence(candidates.Select(candidate => candidate.Task));
        var counts = IntegrationRecordClasses.All.ToDictionary(
            classification => classification,
            _ => 0,
            StringComparer.Ordinal);
        var operatorItems = new List<HistoricalIntegrationOperatorItem>();
        foreach (var (task, record) in alreadyClassified)
        {
            counts[record.Classification]++;
            if (!IntegrationRecordClasses.IsOperatorVisible(record.Classification)) continue;
            operatorItems.Add(new HistoricalIntegrationOperatorItem(
                task.ProjectName,
                task.Key ?? task.Id,
                task.State,
                record.Classification,
                record.Evidence,
                record.FenceRefs));
        }
        var recordsWritten = 0;
        var writeFailures = 0;
        var processed = 0;
        var batchCount = 0;
        var recordedAt = _timeProvider.GetUtcNow().UtcDateTime;

        foreach (var batch in candidates.Chunk(_batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            batchCount++;
            foreach (var candidate in batch)
            {
                var evaluated = Evaluate(candidate, repoEvidence, recordedAt);
                counts[evaluated.Record.Classification]++;
                var write = _mutations.AppendIntegrationRecordOnFolder(
                    candidate.Task.FolderPath,
                    evaluated.Record);
                if (!write.Succeeded)
                {
                    writeFailures++;
                    _logger.LogWarning(
                        "historical-integration-verification write-failed project={Project} task={Task}",
                        candidate.Task.ProjectName,
                        candidate.Task.Key ?? candidate.Task.Id);
                }
                else if (write.Appended)
                {
                    recordsWritten++;
                }

                if (IntegrationRecordClasses.IsOperatorVisible(evaluated.Record.Classification))
                {
                    operatorItems.Add(new HistoricalIntegrationOperatorItem(
                        candidate.Task.ProjectName,
                        candidate.Task.Key ?? candidate.Task.Id,
                        candidate.Task.State,
                        evaluated.Record.Classification,
                        evaluated.Record.Evidence,
                        evaluated.Record.FenceRefs));
                }
                processed++;
            }

            _logger.LogInformation(
                "historical-integration-verification batch={Batch} processed={Processed} total={Total} writes={Writes} failures={Failures}",
                batchCount,
                processed,
                candidates.Count,
                recordsWritten,
                writeFailures);
            await Task.Yield();
        }

        var report = new HistoricalIntegrationVerificationReport(
            Version: 2,
            CompletedAtUtc: recordedAt,
            Completed: writeFailures == 0,
            AlreadyCompleted: false,
            ScannedCards: terminal.Count,
            CandidateCards: candidates.Count + alreadyClassified.Count,
            RecordsWritten: recordsWritten,
            WriteFailures: writeFailures,
            BatchSize: _batchSize,
            BatchCount: batchCount,
            Counts: counts,
            OperatorItems: operatorItems);
        WriteReport(report);
        _logger.LogInformation(
            "historical-integration-verification completed={Completed} scanned={Scanned} candidates={Candidates} writes={Writes} failures={Failures} integratedVerified={Verified} integratedHistorical={Historical} noCodeExpected={NoCode} noAttributionLegacy={NoAttribution} contentOnFence={Fence} genuinelyMissing={Missing} report={Report}",
            report.Completed,
            report.ScannedCards,
            report.CandidateCards,
            report.RecordsWritten,
            report.WriteFailures,
            counts[IntegrationRecordClasses.IntegratedVerified],
            counts[IntegrationRecordClasses.IntegratedHistorical],
            counts[IntegrationRecordClasses.NoCodeExpected],
            counts[IntegrationRecordClasses.NoAttributionLegacy],
            counts[IntegrationRecordClasses.ContentOnFence],
            counts[IntegrationRecordClasses.GenuinelyMissing],
            _reportPath);
        return report;
    }

    private static bool IsSweepRecord(TaskIntegrationRecord record)
        => string.Equals(record.Id, LegacyRecordId, StringComparison.OrdinalIgnoreCase)
           || string.Equals(record.Id, RecordId, StringComparison.OrdinalIgnoreCase);

    private EvaluatedRecord Evaluate(
        VerificationCandidate candidate,
        IReadOnlyDictionary<RepoKey, RepoEvidence> byRepo,
        DateTime recordedAt)
    {
        var task = candidate.Task;
        var key = RepoKeyFor(task);
        byRepo.TryGetValue(key, out var repo);
        repo ??= RepoEvidence.Unavailable(key.IntegrationBranch);

        // Apply the same conservative AGT-2562 policy in memory as a guard for
        // deployments where this sweep resumes after the superseded sweep was
        // interrupted between its task writes and completion report.
        var superseded = SupersededCommitSweepPolicy.Evaluate(
            task.Commits,
            sha => TaskIntegrationStatusService.AncestorSetContains(repo.Ancestors, sha));
        var virtualSuperseded = superseded.Replacements
            .Select(replacement => replacement.SupersededSha)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var effectiveTask = virtualSuperseded.Count == 0
            ? task
            : task with
            {
                Commits = task.Commits.Select(commit => virtualSuperseded.Contains(commit.Sha)
                    ? commit with { SupersededByAttempt = RecordId }
                    : commit).ToList(),
            };
        var effectiveCommits = TaskIntegrationStatusService.AttributedCommits(
            effectiveTask,
            repo.Ancestors);
        var allIntegrated = repo.AncestryReadable
            && effectiveCommits.Count > 0
            && effectiveCommits.All(sha =>
                TaskIntegrationStatusService.AncestorSetContains(repo.Ancestors, sha));
        var acceptedAt = ResolveAcceptedAt(task, candidate.Timeline);
        var hasDeliverables = ResultsInventory.HasActiveArtifacts(task.FolderPath);
        var noCodeExpected = !AcceptanceIntegrationPolicy.IsIntegrationRequired(task)
            || hasDeliverables && HasExplicitNoCodeClaim(task);
        var fenceRefs = FindFenceRefs(task, repo);
        var classification = HistoricalIntegrationVerificationPolicy.Classify(new(
            HasEffectiveCommits: effectiveCommits.Count > 0,
            AllEffectiveCommitsIntegrated: allIntegrated,
            AcceptedBeforeRecordingEra: acceptedAt < RecordingEraStartedAtUtc,
            NoCodeExpected: noCodeExpected,
            HasDeliverables: hasDeliverables,
            HasFenceContent: fenceRefs.Count > 0));

        var evidence = BuildEvidence(
            classification,
            repo,
            effectiveCommits.Count,
            virtualSuperseded.Count,
            hasDeliverables,
            fenceRefs.Count);
        return new EvaluatedRecord(new TaskIntegrationRecord
        {
            Id = RecordId,
            Version = 2,
            Classification = classification,
            RecordedAtUtc = recordedAt,
            AcceptedAtUtc = acceptedAt,
            IntegrationBranch = repo.IntegrationBranch,
            CommitShas = effectiveCommits.ToList(),
            FenceRefs = fenceRefs,
            Evidence = evidence,
        });
    }

    private Dictionary<RepoKey, RepoEvidence> BuildRepoEvidence(IEnumerable<TaskInfo> tasks)
    {
        var result = new Dictionary<RepoKey, RepoEvidence>();
        foreach (var task in tasks)
        {
            var key = RepoKeyFor(task);
            if (result.ContainsKey(key)) continue;
            if (string.IsNullOrWhiteSpace(key.Root))
            {
                result[key] = RepoEvidence.Unavailable(key.IntegrationBranch);
                continue;
            }

            var readRef = _git.ResolveIntegrationReadRef(key.Root, key.IntegrationBranch);
            var branch = readRef.StartsWith("origin/", StringComparison.Ordinal)
                ? readRef["origin/".Length..]
                : readRef;
            var readable = _git.TryGetAncestorShaSet(
                key.Root,
                [branch, _git.ResolveOriginReadRef(branch)],
                out var ancestors);
            var refs = RefNamespaces
                .SelectMany(name => _git.ListRefs(key.Root, name))
                .GroupBy(item => item.FullName, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            result[key] = new RepoEvidence(branch, ancestors, readable, refs);
        }
        return result;
    }

    private RepoKey RepoKeyFor(TaskInfo task)
        => new(
            _git.ResolveRepoRootForWatchPath(task.WatchPath) ?? string.Empty,
            _settings.Get(task.ProjectName).IntegrationBranch);

    private static List<string> FindFenceRefs(TaskInfo task, RepoEvidence repo)
    {
        if (repo.Refs.Count == 0) return [];
        var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var subject = ReviewSubjectStore.Read(task.FolderPath);
        AddRef(exact, subject?.ImmutableResultRef);
        AddRef(exact, subject?.ResultRef);
        foreach (var commit in task.Commits) AddRef(exact, commit.Branch);
        var deliveryFailure = RemoteDeliveryFailureStore.Read(task.FolderPath);
        AddRef(exact, deliveryFailure?.FenceBranch);
        foreach (var mentioned in ReadMentionedFenceRefs(task.FolderPath)) AddRef(exact, mentioned);

        var identities = new[] { task.Key, task.Id }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
        return repo.Refs
            .Where(reference => IsFenceOrResultRef(reference.FullName))
            .Where(reference => exact.Any(expected => RefNamesEqual(expected, reference))
                || identities.Any(identity => RefContainsIdentity(reference.FullName, identity)))
            .Select(reference => reference.FullName)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddRef(ISet<string> refs, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) refs.Add(value.Trim());
    }

    private static bool RefNamesEqual(string expected, GitRefLine actual)
    {
        var normalized = NormalizeRef(expected);
        return string.Equals(normalized, NormalizeRef(actual.FullName), StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, NormalizeRef(actual.ShortName), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRef(string value)
    {
        var normalized = value.Trim().Trim('`', '\'', '"', '(', ')', '[', ']', ',', '.');
        foreach (var prefix in new[] { "refs/remotes/origin/", "refs/heads/", "origin/" })
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return normalized[prefix.Length..];
        }
        return normalized;
    }

    private static bool IsFenceOrResultRef(string value)
        => value.Contains("/agent-studio/salvage/", StringComparison.OrdinalIgnoreCase)
           || value.Contains("/agent-studio/results/", StringComparison.OrdinalIgnoreCase)
           || value.Contains("/runner/", StringComparison.OrdinalIgnoreCase)
           || value.Contains("/task/", StringComparison.OrdinalIgnoreCase);

    private static bool RefContainsIdentity(string reference, string identity)
    {
        var segments = reference.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => string.Equals(
            segment,
            identity,
            StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ReadMentionedFenceRefs(string folderPath)
    {
        foreach (var fileName in new[] { "status.md", "prompt.md" })
        {
            var path = Path.Combine(folderPath, fileName);
            if (!File.Exists(path)) continue;
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "HistoricalIntegrationVerificationSweep: fence note read");
                continue;
            }

            foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!token.Contains("agent-studio/salvage/", StringComparison.OrdinalIgnoreCase)
                    && !token.Contains("agent-studio/results/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                yield return token;
            }
        }
    }

    private static bool HasExplicitNoCodeClaim(TaskInfo task)
    {
        var text = task.Title;
        foreach (var fileName in new[] { "prompt.md", "status.md" })
        {
            var path = Path.Combine(task.FolderPath, fileName);
            try
            {
                if (File.Exists(path)) text += "\n" + File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "HistoricalIntegrationVerificationSweep: no-code evidence read");
            }
        }

        var claims = new[]
        {
            "no code changes",
            "no source changes",
            "documentation-only",
            "docs-only",
            "report-only",
            "analysis-only",
            "concept-only",
            "concept only",
            "no implementation",
            "do not implement",
            "read-only task",
        };
        return claims.Any(claim => text.Contains(claim, StringComparison.OrdinalIgnoreCase));
    }

    private static DateTime ResolveAcceptedAt(
        TaskInfo task,
        IReadOnlyCollection<TimelineEvent> timeline)
    {
        var completed = timeline
            .Where(row => string.Equals(row.Kind, TimelineEventKinds.LaneChanged, StringComparison.Ordinal))
            .Where(row => row.Details?.TryGetValue("to", out var target) == true
                && string.Equals(target, TaskStates.Completed, StringComparison.Ordinal))
            .OrderByDescending(row => row.Ts)
            .FirstOrDefault();
        if (completed is not null) return completed.Ts.ToUniversalTime();
        if (task.State == TaskStates.Completed && task.EnteredLaneAt != default)
            return task.EnteredLaneAt.ToUniversalTime();
        return task.CreatedAt == default
            ? task.EnteredLaneAt.ToUniversalTime()
            : task.CreatedAt.ToUniversalTime();
    }

    private PipelineStepExecution? ReadLatestMergeStep(string folderPath)
    {
        try
        {
            return _pipeline.Read(folderPath)?.Steps.LastOrDefault(step => string.Equals(
                step.StepId,
                PipelineCatalogue.MergeIntoDevelopStepId,
                StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "historical-integration-verification pipeline-read-failed folder={Folder}", folderPath);
            return null;
        }
    }

    private static string BuildEvidence(
        string classification,
        RepoEvidence repo,
        int commitCount,
        int virtuallySuperseded,
        bool hasDeliverables,
        int fenceCount)
    {
        var superseded = virtuallySuperseded == 0
            ? string.Empty
            : $" {virtuallySuperseded} obsolete delivery-round commit(s) were excluded by the superseded policy.";
        return classification switch
        {
            IntegrationRecordClasses.IntegratedVerified =>
                $"All {commitCount} effective commit(s) are ancestors of '{repo.IntegrationBranch}'.{superseded}",
            IntegrationRecordClasses.IntegratedHistorical =>
                $"Acceptance predates integration recording; all {commitCount} effective commit(s) are ancestors of '{repo.IntegrationBranch}'.{superseded}",
            IntegrationRecordClasses.NoCodeExpected =>
                $"The card explicitly expects no code integration and has task deliverables (artifacts present: {hasDeliverables.ToString().ToLowerInvariant()}).",
            IntegrationRecordClasses.NoAttributionLegacy =>
                "Acceptance predates integration recording and no attributed commit or surviving task-associated result or salvage ref is available.",
            IntegrationRecordClasses.ContentOnFence =>
                $"Git content remains on {fenceCount} task-associated result or salvage ref(s); no integration ancestry was proven.{superseded}",
            _ when !repo.AncestryReadable =>
                "No integration proof or recovery ref was found because the project repository or integration graph was unavailable.",
            _ =>
                $"No integration ancestry, no qualifying no-code deliverable, and no surviving task-associated result or salvage ref were found.{superseded}",
        };
    }

    private HistoricalIntegrationVerificationReport? TryReadCompletedReport()
    {
        if (!File.Exists(_reportPath)) return null;
        try
        {
            var report = JsonSerializer.Deserialize<HistoricalIntegrationVerificationReport>(
                File.ReadAllText(_reportPath),
                Json);
            return report?.Completed == true ? report : null;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The historical integration report '{_reportPath}' is unreadable.",
                ex);
        }
    }

    private void WriteReport(HistoricalIntegrationVerificationReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_reportPath)!);
        var temporary = _reportPath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, report, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, _reportPath, overwrite: true);
    }

    private sealed record VerificationCandidate(
        TaskInfo Task,
        IReadOnlyCollection<TimelineEvent> Timeline);

    private sealed record EvaluatedRecord(TaskIntegrationRecord Record);

    private sealed record RepoKey(string Root, string IntegrationBranch);

    private sealed record RepoEvidence(
        string IntegrationBranch,
        IReadOnlySet<string> Ancestors,
        bool AncestryReadable,
        IReadOnlyList<GitRefLine> Refs)
    {
        public static RepoEvidence Unavailable(string branch)
            => new(branch, new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, []);
    }
}

public sealed record HistoricalIntegrationOperatorItem(
    string Project,
    string TaskKey,
    string Lane,
    string Classification,
    string Evidence,
    IReadOnlyList<string> FenceRefs);

public sealed record HistoricalIntegrationVerificationReport(
    int Version,
    DateTime CompletedAtUtc,
    bool Completed,
    bool AlreadyCompleted,
    int ScannedCards,
    int CandidateCards,
    int RecordsWritten,
    int WriteFailures,
    int BatchSize,
    int BatchCount,
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyList<HistoricalIntegrationOperatorItem> OperatorItems);
