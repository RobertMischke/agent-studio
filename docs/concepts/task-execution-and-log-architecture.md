# Task Execution & Log Architecture (concept)

Status: concept / pre-implementation. Resolves the log-contention bug (Windows
file-lock from shared `cli-output.log` + orphaned writers) AND sets up a
distributed Server/Runner split. Written 2026-05-30.

## 1. Terminology — kill the Job/Task ambiguity (one model)

| Term | Meaning | Cardinality |
|------|---------|-------------|
| **Task** | the unit of work the user creates and tracks through the lanes (the card). Long-lived. | the card |
| **Run** | one execution attempt of a Task by an executor (initial, reissue, retry). Has a unique **runId (GUID)**. | Task → many Runs |
| **Stream** | output of ONE process/writer within a Run. A Run may have **several concurrent Streams** (parallel post-step LLMs / aspects). Has a **streamId**. | Run → 1..N Streams |

Logs are always keyed by **(taskId, runId, streamId)** — never a single shared
file. "Job" is renamed to "Task" everywhere (the missing big rename). Internally
we stop saying Job; the card is a Task, an attempt is a Run.

## 2. Two apps + shared (the split)

- **Task-Server (API)** — standalone, remote-capable, own project/folder/lifecycle.
  Owns durable state + aggregated logs + history (DB). Authority for reading back
  logs **for completed tasks, remote viewers, cross-machine, and history**.
  Ingests Stream chunks via API.
- **Runner / Executor** — own project/folder/lifecycle. Spawns the CLIs locally,
  produces the output Streams locally, leases Tasks from the Server, syncs up.
  Can run on a different machine than the Server.
- **Shared** — contracts/models (Task, Run, Stream, log-chunk DTOs, lane states).

## 3. Log lifecycle — resolving the "doppelt gemoppelt"

The tension: writing a local file AND streaming to the server AND reading back
looks like the same work done twice. Resolution = **two phases, one source of
truth per phase** (they never do the same job at the same time):

- **In progress (live) → LOCAL, direct.**
  The executor writes each Stream to its **own append-only local file**:
  `…/<task>/runs/<runId>/<streamId>.log`. The live progress UI reads these
  **directly/locally** — fast, no round-trip, and the executor never depends on a
  (possibly remote) server to operate. Chunks are shipped to the Server
  **async, fire-and-forget** for aggregation, but the live read does NOT block on it.
- **After / history / remote → SERVER, aggregated.**
  The Task-Server holds the merged, durable copy. Reads for completed tasks,
  other machines, and history go to the Server (DB-backed). Local Stream files
  can be GC'd after they've synced.

So the Server is **not** where logs are "made" — it's where they're aggregated
and served for non-local / historical / cross-machine access. While a Task is in
progress, the **direct local access** is the source of truth. This is the
"bottom line" the user asked for.

## 4. Multiple concurrent writers (the actual root cause of the lock bug)

A Run can have **N parallel Streams** (e.g. 4–5 aspect/post-step LLMs producing
output at once). Today everything funnels into ONE `cli-output.log`, so:
- concurrent writers contend for one handle, and
- an interrupted/orphaned writer keeps the handle open → on Windows the next
  open throws `IOException: used by another process` → the run fails with empty
  output (exactly the failures observed).

Fix: **one append-only file + one lock per `streamId`, never shared.** The merged,
interleaved view (by timestamp) is computed **on read** — locally for the live
view, by the Server for history — not by everyone writing the same file. One
writer owning one file means no cross-writer contention by construction.

## 5. Crash / zombie tolerance (falls out of the model)

- Per-Stream files: an orphaned writer only affects its own Stream, never poisons
  the whole Task.
- **Lease model**: the Runner leases a Task/Run from the Server with a heartbeat.
  A crashed Runner's lease expires → the Run is requeued. No stuck local lock, no
  global halt. (Supersedes the current "leftover `.pickup-lock.json` + circuit
  breaker halts everything" failure mode.)
- On every Run exit (success/fail/crash/stop): kill the CLI **process tree** and
  release handles — but with per-Stream files this is damage-control, not the
  primary defense.

## 6. Migration sequence (big steps)

1. **Rename Job → Task** across the backend (internal identifiers first; API
   routes `/api/tasks`→`/api/tasks` as a coordinated breaking change with the FE).
2. **Extract `Shared`** (contracts/models) into its own project.
3. **Per-Stream local log files + lock-per-stream** (kills the file-lock bug
   immediately, even before the split).
4. **Split executor into `Runner`** project; introduce the **lease API**
   (Runner ↔ Server).
5. **Server log ingestion API** + DB aggregation; switch history/remote reads to
   the Server; keep live reads local.
6. Server becomes deployable standalone / remote.

Steps 1–3 already remove the current breakage; 4–6 deliver the distributed vision.

## 7. Current distributed-orchestration steps

AGT-2122 tracks the broader distributed-orchestration direction. AGT-2141 is
the first concrete multi-repository step: the server now places project git
coordinates on each remote claim, and the daemon maintains one shared clone per
project instead of relying on a single host-wide origin. Lease authority and
task state remain server-owned; repository credentials remain host-owned.
