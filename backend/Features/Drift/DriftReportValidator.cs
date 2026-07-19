
namespace AgentStudio.Drift;

/// <summary>
/// In-code validator that mirrors the value sets and required-field rules in
/// <c>docs/system/schemas/drift-report.schema.json</c>. Validation lives next to the
/// in-memory store so every consumer that goes through the store gets the
/// same rejection set. Strict at append time so new garbage cannot enter the
/// projection; lenient on read so one bad legacy line never breaks the
/// projection (lenience is implemented in
/// <see cref="AgentStudio.State.InMemoryStore{T}"/>).
/// </summary>
/// <remarks>
/// The schema is the boundary; the C# enums are the in-memory shape. The
/// parser already rejects out-of-range enum values, so this validator focuses
/// on string fields the schema marks <c>minLength: 1</c>, on the numeric
/// ranges the schema constrains, and on the cross-field invariants
/// (architecture-element id uniqueness, ten-element ceiling, parseError
/// required when parseStatus is MalformedJson, producer non-null).
/// </remarks>
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
        if (report.Summary.Length > 1000) { error = "summary must be at most 1000 characters"; return false; }
        if (report.OverallScore is < 0 or > 100) { error = "overallScore must be 0..100"; return false; }
        if (report.SchemaVersion != 1) { error = $"unsupported schemaVersion {report.SchemaVersion}"; return false; }
        if (report.Scope is null) { error = "scope required"; return false; }
        if (report.Producer is null) { error = "producer required"; return false; }
        if (report.Dimensions is null || report.Dimensions.Count == 0)
        {
            error = "dimensions must contain at least one entry";
            return false;
        }
        if (report.FollowUpTaskSuggestions is null) { error = "followUpTaskSuggestions required"; return false; }

        // parseStatus = MalformedJson must carry the parser error so a
        // reviewer can fix the sidecar without re-running the analysis.
        if (report.ParseStatus == DriftReportParseStatus.MalformedJson
            && string.IsNullOrWhiteSpace(report.ParseError))
        {
            error = "parseError required when parseStatus is MalformedJson";
            return false;
        }

        if (!TryValidateScope(report.Scope, out error)) return false;

        foreach (var dim in report.Dimensions)
        {
            if (dim is null) { error = "dimensions entry is null"; return false; }
            if (dim.Score is < 0 or > 100) { error = $"dimension {dim.Type} score must be 0..100"; return false; }
            if (dim.Confidence is < 0 or > 1) { error = $"dimension {dim.Type} confidence must be 0..1"; return false; }
            if (dim.SourceCoverage is < 0 or > 1) { error = $"dimension {dim.Type} sourceCoverage must be 0..1"; return false; }
            if (string.IsNullOrWhiteSpace(dim.Summary)) { error = $"dimension {dim.Type} summary required"; return false; }
            if (dim.Summary.Length > 1000) { error = $"dimension {dim.Type} summary must be at most 1000 characters"; return false; }
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
            if (!TryValidateScoreInputs(dim, out error)) return false;
            if (!TryValidateFindings(dim, out error)) return false;
        }

        foreach (var s in report.FollowUpTaskSuggestions)
        {
            if (s is null) { error = "followUpTaskSuggestions entry is null"; return false; }
            if (string.IsNullOrWhiteSpace(s.Title)) { error = "follow-up suggestion missing title"; return false; }
            if (s.Title.Length > 200) { error = "follow-up suggestion title must be at most 200 characters"; return false; }
            if (s.TargetState is not null && s.TargetState != TaskStates.Preparation && s.TargetState != TaskStates.Ready)
            {
                error = $"follow-up suggestion targetState must be 1-preparation or 2-ready (was '{s.TargetState}')";
                return false;
            }
        }

        if (report.ArchitectureModel is { } model)
        {
            if (string.IsNullOrWhiteSpace(model.ModelId)) { error = "architectureModel.modelId required"; return false; }
            if (string.IsNullOrWhiteSpace(model.Title)) { error = "architectureModel.title required"; return false; }
            if (model.Elements is null || model.Elements.Count == 0)
            {
                error = "architectureModel.elements must contain at least one entry";
                return false;
            }
            if (model.Elements.Count > 10)
            {
                error = "architectureModel.elements must contain at most ten entries";
                return false;
            }
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var el in model.Elements)
            {
                if (el is null) { error = "architectureModel.elements entry is null"; return false; }
                if (string.IsNullOrWhiteSpace(el.ElementId)) { error = "architecture element elementId required"; return false; }
                if (!seenIds.Add(el.ElementId)) { error = $"architecture element id '{el.ElementId}' is not unique"; return false; }
                if (string.IsNullOrWhiteSpace(el.Label)) { error = $"architecture element {el.ElementId} label required"; return false; }
                if (el.Score is < 0 or > 100) { error = $"architecture element {el.ElementId} score must be 0..100"; return false; }
                if (el.SourceCoverage is < 0 or > 1) { error = $"architecture element {el.ElementId} sourceCoverage must be 0..1"; return false; }
                if (el.EvidenceRefs is null) { error = $"architecture element {el.ElementId} evidenceRefs required"; return false; }
                foreach (var refStr in el.EvidenceRefs)
                {
                    if (string.IsNullOrWhiteSpace(refStr))
                    {
                        error = $"architecture element {el.ElementId} has empty evidenceRef";
                        return false;
                    }
                }
            }
        }

        error = null;
        return true;
    }

    private static bool TryValidateScope(DriftReportScope scope, out string? error)
    {
        switch (scope.Kind)
        {
            case DriftReportScopeKind.Task:
                if (string.IsNullOrWhiteSpace(scope.TaskId))
                {
                    error = "scope.taskId required for Task scope";
                    return false;
                }
                break;
            case DriftReportScopeKind.Run:
                if (string.IsNullOrWhiteSpace(scope.TaskId))
                {
                    error = "scope.taskId required for Run scope";
                    return false;
                }
                break;
        }
        error = null;
        return true;
    }

    private static bool TryValidateScoreInputs(DriftDimension dim, out string? error)
    {
        var inputs = dim.ScoreInputs;
        if (inputs is null) { error = null; return true; }

        if (inputs.RecurrenceCount < 0)
        {
            error = $"dimension {dim.Type} scoreInputs.recurrenceCount must be >= 0";
            return false;
        }
        if (inputs.TrackedFindings < 0)
        {
            error = $"dimension {dim.Type} scoreInputs.trackedFindings must be >= 0";
            return false;
        }
        if (inputs.TotalFindings < 0)
        {
            error = $"dimension {dim.Type} scoreInputs.totalFindings must be >= 0";
            return false;
        }
        if (inputs.TrackedFindings > inputs.TotalFindings)
        {
            error = $"dimension {dim.Type} scoreInputs.trackedFindings must not exceed totalFindings";
            return false;
        }
        if (inputs.OldestFindingAgeDays is { } age && age < 0)
        {
            error = $"dimension {dim.Type} scoreInputs.oldestFindingAgeDays must be >= 0";
            return false;
        }
        if (inputs.FindingsBySeverity is { } counts)
        {
            if (counts.Info < 0 || counts.Warn < 0 || counts.High < 0 || counts.Critical < 0)
            {
                error = $"dimension {dim.Type} scoreInputs.findingsBySeverity counts must be >= 0";
                return false;
            }
        }
        if (inputs.AffectedSurfaces is { } surfaces)
        {
            foreach (var s in surfaces)
            {
                if (string.IsNullOrWhiteSpace(s))
                {
                    error = $"dimension {dim.Type} scoreInputs.affectedSurfaces entry must be non-empty";
                    return false;
                }
            }
        }
        error = null;
        return true;
    }

    private static bool TryValidateFindings(DriftDimension dim, out string? error)
    {
        if (dim.Findings is null) { error = null; return true; }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in dim.Findings)
        {
            if (f is null) { error = $"dimension {dim.Type} findings entry is null"; return false; }
            if (string.IsNullOrWhiteSpace(f.FindingId))
            {
                error = $"dimension {dim.Type} finding missing findingId";
                return false;
            }
            if (!seen.Add(f.FindingId))
            {
                error = $"dimension {dim.Type} finding id '{f.FindingId}' is not unique within the dimension";
                return false;
            }
            if (string.IsNullOrWhiteSpace(f.Summary))
            {
                error = $"dimension {dim.Type} finding {f.FindingId} missing summary";
                return false;
            }
            if (f.EvidenceRefs is { } refs)
            {
                foreach (var r in refs)
                {
                    if (string.IsNullOrWhiteSpace(r))
                    {
                        error = $"dimension {dim.Type} finding {f.FindingId} has empty evidenceRef";
                        return false;
                    }
                }
            }
        }
        error = null;
        return true;
    }
}
