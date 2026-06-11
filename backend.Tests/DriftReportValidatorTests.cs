
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins the drift-report validator's required-field rules and cross-field
/// invariants. Strict at append time so new garbage cannot enter the
/// projection.
/// </summary>
public class DriftReportValidatorTests
{
    [Fact]
    public void Accepts_canonical_report()
    {
        var report = MakeCanonical();
        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
    }

    [Fact]
    public void Rejects_missing_producer()
    {
        var report = MakeCanonical() with { Producer = null };
        Assert.False(DriftReportValidator.TryValidate(report, out var error));
        Assert.Contains("producer", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_malformed_json_without_parse_error()
    {
        var report = MakeCanonical() with
        {
            ParseStatus = DriftReportParseStatus.MalformedJson,
            ParseError = null,
        };
        Assert.False(DriftReportValidator.TryValidate(report, out var error));
        Assert.Contains("parseError", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accepts_malformed_json_when_parse_error_present()
    {
        var report = MakeCanonical() with
        {
            ParseStatus = DriftReportParseStatus.MalformedJson,
            ParseError = "Unexpected token at index 42",
        };
        Assert.True(DriftReportValidator.TryValidate(report, out var error), error);
    }

    [Fact]
    public void Rejects_overall_score_out_of_range()
    {
        var report = MakeCanonical() with { OverallScore = 101 };
        Assert.False(DriftReportValidator.TryValidate(report, out var error));
        Assert.Contains("overallScore", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_dimension_with_out_of_range_confidence()
    {
        var dim = MakeDimension() with { Confidence = 1.2 };
        var report = MakeCanonical() with { Dimensions = new[] { dim } };
        Assert.False(DriftReportValidator.TryValidate(report, out var error));
        Assert.Contains("confidence", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_empty_dimensions()
    {
        var report = MakeCanonical() with { Dimensions = Array.Empty<DriftDimension>() };
        Assert.False(DriftReportValidator.TryValidate(report, out var error));
        Assert.Contains("dimensions", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_short_report_id()
    {
        var report = MakeCanonical() with { ReportId = "abc" };
        Assert.False(DriftReportValidator.TryValidate(report, out var error));
        Assert.Contains("reportId", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_score_inputs_with_tracked_exceeding_total()
    {
        var dim = MakeDimension() with
        {
            ScoreInputs = new DriftScoreInputs(
                TrackedFindings: 5,
                TotalFindings: 2),
        };
        var report = MakeCanonical() with { Dimensions = new[] { dim } };
        Assert.False(DriftReportValidator.TryValidate(report, out var error));
        Assert.Contains("trackedFindings", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_duplicate_finding_ids_within_a_dimension()
    {
        var dim = MakeDimension() with
        {
            Findings = new[]
            {
                new DriftFinding("dup-1", DriftSeverity.Warn, "first", DriftFindingStatus.New),
                new DriftFinding("dup-1", DriftSeverity.High, "second", DriftFindingStatus.New),
            },
        };
        var report = MakeCanonical() with { Dimensions = new[] { dim } };
        Assert.False(DriftReportValidator.TryValidate(report, out var error));
        Assert.Contains("not unique", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_architecture_model_with_more_than_ten_elements()
    {
        var elements = Enumerable.Range(0, 11)
            .Select(i => new DriftArchitectureElement(
                ElementId: $"el-{i}",
                Label: $"Element {i}",
                ExpectedRole: "owns x",
                Score: 90,
                Severity: DriftSeverity.Info,
                SourceCoverage: 0.7,
                Status: DriftFindingStatus.New,
                EvidenceRefs: new[] { "docs/foo.md" }))
            .ToArray();
        var report = MakeCanonical() with
        {
            ArchitectureModel = new DriftArchitectureModel(
                ModelId: "test-model",
                Title: "Test Model",
                Elements: elements),
        };
        Assert.False(DriftReportValidator.TryValidate(report, out var error));
        Assert.Contains("ten", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_architecture_element_with_duplicate_id()
    {
        var report = MakeCanonical() with
        {
            ArchitectureModel = new DriftArchitectureModel(
                ModelId: "test-model",
                Title: "Test Model",
                Elements: new[]
                {
                    new DriftArchitectureElement(
                        "dup", "A", "x", 80, DriftSeverity.Info, 0.8, DriftFindingStatus.New,
                        new[] { "docs/a.md" }),
                    new DriftArchitectureElement(
                        "dup", "B", "y", 80, DriftSeverity.Info, 0.8, DriftFindingStatus.New,
                        new[] { "docs/b.md" }),
                }),
        };
        Assert.False(DriftReportValidator.TryValidate(report, out var error));
        Assert.Contains("not unique", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_follow_up_with_invalid_target_state()
    {
        var report = MakeCanonical() with
        {
            FollowUpTaskSuggestions = new[]
            {
                new DriftFollowUpSuggestion(
                    Title: "Bogus",
                    Summary: "x",
                    Priority: DriftFollowUpPriority.Normal,
                    TargetState: "3-progress"),
            },
        };
        Assert.False(DriftReportValidator.TryValidate(report, out var error));
        Assert.Contains("targetState", error, StringComparison.OrdinalIgnoreCase);
    }

    private static DriftReport MakeCanonical() => new(
        ReportId: "01HX0000000000000000000DRFT",
        Project: "agent-taskboard",
        CreatedAt: new DateTime(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc),
        Trigger: DriftReportTrigger.Manual,
        Scope: new DriftReportScope(DriftReportScopeKind.Project),
        OverallScore: 80,
        ScoreBand: DriftScoreBand.Watch,
        Dimensions: new[] { MakeDimension() },
        Summary: "Two ADR assumptions drifted.",
        FollowUpTaskSuggestions: Array.Empty<DriftFollowUpSuggestion>(),
        Producer: DriftReportDefaults.ManualProducer,
        ParseStatus: DriftReportParseStatus.Structured);

    private static DriftDimension MakeDimension() => new(
        Type: DriftDimensionType.Architecture,
        Score: 75,
        Severity: DriftSeverity.Warn,
        Confidence: 0.8,
        SourceCoverage: 0.7,
        Status: DriftFindingStatus.New,
        Summary: "ADRs out of sync with runner.",
        EvidenceRefs: new[] { "docs/architecture/decisions/adr-archive.md" },
        RecommendedActions: new[] { "Refresh ADR-0017" });
}
