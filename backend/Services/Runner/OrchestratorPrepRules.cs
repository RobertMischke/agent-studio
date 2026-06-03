using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Pure-function rules for the ADR-0026 orchestrator-prep loop:
/// computes a clarity score on a task prompt, maps the score and the
/// per-project autonomy level to a verdict (<c>Accept</c>, <c>Iterate</c>,
/// or <c>Bounce</c>), and exposes the typed bounce reason that the
/// orchestrator-prep hosted service writes to <c>job.json</c>.
///
/// <para>The clarity score is heuristic in the first slice (cheap, no
/// model call, auditable). A future slice may upgrade to a fast-model
/// verdict; the bands and the autonomy gating do not change.</para>
///
/// <para>This file is intentionally free of I/O: it takes the prompt
/// text and a few summaries and returns a verdict. The hosted service
/// owns the moves on disk.</para>
/// </summary>
public static class OrchestratorPrepRules
{
    public const double SharpenThreshold = 0.40;
    public const double AcceptThreshold = 0.70;

    /// <summary>Inputs to one orchestrator-prep tick on a single task.</summary>
    public sealed record PrepInput
    {
        public string PromptText { get; init; } = "";
        public int Iteration { get; init; }
        public int MaxIterations { get; init; } = 3;
        public int AutonomyLevel { get; init; } = 2;

        /// <summary>
        /// The task slug (folder id). Carries semantic markers the prompt
        /// heuristics cannot see - notably the
        /// <see cref="TaskSlugs.HumanDecisionNeededPrefix"/> that forces a
        /// bounce regardless of autonomy level.
        /// </summary>
        public string Slug { get; init; } = "";

        /// <summary>The previous task's prompt text in the queue, or empty.</summary>
        public string PrevPromptText { get; init; } = "";

        /// <summary>The next task's prompt text in the queue, or empty.</summary>
        public string NextPromptText { get; init; } = "";
    }

    public enum Verdict
    {
        /// <summary>Move to <c>2-ready</c>. Optionally with a chat-note (level 4 cap-exit).</summary>
        Accept,

        /// <summary>Stay in <c>1a-orchestrator-prep</c>; rewrite prompt; increment iteration.</summary>
        Iterate,

        /// <summary>Admit to <c>2-ready</c> (the retired 1b-needs-human-review
        /// lane is gone). Carries a typed reason. A human-decision-needed marker
        /// is herded onward to 5-human-review by the runner's pickup sweep.</summary>
        Bounce,

        /// <summary>Stay in <c>1-preparation</c>. Returned at autonomy 0.</summary>
        Hold,
    }

    public enum BounceReason
    {
        None,
        UnderSpecified,
        MissingCriteria,
        ConflictsPrev,
        OutOfScope,
        IterationCap,

        /// <summary>
        /// The card carries the <see cref="TaskSlugs.HumanDecisionNeededPrefix"/>
        /// marker: it exists for a human to decide, so the prep loop bounces it
        /// without running the clarity bands or the autonomy gating. The runner's
        /// pickup sweep then routes the marker to <c>5-human-review</c>. A
        /// semantic marker, not a heuristic threshold.
        /// </summary>
        HumanDecisionNeededMarker,
    }

    public sealed record PrepDecision
    {
        public Verdict Verdict { get; init; }
        public double Clarity { get; init; }
        public BounceReason BounceReason { get; init; } = BounceReason.None;

        /// <summary>Optional human-readable note. At level 4 cap-exit it carries the [supervisor] override message.</summary>
        public string? Note { get; init; }
    }

    /// <summary>
    /// Heuristic clarity score in <c>[0, 1]</c>. Inputs are documented in
    /// <c>docs/mockups/orchestrator-prep-and-autonomy/taxonomy.md</c>.
    /// </summary>
    public static double ScoreClarity(PrepInput input)
    {
        // Belt-and-suspenders to the Decide() marker override: a card minted
        // explicitly for a human decision has zero machine-actionable clarity,
        // whatever its prompt body happens to say.
        if (TaskSlugs.IsHumanDecisionNeeded(input.Slug)) return 0.0;

        var text = input.PromptText ?? "";
        var lower = text.ToLowerInvariant();
        var wordCount = text.Split(new[] { ' ', '\t', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries).Length;

        // Empty / trivial: heavy negative.
        if (wordCount < 8) return 0.05;

        double score = 0.40; // baseline for any non-trivial prompt

        // Positive signals.
        if (lower.Contains("read first") || lower.Contains("read-first")) score += 0.15;
        if (lower.Contains("done when") || lower.Contains("acceptance criteria") || lower.Contains("definition of done")) score += 0.20;
        if (lower.Contains("mockup") || lower.Contains("docs/mockups") || lower.Contains("/spec/") || lower.Contains("adr-")) score += 0.10;
        if (wordCount >= 80 && wordCount <= 1500) score += 0.10;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"`[^`]+\.[a-zA-Z0-9]+`") || System.Text.RegularExpressions.Regex.IsMatch(text, @"\b[a-zA-Z0-9_./-]+\.(cs|ts|tsx|md|json|sh|html|py)\b")) score += 0.10;

