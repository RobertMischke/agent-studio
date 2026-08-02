# Remote Task Server with local Agent Studio

Status: Phase A topology concept from 2026-07-28, extended by the first
AGT-2470 host-execution slice on 2026-08-02. No infrastructure was changed by
this document. The code now wires the existing host-orchestrator protocol and
moves one bounded post-step, while the remaining deployment and migration
actions stay in separately verified Phase B slices.

## Purpose and scope

The Task Server and Orchestrator Engine move to Hetzner and run as independently
supervised services. Robert's Angular Agent Studio remains on Windows at
`[::1]:4011`. `agent-runner-01` reaches the Task Server without the current
reverse tunnel from the Windows machine. Closing, sleeping, or restarting the
Windows machine therefore does not interrupt remote execution.

This plan narrows the earlier "host the control plane" direction. It does not
host Angular, publish a public Studio origin, or make the Task Server an
internet service. It follows the component boundary in
[Distributed Agent Studio target architecture](../concepts/distributed-agent-studio-target-architecture.md)
and the packaged service and recovery contract in
[Task Server deployment and recovery](setup/task-server.md).

The historical Remote-ready kickoff was retired on 2026-07-19. Its binding
outcomes are retained in the current target architecture and ADR-0059 in the
[ADR archive](../system/architecture/decisions/adr-archive.md): one
authoritative Task Server, Git as the code channel, authenticated remote
transport, fenced Runner authority, and no direct remote filesystem writer.

## Proposed decisions

| Topic | Decision |
|---|---|
| Task Server host | Create a dedicated Hetzner VM named `task-server-01`. Do not co-host it with `agent-runner-01`. |
| Runtime | Install the versioned `agent-orchestrator` release. Run Task Server, Orchestrator Engine, and backup timer under systemd. Do not deploy the all-in-one legacy `OrchestratorApi` as the target service. |
| Studio location | Keep Angular and a small loopback-only Studio connector on Robert's Windows machine. Do not serve Angular from Hetzner. |
| Private transport | Use WireGuard between `task-server-01`, `agent-runner-01`, and Robert's Windows device. The Task Server edge listens only on the WireGuard address; Kestrel remains on VM loopback. |
| Application authentication | Use distinct, revocable bearer credentials for Studio, Engine, and each Runner. Enforce authorization on reads, mutations, management routes, and event streams. `X-Client-Id` remains audit attribution only. |
| Task data | Copy the complete `agent-taskboard-workspace`, including `.git`, to a frozen migration source on the VM. Import it into the standalone Task Server only after exact inventory and backup gates pass. |
| Authority | The remote Task Server is the sole writer after cutover. The copied workspace and Windows store remain frozen recovery material, never a second live Task Server. |
| Fallback | Keep an exact-version Windows Task Server and Studio connector profile pre-installed, with a recent verified backup locally available. Rehearse the full switch back in less than 15 minutes before production cutover. |

These decisions are a package. Co-hosting the control plane with the Runner or
using one shared bearer token would invalidate the threat model below.

## Why a dedicated VM

A compromised Runner is an explicit threat, not only an availability failure.
On a shared VM, a root-level Runner compromise can read Task Server data,
service credentials, backups, and systemd configuration regardless of normal
Unix user separation. It can also starve or kill the control plane.

A dedicated small VM provides:

- a host boundary between execution-plane credentials and control-plane data;
- independent reboot, update, resource, and backup lifecycles;
- a firewall policy with no Runner toolchain, repository deploy key, or coding
  agent credential on the Task Server host;
- a meaningful revocation path for a compromised `agent-runner-01`.

Start with 2 vCPU, 4 GiB RAM, and a separately monitored data volume sized from
the measured workspace plus three backup generations. CPU and storage are
capacity starting points, not a purchasing commitment. The Phase B
infrastructure task records measured disk use and adjusts the volume before
installation.

## Target topology

```text
Robert's Windows device
  Angular Studio [::1]:4011
          |
          | same-origin /api and /hubs
          v
  Studio connector [::1]:5031
  injects Studio credential, enforces Origin and CSRF
          |
          | HTTPS over WireGuard
          v
task-server-01, dedicated Hetzner VM
  wg0:443, private TLS edge only
          |
          | loopback HTTP
          v
  Task Server 127.0.0.1:5071  <->  Orchestrator Engine
  systemd + SQLite state + verified backups
          ^
          |
          | HTTPS over WireGuard, Runner credential
          |
agent-runner-01
  Agent Runner systemd service
  repository clones and worktrees
  no Task Server filesystem access
```

The local connector preserves the existing Angular same-origin shape and lets
the stable frontend keep using its loopback backend target. It is a stateless
credential and transport boundary, not a task authority. In remote mode it
forwards to the WireGuard origin. In fallback mode it forwards to the Windows
Task Server on a different loopback port.

The current `studio-bff` is a useful starting point, but it only forwards
`/api/v1` and does not yet cover the complete Studio API, authentication
surface, or `/hubs` WebSocket path. Extending and testing that boundary is a
cutover prerequisite.

## Network design

### WireGuard instead of a persistent SSH forward

WireGuard is the recommended client path for both Studio and Runner:

- it reconnects after Windows sleep and restart without a long-running SSH
  forwarding process;
