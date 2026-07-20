using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentStudio.Management;

/// <summary>
/// Task Server management authority. Consoles call this service only through
/// the authenticated API; all mutations are idempotent and appended to the
/// durable management audit ledger.
/// </summary>
public sealed class ManagementService
{
    private static readonly DateTime StartedAt = DateTime.UtcNow;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IConfiguration _configuration;
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly ClientIdentityStore _clients;
    private readonly IServiceProvider _services;
    private readonly object _gate = new();

    public ManagementService(
        IConfiguration configuration,
        TaskScannerService scanner,
        TaskStateMachine states,
        ClientIdentityStore clients,
        IServiceProvider services)
    {
        _configuration = configuration;
        _scanner = scanner;
        _states = states;
        _clients = clients;
        _services = services;
    }

    private string Root => Path.GetFullPath(_configuration["TaskRepository"]
        ?? Path.Combine(AppContext.BaseDirectory, "workspace"));
    private string Metadata => Path.Combine(Root, ".metadata");
    private string StatePath => Path.Combine(Metadata, "management-state.json");
    private string AuditPath => Path.Combine(Root, ".audit", "management.jsonl");
    private string BackupDirectory => Path.GetFullPath(_configuration["Management:BackupDirectory"]
        ?? Path.Combine(Path.GetDirectoryName(Root)!, Path.GetFileName(Root) + "-backups"));
    private int DefaultRetention => Math.Clamp(_configuration.GetValue("Management:BackupRetention", 7), 1, 100);

    public ManagementStatus Snapshot(string url)
    {
        var jobs = _scanner.ScanAllJobs();
        var maintenance = ReadState();
        var migrations = ReadMigrations();
        var reasons = new List<string>();
        if (!Directory.Exists(Root)) reasons.Add("data-directory-missing");
        if (migrations.Any(x => x.State is "running" or "failed")) reasons.Add("migration-active");
        if (maintenance.Mode != "normal") reasons.Add("maintenance-" + maintenance.Mode);
        var ready = reasons.Count == 0;
        var files = Directory.Exists(Root)
            ? Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).ToArray()
            : [];
        var eventFiles = files.Where(IsEventFile).ToArray();
        var artifactFiles = files.Where(IsArtifactFile).ToArray();
        var lastEvidence = eventFiles.Concat(artifactFiles)
            .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();

        var assembly = typeof(ManagementService).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        var identities = _clients.ListAll();
        var runners = ReadRunners(identities);

        return new ManagementStatus(
            new ServerIdentity(
                _configuration["Management:ServerId"] ?? Environment.MachineName.ToLowerInvariant(),
                url.TrimEnd('/'), version, "1.0", "1.0",
                Math.Max(0, (long)(DateTime.UtcNow - StartedAt).TotalSeconds)),
            new ServerHealth(ready ? "healthy" : reasons.Contains("data-directory-missing") ? "degraded" : "maintenance", true, ready, reasons),
            new StoreStatus(
                files.Sum(SafeLength),
                jobs.Select(x => x.ProjectName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                jobs.Count(x => x.State != TaskStates.Archive),
                jobs.Count(x => x.State == TaskStates.Archive),
                CountJsonLines(eventFiles), artifactFiles.LongLength, identities.Count),
            new EvidenceStatus(
                eventFiles.Length + artifactFiles.Length == 0 ? "empty" : "available",
                eventFiles.LongLength, artifactFiles.LongLength,
                lastEvidence == default ? null : lastEvidence.ToString("O")),
            maintenance,
            migrations,
            runners,
            ReadSecurityStatus(),
            new BackupStatus(BackupDirectory, DefaultRetention, ListBackups(), ReadBackupFailure()));
    }

    public RecoveryDiagnostics Diagnostics()
    {
        var status = Snapshot("local");
        var findings = new List<string>(status.Health.Reasons);
        var writable = CanWriteRoot();
        if (!writable) findings.Add("data-directory-not-writable");
        var drive = new DriveInfo(Path.GetPathRoot(Root)!);
        var latest = status.Backups.Items.FirstOrDefault();
        return new RecoveryDiagnostics(
            DateTime.UtcNow.ToString("O"), status.Health.State, status.Health.Ready,
            status.Maintenance.Mode, Directory.Exists(Root), writable,
            drive.IsReady ? drive.AvailableFreeSpace : 0,
            latest?.Id, latest?.VerificationState, findings,
            "systemd/container/service manager owns process start and restart");
    }

