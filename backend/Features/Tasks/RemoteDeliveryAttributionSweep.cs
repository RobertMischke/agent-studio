using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>
/// One-time, conservative repair for recent remote deliveries stored inside a
/// product repository. It replays immutable ResultEnvelope facts through the
/// same Git verification and attribution guard as live completion, reconstructs
/// the durable token receipt from the CLI log, and reapplies the superseded
/// generation policy after all missing generations have been restored.
/// </summary>
public sealed class RemoteDeliveryAttributionSweep
{
    public const string ReportFileName = "remote-delivery-attribution-v1.json";
    public const int LookbackDays = 7;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly AttemptAuthorityService _authority;
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly AgentStudio.Tokens.RemoteTaskTokenReceiptService _tokens;
    private readonly ILogger<RemoteDeliveryAttributionSweep> _logger;
    private readonly string _reportPath;
    private readonly Func<DateTime> _utcNow;

    public RemoteDeliveryAttributionSweep(
        TaskScannerService scanner,
        TaskMutationService mutations,
        AttemptAuthorityService authority,
        GitService git,
        ProjectSettingsService settings,
        AgentStudio.Tokens.RemoteTaskTokenReceiptService tokens,
        IConfiguration configuration,
        ILogger<RemoteDeliveryAttributionSweep> logger)
        : this(
            scanner,
            mutations,
            authority,
            git,
            settings,
            tokens,
            Path.Combine(
                Path.GetFullPath(configuration["TaskRepository"]
                    ?? Path.Combine(AppContext.BaseDirectory, "workspace")),
                ".metadata",
                "migrations",
                ReportFileName),
            () => DateTime.UtcNow,
            logger)
    {
    }

    internal RemoteDeliveryAttributionSweep(
        TaskScannerService scanner,
        TaskMutationService mutations,
        AttemptAuthorityService authority,
        GitService git,
        ProjectSettingsService settings,
        AgentStudio.Tokens.RemoteTaskTokenReceiptService tokens,
        string reportPath,
        Func<DateTime> utcNow,
        ILogger<RemoteDeliveryAttributionSweep> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _authority = authority;
        _git = git;
        _settings = settings;
        _tokens = tokens;
        _reportPath = reportPath;
        _utcNow = utcNow;
        _logger = logger;
    }

