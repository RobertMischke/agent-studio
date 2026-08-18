using System.Collections.Concurrent;

namespace AgentStudio.HostHealth;

/// <summary>
/// Reads a CLI's <c>--version</c> verdict. A seam so this feature can be
/// tested without spawning anything, and so the CLI layer keeps owning how a
/// binary is resolved.
/// </summary>
public interface ILocalCliVersionProbe
{
    (bool Available, string? Version) Probe(string cliType);
}

/// <summary>What one CLI looks like right now, plus the most recent repair for it.</summary>
public sealed record LocalCliHealthEntry
{
    public required string CliType { get; init; }
    public required string PackageId { get; init; }
    public required string State { get; init; }
    public required string Action { get; init; }
    public required string Summary { get; init; }
    public bool Available { get; init; }
    public string? Version { get; init; }
    public string? PackageVersion { get; init; }
    public DateTime? LastRepairAt { get; init; }
    public bool? LastRepairSucceeded { get; init; }
}

/// <summary>
/// One repair worth telling the operator about. Feeds the status-bar note; the
/// durable record is the <c>cli-repairs.jsonl</c> row.
/// </summary>
public sealed record LocalCliRepairNote
{
    public required string CliType { get; init; }
    public required DateTime At { get; init; }
    public required bool Repaired { get; init; }
    public required string State { get; init; }
    public required string Message { get; init; }
    public string? VersionBefore { get; init; }
    public string? VersionAfter { get; init; }
}

/// <summary>Host-health answer for the whole control plane.</summary>
public sealed record LocalCliHealthSnapshot(
    DateTime CheckedAt,
    IReadOnlyList<LocalCliHealthEntry> Clis,
    IReadOnlyList<LocalCliRepairNote> RecentRepairs);

/// <summary>
/// Coordinates local CLI install health: probe, diagnose, repair once per
/// window, journal, and keep a short note for the UI.
///
/// <para>
/// Flow order is deliberate - boundary validation (is this a CLI we can
/// reinstall), application coordination (probe plus inspect), pure decision
/// (<see cref="LocalCliInstallDiagnosis"/> and
/// <see cref="LocalCliRepairThrottle"/>), then bounded side effects (one npm
/// install, one journal append, one note).
/// </para>
///
/// <para>
/// It deliberately does not touch runner mode. Once the shims are back, the
/// existing CLI-recovery resume in <c>ProjectRunner.TickCliRecoveryResume</c>
/// sees <c>IsAvailable()</c> flip and restores the operator's desired mode by
/// itself, so healing and mode restoration stay one responsibility each.
/// </para>
/// </summary>
public sealed class LocalCliHealthService
{
    private readonly ILocalCliVersionProbe _versionProbe;
    private readonly LocalCliInstallInspector _inspector;
    private readonly IGlobalNpmPackageInstaller _installer;
    private readonly LocalCliRepairJournal _journal;
    private readonly ILogger<LocalCliHealthService> _logger;
    private readonly TimeSpan _repairWindow;
    private readonly Func<DateTime> _utcNow;

    /// <summary>Last automatic repair attempt per CLI; the input to the one-per-window rate limit.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _lastAutoAttemptUtc = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last state journalled per CLI, so a steady unhealthy state does not append a row every tick.</summary>
    private readonly ConcurrentDictionary<string, string> _lastJournalledState = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One in-flight diagnose-and-repair per CLI.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repairGates = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _notesGate = new();
    private readonly List<LocalCliRepairNote> _notes = [];

    /// <summary>How many repair notes the status bar can look back over.</summary>
    private const int MaxNotes = 10;

    public LocalCliHealthService(
        ILocalCliVersionProbe versionProbe,
        LocalCliInstallInspector inspector,
        IGlobalNpmPackageInstaller installer,
        LocalCliRepairJournal journal,
        IConfiguration configuration,
        ILogger<LocalCliHealthService> logger)
        : this(versionProbe, inspector, installer, journal, logger,
               ResolveWindow(configuration), static () => DateTime.UtcNow)
    {
    }

    /// <summary>Test seam: fake clock and explicit window.</summary>
    internal LocalCliHealthService(
        ILocalCliVersionProbe versionProbe,
        LocalCliInstallInspector inspector,
        IGlobalNpmPackageInstaller installer,
        LocalCliRepairJournal journal,
        ILogger<LocalCliHealthService> logger,
        TimeSpan repairWindow,
        Func<DateTime> utcNow)
    {
        _versionProbe = versionProbe;
        _inspector = inspector;
        _installer = installer;
        _journal = journal;
        _logger = logger;
        _repairWindow = repairWindow;
        _utcNow = utcNow;
    }

    /// <summary>Diagnose every known CLI without repairing anything.</summary>
    public LocalCliHealthSnapshot Inspect()
    {
        var entries = LocalCliPackage.Known.Select(package =>
        {
            var (facts, diagnosis) = Diagnose(package);
            return BuildEntry(package, facts, diagnosis);
        }).ToList();

        return new LocalCliHealthSnapshot(_utcNow(), entries, RecentNotes());
    }