- one private address and DNS name serve Studio, Runner, and operations;
- each device has a separately revocable network key;
- `agent-runner-01` reaches the control plane directly and no longer depends on
  a tunnel initiated by Robert's machine;
- the API still has no listener on the VM's public interface.

A persistent local SSH forward remains a viable emergency administration path,
but is not the normal data path. It should not become a second, unmonitored
production route.

WireGuard is network admission, not application authentication. A stolen
WireGuard key must not be enough to read or mutate tasks.

### Listener and firewall contract

- Kestrel listens on `127.0.0.1:5071` only.
- The TLS edge listens on TCP 443 bound to the `wg0` address only.
- The public interface has no TCP listener for the API, Studio, health, or
  management endpoints.
- Hetzner Firewall and `ufw` both default-deny inbound traffic.
- `ufw` permits TCP 443 only on `wg0`.
- Public UDP permits only the WireGuard handshake port. Public SSH remains an
  administration surface restricted to Robert's known source range or an
  existing bastion. No public rule permits 80, 443, 5030, 5031, or 5071.
- IP forwarding is off. WireGuard peers receive only the single Task Server
  route, not general VM or home-network access.
- Private DNS maps a non-public name to the WireGuard address. TLS uses a
  private CA trusted by the two clients, or an explicitly pinned certificate.
  Certificate expiry and rotation are monitored and rehearsed.

Phase B must prove the binding with `ss`, `ufw status`, Hetzner Firewall
inspection, a public-interface connection test, and a WireGuard-interface
connection test. A wildcard bind such as `0.0.0.0` or `[::]` fails the gate.

## Authentication and authorization design

Bearer authentication is selected over mTLS for the first deployment. WireGuard
already supplies device-level encrypted transport, while bearer principals fit
the existing Task Server and Runner seams and are simpler to rotate. TLS remains
mandatory inside WireGuard so credentials are not sent over cleartext HTTP.

The release default of one shared `AUTH=bearer` secret is not sufficient for
this topology. Phase B must provide distinct principals with server-side
hash-only secret storage and route scopes:

| Principal | Credential location | Allowed authority |
|---|---|---|
| `studio-robert-windows` | Windows Credential Manager, read only by the loopback Studio connector | Read allowed resources and perform normal Studio task and orchestration mutations. No Runner lease, credential administration, restore, or server lifecycle authority. |
| `agent-runner-01` | Root-owned Runner credential file with service-group read access | `runner.claim`, `runner.lease`, `runner.logs`, `runner.events`, `runner.artifacts`, and `runner.completion` for its own Runner identity. |
| `orchestrator-engine` | Root-owned Task Server host credential file | Only orchestration claim, renewal, stage completion, and required resource reads. |
| `break-glass-owner` | Offline operator secret, not loaded by Studio or Runner | Identity rotation, restore, maintenance, and emergency revocation. |

Requirements:

1. Every API read and mutation requires an authenticated principal, except the
   minimal non-secret liveness response and protocol negotiation.
2. `/hubs` negotiation, connection, subscription, and reconnect use the same
   authenticated Studio principal.
3. Unsafe Studio methods require connector-issued CSRF proof. The connector
   accepts only the exact local Studio Origin and rejects unexpected Host and
   WebSocket Origin values.
4. The Studio bearer never reaches Angular, browser storage, task content, or
   logs. The connector injects it after the local browser boundary.
5. Tokens are at least 256 bits, one-time reveal, hash-only at rest on the
   server, scoped, expirable, independently revocable, and rotated with an
   overlap-and-prove procedure.
6. The authenticated principal is the authorization input. `X-Client-Id` is
   stored separately as attribution and never changes the decision.
7. `X-Client-Id` without a valid bearer returns 401. A valid Runner bearer on a
   Studio, management, or other Runner identity's mutation returns 403.

The current role-separated Studio and Runner bearer mode is closer to this
target than the shared release token, but it is still only a transition. The
production gate is a distinct revocable credential for `agent-runner-01`, not a
secret shared by all future Runners.

## Threat model

| Attacker | Capability assumed | Controls | Residual risk and response |
|---|---|---|---|
| Internet attacker | Can scan and send arbitrary traffic to the public VM address | No public API listener; Hetzner Firewall plus `ufw`; WireGuard handshake only; SSH source restriction; application auth still required after VPN admission | A WireGuard or SSH implementation flaw remains possible. Patch both hosts and alert on handshake and login anomalies. |
| Compromised Runner | Has `agent-runner-01` WireGuard key, scoped Runner token, repository deploy keys, and coding-agent credentials | Dedicated Task Server VM; no shared filesystem; Runner route scopes; fenced leases; per-identity audit; no management or arbitrary task mutation; immediate revocation of Runner token, WireGuard peer, and repository keys | The attacker can act within an already authorized run and may exfiltrate code available to that Runner. Revoke, fence the host, quarantine active attempts, and require positive no-overlap proof before reissue. |
| Stolen Robert device | May obtain the WireGuard key and, if the device is unlocked or decrypted, the Studio credential | Full-disk encryption; Windows Credential Manager; auto-lock; Studio token has no break-glass authority; separate revocation of VPN peer and Studio token; short session and credential audit | An unlocked compromised device can perform Studio-authorized actions. Revoke both credentials, review audit, rotate affected secrets, and restore from verified evidence if unauthorized mutations occurred. |

