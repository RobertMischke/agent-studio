# Fencing, leases, and authority in distributed execution

> **What this page is.** A concept and glossary page for the four terms that
> guard every write in distributed task execution: **Lease**, **Fencing
> Token**, **Authority Epoch**, and **Idempotency Key**. It explains *why*
> each exists, *how* they compose, and points at the shipped code and the
> Timeline UI chips that surface them. The full implementation-level contract
> (the actual store, endpoints, and restart-recovery rules) is owned by
> [`docs/system/domains/runner.md`](../../system/domains/runner.md) — read
> that for the system of record; read this page for the mental model.

## Why four different guards

A single expiring lease is not enough to keep two writers from stepping on
the same task. A laptop can go into standby: its runner process, heartbeat
timer, and any in-flight HTTP calls all freeze while the Task Server's clock
keeps running. When the lease expires, a second runner may legitimately take
over. But when the first laptop wakes up, its process is still alive and can
still try to send a heartbeat, a completion, or an integration call — a
**zombie writer**. An expiry timestamp alone cannot make that late call
harmless; something has to make it *provably stale* regardless of when it
arrives. That is what fencing is for, and it needs to compose with two other
concerns: recovering cleanly after a Task Server restart (authority epoch),
and making retried network calls safe (idempotency key).

The short version, in order of scope:

| Concept | Question it answers | Scope | Not to be confused with |
|---|---|---|---|
| **Attempt ID** | Which durable run/review attempt does this belong to? | one attempt | Not a lease and not an ordering signal; an attempt can survive a lease change. |
| **Lease** | Who may write, until when? | one attempt | Not proof of death — after expiry the old process may still exist. |
| **Fencing token** | Is this the newest lease generation for this task? | one task, monotonic | Not a write counter and not the attempt number. |
| **Authority Epoch** | Which global claim generation of the whole authority store issued this lease? | the entire authority store | Not per-task; a rotation does not by itself bump every task's fence. |
| **Idempotency Key** | Has this exact delivery already been processed? | one operation on one attempt | Not a permission — a fresh key does not make a stale writer current. |

