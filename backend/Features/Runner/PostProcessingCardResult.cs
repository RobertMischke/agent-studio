namespace AgentStudio.Runner;

/// <summary>
/// Why one card's post-processing pass ended the way it did.
/// <para>
/// The run-boundary queue used to treat every pass that came back without a
/// verdict as a failure and wrote <c>post-processing-blocked</c> with a single
/// generic sentence. That conflated three very different things: a genuine
/// precondition failure, a decision that had already been recorded, and the
/// entirely legitimate hand-off "this card belongs to the canonical review
/// executor, not to me". The third case is self-healing and must never end
/// terminal - a card waiting for its fenced <c>ReviewAttempt</c> executor is
/// healthy, not broken.
/// </para>
/// </summary>
public enum PostProcessingCardStatus
{
    /// <summary>
    /// A decision path ran for this card and owns the lifecycle outcome. The
    /// queue keeps its safety net: if the card is still sitting in
    /// <c>4-auto-review</c> with an active lifecycle afterwards, no verdict
    /// actually landed and it is terminalized with the recorded reason.
    /// </summary>
    Decided,

    /// <summary>
    /// Not this engine's card right now - another owner is expected to move it,
    /// or a transient budget/concurrency limit was hit. Retryable and never
    /// terminal: the card rests in <c>awaiting-review</c> and is re-driven.
    /// </summary>
    Deferred,

    /// <summary>
    /// A precondition failed that no retry resolves (misconfiguration, an
    /// unresolvable watch path, an unreadable run log). Terminal, but always
    /// carrying the concrete reason rather than the generic sentence.
    /// </summary>
    Blocked,
}

/// <summary>
/// Outcome of <see cref="ReviewDecisionOrchestrator.ProcessCardAsync"/> for a
/// single card. <see cref="Reason"/> is a stable, greppable token (e.g.
/// <c>awaiting-canonical-review-executor</c>) so a blocked card names its cause
/// in <c>lifecycle.json</c> and in the log line, instead of only saying that
/// something did not happen.
/// </summary>
/// <param name="Status">How the pass ended.</param>
/// <param name="Reason">Stable machine-readable reason token.</param>
public sealed record PostProcessingCardResult(PostProcessingCardStatus Status, string Reason)
{
    public static PostProcessingCardResult Decided(string reason) =>
        new(PostProcessingCardStatus.Decided, reason);

    public static PostProcessingCardResult Deferred(string reason) =>
        new(PostProcessingCardStatus.Deferred, reason);

    public static PostProcessingCardResult Blocked(string reason) =>
        new(PostProcessingCardStatus.Blocked, reason);

    /// <summary>
    /// Reason token used when the card is held by the canonical remote review
    /// data plane. Shared with the tests so the contract cannot drift silently.
    /// </summary>
    public const string AwaitingCanonicalReviewExecutor = "awaiting-canonical-review-executor";
}
