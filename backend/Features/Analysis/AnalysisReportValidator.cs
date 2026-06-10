namespace AgentStudio.Analysis;

/// <summary>
/// In-code validator that mirrors the value sets and required-field rules in
/// <c>docs/schemas/analysis-report.schema.json</c>. Validation lives here,
/// alongside the in-memory store, so every consumer that goes through the
/// store gets the same rejection set.
/// </summary>
/// <remarks>
/// The schema is the boundary; the C# enums
/// (<see cref="AnalysisReportScopeKind"/>,
/// <see cref="AnalysisReportProducerKind"/>,
/// <see cref="AnalysisReportTrigger"/>,
/// <see cref="AnalysisReportSeverity"/>,
/// <see cref="AnalysisReportParseStatus"/>,
/// <see cref="AnalysisReportReferenceKind"/>) are the in-memory shape. This
/// validator checks the projection of one onto the other: the enums are
/// non-nullable so the parser already rejects out-of-range values; what
/// remains to enforce here are the string fields the schema marks
/// <c>minLength: 1</c> and the scope-specific required pointers.
/// </remarks>
public static class AnalysisReportValidator
{
    public static bool TryValidate(AnalysisReport report, out string? error)
    {
        if (report is null) { error = "report is null"; return false; }
        if (string.IsNullOrWhiteSpace(report.ReportId)) { error = "reportId required"; return false; }
        if (report.ReportId.Length < 8) { error = "reportId must be at least 8 characters"; return false; }
        if (report.CreatedAt == default) { error = "createdAt required"; return false; }
        if (string.IsNullOrWhiteSpace(report.Topic)) { error = "topic required"; return false; }
        if (string.IsNullOrWhiteSpace(report.Summary)) { error = "summary required"; return false; }
        if (report.References is null) { error = "references required"; return false; }
        if (report.FollowUpTaskSuggestions is null) { error = "followUpTaskSuggestions required"; return false; }
        if (report.SchemaVersion != 1) { error = $"unsupported schemaVersion {report.SchemaVersion}"; return false; }

        if (!TryValidateScope(report.Scope, out error)) return false;
        if (!TryValidateProducer(report.Producer, out error)) return false;

        // parseStatus = MalformedJson should carry the parser's error so a
        // reviewer can fix the sidecar without re-running the analysis.
        if (report.ParseStatus == AnalysisReportParseStatus.MalformedJson
            && string.IsNullOrWhiteSpace(report.ParseError))
        {
            error = "parseError required when parseStatus is MalformedJson";
            return false;
        }

        foreach (var reference in report.References)
        {
            if (reference is null) { error = "references entry is null"; return false; }
            if (string.IsNullOrWhiteSpace(reference.Ref))
            {
                error = $"reference of kind {reference.Kind} missing ref";
                return false;
            }
        }

        foreach (var suggestion in report.FollowUpTaskSuggestions)
        {
            if (suggestion is null) { error = "followUpTaskSuggestions entry is null"; return false; }
            if (string.IsNullOrWhiteSpace(suggestion.Title)) { error = "follow-up suggestion missing title"; return false; }
            if (suggestion.TargetState is not null
                && suggestion.TargetState != AnalysisReportFollowUpTargetStates.OnePreparation
                && suggestion.TargetState != AnalysisReportFollowUpTargetStates.TwoReady)
            {
                error = $"follow-up targetState must be 1-preparation or 2-ready (was '{suggestion.TargetState}')";
                return false;
            }
        }

        if (report.Findings is not null)
        {
            foreach (var finding in report.Findings)
            {
                if (finding is null) { error = "findings entry is null"; return false; }
                if (string.IsNullOrWhiteSpace(finding.Topic)) { error = "finding missing topic"; return false; }
                if (string.IsNullOrWhiteSpace(finding.Message)) { error = "finding missing message"; return false; }
            }
        }

        error = null;
        return true;
    }

    private static bool TryValidateScope(AnalysisReportScope scope, out string? error)
    {
        if (scope is null) { error = "scope required"; return false; }
        switch (scope.Kind)
        {
            case AnalysisReportScopeKind.Project:
                if (string.IsNullOrWhiteSpace(scope.Project))
                {
                    error = "scope.project required for Project scope";
                    return false;
                }
                break;
            case AnalysisReportScopeKind.Task:
                if (string.IsNullOrWhiteSpace(scope.Project)) { error = "scope.project required for Task scope"; return false; }
                if (string.IsNullOrWhiteSpace(scope.JobId)) { error = "scope.jobId required for Task scope"; return false; }
                break;
            case AnalysisReportScopeKind.Run:
                if (string.IsNullOrWhiteSpace(scope.Project)) { error = "scope.project required for Run scope"; return false; }
                if (string.IsNullOrWhiteSpace(scope.JobId)) { error = "scope.jobId required for Run scope"; return false; }
                if (scope.RunIndex is null or < 1) { error = "scope.runIndex >= 1 required for Run scope"; return false; }
                break;
            case AnalysisReportScopeKind.TimeWindow:
                if (scope.TimeWindow is null) { error = "scope.timeWindow required for TimeWindow scope"; return false; }
                if (scope.TimeWindow.From == default || scope.TimeWindow.To == default)
                {
                    error = "scope.timeWindow.from and scope.timeWindow.to required";
                    return false;
                }
                if (scope.TimeWindow.To < scope.TimeWindow.From)
                {
                    error = "scope.timeWindow.to must be at or after scope.timeWindow.from";
                    return false;
                }
                break;
        }

        error = null;
        return true;
    }

    private static bool TryValidateProducer(AnalysisReportProducer producer, out string? error)
    {
        if (producer is null) { error = "producer required"; return false; }
        error = null;
        return true;
    }
}