The design does not claim to protect Task Server plaintext from root compromise
of `task-server-01`. That event requires host isolation, credential rotation,
and restore to a clean VM from an off-host verified backup.

## Data and backup layout

The immutable release stays under `/opt/agent-orchestrator/<version>`.
Configuration and credentials stay under `/etc/agent-orchestrator`. Active
Task Server state, migration evidence, and local backup staging stay under
`/var/lib/agent-orchestrator`. None of these paths is a product source checkout.

The Windows `agent-taskboard-workspace` is copied over SSH with `.git`, task
metadata, prompts, timelines, results, and hidden application metadata intact.
It is staged on the VM as a migration source and becomes read-only after the
final copy. The standalone Task Server imports durable authority into its
SQLite store. The staged Git workspace remains migration and rollback evidence;
it is not mounted by the Runner and is never a second live writer.

Backup policy before cutover:

- one frozen filesystem archive of the Windows workspace, including uncommitted
  files, with a SHA-256 manifest;
- one `git bundle` or equivalent full-ref export plus `git fsck --full`
  evidence;
- one Task Server pre-import backup created by the migration endpoint;
- one restore-verified off-host copy outside `task-server-01`;
- one restore-verified copy staged for the Windows fallback.

After cutover, the Task Server backup timer creates integrity-checked SQLite
backups and sends them to separate off-host storage. The maximum planned
recovery point is five minutes. The exact interval is accepted only after a
load and restore rehearsal shows it does not disrupt lease renewal or
completion. The Windows standby regularly receives and verifies the newest
backup but never opens it while the remote server is authoritative.

## Current product gaps that block cutover

These are Phase B work, not assumptions that operations can work around:

1. **Studio API coverage.** Angular still consumes a broad legacy `/api`
   surface, while the standalone Task Server and current `studio-bff` expose
   only the versioned subset. Phase B must classify every Studio route as
   Task Server, local dev-seat helper, or retired, and prove all task,
   orchestration, host, file, event, and management paths remotely.
2. **Current workspace migration.** `LegacyMigrationService` currently scans
   `job.json`, while the active backend writes `task.json`. A production
   inventory could therefore report a false zero. The migrator must consume the
   current layout and add per-project and per-state counts before it is trusted.
3. **Scoped credentials.** The packaged install defaults to one shared bearer.
   It must support separate hash-only Studio, Engine, and per-Runner
   credentials with route authorization and revocation.
4. **Windows fallback artifact.** The documented control-plane release is
   `linux-x64`. A version-matched Windows Task Server package or an equally
   tested Windows service installation, including cross-platform backup
   restore, is required before the move can be called reversible.
5. **Connector security.** The local connector needs complete `/api` and
   `/hubs` forwarding, secret-file or Credential Manager integration, strict
   Origin checks, CSRF, protocol negotiation, health reporting, and an atomic
   remote/local upstream switch.

No API listener may open on `wg0` until gaps 3 and 5 pass their negative
authentication tests.

## Post-processing without an attached Studio

### Concept decision

Moving task state to the remote Task Server and moving post-step execution are
two separate migrations. The former removes the Windows workspace as the task
database. It does not make Git, build, test, lint, or model work executable on
the Task Server. Those steps require a repository checkout and belong on a
fenced execution host.

The target split is:

| Owner | Responsibilities |
|---|---|
| Task Server | Own task and run state, immutable post-step plans, review-cycle epochs, retry budgets, pure verdict policy, reissue decisions, lane transitions, idempotency, and integration dispatch. It never starts a project process or reads a Runner checkout. |
| Runner host | Materialize the exact fenced subject, execute checkout-bound Git, build, test, lint, and model steps, and report typed facts plus content hashes. It cannot move a lane or create a replacement run directly. |
| Orchestrator Engine | Resolve server-owned plans, schedule non-checkout control-plane work, and drive bounded commands through Task Server APIs. It is not a filesystem proxy for the legacy monolith. |
| Operator | Keep explicit acceptance, destructive override, unresolved conflict, credential, and break-glass decisions. Studio is one client for these decisions, not an execution dependency. |

The Angular process is already irrelevant to post-processing execution. The
current availability dependency is the legacy `OrchestratorApi` process and
its local `TaskRepository` filesystem. `AutoReviewPostProcessingWorker` reads a
card from that filesystem, `ReviewDecisionOrchestrator` executes the chain, and
the same process writes decision and lane state back into task folders. The
remote Task Server path must replace all three roles before a wave can drain
without the local backend.

### Placement rule

A step is **host-capable** when all of its side effects can be scoped to an
exact repository subject, a disposable checkout, declared credentials, and a
typed result envelope. A step is **server-decision** when it only combines
persisted facts and chooses a state transition. A step remains
**backend-bound** when it reads the legacy workspace registry or mutates files
outside a fenced project checkout without an API contract. A deferred step can
have an operator or Task Server trigger and still use a host executor.

The classification below describes the current implementation, the missing
host input, and the intended migration order. `H0` is delivered in this slice;
lower numbered groups move first.

The exact current source and serialization map used by the table is:

