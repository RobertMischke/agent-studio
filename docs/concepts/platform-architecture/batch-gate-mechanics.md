# Batch Gate: what is actually decided today

Status: **mostly not decided.** This page exists because the source dossier
was mined for durable content; almost none qualified. Read this page for the
handful of adjacent mechanisms that are already accepted and shipped. Read the
dossier itself for the Batch Gate proposal, which remains a recommendation
awaiting operator sign-off.

## Why this page is short

[`docs/operations/batch-gate-concept/index.html`](../../operations/batch-gate-concept/index.html)
(AGT-W36) proposes assembling every eligible pending delivery into one closed,
fenced candidate branch, running the full suite once, and publishing only a
green exact-SHA tip with per-member evidence and bounded red isolation. As of
2026-08-21 this is **decision-pending**: the dossier's own approval box says
"this is not approval to edit the current pipeline yet," and its final
section ("Decisions to record before implementation") lists primary strategy,
evidence contract, publication authority, red-path budget, and pilot
thresholds as still open. A 2026-08-18 operator note on the same page proposes
changing the trigger sequencing again (full suite as a final gate only, after
review) and is itself explicitly framed as "under consideration," not decided.

So: single-flight batch construction, the exact-SHA publication rule for a
*batch*, the per-member evidence contract for a *batch*, the `log2(n)`-bounded
red-isolation algorithm, and the batch/train ref-mutation authority sharing
rule are all **proposed, not accepted**. Do not treat any of them as current
product behavior. This page does not attempt to document them as settled
mechanisms; when/if the dossier is approved, this page (or a successor) should
be rewritten from the accepted decision, not from the proposal text.

## What the proposal builds on that *is* already decided

The dossier's Batch Gate proposal explicitly reuses four mechanisms that
predate it and are already live, independent of whether Batch Gate itself is
ever approved:

- **Integrate-before-review invariant (AGT-2528 immediate Remote integration
  coordinator, AGT-2603 follow-up state-machine simplification).** A green
  Remote delivery integrates before Human Review under an exact fenced
  subject, deterministic ordering, and visible failure — no acceptance-side
  secret merge. This is the existing authority model the Batch Gate proposal
  says it "keeps," changing only the green predicate and the publication
  unit (batch vs. single card).
- **AGT-2543 honest-completion invariant.** No card may reach Completed, and
  no UI may claim gate-passed, unless the current attempt points to a
  successful gate run, the exact tested candidate, and its complete SHA
  mapping. Git ancestry is stronger than any bookkeeping record. This
  invariant is the correctness floor any future batch-gate evidence contract
  would have to satisfy, not something the proposal introduces.
- **AGT-2594 candidate-SHA promotion train.** The `develop` to `main`
  promotion train already tests and publishes a pinned candidate SHA rather
  than chasing a moving tip. See
  [`docs/operations/develop-main-promotion.md`](../../operations/develop-main-promotion.md)
  for the current contract. The Batch Gate proposal says any future batch
  publisher would have to share the same ref-mutation lease as this train —
  but that sharing rule itself is proposed, not built.
- **`POST /api/tasks/{id}/integration-records` (added 11 August 2026).** This
  endpoint, documented in
  [`docs/system/domains/tasks.md`](../../system/domains/tasks.md), already
  lets an operator- or GPT-reviewed historical classification be appended to
  a card once it is in Human Review or later, using a five-class schema
  (`integrated-verified`, `integrated-historical`, `no-code-expected`,
  `content-on-fence`, `genuinely-missing`). It is real and shipped, but the
  dossier is explicit that this endpoint is **not** a native gate-pass
  authority for batches: it rejects in-flight lanes and never moves a card or
  changes Git. A native, append-only per-member Batch Gate record is called
  out as still needed before any implementation — this is one of the open
  decisions, not something the endpoint already provides.

## Links

- Source dossier:
  [`docs/operations/batch-gate-concept/index.html`](../../operations/batch-gate-concept/index.html)
  (AGT-W36, decision-pending as of the 11 August 2026 evidence cut plus the
  18 August operator note).
- System of record for integration bookkeeping and integration records:
  [`docs/system/domains/tasks.md`](../../system/domains/tasks.md).
- Current integrate-before-review / merge workflow concept:
  [`docs/concepts/task-integration-and-merge-workflow.md`](../task-integration-and-merge-workflow.md).
- Current promotion-train contract:
  [`docs/operations/develop-main-promotion.md`](../../operations/develop-main-promotion.md).
- Related evidence the dossier reconciles:
  [Pipeline Time Economy](../../quality/pipeline-time-economy/index.html)
  and
  [Async Validation and the Test Staging Lane](../../operations/async-validation-staging-lane/index.html).

## Living knowledge log

Append new findings here, newest on top.

- **2026-08-21.** Documentation-transfer extraction pass over the AGT-W36
  dossier. Conclusion: the Batch Gate mechanism itself (single-flight suite,
  exact-SHA batch publication, per-member batch evidence, bounded red
  isolation, promotion-train authority sharing) remains decision-pending;
  nothing in it was extracted as durable per the "only extract accepted
  parts" rule. The 2026-08-18 operator note proposing a review-first
  sequencing change is likewise still under consideration, not decided. Only
  the four pre-existing, already-shipped mechanisms the proposal leans on
  (integrate-before-review, AGT-2543 honest-completion, AGT-2594 candidate-SHA
  promotion, the 11 Aug integration-records endpoint) were recorded above.
  When/if the operator approves the pilot, this page should be rewritten from
  the decision record, not from the proposal text.
