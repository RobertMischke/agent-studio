# Decoupled agent-session lifecycles

Status: operator-vision concept and Workbench-family mockup, 2026-07-12. This
is an architecture target, not a claim that the production runtime already has
these lifecycle guarantees.

The system-level ownership and disconnect contract is coordinated by
[Distributed Agent Studio target architecture](distributed-agent-studio-target-architecture.md).
In particular, "detached" means Studio may disappear while Task Server and
Runner stay online. It does not authorize an autonomous Runner-side Task Store
when Task Server is unavailable.

Review artifact:
[interactive lifecycle Workbench-family mockup](mockups/decoupled-lifecycles.html).
It is browsable in the current Wiki. It is not yet a manifest-backed Project
Hub Workbench because the WB-1 folder contract and catalogue from AGT-2084 are
not implemented; promotion must create the single canonical
`docs/workbenches/decoupled-lifecycles/` source rather than duplicate this HTML.

## Decision in one paragraph

An agent run must stop being a child-lifecycle detail of an Agent Studio UI or
backend. The runner host owns a durable **agent session** through a small
session holder; a separately restartable **consumer channel** publishes its
ordered event stream and arbitrates commands; any number of authenticated
**UI clients** attach and detach without owning either one. The existing remote
runner assignment and fenced task lease remain the authority for who may
execute a task. A new session identity, event cursor, and control lease make a
live execution observable and controllable without confusing browser presence
with process liveness.

## 1. Why three layers are necessary

Today the backend starts the CLI and retains process handles, output callbacks,
and in-memory run state. That is workable on one machine, but it makes backend
and process-tree failure boundaries too similar. A UI restart should already be
harmless in principle, yet a backend restart or harness process sweep can still
kill the CLI or leave descendants whose ownership is no longer provable.

The target separates three different questions:

1. **Is work still executing?** Answered on the execution host by the agent
   session holder and the fenced task lease.
2. **Can a consumer observe or command it?** Answered by the consumer channel,
   its event cursor, and a short control lease.
3. **Is a particular screen open?** Answered only by that UI client. It has no
   bearing on execution liveness.

This builds on, rather than replaces, the current architecture:

- [Remote execution product integration](remote-execution-product-integration.md)
  and the standalone [Linux runner](../operations/setup/linux-runner-host.md)
  already separate the execution host from the Task Server and assign a stable
  `executionRunner` per project. This is the executionRunner and remote-lease
  mechanism to extend, not a second scheduler.
- The standalone [Linux runner](../operations/setup/linux-runner-host.md)
  productively uses the fenced `/api/runner/lease` path. The proposed
  [ADR-0060](../architecture/decisions/proposed/adr-0060-fenced-run-lease-and-runner-identity.md)
  records its contract and history, including the important current gap that
  lease rows are in memory. The lease is execution authority, not a viewer
  connection, and detached lifecycles cannot ship until restart continuity is
  closed as specified below.