| Concern and affected steps | Source paths, files, and locks |
|---|---|
| Catalogue and ordering, all rows | `backend/Features/Pipeline/PipelineCatalogue.cs` defines every standard, UI, concept, drift, and abort step. `backend/Features/Pipeline/PipelineExecutionLog.cs` writes the card-local `pipeline-execution.json`. |
| Post-processing coordinator, completeness, and final decision | `backend/Features/Runner/AutoReviewPostProcessingQueue.cs` owns the in-process worker and its capacity semaphore. `backend/Features/Runner/ReviewDecisionOrchestrator.cs` owns `_tickGate`, `_postProcessingGitGate`, `GuardedMoveJob`, review-cycle files, and the reads of `status.md`, `results/`, and `completion-acceptance.json`. `backend/Features/Runner/CompletionGate.cs` is the pure completeness policy. |
| Aspects and grade | `backend/Features/Runner/AspectRunnerService.cs` uses a per-card `SemaphoreSlim`, local prompt and CLI services, and writes aspect Markdown and JSON into the card folder. `backend/Features/Review/CodeReviewStepService.cs` writes the grade report into that folder. |
| Build, lint, and radar | `backend/Features/Pipeline/BuildTestGateRunner.cs` owns static `ProcessGate`, the machine `flock`, the bounded `RemoteGate`, and disposable review roots. `backend/Features/Pipeline/LintScssRunner.cs` starts stylelint under `<repo>/frontend`. `backend/Features/RegressionRadar/RegressionRadarService.cs` reads the repository SHA range. |
| Worktree, integrate, and conflict | `backend/Features/Runner/ProjectRunner.cs` owns `_integrateLock`; `backend/Features/Runner/WorktreeTaskLifecycle.cs` records containment; `runner/GitWorkspace.cs` owns process-wide `GitMetadataGate`. The active task checkout, task ref, integration ref, and pipeline log are the mutable inputs. |
| Attribution and accepted integration | `backend/Features/Tasks/TaskTransitionService.cs` calls `backend/Features/Tasks/CommitAttributionService.cs` and `backend/Features/Pipeline/MergeIntoDevelopRunner.cs`. The latter owns `_mergeGate` and `_pushGate`; `IntegrationPushQueue.cs` and `IntegrationPushWorker.cs` own deferred push retry. Evidence is written below the card's `post-steps/`. |
| Wiki producers | `WikiMaintenancePostStepRunner.cs` writes `docs/<theme>/common-problems/**`; `WikiLearningsPostStepRunner.cs` writes `docs/operations/learnings/**`; `AgentsWikiSyncPostStepRunner.cs` writes `AGENTS.md` and `docs/concepts/designated-topics/**`. All are under `backend/Features/Pipeline/`. `ManagedProjectArtifactCommitService.cs` serializes publication with per-repository semaphores. |
| Task spawn | `backend/Features/Pipeline/TaskSpawnerPostStepRunner.cs` invokes the local one-shot CLI and `TaskMutationService`; `SpawnedTaskLedger.cs` writes `<card>/.metadata/spawned-tasks.jsonl`. |
| Drift | `backend/Features/Drift/DriftPostStepRunner.cs` reads configured `TaskRepository/projects/<project>`, repository content, task lanes, and prior `logs/drift/*.md`; the dimension services and `DriftReportStore` remain workspace-bound. |

File names without a directory in this map are relative to
`backend/Features/Pipeline/`.

### Standard post-step classification

