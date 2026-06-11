# Post Processing

Post Processing is the orchestrator-owned lane after the main coding CLI finishes and before the task reaches `5-human-review`. The compatibility state key is still `4-auto-review`, but the visible board label now names what is happening: the system is checking the completed run, collecting evidence, and deciding what should be put in front of a person.

This phase must be performed by the orchestrator or a supporting CLI identity, not silently by the same coding identity that just edited the project. The coding agent's `[[TASK_DONE]]` is an input, not the final verdict.

## What the orchestrator does here

The phase can run deterministic checks and configured supporting-agent passes:

- result sanity checks
- security analysis
- QA or test-quality feedback
- design or UX critique
- runtime log analysis
- token summary collection
- orchestrator review
- finding extraction
- follow-up task suggestions.

Each check writes inspectable task evidence such as `aspect-*.md`, `lifecycle.json`, or `post-processing-outcomes.jsonl`. The outcome log records the supporting identity, optional CLI type, step id, summary, evidence reference, finding references, and any follow-up task ids.

The current implementation keeps `4-auto-review` as the durable compatibility lane and stamps `phase = post-processing-running` while the post-processing worker starts. A later lifecycle-lanes migration can collapse this into a `3-progress` sublane without changing the evidence contract.

## Typed outcomes

- **Pass to Review.** The task moves to `5-human-review` for operator sign-off.
- **Findings added.** The task can still move to Review, but the card and evidence include non-blocking findings.
- **Needs follow-up task.** The phase records suggested follow-up work instead of changing source code.
- **Needs human input.** The task escalates to the human-owned decision surface.
- **Failed post-processing.** The phase records the failure and routes through the normal human-review escalation path.

Post-processing may create findings or follow-up tasks. It should not automatically fix source code unless a future task explicitly enables that behavior.

## What to do when the lane stalls

If a job sits here longer than expected, inspect `lifecycle.json`, `post-processing-outcomes.jsonl`, the `aspect-*.md` files, and `logs/cli-output.log`. The header status still comes from the legacy auto-review status API while the compatibility lane remains in place. When the status line stops updating for many minutes, check `logs/meta/<project>/observations.jsonl` for supervisor advisories or use the supervisor pause/resume primitives to nudge the worker.

## Reference

- ADR-0025 (three-stage review pipeline)
- ADR-0026 (multi-aspect orchestrator review)
- `docs/research/expanded-lifecycle-lanes-plan-2026-05.md`
- `docs/research/auto-review-postprocessing-consolidation-2026-06.md`