- The Wiki's
  [runner provenance and host handoff contract](completion-review-and-remote-runner-stability.html#provenance)
  owns the user-visible ordered runner route, the deferred assignment-switch
  default, the positive no-overlap requirement, and the rule that a cross-host
  continuation creates a new process and attempt identity.
- [Remote Dauerbetrieb A/B](../operations/setup/linux-runner-host.md), delivered
  through AGT-2004/AGT-2005, and the
  [persistent connection runbook](../operations/setup/remote-runner-persistent-connection.md)
  establish systemd or an equivalent service supervisor and reconnectable
  host-to-server transport. A long-running service must not be smuggled into a
  disposable task session.
- The client/host registry work from AGT-1921/AGT-1922 and current runner
  telemetry are the discovery and health basis. The registered `X-Client-Id`
  remains attribution only, exactly as documented in the remote-ready kickoff.
  It is not authentication.
- [Orchestrator in-app](orchestrator-in-app.md) and the context-keyed
  [orchestrator session registry](../product/orchestrator-chat.md) provide the
  precedent for stable logical session identities and resumable transcripts.
- [Experiment Workbenches](experimentier-workbench.md), created by AGT-2084,
  provide the repository-owned, self-contained review pattern followed by this
  Wiki-browsable mockup. The manifest-backed Project Hub object remains future
  WB-1 work; this concept references that parallel work without depending on
  its production implementation.
- AGT-2076's overnight forensics remains the warning: process-tree cleanup,
  backend restarts, and orphan reconciliation must preserve provable ownership.
  A detached process with no holder is not success.

## 2. The three-layer model

### Layer 1: agent session and holder

The **agent session** is a logical run with a stable `sessionId`, one task key,
one runner identity, one execution lease and fencing token, and zero or one live
CLI process generation. It is not synonymous with a provider conversation id
or an operating-system PID.

A small **session holder** runs as an independently supervised per-session unit
on the execution host. The runner control service creates and discovers that
unit through the host service manager, but does not parent it. Restarting the
runner control plane therefore does not terminate a healthy holder or CLI. The
holder owns:

- process spawn into a dedicated process group or equivalent containment;
- stdin and stdout/stderr handles;
- one monotonic holder source offset across the whole logical session, with
  process generation carried as event metadata;
- a bounded host-local append journal and unacknowledged output spool;
- provider session/resume metadata and the current process generation;
- the host boot id and a fresh holder-incarnation id for every service start;
- heartbeat and reconciliation with the fenced task lease;
- terminal-process reaping, including descendants.

The holder must not be launched as a child of the UI, an SSH login, a Playwright
test, a disposable backend request, or the restartable runner control process.
On Linux the natural baseline is a systemd-managed per-session service or scope
with a durable descriptor that the runner service can rediscover. `nohup`,
tmux, or an untracked detached PID is not the ownership model: it may keep a
process alive but cannot prove who may command it, fence stale writers, or
reconcile terminal state.

The holder reports five distinct conditions:

- `running`: a live contained CLI generation owns the current execution fence;
- `waiting`: the CLI is alive but awaiting a provider, tool, permission, or
  user response;
- `interrupted-recoverable`: no CLI process is live, but the logical session
  has a durable checkpoint/provider resume id and still owns or can reacquire a
  valid continuation admission;
- `interrupted-needs-action`: no CLI process is live and ownership is
  reconciled, but resume awaits an explicit operator or policy decision;
- `terminal`: completion, cancellation, failure, or supersession has been
  durably recorded.

### Layer 2: consumer channel

The **consumer channel** makes one agent session consumable without becoming
its owner. It is initially a logically independent, reconstructable stream
gateway inside the Task Server deployment, backed by the authenticated API and
runner connection. Its lifecycle is separate because it can restart and rebuild
without terminating the holder; separate deployment is not required in the
first slice. The runner pushes ordered events and accepts fenced commands over
an outbound connection, which fits remote hosts that expose no inbound agent
port.

The channel owns:

- `sessionId` discovery and current holder/host projection;
- one server-allocated canonical event sequence for ordered replay by
  `(sessionId, eventSequence)`, with generation and the holder's source offset
  recorded as event metadata and a bounded cursor;
- fan-out to zero, one, or many read subscribers;
- acknowledgement of holder source offsets so its spool can be compacted;
- one short **control lease** for state-changing input;
- idempotent command ids and an audit record of actor, client, lease, and
  result;
- explicit degradation states such as `holder-connected`, `reconnecting`,
  `replay-gap`, and `session-terminal`.

This channel does not replace the task run lease. The two leases protect
different invariants:

| Lease | Holder | Protects | Loss means |
|---|---|---|---|
| Task execution lease | runner/session holder | Exactly one execution authority may mutate run outcome. | Fence writes, then stop and reap the stale execution generation. |
| Control lease | authenticated user/client attachment | At most one interactive writer issues stdin, steer, or prompt-response input at a time. | Session keeps running; the client becomes read-only. |

Read attachments need no exclusive lease. A control lease is short, renewable,
visibly owned, and may be released on detach. A fenced administrative takeover
with an audited reason is a candidate contract, but its confirmation rule stays
at Robert's DL-0 halt. Losing a browser or network must never cancel the task
execution lease.

### Layer 3: UI clients

A UI client is a replaceable projection. It authenticates to the Task Server,
selects a session, requests replay after its last cursor, and optionally asks
for the control lease. Closing a tab performs a best-effort detach only. No
server or holder correctness depends on receiving that detach.

Multiple clients may watch the same stream from different computers. They see
the same session identity, execution host, process generation, channel health,
last durable event sequence, and control owner. Only the current control holder
sees enabled mutating controls; all other clients remain live read-only
observers.

## 3. Lifecycle matrix

`Survives` means the named logical object and its evidence remain valid. It
does not promise that an operating-system process can survive an OS reboot.

| Event | Agent session / holder | Consumer channel | UI clients | Required visible result |
|---|---|---|---|---|
| One UI tab closes or crashes | Survives unchanged. | Survives; drops one subscriber after timeout. | Other clients are unaffected. | Session still `running`; control lease expires or is released, never execution. |
| All UIs disconnect | Survives unchanged. | Runs with zero subscribers and continues ingest/ack. | None connected. | Reattach replays from a cursor; no synthetic cancellation. |
| A second computer attaches | Survives unchanged. | Adds a read subscriber. | Both see one canonical stream. | Existing controller keeps control; newcomer is read-only unless granted takeover. |
| UI deployment/restart | Survives unchanged. | Survives unchanged. | Clients reload and reattach. | Same `sessionId`, no duplicated CLI. |
| Stream gateway restarts inside a live Task Server | Holder and CLI continue; output spools locally. | Rebuilds from durable session metadata and holder replay. | Show reconnecting, then replay. | Lease authority never restarted; no duplicated CLI. |
| Task Server including lease authority restarts | Holder continues only until its conservative local stop-before deadline. | Unavailable, then rebuilds. | Show authority recovering. | Durable lease/fence continuity is required; otherwise admission stays closed through the restart quarantine described below. |
| Runner control service restarts but host stays up | Independently supervised holder and CLI survive. | Reconnects to the rediscovered holder unit. | Show reconnecting, then current state. | Same `sessionId` and process generation; no duplicate CLI. |
| Per-session holder unit fails | Process state is unknown until the service manager proves the whole containment unit stopped. | Marks holder unavailable and refuses commands. | Show holder recovery, not false running. | Same-boot reconciliation must reap or positively prove the old unit empty before resume. |
| Host heartbeat is lost | Process state is **unknown**, not dead. The holder may still be running behind a partition. | Marks holder unreachable; starts no replacement. | Show host offline and process unknown. | No resume or takeover based on heartbeat loss alone. |
| Execution host reboot is proven | A changed host boot id proves the old OS process ended. Durable logical identity and journal survive if the disk does. | Reconciles the new holder incarnation. | Show generation boundary and recovery status. | Auto-resume only when policy, provider resume support, containment proof, and fence permit; otherwise human action. |
| Network partition, holder to server | CLI continues only before its local stop-before deadline, then the holder attempts to cancel and reap its entire containment unit. | Shows holder disconnected and refuses commands. | Can read durable history but cannot assume live control. | Lease expiry fences Task Server writes, but no replacement starts until process termination or host fencing is positively proven. |
| Execution lease expires or is superseded | Holder cancels/reaps its CLI generation. | Retains history and reports superseded. | Stay attached read-only. | A stale holder cannot publish authoritative completion or accept commands. |
| Control client disappears | Session is unaffected. | Control lease expires after short TTL. | Observers remain. | Another authenticated client can acquire control after expiry. |

The hardest honesty boundary is host restart: the **logical session** can
survive, but the process cannot. Recovery is a new process generation using a
provider resume id and durable task context. UI wording must say `resumed after
host restart`, never imply that the original PID lived through the reboot.

### Restart and partition fencing prerequisite

The current in-memory lease authority described by proposed ADR-0060 is not
sufficient for this topology. A Task Server crash could forget a live lease and
grant a replacement while the old CLI still has external side effects. Fencing
only the eventual completion write would be too late.

Production cutover therefore requires one of these server behaviors, in this
preference order:

1. Persist the current lease row, monotonic per-task fence counter, server
   authority epoch, and expiry atomically. A restarted server restores them
   before admission and never lowers a fence.
2. If durable restoration cannot be proven, enter a fail-closed restart
   quarantine. Elapsed lease time can retire Task Server write authority, but
   cannot by itself authorize a replacement CLI. This is a recovery fallback,
   not the steady-state design.

Every successful renewal gives the holder a server expiry and enough timing
metadata to calculate a conservative deadline on its local monotonic clock. The
holder subtracts at least one heartbeat interval plus the transport/clock
uncertainty margin. If it cannot renew by that **stop-before deadline**, it
cancels and reaps the complete containment unit. This deadline limits the old
holder's intended behavior; it is not evidence that the holder ran, woke from
suspend, or successfully reaped every descendant.

No new generation starts on a higher fence or elapsed deadline alone.
Admission requires durable fence continuity **and positive no-overlap
evidence**: a changed boot id, same-boot service-manager proof that the old
containment unit is empty, or infrastructure-level fencing that makes the old
host unable to execute. If none is available, the task remains visibly
`process-unknown` and requires host recovery or operator fencing. This may
strand work during an ambiguous partition, which is preferable to silently
duplicating external CLI side effects. The execution fence protects
authoritative Task Server writes; positive termination or host fencing protects
effects outside the Task Server.

Host identity uses both the stable registered host id and an observed boot id.
Each holder service start adds a fresh incarnation id. A missing heartbeat only
means `process-unknown`. A changed boot id proves reboot. A new incarnation on
the same boot must first reconcile and reap any process group recorded by the
old incarnation before it may declare the old generation ended. A remote
machine fencing action must be infrastructure-backed and audited; changing a
registry flag is not proof. These proofs, not elapsed time, UI presence, or
reachability, gate resume.

## 4. Attach, detach, and command semantics

### Attach

An attach request carries authenticated principal, registered client id,
`sessionId`, and an optional last-seen canonical event cursor. The channel
returns a snapshot followed by server-sequenced events. Holder output arrives
with an idempotent holder source offset; the gateway assigns the canonical
event sequence when it durably accepts that output. Gateway-owned attach,
control, and audit events use the same server allocator, so offline holder
spooling can never collide with client events. If the cursor predates retained
data, the channel returns a `replay-gap` marker plus the newest durable snapshot
rather than silently skipping output.

Attach defaults to read-only. `requestControl=true` is a separate operation and
never implied by being the first or only viewer. A successful response carries
a control lease id, expiry, and monotonically increasing control fence.

### Detach

Detach removes one subscription and best-effort releases its control lease. It
does not park, cancel, pause, or complete the session. Disconnect detection is
timeout-based because tabs cannot guarantee a final request.

### Commands

Commands such as `send-input`, `steer`, `approve`, `cancel`, and
`request-resume` carry:

- authenticated actor and `X-Client-Id` attribution;
- `sessionId`, process generation, command id, and, for interactive input, the
  expected control fence;
- a typed payload with size and policy limits.

The gateway resolves and stamps the current execution fence from server-owned
state for outcome-affecting commands; it never trusts an execution fence
supplied by the browser. `send-input`, `steer`, and prompt-response input
require the control lease plus interactive-control capability. `approve`,
`cancel`, and `request-resume` use separate task-action capabilities and state
transition guards; they do not wait for or take over the interactive control
lease. In particular, an authorized emergency cancel cannot be blocked by a
healthy or malicious controller. These task actions still carry an idempotent
command id, current server-stamped execution fence, actor attribution, and an
audit record. The channel validates and durably records the command before
delivery. The holder
deduplicates by command id, rejects a stale generation or fence, and emits an
accepted/rejected/result event. Raw browser-to-host sockets and free-form host
commands are out of scope.

## 5. Identity and security

The central URL must authenticate people and machines before this topology is
exposed beyond a trusted tunnel. `X-Client-Id` answers "which registered client
sent this mutation?" It does not answer "is this caller allowed?" and must not
be promoted into a bearer secret.

The trust chain is:

1. a runner/holder uses a scoped machine credential bound to its stable host
   and runner registration;
2. a human UI uses the product's authenticated user/session identity;
3. authorization checks project/session visibility and command capability;
4. `X-Client-Id` supplies device/client attribution within that principal;
5. execution and control fences reject stale but previously legitimate actors;
6. every control mutation is audited without copying secrets or unbounded
   prompt content into general telemetry.

Remote holders initiate outbound connections. The browser talks only to the
central Task Server origin. Session streams must apply output size limits,
backpressure, retention bounds, secret-redaction policy, and per-principal
authorization on both initial attach and reconnect. A guessed `sessionId` is
not access.

## 6. Remote scenario

Robert starts a task assigned to `agent-runner-01`. The central Task Server
grants that runner the fenced task lease. Its systemd-managed holder creates
session `SES-481`, generation 1, launches Codex in a contained process group,
and streams holder-offset events over the runner's outbound connection. The
gateway durably assigns their canonical event sequence.

The first UI can be closed. `SES-481` continues because neither the browser nor
its backend request owns the process. On another computer Robert signs in,
opens the running task, and attaches after event sequence 1842. The channel
returns a snapshot and events from 1843 onward. If the first client still holds
control, the second watches read-only or requests an explicit takeover.

If only the stream gateway restarts, the holder spools output and reconnects.
If the Task Server lease authority also restarts, durable lease restoration or
the fail-closed restart quarantine prevents a second holder. If the remote host
only becomes unreachable, generation 1 remains `process-unknown`, even after
its deadline passes. A changed boot id, same-boot containment reconciliation,
or infrastructure-level host fencing is required to prove it cannot still run.
The restarted holder then reports the durable session record and lands by
default in `interrupted-needs-action`. Only Robert's accepted policy, provider
resume support, and fresh execution admission may start generation 2. The UI
preserves the one logical timeline and marks the proven restart boundary.

## 7. Storage and protocol sketch

This is a naming sketch for follow-up cards, not a frozen wire schema.

```text
AgentSession
  sessionId, taskKey, runnerId, hostId
  hostBootId, holderIncarnationId
  executionLeaseId, executionFence, authorityEpoch, stopBefore
  generation, providerSessionId
  lifecycleState, lastEventSequence, lastAckedHolderSourceOffset
  startedAt, updatedAt, terminalAt?, terminalReason?

SessionEvent
  sessionId, eventSequence, occurredAt
  kind, payload, durability, commandId?
  generation?, holderSourceOffset?

ControlLease
  sessionId, leaseId, controlFence
  principalId, clientId, expiresAt
```

The holder's local journal is the short outage buffer, not a second task store.
The Task Server remains authoritative for durable task state, terminal outcome,
and the canonical replay projection. Compaction occurs only after server
acknowledgement. Retention exhaustion produces a visible gap.

## 8. Non-goals and invariants

- A UI connection never owns, keeps alive, or implicitly cancels a CLI.
- A detached PID without a supervised holder is never considered healthy.
- One logical session admits at most one live process generation only after
  positive process-termination or host-fencing evidence. A Task Server fence
  alone guarantees only one authoritative Task Server writer.
- Lease-authority restart never opens admission until durable fence continuity
  and positive no-overlap evidence are proven.
- Heartbeat loss means process unknown. It never proves process death.
- Many readers are allowed; interactive writing is explicitly leased and
  fenced.
- The task execution lease and control lease are never collapsed into one.
- Task Server restart and execution-host restart are different failure classes.
- No claim is made that an OS process survives a host reboot.
- Provider session resume is a recovery tool, not proof of process continuity.
- This is not terminal multiplexing, a generic remote shell, peer-to-peer
  browser access, a new workflow engine, or a replacement for the orchestrator.

## 9. Decision gate and implementation chain

### Planning card with explicit Robert HALT

**DL-0: Accept the lifecycle contract and recovery promise (planning, user
HALT).** Robert reviews the linked Workbench and decides whether to accept this
recommended boundary: holder on each execution host, authenticated stream
gateway at the Task Server, many read attachments, one separately fenced
controller, and honest host-restart semantics as logical resume rather than
process survival. The card must stop for Robert's explicit `accept`, `revise`,
or `reject`. It must not create the implementation Epic automatically.

Questions deliberately left at that halt:

- Is automatic resume after host restart opt-in per project, or globally off
  until provider-specific resume probes are green?
- What replay retention target is acceptable before `replay-gap` is shown?
- Does control takeover require only capability plus reason, or a second
  confirmation while the current controller is still healthy?

### Executable chain, split by authorization

**Executable now:** DL-1 is a disposable in-memory protocol spike. It changes no
production launch, lease, or UI path and can run while Robert reviews DL-0.

**User HALT:** DL-2 through DL-6 are production feature cards. They may be
drafted now but must not start until Robert accepts or revises DL-0. No
production cutover may precede that decision.

| Card | Size | Dependency | Executable acceptance boundary |
|---|---|---|---|
| **DL-1: Session protocol fixture and failure simulator** | M | none | In-memory holder/channel/UI fixture proves UI death, second-client attach, gateway restart replay, replay gap, control expiry, process-unknown on heartbeat loss, and proven host-restart generation change. No production process launch. |
| **DL-2: Host session holder and durable local journal** | L | DL-0, DL-1 | A per-session service unit, independent of the runner control-process tree, owns a contained CLI generation, monotonic holder source offsets, boot/incarnation identity, ack-based spool, stop-before behavior, and service-manager-verified reaping. Killing or restarting UI, backend, or runner-control test processes does not kill or orphan the holder-owned fixture. |
| **DL-3: Authenticated session stream gateway, read-only** | L | DL-0, DL-1 | Machine-authenticated runner ingestion and authorized UI replay/fan-out work with zero or many subscribers; the server allocates canonical event sequence, and reconnect and retention gap are explicit. No stdin or cancel. |
| **DL-4: Multi-client attach UI** | M | DL-3 | A second authenticated browser attaches from a fresh context, sees host/generation/cursor health, and can close without affecting execution. Both themes, keyboard flow, narrow layout, and reduced motion are covered. |
| **DL-5: Control lease and fenced command path** | L | DL-2, DL-3 | One interactive controller at a time; expiry/takeover is visible and audited; duplicate or stale-generation commands are rejected; disconnect never cancels execution; authorized task cancel bypasses interactive control while remaining fenced, idempotent, capability-checked, and audited. |
| **DL-6: Durable lease restart barrier and provider resume policy** | L | DL-2, DL-3, provider probes | Persisted lease/fence/epoch survives authority restart, with fail-closed quarantine tested as fallback; host loss remains process-unknown until positive containment or infrastructure-fencing proof; no elapsed deadline alone admits a replacement; a new generation requires accepted policy and a fresh fence; unsupported resume lands visibly in `interrupted-needs-action`. |

Recommended delivery order is DL-1, then DL-2 and DL-3 in parallel, then DL-4
and DL-5, with DL-6 last. The production cutover is an Epic boundary because
holder ownership, stream authorization, process containment, and stale-writer
fencing fail together if partially substituted into the current run path.

## 10. Review status

This document and its Wiki-browsable Workbench-family mockup are the complete
deliverables for the concept card. They do not implement session detachment or
the AGT-2084 Project Hub catalogue in production.

Independent second-opinion passes on 2026-07-12 initially returned **no-go**
on restart fencing. It found that the current in-memory lease authority could
forget a live holder on Task Server restart and that heartbeat loss had been
presented as proof of host reboot. It also challenged partition deadlines,
browser-supplied fences, takeover policy, event ordering, two possible sequence
writers, the provisional Workbench location, and a simulator path that visually
assumed automatic provider resume.

The blocking findings were folded into the concept and Workbench:

- durable lease/fence/authority-epoch restoration is now a cutover prerequisite,
  with a fail-closed restart quarantine as fallback;
- the holder has a conservative monotonic stop-before deadline, but neither
  deadline expiry nor a higher fence is treated as proof of termination;
- replacement admission requires positive service-manager containment proof,
  a changed boot id, or audited infrastructure-level host fencing;
- each holder is its own supervised per-session unit and survives runner
  control-service restarts;
- stable host id, boot id, and holder incarnation distinguish partition,
  same-boot service restart, and proven host reboot;
- heartbeat loss is `process-unknown` and cannot trigger replacement;
- the gateway, not the browser, stamps execution authority on privileged
  commands, with stronger capabilities for task actions; authorized emergency
  cancellation does not depend on the interactive control lease;
- the server is the only canonical event-sequence allocator; holder output has
  a separate idempotent source offset and generation metadata;
- the host-restart simulation ends at `interrupted-needs-action` instead of
  assuming automatic resume.
- DL-1 is the only immediately executable disposable spike; DL-2 through DL-6
  remain behind Robert's explicit halt.
- the HTML is described honestly as a current Wiki artifact pending promotion
  into the not-yet-implemented AGT-2084 folder/catalogue contract.

The final second-opinion pass returned **go for concept review** after these
corrections. The concept is ready for Robert's DL-0 review. Production remains
fail-closed until the restart barrier and positive no-overlap proof are
implemented and verified.