| Step | Current local dependencies and serialization | Target placement | Host or server contract needed | Order |
|---|---|---|---|---:|
| `post-orchestrator-review` | Reads task body, `status.md`, CLI log tail, result inventory, `completion-acceptance.json`, and reissue history from the card folder. Uses `CompletionGate`, `PipelineExecutionLog`, and the review-cycle counter. | Server-decision | Persist the structured close-out facts and active review-cycle epoch. Run the pure completeness policy in the Task Server and append its verdict transactionally. | S1 |
| `post-build-test-gate` | `BuildTestGateRunner` resolves the watched repository path, exact SHA, project `BuildProfile`, mode, and timeouts. It creates a checkout under the machine review root and serializes local work through `ProcessGate` plus the machine `flock`; the temporary SSH bridge has its own bounded gate. | Host-capable | Versioned exact-subject plan with repository source, result SHA, commands as argv plus working directory, environment allow-list, mode, timeouts, resource class, and input digest. Report per-command HEAD/tree proof, exit/signal, classified failure, output artifact hashes, and cleanup proof. | H2 |
| `aspect-requirement-fit` | `AspectRunnerService` reads the prompt, task and status evidence, results inventory, branch diff, prompt template, project model settings, call budget, and local CLI service. | Host-capable, partly represented by Remote Review | Immutable ReviewSubject, resolved prompt/model/thinking plan, read-only credentials, typed verdict, token usage, and exact-SHA workspace proof. Task Server owns retry and lane effect. | H3 |
| `aspect-code-quality` | Same checkout, evidence, prompt registry, model routing, CLI, and per-call budget dependencies as the requirement aspect. | Host-capable, partly represented by Remote Review | Same per-aspect ReviewSubject plan and typed report contract. | H3 |
| `aspect-documentation-impact` | Same checkout and model dependencies, plus repository documentation inventory. | Host-capable, partly represented by Remote Review | Same per-aspect plan, with declared documentation inputs in the subject digest. | H3 |
| `aspect-tests-and-evidence` | Same checkout and model dependencies, plus build/test and durable result evidence. | Host-capable, partly represented by Remote Review | Same per-aspect plan, with command and artifact evidence references rather than task-folder reads. | H3 |
| `post-worktree-containment` | `ProjectRunner`, `WorktreeTaskLifecycle`, and the remote `GitWorkspace` secure the task checkout and result ref. Git metadata mutations use the process-wide `GitWorkspace.GitMetadataGate`; durable handoff uses run, lease, fence, base SHA, result SHA, and manifest digest. | Host-capable, delivered | The existing permit plan now names this real step. For a materialized result, the host reports `passed` only after immutable handoff and safe teardown. For preparation failure, it first proves that the partial checkout was removed or secured. The Task Server blocks run completion while the step is incomplete. | H0 |
| `post-integrate-merge` | Runs against the task worktree and task branch from `ProjectRunner`; uses Git metadata, integration-branch state, containment result, and pipeline log. A conflict feeds the following resolution step. | Host-capable | Fenced task branch, expected integration head, result SHA, repository identity, exclusive repository-metadata lease, and typed merged, already-merged, conflict, or environmental result. | H4 |
| `post-conflict-resolution` | Non-idempotent model-guided resolution in the mutable checkout. It depends on the merge conflict set, prompt/model selection, CLI credentials, Git state, and pipeline log. | Host-capable, high risk | A new fenced checkout generation, conflict manifest digest, bounded model plan, resolved tree hash, tests to rerun, and an explicit no-push boundary. A stale integration head invalidates the result. | H5 |
| `post-git-commit-attribution` | `TaskTransitionService` and `CommitAttributionService` read the run's base/head and commit range, then write attribution evidence into the card folder before Auto Review. | Host-capable analysis, server-owned record | Base SHA, result SHA, task branch and run identity are already in the handoff. Host reports the ordered commit set; Task Server stores it under run and fence instead of a card file. | H1 |
| `post-merge-into-develop` | Deferred acceptance action in `TaskTransitionService` calls `MergeIntoDevelopRunner`, `GitService`, and `ProjectSettingsService`. `_mergeGate` serializes the local repository and outcome logs are written under `post-steps/`. | Operator or Task Server trigger, host execution | Task Server issues an integration permit with accepted task/result identity and expected target head. Host performs compare-and-merge under a repository lease and returns new head or a typed conflict. | H4 |
| `post-merge-into-develop-push` | `MergeIntoDevelopRunner`, `_pushGate`, `IntegrationPushQueue`, and `IntegrationPushWorker` push the local integration branch and apply environmental retry. | Task Server trigger, host execution | Follow the successful merge generation only. Require expected local and remote heads, scoped push credential, idempotency key, and remote-head acknowledgement. | H4 |
| `post-lint-scss` | `LintScssRunner` resolves the frontend below the watched repository and starts `npx stylelint`; project settings select off, warn, or fail. The worker writes a task-folder log and the orchestrator decides reissue/escalation. | Host-capable | Exact-subject plan, detected Angular applicability, stylelint argv, toolchain digest, mode, timeout, and bounded output artifact. Task Server applies the pure warn/fail and retry policy. | H2 |
| `post-regression-radar` | `RegressionRadarService` uses `GitService` to diff the run commit chain and classify changed spec files, then records a reporting-only pipeline row in the card folder. | Host-capable analysis | Base/result SHA and repository source in; typed classification artifact and hash out. Task Server stores the reporting-only result. | H1 |
| `post-wiki-maintenance` | Writes the watched checkout's `docs/<theme>/common-problems` tree, occurrence files, and generated index from `WatchPathEntry.RootPath` and `TaskInfo`. Later workspace artifact commit/push services publish the mutation. | Backend-bound for now | Before host migration, define a repository-write subject, producer-owned paths, generated-file manifest, per-project Git lease, commit attribution, and push policy. It must not write the Task Server workspace directly. | B1 |
| `post-wiki-learnings` | Reads task outcome, status, aspect verdicts, diff summary, and results from the card folder; writes `docs/operations/learnings/<task>.md` and its index in the watched checkout. | Backend-bound for now | Replace card-folder reads with API facts, then use the same fenced repository-write and publication contract as wiki maintenance. | B1 |
| `post-agents-wiki-sync` | Reads and writes repository `AGENTS.md`, `docs/concepts/designated-topics/registry.json`, current-state pages, and the generated index. It also derives matches from changed paths and task tags. | Backend-bound for now | API task facts plus a producer-owned path manifest, repository lease, link validation result, commit, and push acknowledgement. | B1 |
| `post-code-review-grade` | `CodeReviewStepService` consumes the local diff, task evidence, build result, prompt registry, model settings, CLI credentials, and token ledger; writes grade evidence into the task folder. | Host-capable | Immutable subject and resolved model plan in; typed A/B/C/D grade, findings, token usage, and artifact hashes out. The grade stays advisory in server policy. | H3 |
| `post-task-spawner` | `TaskSpawnerPostStepRunner` performs an LLM relevance decision, reads target project settings, writes `.metadata/spawned-tasks.jsonl`, and creates a card through local task services. | Split: host analysis, Task Server mutation | Host reports a typed spawn proposal. Task Server validates target/scope, owns the dedupe ledger and limit, and creates the related task through one idempotent transaction. | B2 |
| `post-orchestrator-decision` | `ReviewDecisionOrchestrator` combines gate, aspect, grade, lint, evidence, solution-quality, and retry facts. It writes decision journals and follow-up files, then uses `GuardedMoveJob` under lane/Git serialization for reissue, escalation, or Human Review. | Server-decision | Pure policy matrix over typed step facts and review-cycle epoch, followed by one Task Server transaction that records verdict, consumes budget, creates reissue intent if needed, and moves the lane. | S2 |
| `post-drift-adr-code` | `DriftPostStepRunner` reads `TaskRepository`, project lanes, repository ADR/code/schema data, earlier reports under `logs/drift`, and uses an LLM service. | Backend-bound until snapshot contract exists | Versioned repository/workspace snapshot, resolved model plan, and Task Server report store. Analysis can then run on a host. | B3 |
| `post-drift-software-architecture` | Reads repository source/module/schema/test trees plus architecture models and recent task folders from the legacy workspace; writes the shared drift report store and uses an LLM. | Backend-bound until snapshot contract exists | Same snapshot and report contract, including explicit architecture-model inputs. | B3 |
| `post-drift-docs-marketing` | Reads canonical docs, promoted mockup families, recent task lanes, completed task folders, and the shared drift report history; uses an LLM. | Backend-bound until snapshot contract exists | Same snapshot and report contract, with docs/mockup inputs and no live lane-directory scan. | B3 |
| `post-drift-spec-task-job` | Reads specifications and task/job folders across legacy workspace lanes and writes the shared drift report store; uses an LLM. | Backend-bound until task-history API exists | Task Server task-history projection plus immutable repository snapshot and typed drift report. | B3 |
| `post-drift-code-pattern` | Reads the exact checkout and `docs/system/contracts/code-patterns.md`, then writes the workspace drift report store. The analysis itself is deterministic. | Host-capable analysis | Exact-subject plan and rules digest in; typed findings artifact out. Task Server owns report persistence. | H1 |