        // Negative signals.
        if (HasOutOfScopeToken(lower)) score -= 0.30;
        if (PromptsConflict(lower, (input.PrevPromptText ?? "").ToLowerInvariant())) score -= 0.20;
        if (PromptsConflict(lower, (input.NextPromptText ?? "").ToLowerInvariant())) score -= 0.10;
        if (wordCount < 30) score -= 0.20;

        return System.Math.Clamp(score, 0.0, 1.0);
    }

    private static bool HasOutOfScopeToken(string lower)
    {
        // ADR-0052 lifted the intra-project-parallelism non-goal, so
        // "worktree" / "branch-per-task" / "intra-project parallel" are no
        // longer out-of-scope tokens and no longer penalise the prep score.
        // No out-of-scope tokens remain; re-add here only for a future non-goal.
        _ = lower;
        return false;
    }

    private static bool PromptsConflict(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(b)) return false;
        // Surface-level conflict heuristic: shared file path mention plus a
        // negation or "remove" verb on one side. Cheap, audit-friendly.
        var sharedPath = System.Text.RegularExpressions.Regex.Match(a, @"\b[a-zA-Z0-9_./-]+\.(cs|ts|tsx|md|json|sh|html|py)\b");
        if (!sharedPath.Success) return false;
        if (!b.Contains(sharedPath.Value)) return false;
        var aRemoves = a.Contains("remove ") || a.Contains("delete ") || a.Contains("drop ");
        var bRemoves = b.Contains("remove ") || b.Contains("delete ") || b.Contains("drop ");
        return aRemoves != bRemoves; // one removes, the other touches: surface conflict
    }

    /// <summary>
    /// Map (clarity, autonomy, iteration) to a verdict. The autonomy scale
    /// is the load-bearing knob; the clarity bands are coarse on purpose
    /// so heuristic noise cannot push a task across boundaries.
    /// </summary>
    public static PrepDecision Decide(PrepInput input)
    {
        var clarity = ScoreClarity(input);

        // Semantic-marker override. A card whose slug carries the
        // human-decision-needed prefix exists for a person to decide; the
        // automation must not reason about it at all. Bounce it unconditionally
        // - this overrides the "level 4 never bounces" doctrine on purpose,
        // because the marker is an explicit intent, not a clarity heuristic.
        // Checked first so no autonomy branch (including the level-0 hold and
        // the cap-exit accept) can swallow it. The bounce admits to 2-ready,
        // where the runner's pickup sweep routes the marker to 5-human-review.
        if (TaskSlugs.IsHumanDecisionNeeded(input.Slug))
        {
            return new PrepDecision
            {
                Verdict = Verdict.Bounce,
                Clarity = clarity,
                BounceReason = BounceReason.HumanDecisionNeededMarker,
                Note = "human-decision-needed marker: bouncing for a human decision (routed to 5-human-review)",
            };
        }

        var level = System.Math.Clamp(input.AutonomyLevel, 0, 4);

        // Level 0: orchestrator never moves a task forward without a click.
        if (level == 0)
        {
            return new PrepDecision { Verdict = Verdict.Hold, Clarity = clarity };
        }

        var capReached = input.Iteration >= input.MaxIterations;

        // Cap-exit policy is the most specific case; check it first.
        if (capReached)
        {
            return level switch
            {
                4 => new PrepDecision
                {
                    Verdict = Verdict.Accept,
                    Clarity = clarity,
                    Note = $"[supervisor] orchestrator-prep cap reached (iter={input.Iteration}); fully-auto override accepting at clarity {clarity:F2}",
                },
                3 => new PrepDecision
                {
                    Verdict = Verdict.Accept,
                    Clarity = clarity,
                    Note = $"[supervisor] orchestrator-prep cap reached (iter={input.Iteration}); confident-mode advisory accepting at clarity {clarity:F2}",
                },
                _ => new PrepDecision
                {
                    Verdict = Verdict.Bounce,
                    Clarity = clarity,
                    BounceReason = BounceReason.IterationCap,
                },
            };
        }

        // Below the sharpen threshold: ambiguous.
        if (clarity < SharpenThreshold)
        {
            return level switch
            {
                1 => new PrepDecision { Verdict = Verdict.Bounce, Clarity = clarity, BounceReason = BounceReason.UnderSpecified },
                4 => new PrepDecision
                {
                    Verdict = Verdict.Iterate,
                    Clarity = clarity,
                    Note = "iterating: fully-auto never bounces; sharpening prompt",
                },
                _ => new PrepDecision { Verdict = Verdict.Iterate, Clarity = clarity },
            };
        }

        // Borderline: 0.40..0.69
        if (clarity < AcceptThreshold)
        {
            return level switch
            {
                1 => new PrepDecision { Verdict = Verdict.Bounce, Clarity = clarity, BounceReason = BounceReason.UnderSpecified },
                _ => new PrepDecision { Verdict = Verdict.Accept, Clarity = clarity },
            };
        }

        // Clear: >= 0.70
        return new PrepDecision { Verdict = Verdict.Accept, Clarity = clarity };
    }
}
