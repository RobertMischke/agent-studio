# Pipeline Time Economy — brief

A whole-pipeline time and cost analysis, complementary to the
[token-economy-task-data](../token-economy-task-data/) workbench (AGT-2293).
Where that one inventories field-level signal validity on a 60-card grade-tagged
sample, this one measures **where the time and tokens go** across a larger
all-attempts corpus.

## Decision in one paragraph

Across 847 tasks with structured pipeline data (2,396 attempts), compute is a
minority of wall time and tokens concentrate almost entirely in one step.
Roughly 38% of wall time is waiting (no compute), ~41% is redo (retries), ~21%
is first-attempt work. Of pure compute, the LLM coding step is 66.8% and carries
~98% of all tokens; the test gate is 16.6% and carries none. Most retries are
the intended orchestrator quality loop (978 adapted reissue prompts; 78% of
post-processing outcomes are `findings-added`), not dumb repeats. Implication:
optimizing compute or test time buys throughput, not money; the money lever is
fewer/shorter coding re-runs.

## Evidence classes (per the token-economy workbench convention)

- **Hard:** step `durationMs`, per-step token counters, attempt records, gate
  verdicts, reissue-prompt file counts, `post-processing-outcomes.jsonl` outcome
  kinds. Facts about recorded runs.
- **Interpretation:** money cost (needs a pricing assumption), the wall-time
  "waiting" split (derived as wall − step-sum), the 2000× ratio.
- **Coverage caveats:** `previousAttempts` is capped at 10 on disk, so attempt
  and retry-time figures are a conservative floor for very heavy tasks (the live
  API shows up to 22 attempts). A missing token/duration field is a coverage
  gap, not a zero — except the test gate, a deterministic non-LLM step whose
  zero token count is a true zero.

## Method

Aggregation of `steps[].durationMs` and token fields in
`pipeline-execution.json` across all attempts including `previousAttempts`, over
`projects/agent-taskboard/tasks/**`. Cause taxonomy from counting
`orchestrator-follow-up-history/*-reissue.md` and tallying `outcome` in
`post-processing-outcomes.jsonl` (6,320 events, 496 tasks). Snapshot 2026-07-25.

## Related

- [token-economy-task-data](../token-economy-task-data/) — AGT-2293, field validity
- Sibling proposal: [async-validation-staging-lane](../../operations/async-validation-staging-lane/)
