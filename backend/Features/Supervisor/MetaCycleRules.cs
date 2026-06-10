
namespace AgentStudio.Supervisor;

/// <summary>
/// Pure check + action rules for the per-project meta-cycle. Stateless: every
/// input is passed in; nothing is read from disk or DI. Tests exercise the
/// rules without spinning up a hosted service.
/// </summary>
/// <remarks>
/// The implementation order matches the taxonomy in
/// <c>docs/mockups/orchestrator-meta-cycle/taxonomy.md</c>:
/// <list type="number">
/// <item>Build findings from each inspection block.</item>
/// <item>Pick a verdict (healthy / fix-triggering / escalation-only).</item>
/// <item>Pick exactly one action with a typed reason.</item>
/// </list>
/// Action ordering: <c>escalate-to-user</c> &gt; <c>queue-fix</c> &gt;
/// <c>update-stable-then-resume</c> &gt; <c>resume</c>.
/// </remarks>
public static class MetaCycleRules
{
    /// <summary>
    /// Topics the meta-cycle will refuse to template a fix for. These always
    /// escalate to the user instead of auto-queueing a fix-task.
    /// </summary>
    public static readonly IReadOnlyList<string> EscalateOnlyTopics = new[]
    {
        "adr-drift",
        "prompt-regression",
        "supervisor-logic-change",
        "needs-human",
    };

