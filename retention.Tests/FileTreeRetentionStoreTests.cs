using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Retention;

namespace AgentStudio.Retention.Tests;

public sealed class FileTreeRetentionStoreTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "retention-store-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly string _archive;
    private readonly string _task;

    public FileTreeRetentionStoreTests()
    {
        _workspace = Path.Combine(_parent, "agent-taskboard-workspace");
        _archive = Path.Combine(_parent, "agent-taskboard-archive");
        _task = Path.Combine(_workspace, "projects", "Demo", "tasks", "7-archive", "demo-one");
        Directory.CreateDirectory(Path.Combine(_task, "logs"));
        File.WriteAllText(Path.Combine(_task, "task.json"), """
            {"id":"id-1","key":"DEM-1","state":"7-archive","enteredLaneAt":"2026-07-01T00:00:00Z"}
            """);
        File.WriteAllText(Path.Combine(_task, "status.md"), "done");
        File.WriteAllText(Path.Combine(_task, "review-grades.json"), "{\"verdict\":\"pass\"}");
        File.WriteAllText(Path.Combine(_task, "logs", "cli-output.log"),
            "$ dotnet test\nFAILED exit code 1\nend\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_parent))
            Directory.Delete(_parent, true);
    }

    [Fact]
    public async Task ArchiveManifestHashesRestoreAndRerunAreIdempotent()
    {
        var store = new FileTreeRetentionStore(_workspace, _archive);
        var inventory = await store.EnumerateTasksAndFilesAsync();
        var plan = new RetentionPlanner().Plan(RetentionPolicy.Default(), inventory,
            new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));

        var first = await new RetentionExecutor(store).ApplyAsync(plan, RetentionPolicy.Default(), "test");
        Assert.Equal(1, first.ArchivedTasks);
        Assert.False(File.Exists(Path.Combine(_task, "logs", "cli-output.log")));
        Assert.True(File.Exists(Path.Combine(_task, "excerpts", "logs-cli-output.log.excerpt.md")));
        var pointer = JsonSerializer.Deserialize<ArchiveManifestPointer>(
            File.ReadAllText(Path.Combine(_task, "archive-manifest.json")), RetentionPolicy.JsonOptions)!;
        var manifest = JsonSerializer.Deserialize<RetentionArchiveManifest>(
            File.ReadAllText(pointer.ManifestPath), RetentionPolicy.JsonOptions)!;
        var archived = Assert.Single(manifest.Files);
        Assert.Equal("logs/cli-output.log", archived.RelativePath);
        Assert.Equal(Hash("$ dotnet test\nFAILED exit code 1\nend\n"), archived.Sha256);

        var rerunInventory = await store.EnumerateTasksAndFilesAsync();
        var rerunPlan = new RetentionPlanner().Plan(RetentionPolicy.Default(), rerunInventory,
            new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
        var rerun = await new RetentionExecutor(store).ApplyAsync(rerunPlan, RetentionPolicy.Default(), "test");
        Assert.Equal(0, rerun.ArchivedTasks);

        var stageTwoInventory = await store.EnumerateTasksAndFilesAsync();
        var stageTwoPlan = new RetentionPlanner().Plan(RetentionPolicy.Default(), stageTwoInventory,
            new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero));
        var stageTwo = await new RetentionExecutor(store).ApplyAsync(stageTwoPlan, RetentionPolicy.Default(), "test");
        Assert.Equal(1, stageTwo.ArchivedTasks);
        Assert.False(File.Exists(Path.Combine(_task, "review-grades.json")));
        var chainedPointer = JsonSerializer.Deserialize<ArchiveManifestPointer>(
            File.ReadAllText(Path.Combine(_task, "archive-manifest.json")), RetentionPolicy.JsonOptions)!;
        Assert.Equal(2, chainedPointer.ManifestPaths.Count);

        var restored = await store.RestoreAsync("DEM-1", "Demo");
        Assert.NotNull(restored.RestoredAt);
        Assert.Equal("$ dotnet test\nFAILED exit code 1\nend\n",
            File.ReadAllText(Path.Combine(_task, "logs", "cli-output.log")));
        Assert.Equal("{\"verdict\":\"pass\"}", File.ReadAllText(Path.Combine(_task, "review-grades.json")));
        var restoredAgain = await store.RestoreAsync("DEM-1", "Demo");
        Assert.Equal(restored.RestoredAt, restoredAgain.RestoredAt);
    }

    [Fact]
    public async Task FullBackupVerifyAndRestoreRoundTripIntoEmptyDirectory()
    {
        RunGit(_workspace, "init", "-q", "-b", "main");
        RunGit(_workspace, "config", "user.name", "test");
        RunGit(_workspace, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(_workspace, ".gitignore"), "results/\n");
        RunGit(_workspace, "add", ".");
        RunGit(_workspace, "commit", "-q", "-m", "seed");
        var store = new FileTreeRetentionStore(_workspace, _archive);
        var retentionPlan = new RetentionPlanner().Plan(
            RetentionPolicy.Default(), await store.EnumerateTasksAndFilesAsync(),
            new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(1, (await new RetentionExecutor(store).ApplyAsync(
            retentionPlan, RetentionPolicy.Default(), "backup-test")).ArchivedTasks);
        Directory.CreateDirectory(Path.Combine(_task, "results"));
        File.WriteAllText(Path.Combine(_task, "results", "untracked.md"), "untracked evidence");
        RunGit(_workspace, "status", "--short");
        var backup = Path.Combine(_parent, "backups", "full", "20260906T230000Z");
        var service = new FullBackupService();

        var created = await service.CreateAsync(store, backup);
        var verified = await service.VerifyAsync(backup);

        Assert.Equal(created.SetSha256, verified.SetSha256);
        Assert.True(File.Exists(Path.Combine(backup, "complete.json")));
        var restore = Path.Combine(_parent, "restore-root", "agent-taskboard-workspace");
        Directory.CreateDirectory(restore);
        await service.RestoreAsync(backup, restore);
        Assert.True(File.Exists(Path.Combine(restore, ".git", "HEAD")));
        Assert.Equal("untracked evidence",
            File.ReadAllText(Path.Combine(restore, "projects", "Demo", "tasks", "7-archive", "demo-one", "results", "untracked.md")));
        var restoredStore = new FileTreeRetentionStore(restore);
        await restoredStore.RestoreAsync("DEM-1", "Demo");
        Assert.Equal("$ dotnet test\nFAILED exit code 1\nend\n",
            File.ReadAllText(Path.Combine(restore, "projects", "Demo", "tasks", "7-archive", "demo-one", "logs", "cli-output.log")));
    }

    [Fact]
    public void DefaultArchiveIsWorkspaceSibling()
    {
        var store = new FileTreeRetentionStore(_workspace);
        Assert.Equal(Path.Combine(_parent, "agent-taskboard-archive"), store.ArchiveRoot);
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void RunGit(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
