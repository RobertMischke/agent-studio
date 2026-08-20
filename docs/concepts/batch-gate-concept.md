# Batch Gate: One Full Suite for a Delivery Wave

Status: **proposed, not yet approved or implemented**. This page extracts the
durable mechanism from the decision dossier below so the design survives
independently of the dossier's own lifecycle. It is not a record of current
product behavior; see [task-integration-and-merge-workflow.md](task-integration-and-merge-workflow.md)
for what actually runs today.

Source: [`docs/operations/batch-gate-concept/index.html`](../operations/batch-gate-concept/index.html)
(AGT-W36, source task AGT-2648). The dossier's own header states "Implementation:
none in this card" and closes with a decision statement drafted for operator
sign-off, not a recorded approval. Treat every mechanism below as a proposal.

## Why

Running one full backend/frontend build+test suite per delivered task does not
scale once several deliveries are eligible for integration at the same time.
Batch Gate proposes mechanically assembling every eligible pending delivery
into one closed, fenced temporary candidate, running the full suite once
against that candidate, and publishing an exact green tip with per-member
evidence - rather than serializing one full suite per card.

## Batch construction

- A coordinator creates a temporary branch
  `agent-studio/batch-gate/<project>/<batch-id>` from a fetched, immutable
  `develop` base.
- The manifest (members, order, base SHA, gate profile) is made durable
  *before* any Git work starts.
- Members are ordered by `review-subject.completedAtUtc`, then enqueue
  sequence, then task key. Order, base SHA, members, and gate profile together
  form a **membership digest**.
- Each member is replayed mechanically (not conflict-resolved) in a disposable
  detached worktree, using the same conflict-free mechanical rebase rules as
  ordinary integration. A conflicting member is ejected to the existing
  "Rebase & retry" steer path without poisoning the rest of the batch. A
  **conflict cascade guard** closes the candidate early if more than 25%, or
  3-in-a-row, of members conflict.

## Lifecycle (green path)

1. Persist a batch-run record before execution.
2. Run the complete suite once, on a clean checkout of the exact candidate SHA.
3. Revalidate every member's generation and acquire the project publication
   lease.
4. Verify the `develop` tip still equals the recorded pre-tip.
5. Publish by fast-forward (or a merge commit only if that commit was itself
   the tested candidate).
6. Verify remote `develop` resolves to the tested candidate.
7. Write per-member gate and integration evidence, then release each member to
   Human Review.

If `develop` moved since the recorded pre-tip, the batch is marked
`stale-base` and rerun - it never merges onto a tip it did not test.

## Per-member evidence

Two linked records per member:

- **Gate-passed record**: task key, attempt, epoch, original/replacement SHAs,
  batch id, membership digest, base SHA, candidate SHA, gate profile digest,
  batch-run id, verdict, evidence path.
- **Integration record**: `develop`, exact remote tip, integrated SHA set,
  evidence pointer.

A missing or mismatched identity produces `batch-gate-evidence-missing`; a
push/ancestry failure produces `integration-unverified`. The dossier notes
that the existing `POST /api/tasks/{id}/integration-records` endpoint (added
2026-08-11) is not a native gate-pass authority - a native batch-gate record
would be a required implementation slice, not a reuse of that endpoint.

## Red path: honestly bounded halving

Red results are first classified infrastructure / deterministic / flaky. The
proposed isolation algorithm:

1. Split the ordered members in half; reconstruct and test the first half on
   the same base.
2. If that half is still red, continue narrowing it (single-cause assumption).
   If it is green, continue with the complement.
3. At one member, eject it to a per-task gate; rebuild the survivors and run
   one complete suite.

Stated bound: for one reproducible offender, isolation takes at most
`ceil(log2(n))` diagnostic runs plus one final survivor run
(`ceil(log2(n)) + 2` total including the initial red run). The dossier is
explicit that this is **not** a universal bound for multiple or interacting
offenders - it documents a table of failure shapes and fallbacks for that
case.

## Authority and promotion-train interplay

Two levels of fencing are proposed:

- A durable **batch lease** (keyed by project/repo/branch/gate profile, with a
  fencing token) preventing duplicate coordinators.
- A shared **project ref-mutation lease** serializing Batch Gate publication,
  auto-main advancement, and the promotion train on canonical refs - acquired
  only for final revalidation/publish, not held during the long suite run.

A supersede-mid-batch table defines the required response at each moment
(before the suite starts / during / after green-before-publish / after
verified publish).

Batch Gate is designed to compose with, not replace, two adjacent mechanisms
(both already delivered, per the dossier's related task keys):

- **AGT-2528** (immediate Remote integration coordinator) and **AGT-2603**
  (integration state-machine simplification): proposed flow is settled
  delivery -> per-card model review -> batch pending -> batch gate and
  verified mirror -> Human Review.
- **AGT-2594** (develop-to-main candidate-SHA promotion train, see
  [`docs/operations/develop-main-promotion.md`](../operations/develop-main-promotion.md)):
  Batch Gate and the promotion train share the ref-mutation lease; the train
  never discovers or promotes a temporary batch ref, only its own pinned
  candidate SHA, and their publish phases cannot overlap.

## Scheduling

The full suite runs on one gate-capable host, one active suite per host, one
active Batch Gate per repo. The dossier separates per-card review-plane work
(envelope/fence/subject validation, model review, metadata/merge preflight)
from batch-gate-plane work (clean checkout of the exact candidate, full
backend/frontend build+test, one authoritative full-suite verdict).

## Failure semantics (typed outcomes)

`conflict-ejected`, `member-superseded`, `abandoned-fence`,
`infrastructure-red`, `product-red`, `stale-base`, `publish-waiting`,
`publish-failed`, `batch-gate-evidence-missing`, `integration-record-pending`
- each with a defined batch/member outcome and recovery path in the source
dossier.

## Rollout, if approved

The dossier proposes a staged rollout: shadow manifest -> docs-only pilot ->
low-coupling code pilot -> general decision. None of these stages had started
as of this page's writing (2026-08-20).

## Living knowledge log

- 2026-08-20: Page created from the decision dossier during a curation pass
  (AGT-2671). The dossier's own source task (AGT-2648) and its four related
  tasks (AGT-2543, AGT-2528, AGT-2603, AGT-2594) are archived and delivered,
  but delivery of the *analysis* is not an operator approval of the *batch
  mechanism* - no such approval exists in the dossier text. Status stays
  decision-pending.
