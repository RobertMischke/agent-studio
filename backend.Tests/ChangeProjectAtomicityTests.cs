using System.Runtime.Versioning;
using System.Text.Json;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>AGT-2166 regression: a project change must re-key atomically.</summary>
public sealed class ChangeProjectAtomicityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "change-project-2166-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ChangeProject_RekeysDestination_AndLeavesNoArchivedOrStagingOrphan()
    {
        var source = Path.Combine(_root, "source");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "Agent Studio",
            ["WatchPaths:0:Path"] = source,
            ["WatchPaths:0:RootPath"] = source,
            ["WatchPaths:1:Name"] = "Coding Agent Chat",
            ["WatchPaths:1:Path"] = target,
            ["WatchPaths:1:RootPath"] = target,
        }).Build();
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var sourceProject = registry.EnsureProjectForStorage(source, "Agent Studio", DefaultWorkspace.Id);
        var targetProject = registry.EnsureProjectForStorage(target, "Coding Agent Chat", DefaultWorkspace.Id);
        registry.SetShortCode(sourceProject.Id, "AGT");
        registry.SetShortCode(targetProject.Id, "CAC");

        var sourceTask = Path.Combine(source, "tasks", "002", "AGT-2166");
        Directory.CreateDirectory(sourceTask);
        File.WriteAllText(Path.Combine(sourceTask, "task.json"), JsonSerializer.Serialize(new
        {
            id = "agt-2166-move-failure",
            key = "AGT-2166",
            title = "Move failure",
            state = TaskStates.Archive,
            order = 1,
            agent = "codex",
        }));
        var referencingTask = Path.Combine(source, "tasks", "000", "AGT-2");
        Directory.CreateDirectory(referencingTask);
        File.WriteAllText(Path.Combine(referencingTask, "task.json"), JsonSerializer.Serialize(new
        {
            id = "consumer-reference", key = "AGT-2", title = "Reference", state = TaskStates.Backlog,
            order = 1, agent = "codex", references = new { relatedTo = new[] { "AGT-2166" } },
        }));

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var machine = new TaskStateMachine(
            scanner,
            NullLogger<TaskStateMachine>.Instance,
            new LaneMutexRegistry(NullLogger<LaneMutexRegistry>.Instance),
            projectRegistry: registry);

        Assert.True(machine.ChangeProject("agt-2166-move-failure", target, source));

        Assert.False(Directory.Exists(sourceTask));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(source, "tasks"), ".moving-*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(target, "tasks"), ".incoming-*", SearchOption.AllDirectories));
        var moved = Assert.Single(TaskStorageLayout.EnumerateJobDirs(target));
        var json = File.ReadAllText(Path.Combine(moved, "task.json"));
        Assert.Contains("\"key\": \"CAC-1\"", json);
        Assert.Contains("\"previousKey\": \"AGT-2166\"", json);
        Assert.Contains("CAC-1", File.ReadAllText(Path.Combine(referencingTask, "task.json")));
        Assert.DoesNotContain("AGT-2166", File.ReadAllText(Path.Combine(referencingTask, "task.json")));
        Assert.Equal(TaskStates.Archive, scanner.FindJob("agt-2166-move-failure", target)!.State);
    }

    // Linux-only 02.08. (AGT-2472): the copy failure is injected by removing the
    // write permission from a directory, which Windows ACLs do not reproduce.
    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public void ChangeProject_CopyFailure_RestoresArchivedSourceWithoutOrphan()
    {
        PlatformGate.LinuxOnly("the failure is injected through Unix directory permissions");

        var source = Path.Combine(_root, "failure-source");
        var target = Path.Combine(_root, "failure-target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "Agent Studio",
            ["WatchPaths:0:Path"] = source,
            ["WatchPaths:0:RootPath"] = source,
            ["WatchPaths:1:Name"] = "Coding Agent Chat",
            ["WatchPaths:1:Path"] = target,
            ["WatchPaths:1:RootPath"] = target,
        }).Build();
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var sourceProject = registry.EnsureProjectForStorage(source, "Agent Studio", DefaultWorkspace.Id);
        var targetProject = registry.EnsureProjectForStorage(target, "Coding Agent Chat", DefaultWorkspace.Id);
        registry.SetShortCode(sourceProject.Id, "AGT");
        registry.SetShortCode(targetProject.Id, "CAC");

        var sourceTask = Path.Combine(source, "tasks", "002", "AGT-2166");
        Directory.CreateDirectory(sourceTask);
        File.WriteAllText(Path.Combine(sourceTask, "task.json"), JsonSerializer.Serialize(new
        {
            id = "agt-2166-copy-failure",
            key = "AGT-2166",
            title = "Move failure",
            state = TaskStates.Archive,
            order = 1,
            agent = "codex",
        }));
        File.CreateSymbolicLink(Path.Combine(sourceTask, "broken-attachment"), Path.Combine(_root, "missing-file"));

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var machine = new TaskStateMachine(
            scanner,
            NullLogger<TaskStateMachine>.Instance,
            new LaneMutexRegistry(NullLogger<LaneMutexRegistry>.Instance),
            projectRegistry: registry);

        Assert.False(machine.ChangeProject("agt-2166-copy-failure", target, source));

        Assert.True(Directory.Exists(sourceTask));
        Assert.Equal("AGT-2166", scanner.FindJob("agt-2166-copy-failure", source)!.Key);
        Assert.Empty(TaskStorageLayout.EnumerateJobDirs(target));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(source, "tasks"), ".moving-*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(target, "tasks"), ".incoming-*", SearchOption.AllDirectories));
    }

    // Linux-only 02.08. (AGT-2472): same injection technique as the copy-failure
    // case - a write-protected directory, which has no Windows equivalent here.
    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    // The runtime gate is PlatformGate.LinuxOnly; this states the same fact to the
    // platform-compatibility analyzer, which only understands the annotation.
    [UnsupportedOSPlatform("windows")]
    public void ChangeProject_ReferenceWriteFailure_RestoresSourceAndOriginalReferences()
    {
        PlatformGate.LinuxOnly("the failure is injected through Unix directory permissions");

        var source = Path.Combine(_root, "reference-failure-source");
        var target = Path.Combine(_root, "reference-failure-target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = "Agent Studio",
            ["WatchPaths:0:Path"] = source,
            ["WatchPaths:0:RootPath"] = source,
            ["WatchPaths:1:Name"] = "Coding Agent Chat",
            ["WatchPaths:1:Path"] = target,
            ["WatchPaths:1:RootPath"] = target,
        }).Build();
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var sourceProject = registry.EnsureProjectForStorage(source, "Agent Studio", DefaultWorkspace.Id);
        var targetProject = registry.EnsureProjectForStorage(target, "Coding Agent Chat", DefaultWorkspace.Id);
        registry.SetShortCode(sourceProject.Id, "AGT");
        registry.SetShortCode(targetProject.Id, "CAC");

        var sourceTask = Path.Combine(source, "tasks", "002", "AGT-2166");
        Directory.CreateDirectory(sourceTask);
        File.WriteAllText(Path.Combine(sourceTask, "task.json"), JsonSerializer.Serialize(new
        {
            id = "agt-2166-reference-failure", key = "AGT-2166", title = "Move failure",
            state = TaskStates.Archive, order = 1, agent = "codex",
        }));
        var referencingTask = Path.Combine(source, "tasks", "000", "AGT-2");
        Directory.CreateDirectory(referencingTask);
        var referenceJson = Path.Combine(referencingTask, "task.json");
        File.WriteAllText(referenceJson, JsonSerializer.Serialize(new
        {
            id = "locked-reference", key = "AGT-2", title = "Reference", state = TaskStates.Backlog,
            order = 1, agent = "codex", references = new { relatedTo = new[] { "AGT-2166" } },
        }));
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var machine = new TaskStateMachine(
            scanner,
            NullLogger<TaskStateMachine>.Instance,
            new LaneMutexRegistry(NullLogger<LaneMutexRegistry>.Instance),
            projectRegistry: registry);

        // task.json writes replace the file atomically through a sibling temp
        // file. A read-only target file no longer blocks that correct write
        // strategy, so deny directory creation instead to exercise the strict
        // write failure and rollback boundary.
        File.SetUnixFileMode(
            referencingTask,
            UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        bool changed;
        try
        {
            changed = machine.ChangeProject("agt-2166-reference-failure", target, source);
        }
        finally
        {
            File.SetUnixFileMode(
                referencingTask,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.False(changed);

        Assert.True(Directory.Exists(sourceTask));
        Assert.Equal("AGT-2166", scanner.FindJob("agt-2166-reference-failure", source)!.Key);
        Assert.Contains("AGT-2166", File.ReadAllText(referenceJson));
        Assert.DoesNotContain("CAC-1", File.ReadAllText(referenceJson));
        Assert.Empty(TaskStorageLayout.EnumerateJobDirs(target));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(source, "tasks"), ".moving-*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(target, "tasks"), ".incoming-*", SearchOption.AllDirectories));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
