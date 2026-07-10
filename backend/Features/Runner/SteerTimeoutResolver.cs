namespace AgentStudio.Runner;

/// <summary>
/// Context handed to an <see cref="ISteerTimeoutResolver"/> for one timed-out
/// steer card. Everything the resolver needs to try an unambiguous answer from
/// the task context (concept Rule 2: "AUTO-ANTWORT aus prompt.md/Task-Kontext").
/// </summary>
/// <param name="ConfiguredIntegrationBranch">
/// The project's configured integration branch (may be null / empty; the
/// resolver resolves it against the repo, e.g. to <c>develop</c>).
/// </param>
public sealed record SteerResolveContext(
    string Project,
    string JobId,
    string JobFolder,
    string? WatchPath,
    string? Question,
    string? RepoRoot,
    string? TaskBranch,
    string? ConfiguredIntegrationBranch);

/// <summary>
/// Verdict of a steer-timeout resolver. Either a confident auto-answer (the
/// question was unambiguously answerable) or an ambiguity reason (route the card
/// to a blocked escalation instead of waiting).
/// </summary>
public sealed record SteerResolveResult(bool HasAnswer, string? AnswerText, string? AmbiguityReason)
{
    public static SteerResolveResult Answer(string text) => new(true, text, null);
    public static SteerResolveResult Ambiguous(string reason) => new(false, null, reason);
}

/// <summary>
/// Seam for deriving an unambiguous answer to a timed-out steer question from
/// the task context. Kept an interface so <see cref="SteerTimeoutMonitor"/> is
/// testable with a fake resolver, and so a richer resolver (e.g. an orchestrator
/// "resolve-or-block" turn) can be swapped in without touching the monitor.
/// </summary>
public interface ISteerTimeoutResolver
{
    SteerResolveResult Resolve(SteerResolveContext ctx);
}

/// <summary>
/// Default, conservative steer-timeout resolver. It answers exactly one class of
/// question deterministically from the branch state - <b>"is this already
/// implemented / done / merged?"</b> - which is the named 2067 evidence case
/// (concept Rule 2: "der 2067-Fall ... Branch-/develop-Stand pruefen und
/// antworten"). For that class it checks whether the card's <c>task/&lt;id&gt;</c>
/// branch is already an ancestor of the integration branch (its work is merged);
/// if so it answers "already integrated, finalize"; otherwise, and for every
/// other question shape, it returns ambiguous so the monitor escalates rather
/// than guessing.
///
/// <para>
/// Fail-safe by construction: any missing branch info or git error resolves to
/// ambiguous (-> blocked escalation), never a false auto-answer. "When unsure,
/// escalate; never wait forever."
/// </para>
/// </summary>
public sealed class SteerTimeoutResolver : ISteerTimeoutResolver
{
    private readonly GitService? _git;
    private readonly ILogger<SteerTimeoutResolver>? _logger;

    public SteerTimeoutResolver(GitService? git = null, ILogger<SteerTimeoutResolver>? logger = null)
    {
        _git = git;
        _logger = logger;
    }

    public SteerResolveResult Resolve(SteerResolveContext ctx)
    {
        if (!SteerQuestionClassifier.IsAlreadyImplementedQuestion(ctx.Question))
            return SteerResolveResult.Ambiguous(
                "the steer question is not an 'is this already implemented?' question that the branch-state check can answer");

        if (_git == null
            || string.IsNullOrWhiteSpace(ctx.RepoRoot)
            || string.IsNullOrWhiteSpace(ctx.TaskBranch))
            return SteerResolveResult.Ambiguous(
                "the task branch / repository could not be resolved for the branch-state check");

        string integrationBranch;
        bool merged;
        try
        {
            integrationBranch = _git.ResolveIntegrationBranch(ctx.RepoRoot!, ctx.ConfiguredIntegrationBranch);
            merged = _git.IsAncestor(ctx.RepoRoot!, ctx.TaskBranch!, integrationBranch);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Steer-timeout resolver: branch-state check failed for {Project}/{JobId}", ctx.Project, ctx.JobId);
            return SteerResolveResult.Ambiguous($"branch-state check failed ({ex.Message})");
        }

        if (!merged)
            return SteerResolveResult.Ambiguous(
                $"the task branch {ctx.TaskBranch} is not yet an ancestor of {integrationBranch}, so 'already implemented?' cannot be auto-answered from the branch state");

        return SteerResolveResult.Answer(
            $"Branch-state check: your work on this task is already integrated - the task branch {ctx.TaskBranch} is an ancestor of {integrationBranch}, so the change is already present there. " +
            "There is nothing left to implement. Finalize this run now: emit [[TASK_DONE]] with a one-line note that the work was already merged.");
    }
}
