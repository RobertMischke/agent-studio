using System.Runtime.CompilerServices;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DurableAgentProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "runner-restart-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Replacement_daemon_reattaches_live_fake_job_and_reads_its_terminal_result()
    {
        var worktree = Path.Combine(_root, "worktree");
        var results = Path.Combine(_root, "results");
        var stateRoot = Path.Combine(_root, "state");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(results);
        var options = Options(stateRoot, worktree);
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
    [Trait("Category", "MachineBound")]
    [Trait("Platform", "Linux")]
    public async Task Pid_with_a_different_worktree_is_not_adopted()
    {
        var actual = Path.Combine(_root, "actual");
        var claimed = Path.Combine(_root, "claimed");
        var results = Path.Combine(_root, "results");
        var stateRoot = Path.Combine(_root, "state");
        Directory.CreateDirectory(actual);
        Directory.CreateDirectory(claimed);
        Directory.CreateDirectory(results);
        var options = Options(stateRoot, actual);
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
    [Trait("Category", "MachineBound")]
    public async Task Worker_identity_closes_the_process_start_to_slot_save_restart_window()
    {
        var worktree = Path.Combine(_root, "launch-window");
        var results = Path.Combine(_root, "results");
        var stateRoot = Path.Combine(_root, "state");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(results);
        var options = Options(stateRoot, worktree);
        var lease = Lease("AGT-LAUNCH-WINDOW");
        var firstStore = new RunnerStateStore(stateRoot);
        var slot = firstStore.Create(lease.TaskKey, lease, worktree);
        slot = firstStore.Save(slot with { Phase = "launching" });

        var worker = DurableAgentProcess.Start(
            options, slot.WorkerDirectory, worktree, "", results);
        var replacementSlot = Assert.Single(new RunnerStateStore(stateRoot).LoadAll());

        PersistedRunnerSlot recovered = replacementSlot;
        var reason = string.Empty;
        var identityProven = false;
        // The worker identity may not have reached durable storage yet. Poll
        // tightly so the test observes the recovery window before the fixture
        // exits on either supported host platform.
        for (var i = 0; i < 400 && !identityProven; i++)
        {
            identityProven = DurableAgentProcess.TryRecoverIdentity(replacementSlot, out recovered, out reason);
            if (!identityProven) await Task.Delay(5);
        }

        // TryRecoverIdentity returns true only after it has verified the live PID
        // generation, so this single assertion covers both halves of the contract.
        // Re-verifying liveness afterwards would race the worker's own exit.
        Assert.True(identityProven, reason);
        Assert.Equal(worker.ProcessId, recovered.ProcessId);
        Assert.InRange(
            Math.Abs((worker.ProcessStartedAtUtc - recovered.ProcessStartedAtUtc!.Value).TotalSeconds),
            0,
            2);
        worker.Kill();
    }

    private static RunnerOptions Options(string stateRoot, string worktree) => new()
    {
        ServerUrl = "http://localhost",
        RunnerId = "runner-restart-test",
        RunnerName = "runner-restart-test",
        Hostname = "test-host",
        BackendName = "test",
        WorkDir = worktree,
        StateDir = stateRoot,
        BaseBranch = "main",
        // These tests fake the CLI through CliBin/CliArgs, which only the legacy
        // engine consumes. The worker/reattach mechanics under test are
        // engine-independent; the CAR engine is covered by CarWorkerExecutionTests.
        ExecEngine = RunnerOptions.ExecEngineLegacy,
        CliBin = "node",
        CliArgs = $"\"{FakeCliPath()}\" \"{DurableFixturePath()}\"",
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

    private static string FakeCliPath()
        => Path.Combine(RepoRoot(), "testdata", "cli-fixtures", "fake-cli.mjs");

    private static string DurableFixturePath()
        => Path.Combine(RepoRoot(), "testdata", "runner-fixtures", "durable-job.fixture");

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "agent-taskboard.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Repository root was not found above {sourceFile}.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* a killed test worker may still be unwinding */ }
    }
}