    /// <summary>
    /// Diagnose one CLI and, when the diagnosis licenses it and the rate limit
    /// allows, repair it. Returns the state after the attempt.
    /// </summary>
    public async Task<LocalCliHealthEntry> EnsureHealthyAsync(
        string cliType,
        bool operatorRequested,
        CancellationToken ct)
    {
        var package = LocalCliPackage.Find(cliType);
        if (package is null)
        {
            return new LocalCliHealthEntry
            {
                CliType = cliType,
                PackageId = "",
                State = nameof(LocalCliInstallState.Unknown),
                Action = nameof(LocalCliRepairAction.EscalateToOperator),
                Summary = $"'{cliType}' is not an npm-installed CLI this host can reinstall.",
            };
        }

        // One repair at a time per CLI. The probe loop and an operator request
        // can land together, and two concurrent `npm install --global` runs
        // against the same prefix are precisely how a half-installed CLI is
        // produced - the failure class this feature exists to clean up after.
        var gate = _repairGates.GetOrAdd(package.CliType, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var (facts, diagnosis) = Diagnose(package);

            if (diagnosis.Action != LocalCliRepairAction.GlobalReinstall)
            {
                await JournalOnStateChangeAsync(package, facts, diagnosis, operatorRequested, ct);
                return BuildEntry(package, facts, diagnosis);
            }

            var now = _utcNow();
            _lastAutoAttemptUtc.TryGetValue(package.CliType, out var lastAttempt);
            var throttle = LocalCliRepairThrottle.Decide(
                lastAttempt == default ? null : lastAttempt, now, _repairWindow, operatorRequested);

            if (!throttle.Allowed)
            {
                _logger.LogInformation(
                    "Local {CliType} CLI is broken ({Summary}) but the automatic repair is rate-limited: {Reason}",
                    package.CliType, diagnosis.Summary, throttle.Reason);
                await JournalOnStateChangeAsync(package, facts, diagnosis, operatorRequested, ct, throttle.Reason);
                return BuildEntry(package, facts, diagnosis);
            }

            return await RepairAsync(package, facts, diagnosis, operatorRequested, now, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Most recent repair notes, newest first.</summary>
    public IReadOnlyList<LocalCliRepairNote> RecentNotes()
    {
        lock (_notesGate) return _notes.ToList();
    }

    private async Task<LocalCliHealthEntry> RepairAsync(
        LocalCliPackage package,
        LocalCliInstallFacts before,
        LocalCliInstallDiagnosisResult diagnosis,
        bool operatorRequested,
        DateTime startedAt,
        CancellationToken ct)
    {
        if (!operatorRequested) _lastAutoAttemptUtc[package.CliType] = startedAt;

        // Capture the npm activity that overlaps the breakage BEFORE the
        // repair runs, so our own npm invocation cannot be mistaken for the
        // trigger we are trying to prove.
        var npmActivity = _inspector
            .RecentNpmActivity(startedAt, NpmActivityWindow.DefaultLookBack)
            .Select(log => new LocalCliRepairNpmActivity(log.Name, log.LastWriteUtc, log.Bytes))
            .ToList();

        _logger.LogWarning(
            "Local {CliType} CLI is broken: {Summary} Repairing with npm install --global {PackageId} "
            + "({Trigger}). npm logs in the preceding {LookBackMinutes} minutes: {NpmLogCount}.",
            package.CliType, diagnosis.Summary, package.PackageId,
            operatorRequested ? "operator-requested" : "automatic, one attempt per window",
            (int)NpmActivityWindow.DefaultLookBack.TotalMinutes, npmActivity.Count);

        var install = await _installer.InstallGlobalAsync(package.PackageId, ct);
        var (after, afterDiagnosis) = Diagnose(package);
        var repaired = after.VersionProbeOk;

        var record = new LocalCliRepairRecord
        {
            At = startedAt,
            CliType = package.CliType,
            PackageId = package.PackageId,
            State = diagnosis.State.ToString(),
            Action = diagnosis.Action.ToString(),
            Summary = diagnosis.Summary,
            Attempted = true,
            Repaired = repaired,
            OperatorRequested = operatorRequested,
            VersionBefore = before.ProbedVersion,
            VersionAfter = after.ProbedVersion,
            PackageVersionBefore = before.PackageVersion,
            PackageVersionAfter = after.PackageVersion,
            DurationMs = install.DurationMs,
            NpmActivity = npmActivity,
            InstallerOutput = install.Output.Length == 0 ? null : install.Output,
            Error = repaired ? null : install.Error ?? afterDiagnosis.Summary,
        };
        await _journal.AppendAsync(record, ct);
        _lastJournalledState[package.CliType] = afterDiagnosis.State.ToString();

        if (repaired)
        {
            _logger.LogInformation(
                "Repaired the local {CliType} CLI in {DurationMs:F0} ms: package {Before} -> {After}, CLI reports {Version}.",
                package.CliType, install.DurationMs,
                before.PackageVersion ?? "unknown", after.PackageVersion ?? "unknown",
                after.ProbedVersion ?? "no version");
        }
        else
        {
            // Alarm only on failure: a repair that worked is a note, a repair
            // that did not is the thing an operator has to act on.
            _logger.LogError(
                "Repair of the local {CliType} CLI failed after npm install --global {PackageId}: {Error}",
                package.CliType, package.PackageId, record.Error);
        }

        AddNote(new LocalCliRepairNote
        {
            CliType = package.CliType,
            At = startedAt,
            Repaired = repaired,
            State = afterDiagnosis.State.ToString(),
            Message = repaired
                ? $"{package.CliType} CLI repaired ({DescribeCause(diagnosis.State)})."
                : $"{package.CliType} CLI repair failed: {record.Error}",
            VersionBefore = before.ProbedVersion ?? before.PackageVersion,
            VersionAfter = after.ProbedVersion ?? after.PackageVersion,
        });

        return BuildEntry(package, after, afterDiagnosis);
    }

    private (LocalCliInstallFacts Facts, LocalCliInstallDiagnosisResult Diagnosis) Diagnose(LocalCliPackage package)
    {
        var (available, version) = _versionProbe.Probe(package.CliType);
        var facts = _inspector.Inspect(package, available, version);
        return (facts, LocalCliInstallDiagnosis.Diagnose(facts));
    }

    /// <summary>
    /// Append a diagnosis row only when the state actually changed. A host
    /// that simply has no Codex installed must not write one row per probe
    /// tick forever.
    /// </summary>
    private async Task JournalOnStateChangeAsync(
        LocalCliPackage package,
        LocalCliInstallFacts facts,
        LocalCliInstallDiagnosisResult diagnosis,
        bool operatorRequested,
        CancellationToken ct,
        string? throttledReason = null)
    {
        var state = diagnosis.State.ToString();
        // A throttled observation is its own row shape, but still only worth
        // writing once per episode: with a five-minute probe and a one-hour
        // window a broken host would otherwise append eleven identical rows an
        // hour while deliberately doing nothing.
        var key = throttledReason is null ? state : state + "|throttled";
        var previous = _lastJournalledState.GetValueOrDefault(package.CliType);
        if (string.Equals(previous, key, StringComparison.Ordinal)) return;
        _lastJournalledState[package.CliType] = key;

        if (diagnosis.State == LocalCliInstallState.Ready && previous is null) return;

        await _journal.AppendAsync(new LocalCliRepairRecord
        {
            At = _utcNow(),
            CliType = package.CliType,
            PackageId = package.PackageId,
            State = state,
            Action = diagnosis.Action.ToString(),
            Summary = diagnosis.Summary,
            Attempted = false,
            Repaired = diagnosis.State == LocalCliInstallState.Ready,
            ThrottledReason = throttledReason,
            OperatorRequested = operatorRequested,
            VersionBefore = facts.ProbedVersion,
            PackageVersionBefore = facts.PackageVersion,
        }, ct);
    }

    private LocalCliHealthEntry BuildEntry(
        LocalCliPackage package,
        LocalCliInstallFacts facts,
        LocalCliInstallDiagnosisResult diagnosis)
    {
        var lastNote = RecentNotes().FirstOrDefault(
            note => string.Equals(note.CliType, package.CliType, StringComparison.OrdinalIgnoreCase));

        return new LocalCliHealthEntry
        {
            CliType = package.CliType,
            PackageId = package.PackageId,
            State = diagnosis.State.ToString(),
            Action = diagnosis.Action.ToString(),
            Summary = diagnosis.Summary,
            Available = facts.VersionProbeOk,
            Version = facts.ProbedVersion,
            PackageVersion = facts.PackageVersion,
            LastRepairAt = lastNote?.At,
            LastRepairSucceeded = lastNote?.Repaired,
        };
    }

    /// <summary>Operator-facing wording for what was wrong, for the one-line note.</summary>
    private static string DescribeCause(LocalCliInstallState state) => state switch
    {
        LocalCliInstallState.ShimMissingPackagePresent => "bin shims were missing",
        LocalCliInstallState.PackageBroken => "the installed package was broken",
        LocalCliInstallState.NotInstalled => "the package was not installed",
        _ => "cause unknown",
    };

    private void AddNote(LocalCliRepairNote note)
    {
        lock (_notesGate)
        {
            _notes.Insert(0, note);
            if (_notes.Count > MaxNotes) _notes.RemoveRange(MaxNotes, _notes.Count - MaxNotes);
        }
    }

    private static TimeSpan ResolveWindow(IConfiguration configuration)
    {
        var configured = configuration.GetValue<int?>("HostHealth:CliRepairWindowMinutes");
        return configured is > 0
            ? TimeSpan.FromMinutes(configured.Value)
            : LocalCliRepairThrottle.DefaultWindow;
    }
}
