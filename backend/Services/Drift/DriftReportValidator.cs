namespace OrchestratorApi.Services.Drift;

/// <summary>
/// Minimal in-code validator that mirrors the value sets and required-field
/// rules in <c>docs/schemas/drift-report.schema.json</c>. The companion task
/// <c>drift-report-schema-and-scoring</c> will own the full round-trip
/// validation; this class keeps the producer honest in the meantime.
/// </summary>
public static class DriftReportValidator
{
    public static bool TryValidate(DriftReport report, out string? error)
    {
        if (report is null) { error = "report is null"; return false; }
        if (string.IsNullOrWhiteSpace(report.ReportId)) { error = "reportId required"; return false; }
        if (report.ReportId.Length < 8) { error = "reportId must be at least 8 characters"; return false; }
        if (string.IsNullOrWhiteSpace(report.Project)) { error = "project required"; return false; }
        if (report.CreatedAt == default) { error = "createdAt required"; return false; }
        if (string.IsNullOrWhiteSpace(report.Summary)) { error = "summary required"; return false; }
        if (report.OverallScore is < 0 or > 100) { error = "overallScore must be 0..100"; return false; }
        if (report.SchemaVersion != 1) { error = $"unsupported schemaVersion {report.SchemaVersion}"; return false; }
        if (report.Scope is null) { error = "scope required"; return false; }
        if (report.Dimensions is null || report.Dimensions.Count == 0)
        {
            error = "dimensions must contain at least one entry";
            return false;
        }
        if (report.FollowUpTaskSuggestions is null) { error = "followUpTaskSuggestions required"; return false; }

        foreach (var dim in report.Dimensions)
        {
            if (dim is null) { error = "dimensions entry is null"; return false; }
            if (dim.Score is < 0 or > 100) { error = $"dimension {dim.Type} score must be 0..100"; return false; }
            if (dim.Confidence is < 0 or > 1) { error = $"dimension {dim.Type} confidence must be 0..1"; return false; }
            if (dim.SourceCoverage is < 0 or > 1) { error = $"dimension {dim.Type} sourceCoverage must be 0..1"; return false; }
            if (string.IsNullOrWhiteSpace(dim.Summary)) { error = $"dimension {dim.Type} summary required"; return false; }
            if (dim.EvidenceRefs is null) { error = $"dimension {dim.Type} evidenceRefs required"; return false; }
            if (dim.RecommendedActions is null) { error = $"dimension {dim.Type} recommendedActions required"; return false; }
            foreach (var refStr in dim.EvidenceRefs)
            {
                if (string.IsNullOrWhiteSpace(refStr))
                {
                    error = $"dimension {dim.Type} has empty evidenceRef";
                    return false;
                }
            }
        }

        foreach (var s in report.FollowUpTaskSuggestions)
        {
            if (s is null) { error = "followUpTaskSuggestions entry is null"; return false; }
            if (string.IsNullOrWhiteSpace(s.Title)) { error = "follow-up suggestion missing title"; return false; }
        }

        error = null;
        return true;
    }
}