### Triggered and specialised post-steps

| Step | Current dependency | Target | Order |
|---|---|---|---:|
| `post-abort-review` | `PostAbortReviewStepService` reads local task contracts, CLI output, prompt registry, rerun budget, and writes a card-folder report. | Host-capable review call plus server-owned abort policy and budget after typed abort facts are in the Task Server. | H3, S2 |
| `post-ui-iteration-artifact` | `ReviewDecisionOrchestrator` reads durable screenshot and Playwright evidence in `results/`, task metadata, and `pipeline-execution.json`. | Host uploads the evidence manifest; Task Server validates completeness and records the iteration. | H1 |
| `post-ui-human-review-gate` | `ReviewDecisionOrchestrator` writes the human-review marker and performs the lane transition through `GuardedMoveJob` and `_postProcessingGitGate`. | Task Server decision. It remains an operator gate, with Studio optional. | S2 |
| `post-concept-workbench-placement` | Repository concept document and Workbench paths plus publication Git work from the watched checkout. | Backend-bound until the repository-write contract used by wiki producers exists. | B1 |
| `post-concept-review` | Concept artifact, card evidence, prompt/model/CLI services, and card-local review record. | Host-capable immutable-subject review with a typed server-owned verdict. | H3 |
| `post-concept-sight-review` | Rendered concept evidence, local result inventory, and vision-capable review service. | Host-capable through the Remote Review vision plan; Task Server owns the gate result. | H3 |
| `post-concept-promotion` | Operator acceptance, repository paths, Git commit/push, destination document policy, and local task creation services. | Operator or Task Server trigger with host Git execution after the repository-write and integration contracts exist. | B1, H4 |

### First moved slice: worktree containment

AGT-2470 wires the previously unused `HostOrchestratorJournal` into the
production coding daemon for Task Servers that advertise `host-orchestrator`.
The legacy claim path remains the compatibility path when that capability is
absent.

The active flow is now:

1. The coding host registers the supported host-orchestrator range and its
   `permits`, `local-queue`, and `host-post-processing` capabilities.
2. It sends a sequenced, replay-safe HostReport with capacity, accepted work,
   post-step projection, capabilities, and faults.
3. It accepts a centrally issued WorkPermit, durably journals the acceptance
   before launch, and executes it through the existing isolated Runner path.
4. The Task Server creates one real `post-worktree-containment` execution bound
   to the run, host, lease, and fence. It refuses run completion while that row
   is incomplete.
5. After a materialized result has an immutable result envelope and the
   isolated checkout is safely torn down, the host claims and completes the
   post-step with the envelope digest as evidence. A preparation failure first
   removes or secures its partial checkout, then completes the same containment
   step without inventing a result digest. Claim and completion replay use
   stable keys.
6. Only then can the host send the run completion. The Task Server records
   itself, rather than the deployed backend, as review authority.

The host fails closed on an unknown step id. Adding a future post-step to the
plan without a Runner implementation cannot turn into a false green report.
The journal persists accepted authority and the pending report so a crash does
not silently admit replacement work. Host-orchestrator v1 does not yet define a
lease-instance transfer from an old daemon process to a replacement process.
Consequently, a process restart with accepted work remains fail-closed and
needs the H0.1 instance-adoption contract; it is not claimed as automatic
recovery in this slice. Network retry and same-instance claim/completion replay
are automatic.

This slice proves the control path and removes `HostOrchestratorJournal` from
the dead-code category. It does not claim that the full Auto Review decision is
remote yet. Until `S1`, `S2`, and the selected host execution groups land,
legacy cards still need the monolith compatibility worker.