Only the combination binds a write unambiguously to its attempt and its
current authority. This table and the mental model below are drawn from the
11 August 2026 addendum in the source dossier
([§9, "Lease, Fence und Authority Epoch lesen"](../../operations/haertung-verteilte-ausfuehrung/index.html#lease-fence-epoch)),
written to explain the Timeline UI's `Fence`, `Authority Epoch`, and
`Idempotency Key` chips.

## Lease lifecycle: claim, renew, release, or expire

A lease is the time-bounded, exclusive write permission of one executor for
one attempt. The Task Server is the only authority that issues and evaluates
leases, and it always uses its own clock — a runner's local clock can never
extend an expiry or authorize a takeover.

1. **Claim / acquire.** The runner sends the task, the executor identity, and
   an idempotency key. If the task is eligible and no other lease is
   currently valid, the Task Server atomically mints a lease ID, an attempt
   ID, an expiry, a fencing token, and stamps the current authority epoch.
2. **Heartbeat / renew.** Before expiry, the same executor presents its
   attempt ID, lease ID, fence, authority epoch, and a *new* idempotency key.
   Only a full match extends `expiresAt`. A transport failure is not
   confirmation that the renewal landed — the caller must not assume success.
3. **Release.** On completion, the current holder releases the lease
   cooperatively. Completion and release are themselves fenced, idempotent
   deliveries, so a retried call cannot double-apply a terminal effect.
4. **Expire.** If renewal does not arrive, authority ends by server time.
   After expiry, a takeover may receive a strictly higher fence. This does
   **not** mean the old process is dead — only that it is no longer
   authorized to write.

## Fencing token: the monotone guard against zombie writers

The fence is a per-task, monotonically increasing integer, persisted across
Task Server restarts. "Monotone" means: after fence 9 there can be a fence
10, but there can never again be a *new* write authority minted at fence 9 for
that same task. Every server-mediated write path checks the current attempt,
fence, epoch, and lease state before any side effect is applied.

Why a lease alone is not enough: an expiry timestamp does not remove a
process. If Runner A goes into standby right after being granted fence 9,
wakes up after Runner B has already taken over at fence 10, and then sends a
late heartbeat or completion still carrying fence 9, the Task Server rejects
both calls — `409 StaleFence` / `Superseded` — with no lane, log, or
integration effect. A previously unseen idempotency key on that stale call
does not help; a fresh key cannot resurrect an old fence.

**Boundary of this protection.** The fence only guards write paths that the
Task Server itself validates. A directly usable Git credential can still push
outside this contract. That is why attempt-specific immutable result refs,
protected branches, expected-SHA verification, and credential isolation
remain necessary *in addition to* fencing, not instead of it.

## Authority Epoch: the recovery generation above individual fences

The Authority Epoch is a global, persisted claim-generation counter for the
entire authority store (not per task). A recovery event or a controlled
rotation can advance it; every *newly issued* run or review lease after that
point carries the new epoch. This stops a write from an older authority
generation from being passed off as a write from a current attempt.

The current rotation behavior is a deliberate **soft drain**, not a global
kill switch: a lease that was validly issued before the rotation keeps its
own older epoch and may continue to renew, write, and settle under that exact
identity until it is released, expires, or is superseded by a higher fence.
This avoids a mass requeue on every recovery. The epoch is therefore a
supersede boundary *above* individual generations — it replaces neither lease
expiry nor the per-task fence, and rotating it does not by itself bump the
fence of every in-flight task.

## Distinguishing fence, epoch, and idempotency key

| Property | Fence | Authority Epoch | Idempotency Key |
|---|---|---|---|
| Scope | one task | the whole authority store | one operation on one attempt |
| Changes on | every new lease grant or takeover | a controlled claim-generation rotation | every logically new delivery |
| Stays the same on | a heartbeat of the same lease | a normal claim without rotation | a retry of the same delivery |
| Guards against | a stale or split writer | mixing authority generations | double-applying a delivery after a timeout or lost response |
| Grants write permission? | only combined with the current attempt, lease, and epoch | only as part of an issued lease | no |

**Check order for a write:** is the attempt known and current? does the
authority epoch match this exact attempt? does the fence match? is the lease
still valid and owned by this executor? has this idempotency key for this
operation already been processed? Only after all five checks pass does the
side effect happen. A repeated key yields `Duplicate`; a stale fence yields
`StaleFence`; a superseded attempt yields `Superseded`; a wrong epoch yields
`AuthorityEpochMismatch`; an expired lease yields `LeaseExpired`.

## A different "fencing": worker/platform Git boundary

This page is about **write-authority fencing** between competing executors.
There is a separate, differently-scoped use of the word "fencing" in this
codebase:
[`docs/operations/haertung-verteilte-ausfuehrung/agent-fencing.html`](../../operations/haertung-verteilte-ausfuehrung/agent-fencing.html)
documents the rule that worker CLI agents never commit or push — only the
platform mutates Git history, regardless of model trust level. That is an
authoring-boundary rule enforced by a command guard and a HEAD before/after
check, not a lease/token mechanism. Do not conflate the two when searching
for "fencing" in this codebase.

## How this relates to the other architecture pages

- **System of record (implementation-level):**
  [`docs/system/domains/runner.md`](../../system/domains/runner.md) documents
  `AttemptAuthorityService` + `RunLeaseService` + `AttemptAuthorityEndpoints`
  (AGT-2182): the actual persisted store for `RunAttempt`, `ReviewAttempt`,
  and immutable `ReviewSubject` records, including restart-recovery rules
  (re-adoption requires every unchanged authority field), the
  `agent-studio/results/<attempt>/fence-<n>/<result-sha>` ref convention, and
  how remote completion carries the same attempt/fence/epoch/idempotency
  tuple end to end.
- **Target topology:**
  [`docs/concepts/distributed-agent-studio-target-architecture.md`](../distributed-agent-studio-target-architecture.md)
  covers leases and fencing only briefly — one row of its lifecycle matrix
  ("Fenced run lease... new holder increments fencing token") and the general
  disconnect/restart contract. It does not explain the Authority Epoch or the
  idempotency-key distinction. Read this page for that detail, and that page
  for how fencing fits into the wider Task Server / Runner / Studio split.
- **Older design proposal (partially superseded):**
  [`docs/concepts/parallel-task-execution.md`](../parallel-task-execution.md)
  §8.2C sketched a generic lease/fencing-token contract for multi-system
  execution back in 2026-05-31, before Authority Epoch existed as a concept.
  Its fencing-token description is compatible with what shipped, but it
  predates and does not use the Authority Epoch or idempotency-key vocabulary
  above.
- **Source dossier:**
  [§9, "Lease, Fence und Authority Epoch lesen"](../../operations/haertung-verteilte-ausfuehrung/index.html#lease-fence-epoch)
  (AGT-W7, dated 11 August 2026, marked as an accepted/active addendum). Its
  three inline sequence diagrams (normal lease lifecycle, standby
  zombie-writer rejection, epoch rotation with soft drain) are the primary
  visual reference; this page summarizes their prose in English.
- **Target-architecture subpage:**
  [`docs/operations/haertung-verteilte-ausfuehrung/target-architecture/contracts.md`](../../operations/haertung-verteilte-ausfuehrung/target-architecture/contracts.md)
  lists "lease/epoch semantics" as one of the three core contracts (alongside
  the Task API and the result-SHA handoff) at a one-paragraph summary level;
  this page is the fuller explainer that sits underneath it.

## Living knowledge log

Append new findings here, newest on top.

- **2026-08-21.** Page created during a documentation-transfer extraction
  pass over the AGT-W7 dossier. §9 (dated 11 August 2026) was marked as an
  accepted/active addendum, unlike most of the rest of that dossier (earlier
  sections are mostly in-progress or still-open and were left out of this
  page). Cross-checked against `docs/system/domains/runner.md`'s
  `AttemptAuthorityService` description (AGT-2182) to confirm the terms match
  shipped code, not just the dossier's explainer language. Confirmed
  `docs/concepts/distributed-agent-studio-target-architecture.md` does not
  substantially duplicate this content (one lifecycle-matrix row only), so
  this was written as a full page rather than a link-only addendum.
