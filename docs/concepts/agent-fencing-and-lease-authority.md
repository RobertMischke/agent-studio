# Agent Fencing and Lease Authority

Status: Current implemented behaviour, extracted from the distributed-execution hardening dossier into permanent architecture documentation (2026-08-23).

Concurrent distributed execution requires knowing who may legitimately write, right now. Multiple runner processes, multiple review passes, and recovery/restart cycles can all believe they own the same task at overlapping times: a runner can go into standby mid-attempt, wake up later, and try to write against a task another runner has since taken over. Without an explicit mechanism, "last writer wins" is a race condition, not a decision. The platform solves this with three composed, increasingly coarse-grained tokens plus a delivery-dedup token, all issued and adjudicated by a single authority (the Task Server), never by the executor's own clock.

## The tokens

**A lease** is the base unit: a time-bounded, exclusive write permit for one executor on one attempt (a run or a review). The Task Server issues it on Claim/Acquire, extends it only on a full-match Heartbeat/Renew before expiry, and ends it on cooperative Release or on Expire (server-clock timeout with no renew). Expiry does not imply the holder's process is dead - only that it has lost the right to write. Code: `backend/Features/Runner/RunLeaseService.cs`, `LeaseEndpoints.cs`; runner side `runner/LeaseHeartbeat.cs`, `runner/DurableLeaseAuthority.cs`.

**A fence** answers the next question: among possibly several lease generations issued for the same task over time, which is the current one? It is a per-task, monotonically increasing integer (`Fence` on `AttemptLeaseDto`, `backend/Shared/Attempts/AttemptAuthorityModels.cs`) that survives server restarts. Every new grant or takeover mints a strictly higher fence; every server-mediated write path checks the presented fence against the task's current fence before applying any effect. A late write carrying an old fence is rejected as `StaleFence`, permanently (`backend/Features/Runner/AttemptAuthorityService.cs`) - this is what makes the "zombie writer" scenario safe: a runner that resumes after standby and tries to heartbeat or complete with its old fence is turned away, with no lane, log, or integration effect, regardless of how legitimate its local state looks.

**The authority epoch** generalizes this one level further, across the whole authority store rather than one task: a global, persisted claim-generation counter (`AttemptAuthorityService.AuthorityEpoch`, `RotateAuthorityEpoch`) that a recovery or controlled rotation can increment. New leases issued after a rotation carry the new epoch; a write is then checked against both its task-local fence and its epoch, so a write from a stale global generation cannot be mistaken for a write from the current one. Rotation is deliberately a soft drain, not a kill switch: a lease issued before rotation keeps its own older epoch and may continue to renew, write, and settle under that identity until it is released, expires, or is superseded by a higher fence. This avoids a requeue storm on every recovery while still giving the server a clean generational boundary to reason about.

**An idempotency key**, finally, is orthogonal to authority: it identifies one specific delivery (a claim, a heartbeat, a completion) so that a retried request is recognized as a duplicate and not re-applied, rather than granting or renewing any permission by itself.

Layered together, a write is only accepted if the attempt is known and current, the authority epoch matches, the fence matches, the lease is unexpired and owned by this executor, and the idempotency key has not already been consumed - in that order.

## Git authority: the platform owns history

On top of this authority layer sits a separate, harder boundary: the platform, not any worker agent, owns Git history. Workers may inspect Git and edit files in their assigned worktree, but may not commit, push, merge, or rewrite history; the platform reviews the result and performs the actual commit and managed push (see [Task Integration and the Worktree/Merge Workflow](task-integration-and-merge-workflow.md) for the commit/merge mechanics this feeds). A command guard blocks mutating Git verbs, and a before/after HEAD comparison catches anything that slips through, routing it to quarantine rather than silent acceptance instead of committing it. Model trust, tracked per model from observed evidence (TE-13), adjusts how closely that boundary is supervised - never whether it exists.

## See also

- [Agent fencing](../operations/haertung-verteilte-ausfuehrung/agent-fencing.html) and its [interactive diagram](../operations/haertung-verteilte-ausfuehrung/agent-fencing-diagram.html) - the visual/interactive companions to this page, with the worker/platform trust boundary and evidence-driven oversight strategy in full.
- [Distributed execution hardening](../operations/haertung-verteilte-ausfuehrung/index.html) (source dossier) - lease/fence/authority-epoch mechanics (previously an appendix at its own section 9) and the dated incident narrative (zombie-lease incident, Grade-D wave, Token Economy shared-checkout collision).
- [Runner stability & incident chronicle](../operations/haertung-verteilte-ausfuehrung/historie.html) - dated incidents that exercised this mechanism; kept separate from this page because incidents are evidence, not mechanism.
- `docs/operations/git/commit-push-doctrine.md` - the canonical commit/push implementation detail referenced by `agent-fencing.html`.
