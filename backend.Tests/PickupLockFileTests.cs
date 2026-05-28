using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Unit coverage for the cross-process pickup lock (ADR-0044). The lock is the
/// belt-and-braces guard against a second backend on the same workspace
/// (typically the dev / stable pair under <c>agent-taskboard-devspace/</c>)
/// claiming the same job folder concurrently. The role gate is the first
/// layer; this lock survives a misconfigured role on either side.
///
/// <para>
/// The four properties under test mirror the four <see cref="LockAcquireOutcome"/>
/// branches the runner relies on:
/// </para>
/// <list type="bullet">
///   <item><b>Acquired</b> when no lock exists - the runner stamps it and
///   proceeds.</item>
///   <item><b>AlreadyOwn</b> when the same pid+host+backend asks twice
///   (re-issue path) - the runner proceeds without rewriting.</item>
///   <item><b>ForeignHeld</b> when a different live pid owns the lock - the
///   runner aborts pickup with a structured log entry.</item>
///   <item><b>Stale</b> when the owning pid is no longer running - the
///   runner reclaims the lock and proceeds.</item>
/// </list>
///
/// <para>
/// Each test runs in a fresh temp directory so locks from a previous test
/// cannot leak. Release semantics get their own pair of tests because the
/// "don't delete a foreign lock" rule is what stops a stray retry from
/// silently clobbering the real holder.
/// </para>
/// </summary>
public sealed class PickupLockFileTests : IDisposable
{
    private readonly string _jobFolder;

