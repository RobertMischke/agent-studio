namespace AgentOrchestrator.CodeQuality;

public enum SensorScope
{
    Repository,
    Path,
}

public sealed record SensorScanRequest(
    string RepositoryRoot,
    SensorScope Scope = SensorScope.Repository,
    string? Path = null,
    IReadOnlyDictionary<string, string>? Configuration = null,
    bool PersistMetadata = true);

public sealed class RuleLibrary;

public sealed class RulePrecheckSensor
{
    public RulePrecheckSensor(RuleLibrary? library = null) => _ = library;

    public Task<SensorScanResult> RunAsync(
        SensorScanRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new SensorScanResult(
            true,
            null,
            [new ReviewFinding(
                "finding-1",
                "QS-NG-002",
                FindingSeverity.Medium,
                "Use design tokens",
                "Description",
                "Recommendation",
                "sha256:1",
                [new FindingLocation(
                    request.Path ?? string.Empty,
                    new FindingRange(new FindingPosition(7, 3)))])]));
}

public enum FindingSeverity
{
    Medium,
}

public sealed record SensorScanResult(
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<ReviewFinding> Findings);

public sealed record ReviewFinding(
    string Id,
    string RuleId,
    FindingSeverity Severity,
    string Title,
    string Description,
    string Recommendation,
    string Fingerprint,
    IReadOnlyList<FindingLocation> Locations);

public sealed record FindingLocation(string Path, FindingRange Range);

public sealed record FindingRange(FindingPosition Start);

public sealed record FindingPosition(int Line, int Column);
