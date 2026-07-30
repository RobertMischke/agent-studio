# Remote Task Server with local Agent Studio

Status: Phase A concept, proposed for Robert's approval, 2026-07-28. This
document makes no infrastructure change. Every deployment action belongs to a
separate Phase B task after approval.

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