### Next host payload: build and test gate

`post-build-test-gate` is the first high-value execution migration after the
transport slice. It is already designed around one exact SHA and a disposable
checkout, but the current `PostStepPlanDto` contains only identities and status.
Executing it remotely before adding the following data would force the host to
rediscover server policy or guess commands, which is not acceptable.

The next host-orchestrator contract version adds an immutable
`PostStepExecutionPlan` with:

- step execution, run, task, review-cycle, lease, and claim-fence identities;
- repository identity and source URL or bundle, base SHA, expected result SHA,
  immutable result ref, dependency identities, and subject digest;
- ordered commands as executable plus argv, relative working directory,
  admitted environment names, required flag, and baseline-comparison flag;
- mode, queue wait timeout, execution timeout, cleanup timeout, resource class,
  toolchain requirements, and output limits;
- a plan digest and idempotency key that bind every report to those exact
  inputs.

The host returns one typed result with tested HEAD and tree hash, per-command
exit/signal/duration, classified code or environmental failure, bounded output
hashes, cleanup proof, and toolchain digest. The Task Server validates the
subject and fence, persists the facts, and applies the existing environmental
retry and code-defect reissue policy. The host never chooses the lane.

### Migration sequence and completion condition

1. **H0, delivered here:** host negotiation, reports, permits, durable queue,
   replay, and `post-worktree-containment`.
2. **H0.1:** add an explicit, positively verified Runner-instance adoption
   transaction for accepted queued work and reattached process generations.
   Until then a daemon replacement preserves authority and stops admission.
3. **H1:** commit attribution, regression radar, and code-pattern drift. These
   are repository reads over facts already present in the immutable handoff.
4. **H2:** build/test and lint after the execution-plan contract and separate
   build-pool admission exist.
5. **H3:** aspects and code grade through immutable review subjects and typed
   model-call results.
6. **S1 and S2:** move completeness and final verdict policies into the Task
   Server, including reissue budget, decision record, and lane move. This is the
   point at which Auto Review can drain without the legacy backend.
7. **H4 and H5:** dispatch integration merge/push and conflict resolution to a
   host under repository leases. Operator acceptance remains explicit.
8. **B1 to B3:** replace workspace registry, wiki, task-spawn, and drift
   filesystem dependencies with task-history, snapshot, repository-write, and
   idempotent mutation APIs.

The detached-Studio acceptance test must cover a complete wave, not only a
coding run: stop the Windows Studio connector and legacy backend, finish coding,
execute every enabled host step, produce the server verdict, exercise one
bounded reissue, reach Human Review, trigger an accepted integration, and then
reconnect Studio to the canonical history. No task may remain in
`post-processing-running` solely because Studio or the legacy backend is down.

## Migration runbook

### Entry gates

Before the production maintenance window:

- the exact release passes the topology, protocol, authentication, backup, and
  restore suites;
- the route inventory contains no unclassified Studio request;
- a dry run from a disposable copy preserves all expected task data;
- remote and Windows fallback packages report the same version and schema
  range;
- WireGuard, firewall, certificate rotation, token rotation, and public
  non-reachability have been rehearsed;
- the rollback drill has completed in less than 15 minutes using production
  scripts and representative data;
- there are no active or `process-unknown` attempts at the freeze point.

### Before and after evidence

Save one signed or checksum-protected migration report containing:

- workspace Git HEAD, `git status --porcelain`, `git fsck --full`, and full-ref
  bundle hash;
- total projects and tasks, including archive;
- task counts per project and state;
- event, result artifact, attachment, and evidence-Git counts;
- epic, reference, and project-registry counts where supported;
- source manifest hash, migration ID, import integrity SHA-256, Task Server
  version, and schema version;
- every migration warning, with an explicit accept or stop decision.

Project count, total task count, each per-project and per-state count, events,
and artifacts must match exactly before and after. A warning, missing archived
card, or unexplained count delta stops cutover.

### Production sequence

1. Announce the maintenance window. Drain the local Runner and stop automatic
   pickup. Resolve every active or unknown attempt.
2. Put the local authority in read-only mode. Stop all local Task Server,
   orchestrator, scheduled mutation, and workspace-watcher processes.
3. Capture the final inventory, filesystem archive, Git bundle, and checksums.
   Record the time at which the single-writer freeze began.
4. Copy the frozen workspace to the VM over SSH. Verify the file manifest,
   counts, Git refs, and `git fsck --full` on Linux. Check case-colliding paths
   before import.
5. Start the remote Task Server in `Maintenance` with authentication enabled.
   Run legacy inventory against the staged workspace and compare it to the
   saved Windows inventory.
6. Import with `freezeConfirmed:true` and the exact migration ID. Save the
   automatically created pre-import backup and returned integrity SHA-256.
7. Repeat the full before and after count comparison. Verify the backup without
   changing active data.
8. Start the Orchestrator Engine. Change the local Studio connector from its
   rehearsal target to the remote WireGuard origin. Keep admission closed.
9. Change `agent-runner-01` to the WireGuard Task Server URL and its scoped
   Runner credential. Disable, but do not delete, the old Windows reverse
   tunnel scheduled task.
10. Open normal admission. Prove authenticated Studio read, one reversible
    task mutation, SignalR reconnect, Runner claim and renewal, artifact
    upload, completion, and Studio detach/reconnect.
