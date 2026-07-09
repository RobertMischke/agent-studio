# ADR-0060 - The `/api/runner/lease` API is a fenced, server-authoritative run lease with runner identity (the prepared successor to `.pickup-lock.json`)

**Status.** Proposed. First productive slice of [ADR-0059](../adr-archive.md#adr-0059---remote-execution-is-a-major-goal-linux-runner-hosts--a-central-task-server-url-2026-07-07)'s "phases 3+ replace `.pickup-lock.json` with the lease contract for remote runners." On acceptance it folds into [adr-archive.md](../adr-archive.md) as ADR-0060.

**Date.** 2026-07-08.

**Scope.** RM-3 / AGT-1937 (Runner-Split A). This ADR + its implementation make the previously **unused** `/api/runner/lease` API productive: a fenced run lease (`RunLeaseService`), a runner identity with a token precursor (`RunnerIdentity`), and the §8.2C split-brain acceptance tests. It **prepares** the `.pickup-lock.json` replacement; it does **not** yet cut the live pickup loop over to it (see [Consequences](#consequences)).

---

## 1. Context

The `/api/runner/lease/{acquire,renew,release}` endpoints existed as thin glue over the disk-backed `.pickup-lock.json` primitive (`PickupLockFile`, [ADR-0044](../adr-archive.md#adr-0044)), but nothing called them — they were a placeholder for "the explicit lease/runner contract" that ADR-0044 deferred. [ADR-0059](../adr-archive.md#adr-0059---remote-execution-is-a-major-goal-linux-runner-hosts--a-central-task-server-url-2026-07-07) then committed remote execution as a goal, and [`parallel-task-execution.md` §8.2C](../../concepts/parallel-task-execution.md) specified the lease contract a multi-system runner needs:

- Acquire mints a `leaseId`, increments a **monotonic `fencingToken`**, records the `runnerId`, and sets `expiresAt` from the **server** clock.
- Heartbeat extends the lease only when lease id **and** fencing token still match.
- Expiry lets a new runner acquire a **higher** fenced lease; the old runner's later writes are rejected as stale. *"TTL without fencing is not sufficient."*

The disk lock cannot satisfy this on its own: a `.pickup-lock.json` file uses same-host **pid liveness** (not a fencing token) to decide a lock is live, and its expiry is only load-bearing for a remote owner whose pid cannot be probed. That is the right single-machine guard, but it has no monotonic fence to reject a woken-up stale runner's writes, and the file — not a server — is the authority.

## 2. Decision

Make `/api/runner/lease` a **fenced, server-authoritative run lease**, and give a runner a **stable identity** to present to it.

1. **`RunLeaseService`** (`backend/Features/Runner/`) — an in-memory lease authority keyed by task, sibling to the already-fenced per-project `IntegrationLeaseService` ([ADR-0052](../adr-archive.md#adr-0052)):
   - One holder per task; a monotonic `fencingToken` minted per task on every grant (retained across release, so re-acquire is strictly higher).
   - `TryAcquire` → `Acquired` / `AlreadyOwn` (idempotent, same lease id + token) / `Held` (a live foreign holder is **rejected, not queued** — a run lease is a race, not a queue).
   - `Renew`/`Release` reject `Expired` and `StaleToken`.
   - `IsCurrent(taskKey, leaseId, fencingToken, runnerId)` is the write gate §8.2C requires on every state-affecting write.
   - TTL default **120s** (parity with the ADR-0044 pickup lease it supersedes), clamped `[30s, 10m]`; the server clock is authoritative.
2. **`RunnerIdentity`** (`backend/Features/Runner/`) — `RunnerId` + `RunnerName` + `Hostname` + `BackendName` + a **token precursor** + `ProtocolVersion`. The backend-name convention matches the pickup-lock owner (`Runner:BackendName`, else dev/stable from `Environment:IsDev`) so the lease owner and the disk-lock owner name the same runner during the cutover.
   - The **token precursor** (`Token-Vorstufe`) is a deterministic, opaque credential derived from the runner id (+ optional operator secret). It is a stable "who" field for the wire contract and audit trail **now**; it is **not** a security boundary. The split-brain guard is the fencing token, not this credential. Promoting it to a real least-privilege issued token later is a swap of the derivation, not a contract change.
   - `ProtocolVersion` gives the server the §8.2C "minimum-version checks before lease acquisition" hook.
3. **`/api/runner/lease` rewrite** — `acquire` validates the task exists, stamps the caller's `RunnerIdentity` onto a partial request (so a local caller need only name the task, while a remote runner's own identity wins), and returns `RunLeaseService`'s response verbatim. The old unfenced `LeaseWireModels` are removed (they had no other consumer).

## 3. Consequences

- **Prepared, not cut over.** `.pickup-lock.json` (`PickupLockFile`) remains the live same-machine pickup guard in `ProjectRunner`/`TaskRunnerService`. This ADR ships the fenced authority + identity + tests that the cutover will switch onto; flipping the pickup loop to lease through `RunLeaseService` is a separate slice so the change is reviewable and the running pickup loop is not destabilized in this step.
- **In-memory today.** A server restart forgets leases, so restart takeover is immediate rather than gated by stored expiry. §8.2C's "server restart preserves lease rows" requires persisting lease rows on the shared Task Store; `RunLeaseService` is the fenced contract that store will implement behind. This is called out so the gap is explicit, not silent.
- **Split brain is closed at the contract level.** The acceptance tests (`RunLeaseServiceTests`) cover the §8.2C scenarios: two runners race the same task and only one gets a lease; a lapsed holder is overtaken by a higher fenced lease and its stale heartbeat, release, and `IsCurrent` writes are all rejected.

## 4. Reasoning style

Reuse the shape that already works: the per-project integration lease is fenced, unit-tested, and lives in memory behind the same `Func<DateTime>`-injected clock — the run lease is its per-task sibling minus the queue, so the fencing semantics are proven rather than reinvented. Tie the split-brain guard to the property that makes it necessary (a stale runner can wake up and write) with a monotonic fence checked on every mutation, not to a TTL that a slow-but-alive runner could still race. Keep the risky part small: make the previously-dead API productive and fully tested, but leave the live pickup loop on the disk lock until a dedicated cutover slice — deliver the fenced authority without betting the running queue on it in one step.

## 5. Acceptance

- `backend.Tests/RunLeaseServiceTests.cs` — §8.2C: single-holder race, idempotent re-acquire, higher-fenced takeover + stale-holder rejection (heartbeat/release/`IsCurrent`), heartbeat extension, monotonic fence across release, per-task independence, validation.
- `backend.Tests/RunnerIdentityTests.cs` — identity resolution (defaults, explicit config, dev/stable) and the deterministic, opaque token precursor.
- `dotnet build backend/OrchestratorApi.csproj` green.
