using System.IO.Compression;
using System.Text.Json;
using AgentStudio.Retention;

namespace AgentStudio.Retention.Tests;

public sealed class FileTreeRetentionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "retention-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Archive_manifest_hash_restore_and_idempotent_rerun_round_trip()
    {
        var workspace = Path.Combine(_root, "workspace");
        var task = CreateTask(workspace, "7-archive", DateTimeOffset.UtcNow.AddDays(-31));
        var original = string.Join('\n', Enumerable.Range(1, 900).Select(index => index == 400 ? "ERROR failed" : $"line {index}"));
        Directory.CreateDirectory(Path.Combine(task, "logs"));
        await File.WriteAllTextAsync(Path.Combine(task, "logs", "cli-output.log"), original);
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));
        var store = new FileTreeRetentionStore(workspace, Path.Combine(_root, "archive"), time);
        var inventory = await store.EnumerateTasksAndFilesAsync();
        var plan = RetentionPlanner.Plan(inventory, RetentionPolicy.Default, time.GetUtcNow());
        var applied = await new RetentionExecutor(store, RetentionPolicy.Default, time).ApplyAsync(plan, "test");

        Assert.Equal(1, applied.ExecutedActions);
        Assert.False(File.Exists(Path.Combine(task, "logs", "cli-output.log")));
        Assert.True(File.Exists(Path.Combine(task, ".retention-excerpts", "logs-cli-output.log.md")));
        var pointer = JsonSerializer.Deserialize<ArchivePointer>(await File.ReadAllTextAsync(Path.Combine(task, "archive-manifest.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(await File.ReadAllTextAsync(pointer.ManifestPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Single(manifest.Files);
        Assert.Equal("zip-deflate", manifest.Compression);
        using (var zip = ZipFile.OpenRead(Path.Combine(Path.GetDirectoryName(pointer.ManifestPath)!, "payload.zip")))
            Assert.NotNull(zip.GetEntry("logs/cli-output.log"));
        Assert.Empty(RetentionPlanner.Plan(await store.EnumerateTasksAndFilesAsync(), RetentionPolicy.Default, time.GetUtcNow()).Tasks);

        var restored = await store.RestoreAsync("AGT-1");
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(task, "logs", "cli-output.log")));
        Assert.Equal(1, restored.RestoredFiles);
        Assert.Equal(0, (await store.RestoreAsync("AGT-1")).RestoredFiles);
        Assert.Empty(RetentionPlanner.Plan(await store.EnumerateTasksAndFilesAsync(), RetentionPolicy.Default, time.GetUtcNow()).Tasks);
    }

    [Fact]
    public async Task Full_backup_verifies_and_restores_bundle_untracked_and_cold_payload()
    {
        var workspace = Path.Combine(_root, "workspace");
        var task = CreateTask(workspace, "7-archive", DateTimeOffset.UtcNow.AddDays(-31));
        Directory.CreateDirectory(Path.Combine(task, "results"));
        await File.WriteAllTextAsync(Path.Combine(task, "results", "trace.zip"), "trace");
        Git(workspace, "init");
        Git(workspace, "add", ".");
        Git(workspace, "-c", "user.name=test", "-c", "user.email=test@local", "commit", "-m", "fixture");
        var store = new FileTreeRetentionStore(workspace, Path.Combine(_root, "archive"), new FixedTimeProvider(new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero)));
        var plan = RetentionPlanner.Plan(await store.EnumerateTasksAndFilesAsync(), RetentionPolicy.Default, DateTimeOffset.UtcNow);
        await new RetentionExecutor(store, RetentionPolicy.Default).ApplyAsync(plan, "test");
        Git(workspace, "add", "-A");
        Git(workspace, "-c", "user.name=test", "-c", "user.email=test@local", "commit", "-m", "retention");
        await File.WriteAllTextAsync(Path.Combine(task, "untracked.txt"), "untracked");

        var backup = await new FullBackupService(store).CreateAsync(Path.Combine(_root, "backups", "full", "fixture"));
        var verified = await FullBackupService.VerifyAsync(backup.BackupDirectory);
        Assert.Equal(1, verified.TaskCount);
        var restore = Path.Combine(_root, "full-restore");
        await FullBackupService.RestoreAsync(backup.BackupDirectory, restore);
        Assert.True(File.Exists(Path.Combine(restore, "workspace", "projects", "PROJ-1", "tasks", "7-archive", "AGT-1", "untracked.txt")));
        Assert.True(Directory.EnumerateFiles(Path.Combine(restore, "archive"), "payload.zip", SearchOption.AllDirectories).Any());
        var restoredStore = new FileTreeRetentionStore(Path.Combine(restore, "workspace"), Path.Combine(restore, "archive"));
        Assert.Equal(1, (await restoredStore.RestoreAsync("AGT-1")).RestoredFiles);
    }

    private static string CreateTask(string workspace, string lane, DateTimeOffset terminalAt)
    {
        var task = Path.Combine(workspace, "projects", "PROJ-1", "tasks", lane, "AGT-1");
        Directory.CreateDirectory(task);
        File.WriteAllText(Path.Combine(task, "task.json"), JsonSerializer.Serialize(new { id = "id-1", key = "AGT-1", state = lane, terminalAt }));
        File.WriteAllText(Path.Combine(task, "status.md"), "status");
        return task;
    }

    private static void Git(string path, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = path, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
