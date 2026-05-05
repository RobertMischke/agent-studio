using OrchestratorApi.Services.Drift;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the contract of the architecture-marble extension on
/// <see cref="DriftReport"/>: the model is optional, the validator enforces
/// the schema's ten-element ceiling and id uniqueness, and the
/// <see cref="ArchitectureElementStateStore"/> persists element-state
/// overrides across reload.
/// </summary>
public class DriftReportArchitectureModelTests : IDisposable
{
    private readonly string _workspaceRoot;

    public DriftReportArchitectureModelTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "drift-arch-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_workspaceRoot)) Directory.Delete(_workspaceRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Validator_AcceptsReportWithoutArchitectureModel()
    {
        var report = MakeBaseReport(architectureModel: null);
        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
    }

    [Fact]
    public void Validator_AcceptsReportWithUpToTenElements()
    {
        var elements = Enumerable.Range(0, 10)
            .Select(i => MakeElement($"el-{i}"))
            .ToArray();
        var model = new DriftArchitectureModel("model-a", "Sample Map", elements);
        var report = MakeBaseReport(architectureModel: model);
        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
    }

    [Fact]
    public void Validator_RejectsReportWithMoreThanTenElements()
    {
        var elements = Enumerable.Range(0, 11)
            .Select(i => MakeElement($"el-{i}"))
            .ToArray();
        var model = new DriftArchitectureModel("model-a", "Sample Map", elements);
        var report = MakeBaseReport(architectureModel: model);
        Assert.False(DriftReportValidator.TryValidate(report, out var error));
        Assert.Contains("at most ten", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validator_RejectsDuplicateElementIds()
    {
        var model = new DriftArchitectureModel(
            "model-a",
            "Sample Map",
            new[] { MakeElement("dup"), MakeElement("dup") });
        var report = MakeBaseReport(architectureModel: model);
        Assert.False(DriftReportValidator.TryValidate(report, out var error));
        Assert.Contains("not unique", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validator_RejectsScoreOutOfRange()
    {
        var bad = MakeElement("e1") with { Score = 120 };
        var model = new DriftArchitectureModel("m", "T", new[] { bad });
        var report = MakeBaseReport(architectureModel: model);
        Assert.False(DriftReportValidator.TryValidate(report, out var error));
        Assert.Contains("score", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StateStore_PersistsAndReadsBackElementOverride()
    {
        var store = new ArchitectureElementStateStore();
        var saved = store.Set(_workspaceRoot, "demo", "model-a", "el-1", DriftFindingStatus.Tracked, "investigating");

        Assert.Equal(DriftFindingStatus.Tracked, saved.Status);
        Assert.Equal("investigating", saved.Note);

        var fresh = new ArchitectureElementStateStore();
        var got = fresh.Get(_workspaceRoot, "demo", "model-a", "el-1");
        Assert.NotNull(got);
        Assert.Equal(DriftFindingStatus.Tracked, got!.Status);
        Assert.Equal("investigating", got.Note);
    }

    [Fact]
    public void StateStore_OverridesAreScopedByModelId()
    {
        var store = new ArchitectureElementStateStore();
        store.Set(_workspaceRoot, "demo", "model-a", "el-1", DriftFindingStatus.Accepted, null);
        store.Set(_workspaceRoot, "demo", "model-b", "el-1", DriftFindingStatus.Ignored, null);
        Assert.Equal(DriftFindingStatus.Accepted, store.Get(_workspaceRoot, "demo", "model-a", "el-1")!.Status);
        Assert.Equal(DriftFindingStatus.Ignored, store.Get(_workspaceRoot, "demo", "model-b", "el-1")!.Status);
    }

    private static DriftArchitectureElement MakeElement(string id) =>
        new(
            ElementId: id,
            Label: id.ToUpperInvariant(),
            ExpectedRole: "Owns " + id,
            Score: 80,
            Severity: DriftSeverity.Info,
            SourceCoverage: 0.8,
            Status: DriftFindingStatus.New,
            EvidenceRefs: new[] { $"docs/{id}.md" });

    private static DriftReport MakeBaseReport(DriftArchitectureModel? architectureModel)
    {
        return new DriftReport(
            ReportId: "01TEST" + Guid.NewGuid().ToString("N").Substring(0, 8),
            Project: "demo",
            CreatedAt: DateTime.UtcNow,
            Trigger: DriftReportTrigger.Manual,
            Scope: new DriftReportScope(DriftReportScopeKind.Project),
            OverallScore: 70,
            ScoreBand: DriftScoreBand.Watch,
            Dimensions: new[]
            {
                new DriftDimension(
                    Type: DriftDimensionType.Architecture,
                    Score: 70,
                    Severity: DriftSeverity.Info,
                    Confidence: 0.8,
                    SourceCoverage: 0.8,
                    Status: DriftFindingStatus.New,
                    Summary: "ok",
                    EvidenceRefs: new[] { "docs/architecture-decisions.md" },
                    RecommendedActions: Array.Empty<string>()),
            },
            Summary: "test",
            FollowUpTaskSuggestions: Array.Empty<DriftFollowUpSuggestion>(),
            SchemaVersion: 1,
            ArchitectureModel: architectureModel,
            Producer: DriftReportDefaults.ManualProducer,
            ParseStatus: DriftReportParseStatus.Structured);
    }
}
