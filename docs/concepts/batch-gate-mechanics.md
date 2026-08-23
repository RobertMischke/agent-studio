# Batch Gate mechanics

Status: target mechanism, decision-pending. Nothing described here is
implemented — no `BatchGate`-named code exists in the repository as of
2026-08-18. This page documents the settled *design* of the
[batch-gate-concept dossier](../operations/batch-gate-concept/index.html)
(`AGT-W36`, sourceTaskKey `AGT-2648`) so the mechanism survives independently
of that dossier's still-open rollout decision. The dossier remains the
system-of-record for the open options, pilot evidence, and sign-off state.

## Why a batch gate

Per-task build/test gating serializes suite runs one task at a time even when
many deliveries are independently ready. A Batch Gate is a per-project,
per-repository, per-gate-profile transaction that snapshots every currently
eligible pending delivery into one closed manifest at close time, runs the
full suite once against that manifest, and publishes an exact tested tip with
per-member evidence. New arrivals wait for the next batch; there is no
cross-project mega-batch, no model-based test selection, no suite weakening,
no force-push, no post-test merge commit, and no change to human acceptance.

## Eligibility contract

A delivery is eligible only if all of the following hold:

- its Result Envelope is settled (immutable result ref, result SHA, run
  attempt, delivery epoch, fencing token);
- per-card model review has passed with the build/test aspect explicitly
  recorded as `deferred-to-batch` (never "not applicable");
- it is the current generation (no newer attempt, reissue, or supersede);
- project, repository, integration branch, gate profile, and platform version
  match every other member; and
- it is not already claimed by another batch or by a per-task gate.

The manifest records every eligible key plus every exclusion with a typed
reason. There is no silent subsetting.

## Construction: deterministic replay

The coordinator creates a temporary ref
`agent-studio/batch-gate/<project>/<batch-id>` from a fetched, immutable
`develop` base. Members are ordered by `review-subject.completedAtUtc`, then
enqueue sequence, then task key. Order, base SHA, members, and gate profile
form a membership digest, persisted before any Git work starts.

Each member replays in a disposable detached worktree via the existing
mechanical-rebase rules (see
[rebase and merge invariants](rebase-merge-invariants.md)). A conflicting
member aborts only its own replay, is ejected with a typed conflict/file-list
detail to the existing rebase-and-retry steer path, and the batch continues —
the coordinator never resolves conflicts itself. A cascade guard closes the
batch with already-admitted members and pushes the untouched tail to the next
batch if more than 25% of the batch, or 3 consecutive members, conflict (the
threshold is a pilot parameter; the semantic — shrink the batch rather than
centrally resolve conflicts — is the durable part).

## Green path: exact-SHA publication

1. Persist a batch-run record (IDs, base/candidate SHA, profile digest, host,
   lease fence, evidence location) before execution.
2. Run the full suite once on a clean checkout of the exact candidate SHA.
3. Revalidate every member's generation and acquire the project
   ref-mutation lease.
4. Refetch `develop` and confirm its tip still equals the recorded pre-tip.
5. Publish only by fast-forward, or via a merge commit that was itself
   prebuilt and tested as the candidate — never a new, untested merge commit.
6. Verify remote `develop` resolves to the tested candidate.
7. Write per-member evidence and release each member to Human Review; human
   acceptance stays a separate step.

If `develop` moved since the recorded pre-tip, the run is marked
`stale-base`: reconstruct on the new base and rerun. This is the accepted
cost of preserving the exact-SHA guarantee — a batch never publishes a
candidate that was not the thing actually tested.

## Per-member evidence contract

Two linked records are required per member, not one batch-wide narrative:

- a **gate-passed record** (task key, attempt, epoch, original and
  replacement SHAs, batch ID, membership digest, base/candidate SHA, profile
  digest, batch-run ID, verdict, evidence path) — a missing or mismatched
  identity yields `batch-gate-evidence-missing` and blocks gate-passed status;
- an **integration record** (develop tip, integrated SHA set, pointer to the
  same batch run) — verification failure yields `integration-unverified` and
  blocks Completed.

The existing `POST /api/tasks/{id}/integration-records` endpoint can append
idempotent bookkeeping once a card is in Human Review, but it is explicitly
not a native gate-pass authority. Invariant: no card reaches Completed, and no
UI reports gate-passed, unless the current attempt maps to a successful batch
run and its exact tested candidate — Git ancestry outranks bookkeeping.

## Red-path classification and bounded halving

Reds are classified before any diagnostic work starts:

- **infrastructure red** (host loss, timeout, registry outage, disk pressure,
  lease loss) attributes no member and gets one same-candidate retry, then a
  pause on repeat;
- **deterministic suite red** (a reproduced failing test or build) triggers
  halving, below;
- **flaky red** uses the existing quarantine policy, never majority-vote to
  green.

Halving: split the ordered member set in half, reconstruct and test the first
half on the original base. If red, recurse into it; if green, recurse into
the complement under a single-cause assumption (the green subset is kept as
evidence). At one member, eject it to a per-task gate. Then rebuild all
survivors and run one full suite, repeating or ejecting an unresolved cohort
if the diagnostic budget is exhausted.

This bounds diagnosis to `ceil(log2(n)) + 2` total suite runs, including the
initial red, for one reproducible, monotone, independent offender. It is
explicitly **not** a universal bound for multiple offenders (`O(k log n)`) or
for cross-member interaction failures — a half can be green while the union
is red, with no logarithmic guarantee in that case.

## Authority, fencing, and supersede handling

Two lease levels apply, using the conventions in
[distributed-agent-studio-target-architecture.md, section 6](distributed-agent-studio-target-architecture.md#lease-fence-and-authority-epoch-mechanics):

- a durable **batch coordinator lease**, keyed by project/repo/branch/gate
  profile, with fencing tokens on every state write, preventing duplicate
  coordinators;
- a shared **project ref-mutation lease** that serializes the batch
  publisher, direct integration, auto-main advancement, and the candidate-SHA
  promotion train against `main`/`develop` — acquired only at final
  revalidation/publish, not for the whole suite run.

Lease loss cancels the suite and forbids publication; a successor resumes
only if the manifest digest and candidate fence still match, otherwise it
reconstructs. Supersede handling is time-keyed: pre-suite ejects the stale
generation; mid-suite lets it finish then discards the verdict and
reconstructs; post-green-pre-publish the final membership check fails closed;
post-publish the transaction is final and history is not rewritten.

## Interaction with existing contracts

Batch Gate preserves integrate-before-review but changes the green predicate
and publication unit to batch-level:
`settled delivery → per-card model review → batch pending → batch gate + verified mirror → Human Review`.
A temporary replay is never itself the "integrated" event — only verified
`develop` membership releases a member to review. Against the candidate-SHA
promotion train: the train and the batch publisher share the ref-mutation
lease; both may test concurrently, but publish phases cannot overlap, and the
train only ever promotes its own recorded candidate SHA, never a temporary
batch ref.

## Living knowledge log

- 2026-08-18 (AGT-2671): extracted from the batch-gate-concept dossier as
  part of an operator-mandated dossier curation pass. No implementation
  exists yet; this page documents the settled mechanism design only.
