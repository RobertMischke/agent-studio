# Distributed Agent Studio target architecture

Status: canonical target picture, revised 2026-07-22. This page defines the
intended separation of Agent Studio, the Task Server, and the host-local
orchestrator carried by Agent Runner. It is a product and architecture target,
not a claim that every boundary already ships.

This page is the coordinating source for the implementation tasks listed in
[Delivery map](#12-delivery-map). Detailed concepts remain authoritative inside
their narrower scopes, but they must not contradict the lifecycle, authority,
security, or workspace-organization decisions here.

## 1. Decision summary

Agent Studio for Software is one product made from three independently
deployable runtime components:

1. **Agent Studio** is the replaceable human surface. It is primarily the
   Angular application and, where needed, a thin surface-specific BFF. Closing
   it must not stop work.
2. **Task Server** is the always-on global control plane and durable card system
   of record. It owns users, workspaces and project identity, tasks, lanes,
   cross-host order, project-to-host policy, global gates, durable leases and
   fences, events, artifacts, audit, and management APIs.
3. **Host Orchestrator** is the host-local control and execution plane shipped
   by Agent Runner. It owns host capacity, local admission, its accepted-work
   queue, repository and worktree lifecycle, coding-agent and tool processes,
   and post-processing for attempts executed on that host. It reports those
   facts to Task Server through a versioned contract.

Shared contracts and orchestration libraries are important modules, but they
are not a fourth runtime product.

The release-defining scenario is:

> Start Task Server and Agent Runner as independently supervised services.
> Start a task from Agent Studio, close Agent Studio completely, and prove that
> the task, its post-processing, and its durable state continue. Reopen Agent
> Studio, or open another authorized client, and observe the same task history.

The Host Orchestrator is autonomous only inside authority already granted by
Task Server. It may continue an admitted attempt, its already-authorized
post-processing plan, and durable local evidence across a central restart. It
must not invent a task, change a global lane, bypass a global gate, extend its
own authority, or accept new work while Task Server is unavailable. This is not
a second mutable board and not the isomorphic multi-writer store explored by
AGT-2122.

This makes orchestration deliberately two-level. Global orchestration and card
truth live centrally. Decisions that require truthful host knowledge run on the
host and are reported, not reconstructed centrally from flags.

## 2. One word, four different failure cases

"Offline" is too ambiguous to be an architecture requirement. The product uses
these explicit states instead:

| State | Required behavior |
|---|---|
| **Studio detached** | Fully supported. Task Server and Runner continue. Another Studio or future phone surface can attach. |
| **Host transport interrupted** | No new work acceptance. Continue already-admitted work only within its persisted authority envelope, buffer bounded events and artifacts, and reconcile the same lease and fence after reconnect. |
| **Task Server restarting** | Host processes continue. Task Server restores leases and fences before admission, reconciles matching host reports, and does not create a duplicate attempt. |
| **Task Server unavailable past the authority envelope** | No new task mutations, work acceptance, reissues, or global decisions. The host stops before its explicit offline authority expires and retains replayable evidence. |
| **Host unavailable** | Task Server remains readable and manageable. Cards show `at host X`, the accepted time, and last reported phase. Recovery requires fencing and positive no-overlap evidence, not a guessed flag. |

"Central process restarted, admitted work continues" is a committed
requirement. "Task Server absent indefinitely, new work starts" remains out of
scope.

## 3. Target topology

```text
                         HTTPS, one Task Server origin
                                      |
                 +--------------------+--------------------+
                 |                                         |
        Agent Studio clients                    Task Server management
      Angular SPA, optional BFF                 bootstrap/recovery console
                 |                                         |
                 +--------------------+--------------------+
                                      |
                         +------------+-------------+
                         |      TASK SERVER         |
                         | control plane and truth  |
                         |                          |
                         | users and authorization  |
                         | workspaces and projects  |
                         | tasks and initiatives    |
                         | global orchestration     |
                         | leases and fencing       |
                         | events and artifacts     |
                         | audit and management API |
                         +------------+-------------+
                                      |
                  versioned host-orchestrator exchange
                                      |
              +-----------------------+-----------------------+
              |                                               |
      +-------+--------+                              +-------+--------+
      | HOST ORCH. A   |                              | HOST ORCH. B   |
      | capacity/queue |                              | capacity/queue |
      | admission      |                              | admission      |
      | repo/worktrees |                              | repo/worktrees |
      | CLI/post steps |                              | CLI/post steps |
      | local journal  |                              | local journal  |
      +-------+--------+                              +-------+--------+
              |                                               |
              +---------------- Git remotes ------------------+
                         code channel, not task truth
```

Browsers never connect directly to a Host Orchestrator for task mutations. Host
Orchestrators initiate outbound connections to Task Server. Git carries code;
the Task Server API carries card authority, policy, work availability, leases,
events, evidence metadata, and reported host state. No component shares task or
queue files across machines.

## 4. Component boundaries

| Component | Owns | Must not own |
|---|---|---|
| **Agent Studio** | Human interaction, board and project views, task authoring, orchestration observation, admin UI, explicit operator commands, local UI preferences | Durable task state, lease authority, CLI process handles, hidden filesystem writes, long-running service liveness |
| **Task Server** | Canonical resources and IDs, Task API, lanes and provenance, cross-host order, project eligibility policy, global gates, durable leases/fences, event and artifact ingestion, audit, backup/restore metadata, management API | Host-local capacity decisions, clone or worktree lifecycle, coding-agent processes, host toolchains, Angular component state |
| **Host Orchestrator / Agent Runner** | Host registration, capacity and capability probes, local admission, queue order for work it accepted, project workspaces, CodingAgentRunner integration, process containment, local post-processing, typed state and evidence reports, bounded local journal | Global task namespace or order, user passwords, arbitrary lane changes, policy changes, main/release authority, new work while central authority is absent |
| **Shared contracts** | Versioned resource DTOs, event envelopes, command/result schemas, compatibility rules, deterministic policy primitives | Network hosting, persistence technology, UI projections, OS process ownership |

### Where orchestration runs

Task Server owns the global state machine. It decides which work is available,
which hosts are eligible, the order between hosts, and whether a global gate may
advance a card. It does not select a host slot or infer whether a checkout can
push.

Each Host Orchestrator owns a durable local state machine for work it has
atomically accepted. It evaluates current host facts, places accepted work in
its local queue, owns the checkout and process lifecycle, and executes the
server-authorized post-processing plan. Its decisions become card truth only
after a fenced, idempotent report is accepted by Task Server.

Work availability is therefore two-phase. Task Server publishes eligible work
permits in global order. An eligible host performs local admission and
atomically accepts a permit. Acceptance binds the card to that host, mints or
continues its fenced authority, and makes `at host X since T` centrally visible.
Task Server does not push an assignment into a guessed free slot.

The central card projection keeps these host-placement substates without
duplicating the host queue:

| Central substate | Source of truth |
|---|---|
| `available` | Task Server policy and global order; no host owns it. |
| `at-host-queued` | Successful permit acceptance plus the latest host queue report. |
| `execution-running` | Fenced host report for the accepted run. |
| `post-processing-running` | Fenced host step report from the immutable attempt plan. |
| `host-reconciling` | Server restart or report gap while the prior host binding is preserved. |
| `process-unknown` | Host disappearance or identity mismatch requiring positive containment evidence. |

The lane remains a central field throughout. Host reports supply placement and
phase facts; they never move a card directly.

## 5. Independent lifecycles

### Agent Studio lifecycle

- The web client can open, close, reload, and upgrade without affecting a run.
- It discovers capabilities and server version after login.
- Live views resume from a durable event cursor rather than an in-memory UI
  subscription.
- A thin BFF, if retained, may aggregate view models but cannot become task
  authority or process owner.

### Task Server lifecycle

- Runs as its own service, container, or supervised process with a dedicated
  configuration, data directory, version, health surface, and release cadence.
- Starts and stops independently of Agent Studio and every Runner.
- Owns schema/store migrations, backup, restore, retention, audit, and version
  compatibility.
- Persists lease and fence authority so a service restart does not silently
  admit a duplicate attempt.
- Supports drain/read-only maintenance modes before shutdown or migration.

A server API cannot be the only mechanism that starts its own dead process.
The operating system service manager or container platform owns start, stop,
and restart. The management API owns readiness, drain, maintenance intent,
safe-shutdown preparation, version, migration, and diagnostics.

### Host Orchestrator lifecycle

- Runs as a system service with its own version and configuration.
- Registers an explicit service identity and reports versioned capabilities,
  configured and effective capacity, queue state, active attempts, and faults.
- Survives Studio shutdown because Studio is neither its parent process nor its
  network peer.
- Persists accepted work, authority envelopes, acknowledgements, and outbound
  report sequence numbers in a host-local journal that is not task truth.
- Reconnects to Task Server with bounded backoff, lease reconciliation, and
  idempotent event replay.
- Drains before upgrade. Existing attempts and authorized post-processing may
  finish while new acceptance stops.
- Never treats a lost heartbeat as proof that another process is dead.

## 6. Disconnect and restart contract

| Scenario | New acceptance | Current process | Writes and evidence | Recovery proof |
|---|---:|---|---|---|
| Studio closes | Yes | Continues | Normal server writes | Reopen from durable cursor |
| Studio host powers off | Yes | Continues | Normal server writes | Another authorized client sees the same state |
| Host loses Task Server briefly | No | Continues within its persisted authority envelope | Bounded local journal; no unacknowledged global transition is treated as settled | Reconnect with host instance, report sequence, lease, and fence; replay idempotently |
| Task Server stays unreachable past authority boundary | No | Cancel and reap safely | Preserve unsent evidence locally; mark unresolved on reconnect | Fence validation and operator-visible reconciliation |
| Task Server restarts | Paused until authority is restored and reconciled | Continues under the unchanged envelope | Durable fence prevents duplicate acceptance; reports wait locally | Same host instance and matching lease/fence resume; mismatch enters `process-unknown` |
| Host Orchestrator service restarts | No for that host until its journal is reconciled | Reattach only with positive same-instance containment evidence | Task Server keeps prior reports and last accepted sequence | Same-boot service proof, changed boot ID, or infrastructure fencing |
| Second host accepts the same permit | Atomic accept chooses one host | No overlap allowed | Losing accept receives a typed conflict | Durable lease plus positive no-overlap gate |

The host journal is an outage and restart buffer, not a local Task API. It stores
accepted permit identifiers, authority envelopes, local queue state, typed
events, artifact chunks, report sequence numbers, acknowledgements, and
idempotency keys. It does not store a freely mutable clone of the board.

## 7. Task Server API and management story

### Resource API

The public contract is resource-based and path-free:

```text
/api/v1/workspaces/{workspaceId}
/api/v1/projects/{projectId}
/api/v1/projects/{projectId}/tasks/{taskKey}
/api/v1/runners/{runnerId}
/api/v1/runs/{runId}/events?after={cursor}
```

Absolute client paths and `watchPath` are compatibility inputs only. Public
identity uses stable server, workspace, project, task, run, and runner
IDs. Server-private storage paths and Runner-private checkout paths never cross
the wire as identity.

Commands carry idempotency keys, actor identity, expected resource version,
and, where relevant, execution or control fences. Event streams support cursor
replay and bounded retention.

### Host orchestration exchange

Host orchestration is a separately negotiated contract carried over the Task
Server API. Its first schema is `host-orchestrator/v1`. The schema version is
independent of the HTTP path version so additive Task API changes do not
silently change host semantics.

The target resource routes are:

```text
PUT  /api/v1/runners/{runnerId}
POST /api/v1/runners/{runnerId}/reports
POST /api/v1/work-permits/{permitId}/accept
POST /api/v1/runs/{runId}/reconcile
```

Registration declares the host-orchestrator contract range and capabilities.
Reports and acceptance carry `schemaVersion: "host-orchestrator/v1"` in the
body as well as the normal Task API protocol header. Reconciliation is allowed
only for an already persisted authority envelope and can never mint a new one.

Every exchange is an authenticated, idempotent pair:

| Host report | Meaning |
|---|---|
| `schemaVersion`, `hostId`, `instanceId`, `sequence`, `observedAt` | Identifies one monotonic fact report from one supervised host generation. |
| `capacity.configured`, `capacity.effective`, `capacity.active`, `capacity.queued`, `capacity.free` | Host-owned slot truth. Task Server validates arithmetic but does not recalculate it from lanes. |
| `capabilities[]` | Versioned toolchain, clone, push, post-step, and containment capabilities. Project readiness carries a reason and observation time, not one green boolean. |
| `work[]` | Accepted permit, task/run identity, lease and fence, local phase, queue position, process identity, and last local activity. |
| `faults[]` | Typed host, repository, worktree, process, or toolchain fault with affected scope and recovery hint. |
| `acknowledgedCommands[]` | Idempotent acknowledgement of central policy or recovery commands. |

Task Server replies with:

| Central response | Meaning |
|---|---|
| `acceptedSequence` | Highest host report durably folded into central projections. |
| `contract.minimum`, `contract.maximum` | Supported host-orchestrator schema range. |
| `policyVersion`, `mode` | Exact project eligibility and `active`, `drain`, `reconcile`, or `rejected` state for this host. |
| `availableWork[]` | Ordered, expiring permits the host may evaluate. A permit is not an assignment and occupies no local slot. |
| `commands[]` | Fenced cancellation, drain, reconciliation, or global-gate result. Commands cannot require an unreported host capability. |

Permit acceptance is a separate atomic call. It carries the permit id, host and
instance id, last accepted report sequence, project policy version, and an
idempotency key. A successful response returns the durable run id, lease, fence,
offline authority deadline, and immutable execution plus post-processing plan.
Only then may the host enqueue or start the work. A stale permit, policy, report
sequence, or competing acceptance fails without creating local authority.

Compatibility fails before work acceptance. A server requiring
`host-orchestrator/v1` answers an older host with HTTP 426 and the typed code
`host-orchestrator-contract-unsupported`, plus its supported range. During the
migration only, project policy may explicitly retain `legacy-claim/v1`; there
is no silent fallback when a project has switched to host orchestration.

Report snapshots are facts, while events remain the durable history. A newer
snapshot replaces the central host projection only when its sequence is higher.
Replayed events and artifacts retain idempotency keys and fences. This lets the
central UI show the last reported fact and its age without presenting inference
as live host state.

### Management API

Every Task Server exposes an authenticated management surface for:

- bootstrap state and first-owner creation;
- users, password reset, sessions, roles, and project membership;
- Runner service identities, enrollment, token rotation, revoke, drain, and
  retirement;
- workspace, project, and repository bindings;
- server health, version, protocol compatibility, storage usage, and active
  migrations;
- backup creation, restore verification, retention, archive sweeps, and orphan
  diagnostics;
- audit search and export;
- maintenance mode, read-only mode, and safe shutdown preparation.

The first live contract is rooted at `/api/v1/management`. `GET /status` and
`GET /diagnostics` provide the shared read model. `POST /commands` accepts a
command kind, dry-run flag, exact confirmation, and idempotency key. Applied
commands append durable evidence to the server audit ledger. Backup archives
live outside the active data directory and are hash-checked and opened before
the server reports success. Runner rows are projections of the existing Runner
service-identity registry; this API does not maintain another client list.

### Management consoles

Two surfaces use the same API:

1. **Agent Studio administration** is the rich daily management console.
2. **Task Server bootstrap/recovery console** is a small server-hosted surface
   for first-owner setup, health, credentials, backup/restore, and recovery when
   Agent Studio is unavailable.

Neither console bypasses authorization or writes server files directly.
The server-hosted console is available at `/recovery`. It can establish the
first owner or an authenticated owner session, then uses the same management
routes as Agent Studio. It reports the systemd, container, or service-manager
lifecycle boundary and never offers a fictional self-start operation.

## 8. Security baseline for an internet-reachable server

The product remains deliberately small and single-organization. That does not
make an internet-facing task system safe without real identity boundaries.

### Human identity

- Task Server owns users.
- Initial authentication is username plus password, not enterprise SSO.
- Passwords use a modern memory-hard password hash with unique salts.
- Browser clients use secure, HttpOnly, SameSite session cookies over HTTPS.
  The Angular application never stores a reusable password or long-lived bearer
  token in local storage.
- State-changing browser requests have CSRF protection.
- The first useful roles are `owner`, `operator`, and `viewer`, with project
  membership where needed. A future OIDC provider can replace login without
  changing resource authorization.

### Host Orchestrator identity

- A Host Orchestrator never receives a person's password.
- One-time enrollment creates a distinct, revocable service principal.
- The resulting credential is scoped, rotatable, and stored in the host's
  protected service configuration or secret store.
- A run records both the human or automation principal that requested the work
  and the host service principal that executed it. "Runs in a user's context"
  means auditable delegated authority, not shared credentials.
- Repository deploy keys and coding-agent CLI credentials remain host-local
  secrets and are never returned to Studio or Task Server logs.

### Transport and operations

- HTTPS is mandatory before the Task Server is reachable beyond loopback or an
  SSH-only development tunnel. WebSocket/event endpoints use the same origin
  and authentication.
- A reverse proxy or ingress owns certificates, modern TLS, request limits, and
  secure headers. Plain HTTP may exist only on a private loopback hop.
- Login and enrollment endpoints are rate-limited and audited.
- Secrets are redacted from task output, diagnostics, and audit records.
- Backup and restore procedures include identity, task state, event history,
  artifacts, audit, and fence continuity.

`X-Client-Id` remains useful attribution. It is not authentication.

### Principal and data flow

```text
Browser
  username/password once
  <- Secure HttpOnly session cookie
  -> same-origin /api and /hubs with CSRF on mutations

Owner
  -> one-time enrollment code
Host Orchestrator
  enrollment code once
  <- one-time service bearer
  -> outbound HTTPS report, permit accept, lease reconcile, events, artifacts

Task Server
  password hashes, session hashes, host service secret hashes
  task state, fenced leases, dual-principal run audit
```

### Lifecycle matrix

| Identity or credential | Created by | Plaintext visibility | Normal use | Expiry | Rotation or reset | Revocation |
|---|---|---|---|---|---|---|
| First owner | Anonymous one-time bootstrap over HTTPS, only while no users exist | Password exists only in the browser request | Login and owner administration | Account does not auto-expire | Owner changes password | Another owner disables account; final active owner is protected |
| Human password | Owner at user creation or reset; user on change | Creation/reset response only for temporary value | Login only | Policy-driven, no scheduled expiry in the small-org baseline | Change verifies current password; reset revokes sessions and forces change | Disable user and revoke sessions |
| Human session | Task Server after successful login | Cookie only; HttpOnly prevents Angular access | Same-origin Studio API and hubs | Sliding idle and absolute deadline | New login creates a new independent session | Logout, password reset, disable, or expiry |
| CSRF value | Task Server with session | Secure SameSite cookie readable by Angular | Header on browser mutations | Session lifetime | Reissued with a new session | Invalid when its session ends |
| Runner enrollment code | Owner | One creation response and operator handoff | One enrollment call | Default 15 minutes, maximum 24 hours | Create another code | Single use or expiry |
| Runner credential | Task Server during enrollment or rotation | One response only, then Runner secret store | Only endpoints allowed by explicit scopes | Optional credential deadline | Add overlapping credential, prove daemon, revoke old credential | Individual credential or whole Runner identity |
| Fenced authority envelope | Task Server after atomic permit acceptance | Authenticated host response plus local journal | One run and its authorized post-processing plan | Bounded offline deadline plus online renewal | Matching host instance reconciles the same lease and fence | Release, deadline plus positive containment proof, or fenced takeover |

### Authorization matrix

| Capability | Owner | Operator | Viewer | Host Orchestrator |
|---|---:|---:|---:|---:|
| Read allowed projects | Yes | Yes | Yes | Claimed task input only |
| Mutate allowed projects | Yes | Yes | No | Scoped run protocol only |
| Manage users and Runner identities | Yes | No | No | No |
| Connect Studio SignalR stream | Yes | Yes | Yes | No |
| Report state and accept permitted work | No | No | No | Required per-route scope |

Detailed controls live in
[security requirements](../operations/security/requirements.md), and the
operator deployment is
[networked Task Server](../operations/setup/networked-task-server.md).

## 9. Organizing one large product

Recursive subprojects are not the recommended model. They mix navigation,
authorization, configuration inheritance, repository layout, and delivery
ownership into an unbounded tree.

Use three explicit planning levels instead:

| Level | Purpose | Example |
|---|---|---|
| **Workspace / solution** | Groups component projects that form one product, and owns their shared defaults and access policy | `Agent Studio for Software` |
| **Component project** | Own backlog, task key, lifecycle, release/deployment target, host eligibility policy, and health | `Agent Studio`, `Task Server`, `Agent Runner` |
| **Task** | One owned change in exactly one component project | `TS-24 Extract durable lease store` |

A cross-component **initiative** or epic groups tasks from several component
projects. It does not own execution settings and does not pretend that all work
belongs to one board project. A future portfolio view may aggregate several
workspaces, but it is not another configuration-inheritance level.

### Board behavior

- A component board shows only its own tasks by default.
- A workspace board is an aggregate solution view over its component projects.
- Solution-wide initiatives, dependencies, and milestones are visible without
  copying cards.
- The selected scope is explicit: workspace, initiative, or component
  project.
- Aggregate counts equal the visible child scopes.

### Project is not repository

A component project is a planning and deployment boundary, not a synonym for a
Git repository. Repository binding is separate:

```text
ComponentProject
  -> one or more RepositoryBinding records
     { repositoryId, optional subpath, branch model, execution role }
```

During extraction, Agent Studio, Task Server, and Agent Runner may share one
monorepo while already having separate projects and release lifecycles. Later
they may move to separate repositories without changing task identity or the
workspace/component hierarchy.

For execution safety, one task initially selects exactly one primary repository
binding. Multi-repository tasks require an explicit run-plan contract and are
not inferred from workspace membership.

This supersedes the strict `Project 1 -> 1 Git repository` assumption in the
earlier Project Relationship Model / Branch-Aware Wiki concept (since retired;
this page absorbed it). The branch and provenance rules remain valuable; only
repository cardinality
and project identity need revision.

## 10. Code and release organization

The runtime split should be visible in source before repositories are split:

```text
Agent Studio solution
  studio-web/            Angular surface and generated API clients
  studio-bff/            optional surface aggregation only
  task-server/           Task API, control plane, persistence, auth, management
  agent-runner/          host orchestrator, local journal, execution adapter
  contracts/             versioned wire contracts and compatibility fixtures
  orchestration-core/    deterministic state and policy primitives
```

CodingAgentRunner remains the process and CLI-protocol library used by
`agent-runner`. The Host Orchestrator must not recreate a second unstructured
Codex invocation path.

Each deployable publishes its own version and compatibility range. Contract
tests pin supported combinations. Release order is additive first: Task Server
accepts both old and new clients, then Runners and Studio upgrade, then obsolete
protocols are removed after telemetry proves they are unused.

Repository extraction is optional and follows operational need. Independent
service lifecycle, board ownership, versioning, tests, and deployment do not
wait for a repository split.

## 11. Acceptance and failure tests

The target needs a topology harness, not only unit tests. It starts each runtime
as a separate process with separate configuration and data directories.

The required test program is distributed across the delivery tasks. Owner tags
below identify the task that must supply the scenario and evidence. AGT-2196
owns the cross-process harness and composes the runtime scenarios; it does not
absorb the management-console or project-model implementations.

Required scenarios:

1. **Client-off golden path (AGT-2196):** start a real task, stop Agent Studio
   and its BFF, observe Runner and Task Server complete the lifecycle, then
   reconnect and replay the full history.
2. **Task Server process independence (AGT-2192, AGT-2196):** restart Studio
   repeatedly without a Task Server restart. Restart Task Server while a host
   runs work and prove the local process and post-processing are not its child.
3. **Host disconnect (AGT-2183, AGT-2196):** interrupt transport, prove no new
   permit acceptance, bounded local journaling, authority-deadline safety stop,
   reconnect, lease reconciliation, and idempotent replay.
4. **Durable fencing (AGT-2182, AGT-2196):** restart Task Server during an active
   attempt, reconcile the same host lease and fence, and prove no second host
   overlaps and stale completion cannot win.
5. **Host crash (AGT-2182, AGT-2185, AGT-2196):** kill the Host Orchestrator and
   its process group, verify centrally visible `at host X since T`, honest
   `process-unknown`, containment proof, and safe recovery.
6. **Authentication (AGT-2193, AGT-2196):** unauthenticated reads and mutations
   fail; viewer cannot mutate; revoked Runner cannot renew; password/session
   and CSRF behavior pass.
7. **TLS deployment (AGT-2193, AGT-2196):** Studio and Runner connect through
   the real HTTPS origin, including event-stream upgrade and certificate
   renewal rehearsal.
8. **Management recovery (AGT-2194):** bootstrap, backup, restore verification,
   drain, maintenance mode, and credential rotation work without filesystem
   edits.
9. **Compatibility (AGT-2192, AGT-2170, AGT-2196):** supported mixed versions
   work; unsupported Task API or host-orchestrator contract versions fail before
   a permit is accepted.
10. **Project scope (AGT-2195):** workspace aggregate and three component boards
    show the same cards without duplication; cross-project initiative and
    dependency links resolve.
11. **Host post-processing:** a task executed remotely records its post-processing
    step executions on the same host identity, with fenced results visible after
    reconnect.
12. **Reported state fidelity:** central host capacity, queue, active work, and
    faults match the host journal at the same accepted report sequence. Stale
    reports display their age and are never rendered as a live inference.

### Release proof

AGT-2196 supplies the release-blocking cross-process proof in
[`task-server.Tests/TopologyTests.cs`](../../task-server.Tests/TopologyTests.cs).
The harness launches the built Task Server, Studio BFF, and Agent Runner
assemblies as sibling OS processes with isolated ports, data roots, Runner
workspaces, and configuration. It uses a real local Git remote and a bounded
fixture agent process. The fixture is a deterministic CLI participant, not a
second task store or scheduler.

The CI gate runs four real-process scenarios:

1. The client-off golden path starts a task through Studio, stops and restarts
   the BFF three times, completes while detached into the deployed backend's
   auto-review handoff, then reconnects from a fresh BFF and replays the
   canonical task history.
2. The brief transport path interrupts only Runner-to-server connectivity,
   keeps a second task unclaimed, retains bounded output, then reconnects and
   proves typed event replay is idempotent.
3. The outage path holds Task Server unavailable beyond the Runner renewal
   safety boundary, observes cancellation, restarts authority into
   `process-unknown`, rejects a contender, kills the old Runner, records
   positive no-overlap proof, and admits one higher-fence replacement.
4. The network path runs Task Server over real HTTPS with separate Studio and
   Runner bearer credentials, proves anonymous history and event reads fail,
   and proves authenticated Runner ingestion plus Studio cursor replay.

Canonical replay is
`GET /api/v1/projects/{projectId}/tasks/{taskIdentity}/history?after={cursor}`.
It returns stable task and run identities, server-sequenced typed events,
artifact metadata, task/run audit rows, and the last returned cursor. The
Runner maps plain stdout to `agent.message`, structured tool or command frames
to `tool.trace`, and Runner-owned diagnostics to bounded trace event kinds.
Task Server adds typed completion and post-processing evidence while the
deployed backend remains the review and reissue authority. Runner
disconnect/reconnect, Task Server unavailable, `process-unknown`, Runner
unavailable, and no-overlap events keep the failure classes distinct in replay.
Idempotency keys remain unique per run and payload.

The executable operator command and release decision rule are in the
[stable release contract](../operations/stable-release-contract.md#three-component-topology-gate).
Protocol fixtures remain the supported mixed-version source of truth. The same
CI step runs them and rejects an unsupported protocol with HTTP 426 before
registration or claim.

## 12. Delivery map

This table is the human-readable synchronization point. Every new task created
from this target must link this page in its prompt. This page links the stable
task key back. Task status remains authoritative on the board.

Until the workspace/component model exists, all delivery slices stay in the
current Agent Studio project and AGT-2129 epic. AGT-2195 owns the migration and
key-alias decision. Do not duplicate or renumber these tasks in the meantime.

| Area | Existing or planned task | Relationship to target |
|---|---|---|
| Decoupled UI and session lifecycles | AGT-2091 | Supplies detach/reattach and process-ownership rules. |
| Remote daemon and persistent connectivity | AGT-2004, AGT-2005 | Existing Runner service and connectivity foundation. |
| Remote host registry and management | AGT-1921, AGT-2094 | Existing host management and onboarding slices. |
| Task Server management UI and URL onboarding | AGT-1924 | Existing management surface; must consume the separated server API. |
| Remote execution hardening | AGT-2129 | Execution-plane epic. Its former "everything on Runner" wording is narrowed by this target. |
| Autonomous/isomorphic-store exploration | AGT-2122 | Historical exploration. Superseded for the current requirement by the fail-closed Task Server authority here. |
| Typed Runner event integration | AGT-2170 | Prevents raw CLI transport from leaking into host-specific parsers. |
| Standalone Task Server extraction | AGT-2192 | New control-plane service and migration boundary. |
| Network security and identity baseline | AGT-2193 | Human login, Runner service identity, TLS, authorization, and audit. |
| Live management API and recovery console | AGT-2194 | Replaces AGT-1924 seed/simulation with an authenticated server contract. |
| Workspace/component project model | AGT-2195 | Solution workspace, component boards, initiatives, and repository bindings. |
| Client-off topology acceptance harness | AGT-2196 | Release-blocking proof of independent lifecycles. |
| Host-local orchestration authority split | AGT-2229 | Replaces central host inference and slot assignment with the two-level authority and contract defined here. |
| Host-owned capacity reports | AGT-2230 | First independently shippable migration slice; moves capacity truth to the host without changing task pickup yet. |

## 13. Delivery sequence

1. **Freeze the two-level authority model.** Record central card truth,
   host-local operational ownership, restart reconciliation, and the
   workspace/component hierarchy.
2. **Make public identity path-free.** Finish stable resource IDs and remove
   client paths from new contracts.
3. **Land security before exposure.** Replace localhost-only requirements with
   the human and service identity model, then verify HTTPS deployment.
4. **Extract Task Server.** Give it an independent process, persistence,
   migrations, health, backup/restore, and management API while retaining local
   compatibility.
5. **Connect Agent Studio only by API.** Remove in-process task-store and Runner
   ownership from the surface runtime.
6. **Report host capacity.** Add `host-orchestrator/v1` negotiation and cyclic
   configured/effective/active/queued/free reports. Keep legacy pickup behavior
   during this slice so it is useful and reversible on its own.
7. **Make post-processing claimable.** Represent post-processing as fenced work
   from the attempt plan and let the executing host claim and report each step.
8. **Move queue and admission to the host.** Publish ordered eligible permits;
   let a compatible host admit and atomically accept them. Disable legacy
   assignment per project only after a compatible host is observed.
9. **Replace inferred host state.** Drive central projections and UI exclusively
   from sequenced host reports, with explicit staleness and reconciliation.
10. **Pass the client-off golden path.** This is the first complete target
   milestone.
11. **Add workspace/component organization.** Split boards and lifecycles without
   requiring a repository split.
12. **Operate on the public network.** Complete certificate, credential,
   monitoring, rotation, backup, and recovery rehearsals before declaring the
   central URL stable.

### Migration slice exit gates

| Slice | Useful state after this slice | Required proof before enabling it | Rollback boundary |
|---|---|---|---|
| 1. Capacity | Central operators can see configured, effective, active, queued, and free host slots at a known report sequence; current pickup semantics are unchanged. | Change host capacity, observe the next cyclic report, and prove central values exactly match the host while an older contract is rejected from the new route. | Stop consuming capacity reports; legacy claim remains untouched. Persisted reports stay as audit evidence. |
| 2. Post-processing | An executing host can drain post-processing for its own attempt without moving card authority off Task Server. | Record host identity on every step, restart Task Server mid-step, reconcile, and prove one fenced result is accepted. | Stop issuing host post-step permits; unaccepted steps return to the central compatibility worker. Accepted steps finish on their host. |
| 3. Local queue and admission | Compatible hosts choose from centrally ordered permits, reject work they cannot safely run, and order accepted work locally. | Race two hosts on one permit, prove one acceptance and one fence, then restart central and prove no duplicate process. | Drain new permit acceptance per project. Already accepted work remains host-owned; only unaccepted work returns to `legacy-claim/v1`. |
| 4. Reported state | UI and management APIs show the host's sequenced facts and explicit staleness instead of mirrored flags. | Compare central projection with the host journal at the same sequence for active, queued, free, and faulted states, including a disappeared host. | Re-enable the compatibility projection only for legacy hosts. Never replace a newer host report with an inferred value. |

## 14. Explicit non-goals

- Accepting new work or changing global card state while Task Server is
  unavailable.
- A second mutable Task API or board store on each Runner.
- A shared task, queue, clone, or worktree filesystem between central and host
  services.
- Central reconstruction of live host state from lane membership, lease age, or
  cached booleans.
- Recursive subprojects with inherited settings and permissions.
- Enterprise SSO, multi-tenant billing, or a general workflow engine.
- Browser-to-Runner task mutations.
- Using a user's password as a Runner credential.
- Treating lease expiry alone as proof that an old process is dead.
- Requiring a repository split before service and project lifecycles separate.
- Making Agent Studio or its BFF an always-on dependency of execution.

## 15. Open decisions with recommended defaults

| Decision | Recommended default |
|---|---|
| First Task Server host | A small independently backed-up VM or service host, not the operator workstation. |
| Human auth | Local username/password accounts with secure server sessions; OIDC is a later adapter. |
| Runner credential | One-time enrollment followed by a rotatable scoped service credential. |
| Studio BFF | Optional and stateless; never authority. Start co-hosted, preserve a removable boundary. |
| Solution hierarchy | One solution Workspace -> Component Project, with cross-project initiatives and no recursive project tree. |
| Repository layout | Keep the monorepo until independent release or ownership pressure justifies extraction. |
| Task Server restart | Continue admitted work under the persisted bounded authority envelope; reconcile before central admission reopens. |
| Extended Task Server outage | No new work; bounded host journal and fail-closed authority stop. |
| Direct Runner UI attachment | Not required for the first target. Reconnect through the surviving Task Server. |