    public ManagementCommandResult Execute(ManagementCommandRequest request, string actor, string headerKey)
    {
        var kind = (request.Kind ?? "").Trim().ToLowerInvariant();
        var key = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? headerKey : request.IdempotencyKey.Trim();
        if (string.IsNullOrWhiteSpace(key)) throw new ManagementException(400, "idempotency-key-required");

        lock (_gate)
        {
            var prior = FindPrior(key, kind);
            if (prior is not null) return prior;
            if (!request.DryRun && !string.Equals(request.Confirmation, kind, StringComparison.Ordinal))
                throw new ManagementException(400, $"confirmation must exactly equal '{kind}'");

            try
            {
                var result = kind switch
                {
                    "archive-sweep" => SweepArchive(request.DryRun, actor, key),
                    "orphan-sweep" => SweepOrphans(request.DryRun, actor, key),
                    "fixture-sweep" => SweepFixtures(request.DryRun, actor, key),
                    "backup-create" => CreateBackup(request.DryRun, actor, key, request.RetentionCount),
                    "restore-verify" => VerifyRestore(request.DryRun, actor, key, request.BackupId),
                    "backup-retention" => ApplyRetention(request.DryRun, actor, key, request.RetentionCount),
                    "maintenance-enter" => SetMaintenance(request.DryRun, actor, key, "maintenance", request.Reason),
                    "maintenance-read-only" => SetMaintenance(request.DryRun, actor, key, "read-only", request.Reason),
                    "maintenance-exit" => SetMaintenance(request.DryRun, actor, key, "normal", request.Reason),
                    "shutdown-prepare" => PrepareShutdown(request.DryRun, actor, key, request.Reason),
                    _ => throw new ManagementException(400, "unknown-management-command")
                };
                AppendAudit(result);
                return result;
            }
            catch (Exception ex)
            {
                AppendAudit(new ManagementCommandResult(
                    "cmd_" + Guid.NewGuid().ToString("N"), kind, request.DryRun, "failed",
                    0, 0, ex.Message, DateTime.UtcNow.ToString("O"), actor, key));
                throw;
            }
        }
    }

    private ManagementCommandResult SweepArchive(bool dry, string actor, string key)
    {
        var candidates = _scanner.ScanAllJobs().Where(x => x.State == TaskStates.Completed).ToArray();
        var affected = 0;
        if (!dry)
            foreach (var item in candidates)
                if (_states.MoveJob(item.Id, TaskStates.Archive, item.WatchPath, "management-archive-sweep").Status == MoveJobStatus.Success) affected++;
        return Result("archive-sweep", dry, candidates.Length, affected,
            dry ? $"{candidates.Length} completed tasks would be archived." : $"Archived {affected} completed tasks.", actor, key);
    }

    private ManagementCommandResult SweepOrphans(bool dry, string actor, string key)
    {
        var rows = new List<(string WatchPath, string Lane, string Folder)>();
        foreach (var watch in _scanner.GetWatchPaths())
            foreach (var lane in new[] { TaskStates.Archive, TaskStates.FailedPickup })
            {
                var lanePath = Path.Combine(watch.Path, lane);
                if (!Directory.Exists(lanePath)) continue;
                rows.AddRange(Directory.EnumerateDirectories(lanePath)
                    .Where(x => !File.Exists(Path.Combine(x, "task.json")))
                    .Select(x => (watch.Path, lane, Path.GetFileName(x))));
            }
        var affected = 0;
        if (!dry)
            foreach (var row in rows)
                if (_states.DeleteOrphanFolder(row.WatchPath, row.Lane, row.Folder).Status == OrphanFolderDeleteStatus.Success) affected++;
        return Result("orphan-sweep", dry, rows.Count, affected,
            dry ? $"{rows.Count} terminal orphan folders would be removed." : $"Removed {affected} terminal orphan folders.", actor, key);
    }