    public PickupLockFileTests()
    {
        _jobFolder = Path.Combine(Path.GetTempPath(), "atp-pickup-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_jobFolder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void TryAcquire_OnEmptyFolder_ReturnsAcquired()
    {
        var lockFile = new PickupLockFile(NullLogger<PickupLockFile>.Instance);
        var owner = BuildOwner(pid: 1234, backend: "stable");

        var outcome = lockFile.TryAcquire(_jobFolder, owner, out var existing);

        Assert.Equal(LockAcquireOutcome.Acquired, outcome);
        Assert.Null(existing);
        Assert.True(File.Exists(Path.Combine(_jobFolder, PickupLockFile.LockFileName)));
    }

    [Fact]
    public void TryAcquire_Twice_SameOwner_ReturnsAlreadyOwn()
    {
        var lockFile = new PickupLockFile(NullLogger<PickupLockFile>.Instance);
        var owner = BuildOwner(pid: 1234, backend: "stable");

        lockFile.TryAcquire(_jobFolder, owner, out _);
        var outcome = lockFile.TryAcquire(_jobFolder, owner, out var existing);

        Assert.Equal(LockAcquireOutcome.AlreadyOwn, outcome);
        Assert.NotNull(existing);
        Assert.Equal("stable", existing!.BackendName);
    }

    /// <summary>
    /// A lock written by another live pid blocks pickup. We use the current
    /// process id as the "foreign live" owner because it's the only pid we
    /// can guarantee is still running across the test's runtime.
    /// </summary>
    [Fact]
    public void TryAcquire_ForeignLivePid_ReturnsForeignHeld()
    {
        var lockFile = new PickupLockFile(NullLogger<PickupLockFile>.Instance);
        var foreignLivePid = System.Environment.ProcessId; // guaranteed alive
        var foreignOwner = BuildOwner(pid: foreignLivePid, backend: "dev");
        lockFile.TryAcquire(_jobFolder, foreignOwner, out _);

        // Pretend we are a different backend (different name) trying to grab
        // the same folder.
        var ourOwner = BuildOwner(pid: foreignLivePid, backend: "stable");
        var outcome = lockFile.TryAcquire(_jobFolder, ourOwner, out var existing);

        Assert.Equal(LockAcquireOutcome.ForeignHeld, outcome);
        Assert.NotNull(existing);
        Assert.Equal("dev", existing!.BackendName);
        Assert.Equal(foreignLivePid, existing.Pid);
    }

    /// <summary>
    /// A lock left behind by a dead pid is treated as stale and reclaimed
    /// (Stale outcome). We synthesise the stale state by writing the lock
    /// file directly with an obviously-dead pid.
    /// </summary>
    [Fact]
    public void TryAcquire_StaleLock_ReclaimedAsStale()
    {
        WriteRawLock(new PickupLockInfo
        {
            Pid = 0x7FFFFFFE,
            Hostname = System.Environment.MachineName,
            Role = RunnerRoles.Orchestrator,
            BackendName = "stable",
            AcquiredAt = DateTime.UtcNow.AddHours(-1)
        });
        var lockFile = new PickupLockFile(NullLogger<PickupLockFile>.Instance);
        var ourOwner = BuildOwner(pid: System.Environment.ProcessId, backend: "stable");

        var outcome = lockFile.TryAcquire(_jobFolder, ourOwner, out var existing);

        Assert.Equal(LockAcquireOutcome.Stale, outcome);
        Assert.NotNull(existing);

        // The file on disk should now point at our owner.
        var current = lockFile.Peek(_jobFolder);
        Assert.NotNull(current);
        Assert.Equal(System.Environment.ProcessId, current!.Pid);
    }

    [Fact]
    public void Release_WhenOwner_DeletesLock()
    {
        var lockFile = new PickupLockFile(NullLogger<PickupLockFile>.Instance);
        var owner = BuildOwner(pid: 1234, backend: "stable");
        lockFile.TryAcquire(_jobFolder, owner, out _);
        Assert.True(File.Exists(Path.Combine(_jobFolder, PickupLockFile.LockFileName)));

        lockFile.Release(_jobFolder, owner);

        Assert.False(File.Exists(Path.Combine(_jobFolder, PickupLockFile.LockFileName)));
    }

    /// <summary>
    /// The "don't delete a foreign lock on Release" rule. If a runner
    /// somehow tries to release a folder it never locked (or whose lock has
    /// already been re-acquired by the real holder), the file must stay
    /// where it is. Without this guard a stray retry could resurrect the
    /// double-pickup race the lock exists to prevent.
    /// </summary>
    [Fact]
    public void Release_WhenForeign_LeavesLockInPlace()
    {
        var lockFile = new PickupLockFile(NullLogger<PickupLockFile>.Instance);
        var foreignOwner = BuildOwner(pid: System.Environment.ProcessId, backend: "dev");
        lockFile.TryAcquire(_jobFolder, foreignOwner, out _);

        var notOwner = BuildOwner(pid: System.Environment.ProcessId, backend: "stable");
        lockFile.Release(_jobFolder, notOwner);

        Assert.True(File.Exists(Path.Combine(_jobFolder, PickupLockFile.LockFileName)));
    }

    /// <summary>
    /// ClearIfStale is the boot-time sweep hook so a crashed backend does
    /// not leave its successor wedged. It must delete a stale lock and skip
    /// a live one.
    /// </summary>
    [Fact]
    public void ClearIfStale_DropsDeadPidLockButKeepsLivePidLock()
    {
        var lockFile = new PickupLockFile(NullLogger<PickupLockFile>.Instance);

        WriteRawLock(new PickupLockInfo
        {
            Pid = 0x7FFFFFFE,
            Hostname = System.Environment.MachineName,
            BackendName = "stable",
            Role = RunnerRoles.Orchestrator,
            AcquiredAt = DateTime.UtcNow.AddDays(-1)
        });
        Assert.True(lockFile.ClearIfStale(_jobFolder));
        Assert.False(File.Exists(Path.Combine(_jobFolder, PickupLockFile.LockFileName)));

        WriteRawLock(new PickupLockInfo
        {
            Pid = System.Environment.ProcessId,
            Hostname = System.Environment.MachineName,
            BackendName = "stable",
            Role = RunnerRoles.Orchestrator,
            AcquiredAt = DateTime.UtcNow
        });
        Assert.False(lockFile.ClearIfStale(_jobFolder));
        Assert.True(File.Exists(Path.Combine(_jobFolder, PickupLockFile.LockFileName)));
    }

    private static PickupLockOwner BuildOwner(int pid, string backend) => new()
    {
        Pid = pid,
        Hostname = System.Environment.MachineName,
        Role = backend == "dev" ? RunnerRoles.TestSubject : RunnerRoles.Orchestrator,
        BackendName = backend,
        BackendPort = backend == "dev" ? 5030 : 5020,
        ProjectName = "demo",
        JobId = "job-1"
    };

    private void WriteRawLock(PickupLockInfo info)
    {
        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(Path.Combine(_jobFolder, PickupLockFile.LockFileName), json);
    }
}
