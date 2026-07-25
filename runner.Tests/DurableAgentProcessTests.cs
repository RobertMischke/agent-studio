using System.Diagnostics;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DurableAgentProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "runner-restart-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Replacement_daemon_reattaches_live_fake_job_and_reads_its_terminal_result()
    {
        var worktree = Path.Combine(_root, "worktree");
        var results = Path.Combine(_root, "results");
        var stateRoot = Path.Combine(_root, "state");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(results);
        var options = Options(stateRoot, worktree,
            "-c \"sleep 1; printf 'before-restart\\n[[TASK_DONE]]\\n'\"");
        var lease = Lease("AGT-RESTART");
        var firstStore = new RunnerStateStore(stateRoot);
        var slot = firstStore.Create(lease.TaskKey, lease, worktree);

        var original = DurableAgentProcess.Start(options, slot.WorkerDirectory, worktree, "", results);
        firstStore.Save(slot with
        {
            ProcessId = original.ProcessId,
            ProcessStartedAtUtc = original.ProcessStartedAtUtc,
            Phase = "running",
        });

        // A replacement store/handle has no Process object or inherited pipe
        // from the starter. It can adopt only from the durable PID + cwd proof.
        var replacementStore = new RunnerStateStore(stateRoot);
        var recovered = Assert.Single(replacementStore.LoadAll());
        Assert.True(DurableAgentProcess.VerifyLive(recovered, out var proof), proof);
        var attached = DurableAgentProcess.Attach(recovered);

        DetachedJobResult? result = null;
        for (var i = 0; i < 40 && result is null; i++)
        {
            await Task.Delay(100);
            result = attached.ReadResult();
        }

        Assert.NotNull(result);
        Assert.Equal(0, result!.ExitCode);
        Assert.Contains("[[TASK_DONE]]", result.StdOut);
        Assert.Contains(attached.ReadAfter(0), line => line.Text == "before-restart");
        Assert.Contains(attached.ReadAfter(0), line => line.Text == "[[TASK_DONE]]");
    }

    [Fact]
    public async Task Pid_with_a_different_worktree_is_not_adopted()
    {
        var actual = Path.Combine(_root, "actual");
        var claimed = Path.Combine(_root, "claimed");
        var results = Path.Combine(_root, "results");
        var stateRoot = Path.Combine(_root, "state");
        Directory.CreateDirectory(actual);
        Directory.CreateDirectory(claimed);
        Directory.CreateDirectory(results);
        var options = Options(stateRoot, actual, "-c \"sleep 2\"");
        var lease = Lease("AGT-MISMATCH");
        var store = new RunnerStateStore(stateRoot);
        var slot = store.Create(lease.TaskKey, lease, claimed);
        var process = DurableAgentProcess.Start(options, slot.WorkerDirectory, actual, "", results);
        slot = store.Save(slot with
        {
            ProcessId = process.ProcessId,
            ProcessStartedAtUtc = process.ProcessStartedAtUtc,
            Phase = "running",
        });

        Assert.False(DurableAgentProcess.VerifyLive(slot, out var reason));
        Assert.Contains("does not match worktree", reason);
        process.Kill();
        await Task.Delay(50);
    }

    [Fact]
    public async Task Worker_identity_closes_the_process_start_to_slot_save_restart_window()
    {
        var worktree = Path.Combine(_root, "launch-window");
        var results = Path.Combine(_root, "results");
        var stateRoot = Path.Combine(_root, "state");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(results);
        var options = Options(stateRoot, worktree, "-c \"sleep 1; printf 'done\\n'\"");
        var lease = Lease("AGT-LAUNCH-WINDOW");
        var firstStore = new RunnerStateStore(stateRoot);
        var slot = firstStore.Create(lease.TaskKey, lease, worktree);
        slot = firstStore.Save(slot with { Phase = "launching" });

        var worker = DurableAgentProcess.Start(
            options, slot.WorkerDirectory, worktree, "", results);
        var replacementSlot = Assert.Single(new RunnerStateStore(stateRoot).LoadAll());

        PersistedRunnerSlot recovered = replacementSlot;
        var reason = string.Empty;
        for (var i = 0; i < 40; i++)
        {
            if (DurableAgentProcess.TryRecoverIdentity(replacementSlot, out recovered, out reason))
                break;
            await Task.Delay(50);
        }

        Assert.Equal(worker.ProcessId, recovered.ProcessId);
        Assert.InRange(
            Math.Abs((worker.ProcessStartedAtUtc - recovered.ProcessStartedAtUtc!.Value).TotalSeconds),
            0,
            2);
        Assert.True(DurableAgentProcess.VerifyLive(recovered, out var proof), proof);
        using var workerProcess = Process.GetProcessById(worker.ProcessId);
        worker.Kill();
        await workerProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static RunnerOptions Options(string stateRoot, string worktree, string cliArgs) => new()
    {
        ServerUrl = "http://localhost",
        RunnerId = "runner-restart-test",
        RunnerName = "runner-restart-test",
        Hostname = "test-host",
        BackendName = "test",
        WorkDir = worktree,
        StateDir = stateRoot,
        BaseBranch = "main",
        CliBin = "/bin/sh",
        CliArgs = cliArgs,
        TtlSeconds = 120,
        HeartbeatSeconds = 30,
        RunTimeoutSeconds = 10,
        HostMaxParallelism = 1,
        PollSeconds = 1,
    };

    private static RunLeaseInfoDto Lease(string taskKey) => new(
        taskKey,
        "runner-restart-test",
        "runner-restart-test",
        "test-host",
        Environment.ProcessId,
        "test",
        $"lease-{taskKey}",
        1,
        DateTime.UtcNow,
        DateTime.UtcNow.AddMinutes(2));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* a killed test worker may still be unwinding */ }
    }
}
