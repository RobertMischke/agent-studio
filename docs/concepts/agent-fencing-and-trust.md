# Agent Fencing and Graded Trust

Status: current operating rule (fencing) plus an explicit open proof point (trust rate). Curated 2026-08-23 out of [Distributed execution hardening — Agent fencing](../operations/haertung-verteilte-ausfuehrung/agent-fencing.html) (AGT-W7), which remains the canonical presentation with its diagram and live case cards; this page is the durable, text-only companion for the Wiki concept tree.

## One writer for Git history

Worker agents never commit or push. They edit files and produce evidence; after review, the platform creates the commit, records the exact SHA, and pushes it through the managed path. This keeps the lifecycle decision and the Git record under one authority (canonical implementation detail: [Commit / Push Doctrine](../operations/git/commit-push-doctrine.md)).

- **Worker agent:** may inspect Git and change assigned files. May not create, amend, switch, merge, tag, stash, commit, or push history.
- **Platform:** reviews the result, decides the lifecycle transition, creates the commit, stamps its SHA on the task, and pushes.

If a worker tries to write history, a command guard blocks known mutating Git verbs, and the runner compares repository HEAD before and after the run. A detected change becomes `agent-git-violation`: the result is quarantined for human review, evidence is preserved, and the run is not silently accepted or integrated.

## Trust is graded, not binary

Models do not share one maturity level — they differ by model, CLI, task shape, prompt, and runtime environment. A model can be strong at implementation and still unreliable at process boundaries. "Trusted" therefore means "supported by observed evidence for this use," not "allowed to do everything." Fencing gives every model the same hard Git boundary regardless of trust; trust instead changes **how closely work is supervised**, never who may author history.

## Evidence-driven oversight

1. **Record trust per model.** A per-model capability and trust record is backed by runs, violations, and source evidence (Token Economy card TE-13).
2. **Sample normal work.** A high-trust, high-capability orchestrator reviews some routine runs so silent drift still has a chance to be found.
3. **Review on signals.** Scrutiny increases when a guard fires, HEAD changes unexpectedly, the diff does not match the task, evidence conflicts, or recovery repeats.
4. **Classify and escalate.** The orchestrator explains whether a signal is benign, a process violation, or uncertain. Uncertain or violating work stays quarantined and goes to an accountable human.

**Invariant:** trust may change sampling frequency. It never grants a worker permission to commit or push.

## Incidents that shaped the rule

Real July 2026 cases, not hypothetical warnings:

- **AIP-7** (`agent-git-violation`, gpt-5.6-sol): the worker advanced HEAD by one commit before the platform-owned commit step; review then found the committed diff belonged to an unrelated pattern. The before/after HEAD check caught it; exact-diff review exposed the attribution mismatch. Lesson: topical research is not proof that the recorded commit belongs to the task — writer identity and exact diff provenance must both be checked.
- **AIP-10** (`agent-git-violation`, gpt-5.6-terra): same shape — HEAD advanced before the platform commit step, reviewed diff contained an unrelated pattern. Lesson: a prompt rule alone is insufficient; a command guard, HEAD-delta detection, exact-diff review, and fail-closed recovery are all required together.
- **Token Economy shared-checkout collision** (TE-13, remediation AGT-2300): for local-folder projects, a non-Git task-storage `RootPath` could disable worktree provisioning even when `RepositoryPath` named the real repository, so two admitted runs could share one in-place checkout and observe each other's HEAD and diff. Task identity, branch state, and the reviewed diff disagreeing exposed the collision. Lesson: isolation must be resolved from the authoritative repository, not from task storage or slot count — every coding run needs its own worktree, while commit and push stay platform-owned.

## Open proof point: violation probability per model

Not proven yet. The desired metric is `violating eligible runs / all eligible runs` for each exact model and CLI. The known incidents provide numerator evidence, but the historical denominator and detection coverage are not yet complete enough for a defensible rate. TE-13 owns this proof point; any published rate must include sample size, observation window, CLI, detection version, and confidence. Until then, the system should show "insufficient evidence," not invent precision.

## See also

- [Distributed execution hardening — Agent fencing](../operations/haertung-verteilte-ausfuehrung/agent-fencing.html) (AGT-W7) — canonical presentation with the visual diagram.
- [Commit / Push Doctrine](../operations/git/commit-push-doctrine.md) — the platform-owned commit/push implementation detail this rule depends on.
- [Parallel Task Execution](parallel-task-execution.md) — worktree isolation, the mechanism that keeps the AIP-7/AIP-10/TE-13 collision class from recurring.