    private ManagementCommandResult SweepFixtures(bool dry, string actor, string key)
    {
        var candidates = _scanner.ScanAllJobs().Where(x => x.Fixture || FixtureHeuristics.IsLikelyFixture(x)).ToArray();
        var affected = 0;
        if (!dry)
            foreach (var item in candidates)
                if (_states.DeleteJob(item.Id, item.WatchPath)) affected++;
        return Result("fixture-sweep", dry, candidates.Length, affected,
            dry ? $"{candidates.Length} fixture tasks would be deleted." : $"Deleted {affected} fixture tasks.", actor, key);
    }

    private ManagementCommandResult CreateBackup(bool dry, string actor, string key, int? retention)
    {
        if (!Directory.Exists(Root)) throw new ManagementException(409, "data-directory-missing");
        if (dry) return Result("backup-create", true, 1, 0, "A consistent data-directory backup would be created and verified.", actor, key);
        Directory.CreateDirectory(BackupDirectory);
        var id = "backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var path = Path.Combine(BackupDirectory, id + ".zip");
        try
        {
            ZipFile.CreateFromDirectory(Root, path, CompressionLevel.Fastest, includeBaseDirectory: false);
            var verified = InspectBackup(path);
            if (verified.VerificationState != "verified") throw new InvalidDataException("Backup verification failed.");
            Retain(Math.Clamp(retention ?? DefaultRetention, 1, 100));
            ClearBackupFailure();
            return Result("backup-create", false, 1, 1, $"Created and verified backup {id}.", actor, key, verified);
        }
        catch (Exception ex)
        {
            WriteBackupFailure(ex.Message);
            throw;
        }
    }

    private ManagementCommandResult VerifyRestore(bool dry, string actor, string key, string? backupId)
    {
        var summary = ResolveBackup(backupId);
        if (dry) return Result("restore-verify", true, 1, 0, $"Backup {summary.Id} would be extracted into isolated restore staging and verified.", actor, key);
        var backupPath = Path.Combine(BackupDirectory, summary.FileName);
        var verified = InspectBackup(backupPath);
        var staging = Path.Combine(BackupDirectory, ".restore-verification", summary.Id + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(backupPath, staging);
            var extractedFiles = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Count();
            var state = verified.VerificationState == "verified" && extractedFiles == verified.EntryCount
                ? "verified" : "failed";
            return Result("restore-verify", false, 1, state == "verified" ? 1 : 0,
                $"Backup {verified.Id} restore verification: {state} ({extractedFiles} files extracted).",
                actor, key, new { backup = verified, stagingState = state, extractedFiles });
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private ManagementCommandResult ApplyRetention(bool dry, string actor, string key, int? requested)
    {
        var keep = Math.Clamp(requested ?? DefaultRetention, 1, 100);
        var matched = Math.Max(0, ListBackups().Count - keep);
        var affected = dry ? 0 : Retain(keep);
        return Result("backup-retention", dry, matched, affected,
            dry ? $"{matched} backups would be removed; newest {keep} retained." : $"Removed {affected} backups; newest {keep} retained.", actor, key);
    }

    private ManagementCommandResult SetMaintenance(bool dry, string actor, string key, string mode, string? reason)
    {
        if (!dry) WriteState(new MaintenanceStatus(mode, mode != "normal", false, reason, DateTime.UtcNow.ToString("O"), actor));
        return Result(mode == "normal" ? "maintenance-exit" : mode == "read-only" ? "maintenance-read-only" : "maintenance-enter",
            dry, 1, dry ? 0 : 1, dry ? $"Server would enter {mode} mode." : $"Server entered {mode} mode.", actor, key);
    }

    private ManagementCommandResult PrepareShutdown(bool dry, string actor, string key, string? reason)
    {
        if (!dry) WriteState(new MaintenanceStatus("maintenance", true, true, reason, DateTime.UtcNow.ToString("O"), actor));
        return Result("shutdown-prepare", dry, 1, dry ? 0 : 1,
            dry ? "Server would drain and prepare for service-manager shutdown." : "Server drained and is prepared for service-manager shutdown.", actor, key);
    }

    private ManagementCommandResult Result(string kind, bool dry, int matched, int affected, string summary, string actor, string key, object? detail = null)
        => new("cmd_" + Guid.NewGuid().ToString("N"), kind, dry, "completed", matched, affected,
            summary, DateTime.UtcNow.ToString("O"), actor, key, detail);

    private void AppendAudit(ManagementCommandResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AuditPath)!);
        var row = new ManagementAuditEvent(result.CompletedAt, result.CommandId, result.Actor,
            result.Kind, result.DryRun, result.IdempotencyKey, result.State,
            result.Matched, result.Affected, result.Summary);
        File.AppendAllText(AuditPath, JsonSerializer.Serialize(row, Json) + Environment.NewLine);
    }