    public RemoteDeliveryAttributionSweepReport RunOnce()
    {
        if (File.Exists(_reportPath))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<RemoteDeliveryAttributionSweepReport>(
                    File.ReadAllText(_reportPath),
                    Json);
                return existing is null
                    ? RemoteDeliveryAttributionSweepReport.CompletedEarlier()
                    : existing with { AlreadyCompleted = true };
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"The remote delivery attribution report '{_reportPath}' is unreadable.",
                    ex);
            }
        }

        var completedAt = _utcNow();
        var cutoff = completedAt.AddDays(-LookbackDays);
        var rows = new List<RemoteDeliveryAttributionSweepTaskReport>();
        var repairedCommits = 0;
        var repairedTokenCalls = 0;
        var supersededCommits = 0;
        var unresolvedTasks = 0;

        foreach (var task in _scanner.ScanAllJobsWithArchive()
                     .Where(task => TaskIntegrationStatusService.DeliveredLanes.Contains(task.State)))
        {
            // Remote lease authority is keyed by the runner-facing reference
            // key (TE-41/CAC-...), while TaskInfo.TaskKey is the local
            // watchPath::id address used by board projections.
            var projection = _authority.GetTaskProjection(
                task.Key ?? task.Id,
                includeArchived: true);
            var attempts = projection.RunAttempts
                .Where(IsDeliveredCodingAttempt)
                .Where(attempt => attempt.TerminalAt >= cutoff)
                .OrderBy(attempt => attempt.CreatedAt)
                .ToList();
            if (attempts.Count == 0) continue;

            var repoRoot = _git.ResolveRepoRootForWatchPath(task.WatchPath);
            if (!IsInRepositoryStorage(task.WatchPath, repoRoot)) continue;

            var taskErrors = new List<string>();
            var taskRepairedCommits = 0;
            var taskRepairedTokenCalls = 0;
            var taskSupersededCommits = 0;
            try
            {
                var existingAttemptIds = task.Commits
                    .Where(commit => !string.IsNullOrWhiteSpace(commit.RunAttemptId))
                    .Select(commit => commit.RunAttemptId!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var attempt in attempts)
                {
                    if (existingAttemptIds.Contains(attempt.AttemptId)) continue;
                    var envelope = attempt.ResultEnvelope!;
                    if (!EnvelopeIsValid(attempt))
                    {
                        taskErrors.Add($"Attempt {attempt.AttemptId} has an invalid immutable result envelope.");
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(envelope.ImmutableRemoteRef))
                    {
                        taskErrors.Add($"Attempt {attempt.AttemptId} has no immutable Git result ref.");
                        continue;
                    }

                    var range = _git.InspectRemoteDeliveryCommitRange(
                        repoRoot!,
                        envelope.ImmutableRemoteRef,
                        envelope.ResultSha,
                        task.IntegrationBranch ?? _settings.Get(task.ProjectName).IntegrationBranch,
                        envelope.BaseSha);
                    if (!range.Success)
                    {
                        taskErrors.Add($"Attempt {attempt.AttemptId}: {range.Warning}");
                        continue;
                    }

                    var attribution = RemoteCommitAttributionGuard.Attribute(
                        task.Key ?? task.Id,
                        envelope.ImmutableRemoteRef,
                        range.Commits);
                    if (!attribution.Accepted)
                    {
                        taskErrors.Add($"Attempt {attempt.AttemptId}: {attribution.Warning}");
                        continue;
                    }
                    if (attribution.Commits.Count == 0) continue;

                    _mutations.SetRunIntegrationBranchOnFolder(task.FolderPath, range.IntegrationBranch!);
                    var written = _mutations.SetRemoteCommitAttributionOnFolder(
                        task.FolderPath,
                        attempt.AttemptId,
                        attempt.Lease?.ExecutorId ?? "remote-attribution-sweep",
                        envelope.ResultSha,
                        attribution.Commits);
                    if (!written)
                    {
                        taskErrors.Add($"Attempt {attempt.AttemptId}: commit attribution mutation failed.");
                        continue;
                    }

                    taskRepairedCommits += attribution.Commits.Count;
                    existingAttemptIds.Add(attempt.AttemptId);
                }

                var tokenResult = _tokens.RecordFromTaskLog(task);
                taskRepairedTokenCalls = tokenResult.AddedCalls;
                if (!string.IsNullOrWhiteSpace(tokenResult.Warning))
                    taskErrors.Add(tokenResult.Warning);

                var supersession = ApplySupersededPolicy(task, repoRoot!);
                taskSupersededCommits = supersession.MarkedCommits;
                taskErrors.AddRange(supersession.Unresolved);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "remote-delivery-attribution-sweep failed project={Project} task={Task}",
                    task.ProjectName,
                    task.Key ?? task.Id);
                taskErrors.Add(ex.Message);
            }

            if (taskRepairedCommits == 0
                && taskRepairedTokenCalls == 0
                && taskSupersededCommits == 0
                && taskErrors.Count == 0)
            {
                continue;
            }

            repairedCommits += taskRepairedCommits;
            repairedTokenCalls += taskRepairedTokenCalls;
            supersededCommits += taskSupersededCommits;
            if (taskErrors.Count > 0) unresolvedTasks++;
            rows.Add(new RemoteDeliveryAttributionSweepTaskReport(
                task.Key ?? task.Id,
                task.ProjectName,
                taskRepairedCommits,
                taskRepairedTokenCalls,
                taskSupersededCommits,
                taskErrors));
        }

        var report = new RemoteDeliveryAttributionSweepReport(
            Version: 1,
            CompletedAtUtc: completedAt,
            CutoffUtc: cutoff,
            AlreadyCompleted: false,
            RepairedCommits: repairedCommits,
            RepairedTokenCalls: repairedTokenCalls,
            SupersededCommits: supersededCommits,
            UnresolvedTasks: unresolvedTasks,
            Tasks: rows);
        WriteReport(report);
        _logger.LogInformation(
            "remote-delivery-attribution-sweep completed repairedCommits={Commits} repairedTokenCalls={TokenCalls} supersededCommits={Superseded} unresolvedTasks={Unresolved} report={Report}",
            repairedCommits,
            repairedTokenCalls,
            supersededCommits,
            unresolvedTasks,
            _reportPath);
        return report;
    }

    private SupersededRepairResult ApplySupersededPolicy(TaskInfo original, string repoRoot)
    {
        var current = _scanner.ScanAllJobsWithArchive().FirstOrDefault(task =>
            string.Equals(task.FolderPath, original.FolderPath, StringComparison.OrdinalIgnoreCase));
        if (current is null
            || current.Commits.Count < 2
            || !current.Commits.Any(commit =>
                !TaskCommitSupersession.IsSuperseded(commit)
                && SupersededCommitSweepPolicy.IsRunnerFence(commit)))
        {
            return SupersededRepairResult.None;
        }

        var configuredBranch = current.IntegrationBranch
            ?? _settings.Get(current.ProjectName).IntegrationBranch;
        var integrationRef = _git.ResolveIntegrationReadRef(repoRoot, configuredBranch);
        if (!_git.TryGetAncestorShaSet(
                repoRoot,
                [integrationRef, _git.ResolveOriginReadRef(integrationRef)],
                out var ancestors))
        {
            return new SupersededRepairResult(
                0,
                [$"The integration graph for '{integrationRef}' could not be read for superseded-delivery classification."]);
        }

        var enriched = current.Commits.Select(commit =>
        {
            if (commit.Files.Count > 0) return commit;
            var files = _git.GetCommitFiles(current.Id, current.WatchPath, commit.Sha);
            return files.Count == 0 ? commit : commit with
            {
                FilesChanged = files.Count,
                Files = files.Select(file => file.Path).ToList(),
            };
        }).ToList();
        var decision = SupersededCommitSweepPolicy.Evaluate(
            enriched,
            sha => TaskIntegrationStatusService.AncestorSetContains(ancestors, sha));
        var unresolved = decision.Ambiguous
            .Select(item => $"Superseded delivery {item.FenceSha}: {item.Reason}")
            .ToList();
        if (decision.Replacements.Count == 0)
            return new SupersededRepairResult(0, unresolved);

        var write = _mutations.MarkCommitsSupersededOnFolder(
            current.FolderPath,
            decision.Replacements.ToDictionary(
                replacement => replacement.SupersededSha,
                replacement => replacement.ReplacementAttempt,
                StringComparer.OrdinalIgnoreCase));
        if (!write.Succeeded)
            unresolved.Add("The superseded-delivery mutation failed.");
        return new SupersededRepairResult(
            write.Succeeded ? write.MarkedCommits : 0,
            unresolved);
    }

    private static bool IsDeliveredCodingAttempt(RunAttemptDto attempt)
        => attempt.State == AttemptLifecycleState.Completed
           && attempt.ResultEnvelope is not null
           && attempt.TerminalAt.HasValue
           && attempt.TerminalOutcome is not null
           && (string.Equals(attempt.TerminalOutcome, "done", StringComparison.OrdinalIgnoreCase)
               || string.Equals(attempt.TerminalOutcome, "noop", StringComparison.OrdinalIgnoreCase));

    private static bool EnvelopeIsValid(RunAttemptDto attempt)
    {
        try
        {
            if (attempt.ResultEnvelope is null || string.IsNullOrWhiteSpace(attempt.ResultEnvelopeDigest))
                return false;
            AgentStudio.TaskServer.Contracts.ResultEnvelopeDigest.Validate(attempt.ResultEnvelope);
            return string.Equals(
                AgentStudio.TaskServer.Contracts.ResultEnvelopeDigest.Compute(attempt.ResultEnvelope),
                attempt.ResultEnvelopeDigest,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsInRepositoryStorage(string watchPath, string? repoRoot)
    {
        if (string.IsNullOrWhiteSpace(watchPath) || string.IsNullOrWhiteSpace(repoRoot)) return false;
        try
        {
            var expected = Path.GetFullPath(Path.Combine(repoRoot, ".orchestrator", "jobs"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var actual = Path.GetFullPath(watchPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(
                expected,
                actual,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "RemoteDeliveryAttributionSweep: storage path could not be normalized.");
            return false;
        }
    }

    private void WriteReport(RemoteDeliveryAttributionSweepReport report)
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

    private sealed record SupersededRepairResult(
        int MarkedCommits,
        IReadOnlyList<string> Unresolved)
    {
        public static SupersededRepairResult None { get; } = new(0, []);
    }
}

public sealed record RemoteDeliveryAttributionSweepTaskReport(
    string TaskKey,
    string Project,
    int RepairedCommits,
    int RepairedTokenCalls,
    int SupersededCommits,
    IReadOnlyList<string> Errors);

public sealed record RemoteDeliveryAttributionSweepReport(
    int Version,
    DateTime CompletedAtUtc,
    DateTime CutoffUtc,
    bool AlreadyCompleted,
    int RepairedCommits,
    int RepairedTokenCalls,
    int SupersededCommits,
    int UnresolvedTasks,
    IReadOnlyList<RemoteDeliveryAttributionSweepTaskReport> Tasks)
{
    public static RemoteDeliveryAttributionSweepReport CompletedEarlier()
        => new(1, default, default, true, 0, 0, 0, 0, []);
}
