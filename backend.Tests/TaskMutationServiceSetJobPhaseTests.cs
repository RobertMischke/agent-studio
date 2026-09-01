using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2699: <see cref="TaskMutationService.SetJobPhase"/> used to write
/// <c>phase</c> and <c>phaseEnteredAt</c> as two separate atomic rewrites and
/// invalidate the index cache unconditionally, even when called with the
/// phase the card was already in (e.g. a re-entrant intake tick, or the
/// lane-move cleanup that clears an already-empty phase). Locks: a call that
/// would not change the persisted phase does neither write nor invalidate;
/// a call that does change it lands both fields in one rewrite and still
/// invalidates.
/// </summary>
public sealed class TaskMutationServiceSetJobPhaseTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "atp-set-job-phase-" + Guid.NewGuid().ToString("N"));
    private readonly TaskIndexCache _cache;
    private readonly TaskMutationService _mutations;

    public TaskMutationServiceSetJobPhaseTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "task.json"), "{\"id\":\"job-1\",\"state\":\"2-ready\"}");

        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>()).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        _cache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(_cache);
        _mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ClearingAnAlreadyEmptyPhase_DoesNotWriteOrInvalidate()
    {
        var before = _cache.MutationInvalidations;

        var updated = _mutations.SetJobPhase(_folder, null);

        Assert.False(updated);
        Assert.Equal(before, _cache.MutationInvalidations);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "task.json")));
        Assert.False(doc.RootElement.TryGetProperty("phaseEnteredAt", out _));
    }

    [Fact]
    public void RepeatingTheSamePhase_DoesNotWriteOrInvalidate()
    {
        Assert.True(_mutations.SetJobPhase(_folder, LifecyclePhases.PostProcessingRunning));
        using var firstWrite = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "task.json")));
        var enteredAt = firstWrite.RootElement.GetProperty("phaseEnteredAt").GetString();
        var before = _cache.MutationInvalidations;

        var updated = _mutations.SetJobPhase(_folder, LifecyclePhases.PostProcessingRunning);

        Assert.False(updated);
        Assert.Equal(before, _cache.MutationInvalidations);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "task.json")));
        Assert.Equal(enteredAt, doc.RootElement.GetProperty("phaseEnteredAt").GetString());
    }

    [Fact]
    public void ChangingThePhase_WritesBothFieldsInOneRewrite_AndInvalidates()
    {
        var before = _cache.MutationInvalidations;

        var updated = _mutations.SetJobPhase(_folder, LifecyclePhases.PostProcessingRunning);

        Assert.True(updated);
        Assert.Equal(before + 1, _cache.MutationInvalidations);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "task.json")));
        Assert.Equal(LifecyclePhases.PostProcessingRunning, doc.RootElement.GetProperty("phase").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("phaseEnteredAt").GetString()));
    }

    [Fact]
    public void ClearingASetPhase_WritesBothFieldsInOneRewrite_AndInvalidates()
    {
        _mutations.SetJobPhase(_folder, LifecyclePhases.PostProcessingRunning);
        var before = _cache.MutationInvalidations;

        var updated = _mutations.SetJobPhase(_folder, null);

        Assert.True(updated);
        Assert.Equal(before + 1, _cache.MutationInvalidations);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "task.json")));
        Assert.Equal("", doc.RootElement.GetProperty("phase").GetString());
        Assert.Equal("", doc.RootElement.GetProperty("phaseEnteredAt").GetString());
    }
}
