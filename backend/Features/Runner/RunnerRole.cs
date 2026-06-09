namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Pickup role for a project runner (ADR-0044). Distinguishes the
/// orchestrator that drives a project's pipeline from a test-subject
/// runner that is observed by Playwright specs but never auto-picks.
/// </summary>
public enum RunnerRole
{
    /// <summary>
    /// Default / production seat. The per-project pickup loop runs
    /// normally: <c>auto-single</c> / <c>auto-continuous</c> trigger
    /// <see cref="ProjectRunner.TickAsync"/> to claim ready / progress
    /// folders. Stable is this seat (see AGENTS.md "Dev backend lifecycle:
    /// Playwright-only").
    /// </summary>
    Orchestrator,

    /// <summary>
    /// Regression-target seat. The pickup loop is structurally disabled:
    /// <see cref="ProjectRunner.TickAsync"/> still ticks watchdog /
    /// pending-decision scanner / reconciliation so the surface the test
    /// is observing stays live, but it never claims a new job and never
    /// reverts the mode. Explicit <c>POST /api/tasks/{id}/start</c> calls
    /// still run so Playwright fixtures (driving dev's backend through
    /// <c>dev-lifecycle.sh</c>) can exercise a specific job on demand.
    /// Dev is this seat.
    /// </summary>
    TestSubject
}

/// <summary>
/// Backend-wide role assignment that decides whether this process is
/// allowed to auto-pick tasks from a watched workspace.
///
/// <para>
/// Two backends can share one workspace (the dev / stable checkout pair
/// under <c>agent-taskboard-devspace/</c>). Without an explicit role,
/// both runners' auto-pickup ticks see the same <c>3-progress</c> folder
/// and race for it, producing duplicate CLI spawns, file-lock crashes,
/// and crash-recovery spirals. The role is configured per backend (via
/// <c>Runner:Role</c> or, when unset, derived from <c>Environment:IsDev</c>)
/// so the shared workspace has exactly one auto-pickup driver.
/// </para>
///
/// <para>
/// The role is read from <c>Runner:Role</c> in configuration; default is
/// <see cref="RunnerRole.Orchestrator"/> so an unconfigured backend behaves
/// like the stable seat rather than silently going dark.
/// </para>
/// </summary>
public static class RunnerRoles
{
    public const string Orchestrator = "orchestrator";
    public const string TestSubject = "test-subject";

    /// <summary>
    /// Normalises an operator-supplied role value to one of the two
    /// canonical constants. Unknown / blank values fall back to
    /// <see cref="Orchestrator"/> so a typo never silently mutes a
    /// supervisor seat.
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Orchestrator;
        var trimmed = raw.Trim().ToLowerInvariant();
        return trimmed switch
        {
            TestSubject => TestSubject,
            "test_subject" => TestSubject,
            "subject" => TestSubject,
            "observed" => TestSubject,
            Orchestrator => Orchestrator,
            "stable" => Orchestrator,
            _ => Orchestrator
        };
    }

    public static bool IsTestSubject(string? role) => Normalize(role) == TestSubject;

    /// <summary>
    /// Parses a config string into the strongly-typed <see cref="RunnerRole"/>
    /// enum. Wraps <see cref="Normalize"/> so the input vocabulary
    /// (aliases, casing) matches the string-side helpers.
    /// </summary>
    public static RunnerRole Parse(string? raw)
        => Normalize(raw) == TestSubject ? RunnerRole.TestSubject : RunnerRole.Orchestrator;

    /// <summary>
    /// Reverse of <see cref="Parse"/>: formats an enum value back to the
    /// canonical string vocabulary so API responses, logs, and the runner
    /// status DTO agree on the spelling.
    /// </summary>
    public static string Format(RunnerRole role)
        => role == RunnerRole.TestSubject ? TestSubject : Orchestrator;

    /// <summary>
    /// Resolves the effective role for a backend from configuration.
    /// Explicit <c>Runner:Role</c> wins; when nothing is configured,
    /// <c>Environment:IsDev=true</c> implies <see cref="RunnerRole.TestSubject"/>
    /// (the dev checkout convention) and everything else stays
    /// <see cref="RunnerRole.Orchestrator"/>.
    /// </summary>
    public static RunnerRole ResolveFromConfig(Microsoft.Extensions.Configuration.IConfiguration config)
    {
        var explicitRole = config?["Runner:Role"];
        if (!string.IsNullOrWhiteSpace(explicitRole))
            return Parse(explicitRole);
        var isDev = config?.GetValue<bool>("Environment:IsDev") ?? false;
        return isDev ? RunnerRole.TestSubject : RunnerRole.Orchestrator;
    }
}
