# Parked-Card Recall (Wiedervorlage)

Status: implemented (AGT-2492). Owner surface: `backend/Features/Tasks/ParkedCards/`.

A card parked in a human-decision lane records **what it is waiting for** in a
machine-readable form, a background sweep re-checks that condition, and a card
whose blocker is provably gone is **reported** to the board. It is never
re-queued automatically.

## Why this exists

AGT-2220 was parked on 2026-07-29 at 22:07 with the reason
`4x ReviewInfra/BaselineUnavailable - parked for an operator decision, no auto rerun`.
On 2026-08-02 at 19:14 the documented remedy was executed (the card branch was
merged fresh onto `develop`, `cd7923b2c`), so the precondition was gone. The card
was not re-listed: until 2026-08-03 there is no entry for AGT-2220 in the backend
logs, the bus logs, or the timeline after 2026-07-29. Four days of standstill on a
card that was ready.

Nothing was broken in the sense of throwing an error. The park reason was prose,
and prose is not something a machine can re-evaluate. There was no mechanism that
reacted to "blocker resolved", and no Wiedervorlage.

## The three parts

### 1. A parked card carries a machine-readable blocker

Entering `5-human-review` or `5e-escalated` writes `parked-blocker.json` into the
job folder:

```jsonc
{
  "version": 1,
  "blockerType": "review-subject-unmaterialisierbar", // escalation category, or operator-decision
  "condition": {
    "kind": "git-ancestor",
    "parameters": {},
    "description": "The card branch carries the current integration branch, so a review baseline can be materialized again."
  },
  "lane": "5-human-review",
  "parkedAt": "2026-07-29T22:07:00Z",
  "reason": "4x ReviewInfra/BaselineUnavailable - parked for an operator decision, no auto rerun",
  "lastEvaluation": { "status": "blocked", "at": "...", "detail": "..." },
  "reportedRecallableAt": null
}
```

The freetext reason is preserved verbatim next to the structured condition; the
structure is an addition, not a replacement.

The write happens in `TaskStateMachine.RecordParkedBlocker`, next to the
`lane_changed` ledger row. That is the single choke point every lane move passes
through, so all ~15 park paths (the escalation funnel, the remote review park, the
UI-iteration gate, an operator drag) get a marker without each call site having to
remember. Leaving a parked lane clears the marker.

`ParkedBlockerCatalog` is the pure mapping from park category to condition:

| Blocker type | Condition kind | Clears when |
|---|---|---|
| `review-subject-unmaterialisierbar` | `git-ancestor` | The card branch contains the integration branch, so a review baseline can be materialized (the AGT-2220 case) |
| everything else | `manual` | Only a person can decide |

The mapping is deliberately conservative. A category leaves `manual` only when a
probe can decide it from facts the platform already owns. Claiming a checkable
condition that nothing actually evaluates would reproduce the exact failure this
feature removes: a card that looks handled and is not.

Adding a condition kind is a triple in one change: a constant in
`ParkedBlockerConditionKinds`, a branch in `ParkedBlockerProbe`, and a row in
`ParkedBlockerPolicyTests`.

### 2. A sweep reports resolvable cases

`ParkedCardRecallSweep` walks every parked card, evaluates its condition through
`IParkedBlockerProbe`, and produces one `ParkedCardRecall` per card. Three
verdicts:

- `recallable` - the precondition is provably gone.
- `blocked` - it still holds.
- `undeterminable` - no probe could decide (a manual blocker, an unreadable
  repository). Reported separately so the board can tell "still blocked" from
  "nobody can tell". The probe fails to `undeterminable`, never to `recallable`:
  an operator acts on a recall signal, so it has to be trustworthy.

A resolved blocker is announced once on the card's timeline as
`parked_blocker_resolved`, carrying the age and why the blocker is considered
gone. Re-announcement is suppressed until the condition goes back to blocked, so
a 30-minute sweep does not bury the card's history under ~48 identical rows a day.

`ParkedCardRecallSweepHostedService` runs it every 30 minutes by default
(`Supervisor:ParkedCardRecallSweepIntervalMinutes`). `GET /api/parked-cards`
evaluates on demand, so an operator who just cleared a precondition sees it
without waiting for the next tick.

**Report-only, by construction.** `ParkedCardRecall` has no target lane and no
requeue instruction anywhere on it. "No auto rerun" was a deliberate decision by
whoever parked the card, and a resolved infrastructure precondition does not
overrule it - it only means the card is worth a person's attention again.
Re-queueing stays the existing operator lane move, which opens a fresh
review-attempt epoch through `OperatorReviewRequeueService`. The sweep performs no
lane write at all, which is why it is not on the `HumanReviewVerdictDriftTest`
whitelist.

### 3. Aging is visible

`TaskInfo.ParkedBlocker` projects the marker onto every parked card:
blocker type, condition kind and description, the original reason, the latest
recall verdict, and `parkedForSeconds` - how long the card has been sitting in
the lane. It is a read-time projection of the marker file and is never persisted
on `task.json`.

Cards parked before this feature existed have no marker. The sweep backfills one
from the lane-entry stamp, so a legacy park ages visibly instead of staying
invisible - AGT-2220 itself is in that class.

## Key code

- `backend/Features/Tasks/ParkedCards/ParkedBlockerRecord.cs` - condition
  vocabulary, verdicts, the durable record.
- `ParkedBlockerCatalog.cs` - pure category-to-condition mapping.
- `ParkedBlockerMarker.cs` - sidecar read/write plus the board projection.
- `ParkedBlockerProbe.cs` - the only part that touches the outside world.
- `ParkedCardRecallPolicy.cs` - pure decision and announcement folding.
- `ParkedCardRecallSweep.cs` / `ParkedCardRecallSweepHostedService.cs` - the sweep.
- `ParkedCardEndpoints.cs` - `GET /api/parked-cards`.
- Tests: `backend.Tests/ParkedCardRecallSweepTests.cs` (acceptance) and
  `backend.Tests/ParkedBlockerPolicyTests.cs` (matrix plus a real-Git
  reproduction of the AGT-2220 remedy).

## Related

- [tasks domain map](../system/domains/tasks.md) - lanes, task metadata, the
  Result transition invariant.
- [operator decision surface](../operations/decision-surface/README.md) - what an
  operator does with an escalated card.
