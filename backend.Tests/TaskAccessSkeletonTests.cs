using System.Reflection;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Phase 1 contract pin for the Task Access Layer (ADR-0024). The
/// implementation lands in phases 2 and 3; this test only locks the
/// interface surface and the typed records so the next phase has a
/// stable starting point. Once the in-memory store ships, this file
/// is replaced by behavioural tests (load-200-jobs, find-hits-index,
/// watcher-patches-deletion).
/// </summary>
public class TaskAccessSkeletonTests
{
    [Fact]
    public void ITaskAccess_ExposesTheReadAndMutationSurface()
    {
        var t = typeof(ITaskAccess);
        Assert.True(t.IsInterface);

        AssertMethod(t, nameof(ITaskAccess.FindJob));
        AssertMethod(t, nameof(ITaskAccess.GetJobDetail));
        AssertMethod(t, nameof(ITaskAccess.ListByLane));
        AssertMethod(t, nameof(ITaskAccess.ListByProject));
        AssertMethod(t, nameof(ITaskAccess.Snapshot));
        AssertMethod(t, nameof(ITaskAccess.MutateAsync));
        AssertMethod(t, nameof(ITaskAccess.TransitionLaneAsync));
        AssertMethod(t, nameof(ITaskAccess.Subscribe));
    }

    [Fact]
    public void ITaskAccessHost_ExposesTheLifecycleSurface()
    {
        var t = typeof(ITaskAccessHost);
        Assert.True(t.IsInterface);

        AssertMethod(t, nameof(ITaskAccessHost.BootAsync));
        AssertMethod(t, nameof(ITaskAccessHost.ReloadProjectAsync));
        AssertMethod(t, nameof(ITaskAccessHost.ShutdownAsync));
    }

    [Fact]
    public void TaskMutationRequest_NarrowsKindEnumToTheFourPhase3Operations()
    {
        var values = Enum.GetNames(typeof(TaskMutationKind));
        Assert.Contains("UpdateField", values);
        Assert.Contains("AttachPrompt", values);
        Assert.Contains("AppendLogLine", values);
        Assert.Contains("Create", values);
    }

    [Fact]
    public void TaskMutationStatus_CoversAppliedNotFoundConflictRejected()
    {
        var values = Enum.GetNames(typeof(TaskMutationStatus));
        Assert.Contains("Applied", values);
        Assert.Contains("NotFound", values);
        Assert.Contains("Conflict", values);
        Assert.Contains("Rejected", values);
    }

    [Fact]
    public void TaskAccessVersion_IsConstructibleAsTheConcurrencyToken()
    {
        var version = new TaskAccessVersion(7, new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(7, version.Version);
        Assert.Equal(DateTimeKind.Utc, version.Mtime.Kind);
    }

    private static void AssertMethod(Type t, string name)
    {
        var method = t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
    }
}