    /// <summary>
    /// Topics the meta-cycle has a templated fix-task prompt for. Anything
    /// outside this list escalates to <c>1-preparation</c> as a review-needed
    /// task (still <c>1-preparation</c>, never <c>2-ready</c>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> KnownFixTemplates =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["last-crash-marker"] = "auto-fix-orphan-changes",
            ["expected-artefact-missing"] = "auto-fix-missing-artefacts",
            ["stuck-in-progress"] = "auto-fix-stuck-progress",
            ["commit-log-diff"] = "auto-fix-zero-commits",
            ["runner-mode-drift"] = "auto-fix-mode-drift",
        };

    /// <summary>
    /// Build the typed list of findings from an inspection. Findings are the
    /// "what we noticed" layer; verdict + action are derived from them.
    /// </summary>
    public static IReadOnlyList<MetaCycleFinding> DeriveFindings(
        MetaCycleInspection inspection,
        IReadOnlyList<MetaCycleJobObservation> jobs,
        bool autoCommitEnabled,
        IReadOnlyList<string> extraAdvisoryTopics,
        string extraGlobAction)
    {
        var findings = new List<MetaCycleFinding>();

        if (inspection.LastCrashMarker.Present)
        {
            findings.Add(new MetaCycleFinding(
                Topic: "last-crash-marker",
                Severity: SupervisorSeverity.High,
                Message: BuildCrashMessage(inspection.LastCrashMarker),
                JobId: null));
        }

        if (autoCommitEnabled && inspection.CommitLogDiff.TotalCommits == 0 && jobs.Count > 0)
        {
            findings.Add(new MetaCycleFinding(
                Topic: "commit-log-diff",
                Severity: SupervisorSeverity.Warn,
                Message: $"{jobs.Count} job(s) closed but no new commits since {inspection.CommitLogDiff.FromSha ?? "(unknown)"}.",
                JobId: null));
        }

        if (inspection.SupervisorAdvisories.CountAtOrAboveThreshold > 0)
        {
            var topics = string.Join(", ", inspection.SupervisorAdvisories.Topics);
            findings.Add(new MetaCycleFinding(
                Topic: "supervisor-advisories",
                Severity: SupervisorSeverity.High,
                Message: $"{inspection.SupervisorAdvisories.CountAtOrAboveThreshold} advisory/advisories at or above the threshold ({topics}).",
                JobId: null));
        }

        if (inspection.StuckInProgress.Count > 0)
        {
            findings.Add(new MetaCycleFinding(
                Topic: "stuck-in-progress",
                Severity: SupervisorSeverity.High,
                Message: $"{inspection.StuckInProgress.Count} job(s) still in 3-progress past the threshold.",
                JobId: null,
                Evidence: inspection.StuckInProgress.JobIds));
        }

        if (inspection.ExpectedArtefacts.MissingCount > 0)
        {
            findings.Add(new MetaCycleFinding(
                Topic: "expected-artefact-missing",
                Severity: SupervisorSeverity.Warn,
                Message: $"{inspection.ExpectedArtefacts.MissingCount} job(s) closed without expected artefacts (results/, cli-output.log).",
                JobId: null,
                Evidence: inspection.ExpectedArtefacts.JobIds));
        }

        if (inspection.RunnerModeDrift.Drifted)
        {
            findings.Add(new MetaCycleFinding(
                Topic: "runner-mode-drift",
                Severity: SupervisorSeverity.Warn,
                Message: $"Runner mode drifted from '{inspection.RunnerModeDrift.Expected}' to '{inspection.RunnerModeDrift.Actual}' mid-cycle."));
        }

        // Extension hooks: extra advisory topics escalate any matching topic
        // already in the advisory list to a fix-trigger regardless of severity.
        // Implemented by emitting an Info finding tagged with the topic so the
        // verdict pass treats it as fix-triggering.
        if (extraAdvisoryTopics.Count > 0 && inspection.SupervisorAdvisories.Topics.Count > 0)
        {
            foreach (var t in inspection.SupervisorAdvisories.Topics)
            {
                if (extraAdvisoryTopics.Contains(t, StringComparer.Ordinal))
                {
                    findings.Add(new MetaCycleFinding(
                        Topic: $"extra-topic:{t}",
                        Severity: SupervisorSeverity.Warn,
                        Message: $"Extra topic '{t}' escalated by per-project hook."));
                }
            }
        }

        // Extras (extraGlobs) come in via the inspection.Extras map; treat any
        // entry as fix-triggering only when the user opted in via extraGlobAction.
        if (inspection.Extras != null
            && inspection.Extras.Count > 0
            && string.Equals(extraGlobAction, "fix-trigger", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new MetaCycleFinding(
                Topic: "extra-glob-match",
                Severity: SupervisorSeverity.Warn,
                Message: $"{inspection.Extras.Count} extra-glob match(es) escalated to fix-trigger by per-project hook."));
        }

        return findings;
    }

    /// <summary>
    /// Decide the verdict from the typed findings.
    /// </summary>
    public static MetaCycleVerdict DeriveVerdict(IReadOnlyList<MetaCycleFinding> findings)
    {
        if (findings.Count == 0) return MetaCycleVerdict.Healthy;

        if (findings.Any(f => EscalateOnlyTopics.Contains(f.Topic, StringComparer.Ordinal)))
            return MetaCycleVerdict.EscalationOnly;

        var hasFixTrigger = findings.Any(f => f.Severity >= SupervisorSeverity.Warn);
        return hasFixTrigger ? MetaCycleVerdict.FixTriggering : MetaCycleVerdict.Healthy;
    }

    /// <summary>
    /// Pick exactly one action. Ties broken in the order documented in
    /// <c>taxonomy.md</c>:
    /// <c>escalate-to-user</c> &gt; <c>queue-fix</c> &gt;
    /// <c>update-stable-then-resume</c> &gt; <c>resume</c>.
    /// </summary>
    public static MetaCycleAction DeriveAction(
        MetaCycleVerdict verdict,
        IReadOnlyList<MetaCycleFinding> findings,
        bool runUpdateStable,
        int autoFixesInTrailingHour,
        int maxFixesPerHour)
    {
        if (verdict == MetaCycleVerdict.Aborted)
            return new MetaCycleAction(MetaCycleActionKind.NoOp, "aborted");

        if (verdict == MetaCycleVerdict.EscalationOnly)
        {
            var topic = findings.FirstOrDefault(f => EscalateOnlyTopics.Contains(f.Topic, StringComparer.Ordinal))?.Topic ?? "needs-human";
            return new MetaCycleAction(MetaCycleActionKind.EscalateToUser, $"escalate:{topic}", FollowUpState: TaskStates.Preparation);
        }

        if (verdict == MetaCycleVerdict.FixTriggering)
        {
            if (autoFixesInTrailingHour >= maxFixesPerHour)
            {
                return new MetaCycleAction(
                    MetaCycleActionKind.EscalateToUser,
                    "auto-fix-rate-limit",
                    FollowUpState: TaskStates.Preparation);
            }

            // Pick the highest-severity finding that has a known template.
            var primary = findings
                .Where(f => KnownFixTemplates.ContainsKey(f.Topic))
                .OrderByDescending(f => (int)f.Severity)
                .FirstOrDefault();

            if (primary == null)
            {
                return new MetaCycleAction(
                    MetaCycleActionKind.EscalateToUser,
                    "no-template-for-finding",
                    FollowUpState: TaskStates.Preparation);
            }

            return new MetaCycleAction(
                MetaCycleActionKind.QueueFix,
                $"queue-fix:{primary.Topic}",
                FollowUpState: TaskStates.Preparation);
        }

        // Healthy
        return runUpdateStable
            ? new MetaCycleAction(MetaCycleActionKind.UpdateStableThenResume, "healthy:update-stable-then-resume")
            : new MetaCycleAction(MetaCycleActionKind.Resume, "healthy");
    }

    /// <summary>
    /// One-shot helper for callers (and tests) that want to go from inputs to
    /// the full report shape.
    /// </summary>
    public static MetaCycleReport BuildReport(
        string cycleId,
        string project,
        DateTime startedAt,
        DateTime completedAt,
        MetaCycleConfig config,
        IReadOnlyList<MetaCycleJobObservation> jobs,
        MetaCycleInspection inspection,
        bool autoCommitEnabled,
        int autoFixesInTrailingHour)
    {
        var findings = DeriveFindings(
            inspection,
            jobs,
            autoCommitEnabled,
            config.ExtraAdvisoryTopics,
            config.ExtraGlobAction);

        var verdict = DeriveVerdict(findings);
        var action = DeriveAction(
            verdict,
            findings,
            config.RunUpdateStableOnHealthy,
            autoFixesInTrailingHour,
            config.MaxFixesPerHour);

        return new MetaCycleReport(
            CycleId: cycleId,
            Project: project,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            CycleLengthN: config.CycleLengthN,
            JobsObserved: jobs,
            Inspection: inspection,
            Findings: findings,
            Verdict: verdict,
            Action: action);
    }

    private static string BuildCrashMessage(MetaCycleCrashMarker marker)
    {
        if (marker.At.HasValue)
        {
            var details = string.IsNullOrWhiteSpace(marker.Details) ? string.Empty : $" ({marker.Details})";
            return $"Backend crash recorded at {marker.At:u}{details}.";
        }
        return marker.Details ?? "Backend crash marker present.";
    }
}