11. Turn the Windows Studio processes off for an acceptance interval. The
    Runner and Engine must continue and the task history must be present after
    Studio returns.
12. Mark the remote server authoritative only after all evidence is saved. Keep
    the frozen Windows source and the prior tunnel configuration through the
    agreed stabilization period.

At no time may both the legacy Windows writer and the remote Task Server accept
mutations for the same logical workspace.

## Rollback in less than 15 minutes

The rollback target is the version-matched Windows Task Server, not direct
editing of the frozen workspace. The local Studio connector changes upstream;
Angular stays at `[::1]:4011`.

Two data cases exist:

- **Before the first accepted remote mutation:** use the untouched frozen
  Windows authority and source backup.
- **After remote mutations:** restore the newest verified remote Task Server
  backup on Windows. Never restart the stale pre-cutover writer.

The rehearsed timeline is:

| Elapsed | Action |
|---:|---|
| 0 to 2 min | Stop admission, drain if reachable, and fence the remote VM. If the host cannot be contacted, use the Hetzner control plane to power it off or detach its network before starting local authority. Loss of heartbeat alone is not fencing. |
| 2 to 5 min | Select and verify the newest local backup by ID and SHA-256. Record its recovery point. |
| 5 to 8 min | Restore it to the pre-installed Windows Task Server, start in maintenance, verify integrity and counts, then enter normal mode. |
| 8 to 10 min | Atomically switch the loopback Studio connector upstream to the Windows Task Server and verify board read plus one authenticated mutation. |
| 10 to 13 min | Re-enable the preserved reverse SSH tunnel, point `agent-runner-01` at `127.0.0.1:15031`, retain Runner authentication, and restart its service. |
| 13 to 15 min | Verify Runner health, lease renewal, Studio events, and sole-writer status. Publish the actual recovery point and elapsed time. |

The Windows fallback Task Server continues to require token authentication
while the reverse tunnel is enabled. A loopback listener does not make a
tunneled mutation anonymous or safe.

Failback to Hetzner is a new migration window with the same freeze, inventory,
backup, and sole-writer gates. It is never an automatic resynchronization.

## Phase B delivery slices and effort

Every slice is a separate task and starts only after Robert approves this
concept.

| Order | Slice | Acceptance result | Estimate |
|---:|---|---|---:|
| B1 | Studio route ownership and secure local connector | Full `/api` and `/hubs` matrix; remote task workflows pass; local-only dev-seat routes are explicit; connector keeps secrets out of Angular and enforces Origin and CSRF | 5 to 8 engineering days |
| B2 | Task Server principal and scope hardening | Separate hash-only Studio, Engine, and per-Runner tokens; route scopes; hub auth; rotation and revoke tests; `X-Client-Id` negative tests | 3 to 5 engineering days |
| B3 | Current-workspace migration and evidence | `task.json` support; exact per-project and per-state counts; archive, events, artifacts, Git evidence, backup, import, and mismatch-stop tests | 3 to 5 engineering days |
| B4 | Windows fallback and switch tooling | Version-matched Windows service; Linux-backup restore; warm standby; atomic connector profile; authenticated reverse-tunnel fallback; measured sub-15-minute drill | 3 to 5 engineering days |
| B5 | Private Hetzner foundation | Dedicated VM, WireGuard peers, private TLS, dual firewall, systemd packages, off-host backup, monitoring, and proof of no public API listener | 2 to 3 engineering days plus operator access |
| B6 | Rehearsal and production cutover | Representative dry run, signed evidence, maintenance-window cutover, detached-Studio proof, rollback drill, and operator handoff | 2 to 4 engineering days plus one operator window |

Expected total: 18 to 30 engineering days plus one to two operator days. The
largest uncertainty is B1 because current Angular functionality still spans
legacy backend routes. B1 produces a route-counted estimate before the
remaining schedule is committed.

## Release gates

Phase B is complete only when all of the following are true:

- public connection attempts to every API and health port fail;
- WireGuard clients can reach the private TLS origin and unregistered peers
  cannot;
- missing or invalid auth returns 401, insufficient scope returns 403, and
  `X-Client-Id` alone never authorizes;
- a compromised-Runner simulation cannot call Studio or management mutations;
- systemd restarts Task Server and Engine independently of Studio;
- a run continues through Windows sleep or shutdown;
- migration counts, Git evidence, and integrity hashes match exactly;
- off-host backup restore succeeds on Linux and Windows;
- the old reverse tunnel is disabled in remote mode but remains documented and
  tested for fallback;
- the sole-writer invariant is visible in both cutover and rollback evidence;
- the measured rollback completes in less than 15 minutes.

## Related documents

- [Distributed Agent Studio target architecture](../concepts/distributed-agent-studio-target-architecture.md)
- [Task Server deployment and recovery](setup/task-server.md)
- [Security overview](security/overview.md)
- [Security requirements](security/requirements.md)
- [Release, installation, update, and rollback](releases.md)
- [Remote runner persistent connection](setup/remote-runner-persistent-connection.md)
- [Token refresh without a tunnel](token-refresh-ohne-tunnel.md)
- [Distributed execution connection health](haertung-verteilte-ausfuehrung/target-architecture/connection-health.md)
- [Control plane as a distributable](haertung-verteilte-ausfuehrung/target-architecture/distributable.md)
