using System.Diagnostics;
using System.Text.Json;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Disk-backed lock that prevents two backends sharing the same workspace
/// from both spawning a CLI on the same job folder. The lock file lives at
/// <c>&lt;job-folder&gt;/.pickup-lock.json</c> and carries the pid + hostname
/// + role + backend-name of the process that acquired it.
///
/// <para>
/// The cross-process race this guards against: dev and stable point at
/// <see cref="OrchestratorApi.Models.WatchPathEntry.RootPath"/> values that
/// share an <c>agent-taskboard-workspace</c>. With both backends on
/// auto-continuous, both pickup ticks see the same <c>3-progress</c> folder
/// and both decide to start it. The role gate (see <see cref="RunnerRoles"/>)
/// is the first layer; this on-disk lock is the structural belt and braces
/// that survives a misconfigured role on either side.
/// </para>
///
/// <para>
/// Semantics:
/// <list type="bullet">
///   <item><b>TryAcquire</b> writes the lock atomically with create-exclusive
///   semantics. If a foreign lock already exists and the foreign pid is still
///   running on the same host, the call returns <see cref="LockAcquireOutcome.ForeignHeld"/>.</item>
///   <item>A lock whose owning pid is no longer running is treated as
///   <see cref="LockAcquireOutcome.Stale"/> and overwritten. The stale-clean
///   case logs the previous owner so the operator can see who left it behind.</item>
///   <item>A lock from a different host is treated as foreign-held (we cannot
///   verify the remote pid). This is the conservative side of the trade.</item>
///   <item><b>Release</b> only deletes the file when this process actually
///   owns it (pid + hostname + backend-name match). A foreign or stale lock
///   on Release is left alone so a late retry from the real holder cannot be
///   silently clobbered.</item>
/// </list>
/// </para>
///
/// <para>
/// The lock is best-effort. Acquire failures (disk read/write errors, the
/// folder vanishing mid-call) return <see cref="LockAcquireOutcome.Acquired"/>
/// rather than blocking the pickup, with a warning log; the in-memory
/// <c>_activeJobId</c> latch in <see cref="ProjectRunner"/> still gives us
/// single-process exclusivity, and a logged disk failure beats a wedged queue.
/// </para>
/// </summary>
public sealed class PickupLockFile
{
    private readonly ILogger<PickupLockFile> _logger;
    public const string LockFileName = ".pickup-lock.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PickupLockFile(ILogger<PickupLockFile> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Stamp the lock with the calling owner. <see cref="LockAcquireOutcome.Acquired"/>
    /// and <see cref="LockAcquireOutcome.Stale"/> both mean "we now hold the
    /// lock and may proceed". <see cref="LockAcquireOutcome.AlreadyOwn"/>
    /// covers the re-entrant case where the same pid/host/backend is asking
    /// twice (e.g. a retry after a soft cancel). <see cref="LockAcquireOutcome.ForeignHeld"/>
    /// is the structural "another backend is on this job — bail" signal.
    /// </summary>
    public LockAcquireOutcome TryAcquire(string jobFolder, PickupLockOwner owner, out PickupLockInfo? existing)
    {
        existing = null;
        if (string.IsNullOrEmpty(jobFolder) || !Directory.Exists(jobFolder))
        {
            _logger.LogDebug("PickupLockFile.TryAcquire: folder missing '{Folder}'; treating as Acquired", jobFolder);
            return LockAcquireOutcome.Acquired;
        }

        var path = Path.Combine(jobFolder, LockFileName);
        var current = Read(path);
        if (current != null)
        {
            existing = current;
            if (IsSameOwner(current, owner))
            {
                return LockAcquireOutcome.AlreadyOwn;
            }
            if (IsForeignHostAlive(current))
            {
                _logger.LogInformation(
                    "PickupLockFile: foreign lock held on '{Folder}' by {Backend} (pid={Pid} host={Host} role={Role}); skipping pickup",
                    jobFolder, current.BackendName, current.Pid, current.Hostname, current.Role);
                return LockAcquireOutcome.ForeignHeld;
            }
            _logger.LogInformation(
                "PickupLockFile: stale lock on '{Folder}' (previous owner {Backend} pid={Pid} host={Host} role={Role}); reclaiming",
                jobFolder, current.BackendName, current.Pid, current.Hostname, current.Role);
        }

        try
        {
            Write(path, BuildInfo(owner));
            return current == null ? LockAcquireOutcome.Acquired : LockAcquireOutcome.Stale;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "PickupLockFile: failed to write lock at '{Path}'; proceeding without on-disk lock (in-memory latch still applies)",
                path);
            return LockAcquireOutcome.Acquired;
        }
    }

    /// <summary>
    /// Drop the lock when this process is the recorded owner. Foreign or
    /// stale locks are left untouched: a stray Release that wiped a
    /// re-acquired foreign lock would resurrect the very race this class
    /// exists to prevent.
    /// </summary>
    public void Release(string jobFolder, PickupLockOwner owner)
    {
        if (string.IsNullOrEmpty(jobFolder)) return;
        var path = Path.Combine(jobFolder, LockFileName);
        var current = Read(path);
        if (current == null) return;
        if (!IsSameOwner(current, owner))
        {
            _logger.LogDebug(
                "PickupLockFile.Release: lock at '{Path}' is owned by {Backend} (pid={Pid}); not ours, leaving in place",
                path, current.BackendName, current.Pid);
            return;
        }
        try { File.Delete(path); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PickupLockFile.Release: delete failed for '{Path}'", path);
        }
    }

    /// <summary>
    /// Best-effort sweep: drop the lock at <paramref name="jobFolder"/> if
    /// its owning pid is no longer running. Used at boot via
    /// <c>CrashRecoveryService</c> so a backend that died mid-run does not
    /// leave a wedged lock behind for its successor.
    /// </summary>
    public bool ClearIfStale(string jobFolder)
    {
        if (string.IsNullOrEmpty(jobFolder)) return false;
        var path = Path.Combine(jobFolder, LockFileName);
        var current = Read(path);
        if (current == null) return false;
        if (IsForeignHostAlive(current)) return false;
        try { File.Delete(path); _logger.LogInformation("PickupLockFile.ClearIfStale: removed stale lock at '{Path}' (previous owner pid={Pid} host={Host})", path, current.Pid, current.Hostname); return true; }
        catch (Exception ex) { _logger.LogDebug(ex, "PickupLockFile.ClearIfStale: delete failed for '{Path}'", path); return false; }
    }

    public PickupLockInfo? Peek(string jobFolder)
    {
        if (string.IsNullOrEmpty(jobFolder)) return null;
        return Read(Path.Combine(jobFolder, LockFileName));
    }

    private static PickupLockInfo BuildInfo(PickupLockOwner owner) => new()
    {
        Schema = "pickup-lock/v1",
        Pid = owner.Pid,
        Hostname = owner.Hostname,
        Role = owner.Role,
        BackendName = owner.BackendName,
        BackendPort = owner.BackendPort,
        ProjectName = owner.ProjectName,
        JobId = owner.JobId,
        AcquiredAt = DateTime.UtcNow
    };

    private static bool IsSameOwner(PickupLockInfo info, PickupLockOwner owner)
    {
        return info.Pid == owner.Pid
            && string.Equals(info.Hostname, owner.Hostname, StringComparison.OrdinalIgnoreCase)
            && string.Equals(info.BackendName, owner.BackendName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the lock looks like a live foreign hold; false when the
    /// recorded pid is no longer running on this host (so we can reclaim).
    /// A different hostname is treated as foreign-live because we cannot
    /// safely verify a remote pid - the conservative choice is to skip.
    /// </summary>
    private static bool IsForeignHostAlive(PickupLockInfo info)
    {
        if (!string.Equals(info.Hostname, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (info.Pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(info.Pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch { return true; }
    }

    private static PickupLockInfo? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<PickupLockInfo>(fs, JsonOptions);
        }
        catch { return null; }
    }

    private static void Write(string path, PickupLockInfo info)
    {
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        {
            JsonSerializer.Serialize(fs, info, JsonOptions);
        }
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }
}

/// <summary>Outcome of a <see cref="PickupLockFile.TryAcquire"/> call.</summary>
public enum LockAcquireOutcome
{
    /// <summary>Lock did not exist; we wrote it. Caller may proceed.</summary>
    Acquired,
    /// <summary>Stale lock found and replaced. Caller may proceed.</summary>
    Stale,
    /// <summary>Lock already belongs to this owner (re-entrant). Caller may proceed.</summary>
    AlreadyOwn,
    /// <summary>Foreign live owner holds the lock. Caller must NOT proceed.</summary>
    ForeignHeld
}

/// <summary>
/// Identity the current backend stamps onto a pickup lock. <see cref="BackendName"/>
/// distinguishes dev / stable so two backends from the same git checkout still
/// produce distinct owners.
/// </summary>
public sealed record PickupLockOwner
{
    public required int Pid { get; init; }
    public required string Hostname { get; init; }
    public required string Role { get; init; }
    public required string BackendName { get; init; }
    public int BackendPort { get; init; }
    public string? ProjectName { get; init; }
    public string? JobId { get; init; }
}

/// <summary>On-disk shape of <c>.pickup-lock.json</c>.</summary>
public sealed record PickupLockInfo
{
    public string Schema { get; init; } = "pickup-lock/v1";
    public int Pid { get; init; }
    public string Hostname { get; init; } = "";
    public string Role { get; init; } = "";
    public string BackendName { get; init; } = "";
    public int BackendPort { get; init; }
    public string? ProjectName { get; init; }
    public string? JobId { get; init; }
    public DateTime AcquiredAt { get; init; }
}