    private ManagementCommandResult? FindPrior(string key, string kind)
    {
        if (!File.Exists(AuditPath)) return null;
        foreach (var line in File.ReadLines(AuditPath).Reverse())
        {
            try
            {
                var row = JsonSerializer.Deserialize<ManagementAuditEvent>(line, Json);
                if (row?.IdempotencyKey == key && row.Kind == kind)
                    return new ManagementCommandResult(row.CommandId, row.Kind, row.DryRun, row.Outcome,
                        row.Matched, row.Affected, row.Summary, row.Timestamp, row.Actor, row.IdempotencyKey);
            }
            catch (JsonException) { continue; }
        }
        return null;
    }

    private MaintenanceStatus ReadState()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<MaintenanceStatus>(File.ReadAllText(StatePath), Json) ?? NormalState() : NormalState(); }
        catch (JsonException) { return new("maintenance", true, false, "management-state-invalid", null, null); }
    }
    private static MaintenanceStatus NormalState() => new("normal", false, false, null, null, null);
    private void WriteState(MaintenanceStatus state)
    {
        Directory.CreateDirectory(Metadata);
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, StatePath, true);
    }

    private IReadOnlyList<MigrationStatus> ReadMigrations()
    {
        var path = Path.Combine(Metadata, "migrations.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<MigrationStatus>>(File.ReadAllText(path), Json) ?? [] : []; }
        catch (JsonException ex) { return [new("migration-state", "failed", null, ex.Message)]; }
    }

    private SecurityManagementStatus ReadSecurityStatus()
    {
        var (type, store) = TrySecurityStore();
        if (type is null)
            return new(false, 0, 0, "/api/auth/session", "/api/auth/users", "/api/auth/runners",
                "AGT-2193 security store is not present in this build");
        if (store is null)
            return new(false, 0, 0, "/api/auth/session", "/api/auth/users", "/api/auth/runners",
                "AGT-2193 security store is not registered");
        var users = type.GetMethod("ListUsers")?.Invoke(store, null);
        var runners = type.GetMethod("ListRunners")?.Invoke(store, null);
        return new(true, CountEnumerable(users), CountEnumerable(runners),
            "/api/auth/session", "/api/auth/users", "/api/auth/runners",
            "Shared AGT-2193 user, session, and Runner credential authority");
    }

    private static int CountEnumerable(object? value)
        => value is System.Collections.IEnumerable rows ? rows.Cast<object>().Count() : 0;

    private (Type? Type, object? Store) TrySecurityStore()
    {
        var type = typeof(ManagementService).Assembly.GetType("AgentStudio.Security.AccessSecurityStore");
        return (type, type is null ? null : _services.GetService(type));
    }

    private IReadOnlyList<RunnerManagementStatus> ReadRunners(IReadOnlyList<ClientIdentity> legacy)
    {
        var (type, store) = TrySecurityStore();
        var secured = type?.GetMethod("ListRunners")?.Invoke(store, null) as System.Collections.IEnumerable;
        if (secured is not null)
        {
            var rows = new List<RunnerManagementStatus>();
            foreach (var runner in secured.Cast<object>())
            {
                var runnerType = runner.GetType();
                var id = runnerType.GetProperty("Id")?.GetValue(runner)?.ToString() ?? "unknown";
                var name = runnerType.GetProperty("Name")?.GetValue(runner)?.ToString() ?? id;
                var revokedAt = runnerType.GetProperty("RevokedAt")?.GetValue(runner) as DateTime?;
                var credentialRows = runnerType.GetProperty("Credentials")?.GetValue(runner) as System.Collections.IEnumerable;
                var lastUsed = credentialRows?.Cast<object>()
                    .Select(item => item.GetType().GetProperty("LastUsedAt")?.GetValue(item) as DateTime?)
                    .Where(value => value is not null).Max();
                var runtime = legacy.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                rows.Add(new RunnerManagementStatus(
                    id, name, revokedAt is not null ? "revoked" : runtime?.RunnerDaemonState ?? "enrolled",
                    lastUsed?.ToUniversalTime().ToString("O"), runtime?.RunnerLastClaimAt?.ToUniversalTime().ToString("O"),
                    runtime?.RunnerActiveSlots ?? 0, runtime?.RunnerAvailableSlots ?? 0,
                    runtime?.DrainRequestedAt is not null, runtime?.RetireRequestedAt is not null,
                    $"/api/auth/runners/{Uri.EscapeDataString(id)}"));
            }
            return rows;
        }

        return legacy
            .Where(x => x.Kind is ClientIdentityKind.AgentInstance or ClientIdentityKind.Service or ClientIdentityKind.Retired)
            .Select(x => new RunnerManagementStatus(
                x.Id, x.DisplayName, x.Kind == ClientIdentityKind.Retired ? "retired" : x.RunnerDaemonState ?? "enrolled",
                x.LastSeenAt?.ToUniversalTime().ToString("O"), x.RunnerLastClaimAt?.ToUniversalTime().ToString("O"),
                x.RunnerActiveSlots.GetValueOrDefault(), x.RunnerAvailableSlots.GetValueOrDefault(),
                x.DrainRequestedAt is not null, x.RetireRequestedAt is not null,
                $"/api/auth/runners/{Uri.EscapeDataString(x.Id)}"))
            .ToArray();
    }

    private IReadOnlyList<BackupSummary> ListBackups()
    {
        if (!Directory.Exists(BackupDirectory)) return [];
        return Directory.EnumerateFiles(BackupDirectory, "backup-*.zip")
            .OrderByDescending(File.GetCreationTimeUtc).Select(InspectBackup).ToArray();
    }
    private BackupSummary ResolveBackup(string? id)
    {
        var item = ListBackups().FirstOrDefault(x => string.IsNullOrWhiteSpace(id) || x.Id == id);
        return item ?? throw new ManagementException(404, "backup-not-found");
    }
    private static BackupSummary InspectBackup(string path)
    {
        var info = new FileInfo(path);
        var state = "verified";
        var entries = 0;
        try { using var zip = ZipFile.OpenRead(path); entries = zip.Entries.Count(entry => !string.IsNullOrEmpty(entry.Name)); if (entries == 0) state = "failed"; }
        catch (InvalidDataException) { state = "failed"; }
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new(Path.GetFileNameWithoutExtension(path), Path.GetFileName(path), info.Length,
            info.CreationTimeUtc.ToString("O"), hash, state, entries);
    }
    private int Retain(int keep)
    {
        var removed = 0;
        foreach (var item in ListBackups().Skip(keep)) { File.Delete(Path.Combine(BackupDirectory, item.FileName)); removed++; }
        return removed;
    }

    private bool CanWriteRoot()
    {
        try
        {
            Directory.CreateDirectory(Metadata);
            var path = Path.Combine(Metadata, ".management-write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(path, "probe"); File.Delete(path); return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    private static long SafeLength(string path) { try { return new FileInfo(path).Length; } catch (IOException) { return 0; } }
    private static bool IsEventFile(string path) => path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || path.Contains("timeline", StringComparison.OrdinalIgnoreCase);
    private static bool IsArtifactFile(string path) => path.Contains($"{Path.DirectorySeparatorChar}results{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) || path.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    private static long CountJsonLines(IEnumerable<string> files)
    {
        long count = 0;
        foreach (var file in files.Where(x => x.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
            try { count += File.ReadLines(file).LongCount(); }
            catch (IOException ex) { SilentCatch.Note(ex, $"management event count: {file}"); }
        return count;
    }
    private string? ReadBackupFailure() { var path = Path.Combine(Metadata, "last-backup-failure.txt"); return File.Exists(path) ? File.ReadAllText(path) : null; }
    private void WriteBackupFailure(string message) { Directory.CreateDirectory(Metadata); File.WriteAllText(Path.Combine(Metadata, "last-backup-failure.txt"), message); }
    private void ClearBackupFailure() { var path = Path.Combine(Metadata, "last-backup-failure.txt"); if (File.Exists(path)) File.Delete(path); }
}

public sealed class ManagementException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
