# Task Server deployment and recovery

Status: production bootstrap, topology release, and sole v1 ownership contract,
AGT-2192/AGT-2196/AGT-2330, 2026-07-25.

This runbook implements the Task Server boundary from
[Distributed Agent Studio target architecture](../../concepts/distributed-agent-studio-target-architecture.md).
The service is the durable task and orchestration authority. Agent Studio,
OrchestratorApi, and Agent Runner are clients. The Task Server is the only
owner of its SQLite store and `/api/v1`. Internet-reachable deployments require HTTPS,
authenticated mode, protected credentials, and the broader AGT-2193 controls
in [networked Task Server](networked-task-server.md).

## Package and process boundary

| Package | Runtime responsibility | Durable data |
|---|---|---|
| `contracts/TaskServer.Contracts` | Versioned resource, runner, review, event, artifact, management, and compatibility DTOs | None |
| `task-server` | Stable identities, tasks, runs, immutable review subjects, review attempts, reports, events, artifacts, audit, migrations, backup/restore, leases, fences, and management API | Its configured data directory only |
| `studio-bff` | Optional stateless same-origin proxy for Agent Studio | None |
| `runner` | Separately registered coding and review services, host probes, Git worktrees, CLI and review processes, bounded execution, and durable result delivery through protocol 2 | Host worktrees, fsynced outboxes, and bounded transfer state only |

The Task Server project references shared contracts and SQLite persistence. It
does not reference Angular, the legacy Studio backend, Agent Runner, coding
agent libraries, repository worktree code, or host process execution.

## Install and supervise

Publish each process independently:

```bash
dotnet publish task-server/TaskServer.csproj -p:PublishProfile=linux-x64 -o out/task-server
dotnet publish studio-bff/StudioBff.csproj -c Release -o out/studio-bff
dotnet publish runner/AgentRunner.csproj -c Release -o out/runner
```

The Task Server profile emits one self-contained `linux-x64` executable with
the SQLite native runtime embedded. It needs neither a repository checkout nor
a .NET installation and reads host-specific bootstrap values only from
`server.env`.

For Linux, install the release under the versioned
`/opt/agent-orchestrator/<version>/` directory and point
`/opt/agent-orchestrator/current` at it. Copy
[`agent-task-server.service`](../../../deploy/systemd/agent-task-server.service)
and the
[`backup service`](../../../deploy/systemd/agent-task-server-backup.service)
and [`timer`](../../../deploy/systemd/agent-task-server-backup.timer) to
`/etc/systemd/system/`. Create `/etc/agent-orchestrator/server.env` from
[`agent-task-server.env.example`](../../../deploy/systemd/agent-task-server.env.example).
The data directory must be owned by the dedicated service account and backed up
independently of the installation directory.

Create the bootstrap bearer file without putting the secret in shell history:

```bash
sudo install -d -m 0750 -o root -g agent-orchestrator /etc/agent-orchestrator
sudo sh -c 'umask 0077; read -r secret; printf "%s\n" "$secret" > /etc/agent-orchestrator/task-server.token'
sudo chown root:agent-orchestrator /etc/agent-orchestrator/task-server.token
sudo chmod 0640 /etc/agent-orchestrator/task-server.token
```

Use a randomly generated value of at least 32 characters and transfer client
copies through the host administration channel. Never put the value in a
command line, task, log, or committed file.

The service manager owns process start, stop, restart, and upgrade:

```bash
sudo systemctl enable --now agent-task-server
sudo systemctl enable --now agent-task-server-backup.timer
sudo systemctl status agent-task-server
sudo systemctl stop agent-task-server
sudo systemctl restart agent-task-server
```

Before an upgrade, put the server in `Draining`, wait for active attempts to
finish, create a backup, switch to `Maintenance`, stop the unit, replace the
published package, and start it again. Startup applies additive schema
migrations before `/readyz` reports that lease and fence authority is restored.

## Configuration and health

The production binary consumes one host-owned `server.env` bootstrap contract.
These values are process prerequisites and are not agent-editable operational
settings.

| Setting | Meaning | Default |
|---|---|---|
| `LISTEN_URL` | Kestrel addresses. `AUTH=none` is rejected unless every address is loopback. | `http://127.0.0.1:5071` |
| `STORE_PATH` | Private database and migration evidence root, outside every version directory | `data` beside the installed service |
| `BACKUP_PATH` | Verified SQLite backup destination | `<STORE_PATH>/backups` |
| `AUTH` | `bearer` in production; `none` is loopback-only | `none` |
| `AUTH_TOKEN_FILE` | Host-owned bearer secret file, minimum 32 characters | Required with `AUTH=bearer` unless `AUTH_TOKEN` is set |
| `AUTH_TOKEN` | Direct secret alternative, mainly for ephemeral deployments | Unset |
| `TaskServer:MinimumLeaseSeconds` | Lower clamp for Runner leases | `30` |
| `TaskServer:MaximumLeaseSeconds` | Upper clamp for Runner leases | `600` |
| `TaskServer:ResultFinalizationMaxAttempts` | Bounded application-owned summary attempts after CORE completion | `3` |
| `TaskServer:InvariantReconciliationSeconds` | Interval for Tranche 0 invariant comparison | `30` |
| `TaskServer:InventoryGraceSeconds` | Minimum age before inventory mismatches are actionable | `120` |
| `TaskServer:MaximumEventPayloadBytes` | Hard UTF-8 size limit for one typed event payload | `262144` |
| `TaskServer:RequireAuthentication` | Require distinct Studio and Runner bearer credentials on `/api/v1` | `false` |
| `TaskServer:StudioBearerToken` | Studio/BFF read and management credential | unset |
| `TaskServer:RunnerBearerToken` | Runner registration, claim, renew, event, artifact, and completion credential | unset |

- Configure exactly one of `AUTH_TOKEN_FILE` or `AUTH_TOKEN`.
- `GET /api/v1/protocol` and `POST /api/v1/protocol/compatibility` remain open
  so a client can negotiate before registration. All other v1 requests require
  the bearer credential when `AUTH=bearer`.
- `GET /healthz` proves the process is live.
- `GET /readyz` succeeds only after schema integrity and durable lease/fence
  authority are restored.
- `GET /api/v1/management/status` reports server identity, version, schema,
  data root, mode, and supported protocol range. In the local Studio
  compatibility profile, the loopback `local-default` operator can use the
  management plane without creating a human account. Networked Studio
  deployments require a signed-in owner or operator.
- `GET /api/v1/management/invariants` reports invariant definitions, recent
  violations, and pending idempotent runner actions.
- `GET /api/v1/protocol` publishes the compatibility range. Every versioned
  resource request must carry `X-Task-Protocol-Version`. An unsupported or
  missing version gets HTTP 426 with a structured reason before any mutation.
- `GET /api/v1/projects/{projectId}/tasks/{taskIdentity}/history?after={cursor}`
  is the canonical reconnect projection. It includes every run, cursor-ordered
  typed events after the requested cursor, artifacts, related audit records,
  the latest typed Result-finalization state, and the last returned cursor.
- `POST /api/v1/runs/{runId}/result-finalization` is the fenced, idempotent
  awaited post-core gate. The Runner repeats only this request while the server
  returns `Retryable`; `Ready` includes the generated `status.md` artifact hash,
  and bounded exhaustion returns terminal `Degraded` without reissuing CORE.

Every release answers `task-server --version` with the release and stamped Git
SHA. This output is also used by deployment verification:

```text
task-server <VERSION>+sha.<40-character-commit>
```

## Sole v1 owner and transition proxy

When OrchestratorApi has `TaskServer:BaseUrl` configured, it maps `/api/v1`
only as a transparent proxy to that origin and does not map its local
management v1 routes. `TaskServer:AuthTokenFile` or `TaskServer:AuthToken`
supplies the proxy's service credential. Without `TaskServer:BaseUrl`, the
legacy local management route remains available for the interim monolith
profile. Any AGT-2325 compatibility review routes belong only to that fallback
profile. They must never be mounted beside the standalone proxy.

The canonical production bootstrap uses one service credential through
`AUTH=bearer`. The interim compatibility profile may instead set
`TaskServer:RequireAuthentication` and distinct `StudioBearerToken` and
`RunnerBearerToken` values. Do not configure both modes. Studio BFF reads
`TaskServer:AuthTokenFile`, `TaskServer:AuthToken`, or the compatibility
`TaskServer:BearerToken`. Agent Runner reads its secret from
`RUNNER_AUTH_TOKEN_FILE` or `RUNNER_AUTH_TOKEN`. A private-CA or rehearsal
deployment may pin the Task Server leaf certificate by SHA-256 through
`TaskServer:TlsServerCertificateSha256` on the BFF and
`RUNNER_TLS_CERTIFICATE_SHA256` on the Runner. Public deployments should use
the operating-system trust store.

For a zero-argument local profile, set `TASK_SERVER_PROFILE=local-compatibility`.
The service listens on `127.0.0.1:5031` and uses the current user's application
data directory. The topology test separately proves the service with another
process and temporary data root.

## Modes and durable authority

- `Normal` admits work and accepts writes.
- `Draining` stops new claims while allowing current fenced attempts to finish.
- `ReadOnly` permits observation and backup but blocks mutations.
- `Maintenance` blocks mutations and is required for import and restore.

A Runner lease release closes that attempt and atomically returns a matching
`3-progress` task to `2-ready`. This is the normal dead-process recovery path;
the later claim mints a higher fence. A successful completion instead closes
the lease and moves the task to `4-auto-review`.

Mode changes use `PUT /api/v1/management/mode` with a reason. On restart, every
previously active coding lease becomes `process-unknown`; its task cannot be
claimed by another Coding Executor. An operator must submit positive containment proof to
`POST /api/v1/management/attempts/{runId}/resolve-unknown`. The next claim then
uses a higher fence. Lease expiry alone never proves that the previous process
stopped.

A previously leased Remote ReviewAttempt also becomes `process-unknown`, but it
is safely reclaimable by a Review Executor with a higher durable fence. Review
workspaces are disposable, carry no product write credential, and cannot publish
product changes. The old executor's renew, report, and cleanup deliveries are
then rejected as stale. An infrastructure-only report creates a new
ReviewAttempt for the same immutable subject and leaves the task in Auto Review.
It never creates a coding run or returns the task to Ready.
Draining rejects new review claims while allowing an already fenced attempt to
renew, report, and clean up. Safe-shutdown and restore checks count unresolved
coding and review authority, and the integrity digest inventories the review
subject, attempt, fence, and delivery tables.

## Fully remote review authority

`POST /api/v1/reviews/subjects` records one immutable subject after a fenced
coding completion has persisted the same repository identity and URL, full
Result-SHA, and immutable ref or source-bundle digest. The review policy is a command plan:
completion interpretation, build and tests, requirements, code quality,
documentation, evidence, artifacts, and optional vision remain the existing
review steps, but their processes run only on a claimed Remote Review Executor.

Review lifecycle routes:

- `POST /api/v1/runners/{id}/review-claims`
- `POST /api/v1/reviews/attempts/{id}/lease/renew`
- `POST /api/v1/reviews/attempts/{id}/report`
- `POST /api/v1/reviews/attempts/{id}/cleanup`

The fenced report binds repository identity, expected and actual HEAD, tree
hash, dirty-before and dirty-after facts, environment, executable-digest
toolchain identity, exact command arguments, exit or signal, output digests,
artifacts, and typed aspect verdicts.
The Task Server validates containment and subject identity but starts no Git,
build, test, provider CLI, semantic, or vision process. Product and pass
outcomes advance to Human Review, which remains the final decision surface.
`ReviewInfra` stays in Auto Review and schedules another ReviewAttempt on the
same subject. Coding and review capabilities require distinct registered
identities, and a registered identity cannot be switched between those roles.
A stale report is rejected if a newer task lifecycle or result has replaced its
immutable review subject.

After draining, `POST /api/v1/management/prepare-shutdown` verifies that no
`active` or `process-unknown` attempt authority remains, records the operator
reason, and enters `Maintenance`. A safe response is permission for the service
manager to stop the process; the API does not try to stop its own host process.

## Backup and restore rehearsal

`POST /api/v1/management/backups` creates a consistent SQLite backup, runs an
integrity check, and returns its SHA-256. Backups contain server/workspace/
project/task/run identities, task state, events, artifact content, audit,
Runner records, coding and review leases, immutable review subjects, fenced
reports, and fence counters.

The packaged timer calls the same implementation through the binary:

```bash
/opt/agent-orchestrator/current/task-server backup --name timer
```

The command reads the same `server.env`, applies schema migrations
idempotently, takes and verifies the snapshot, writes the audit record, prints
the backup result as JSON, and exits. It does not turn live leases into
`process-unknown`; taking a backup is not a server restart.

Verify a backup without changing data:

```json
POST /api/v1/management/restore
{"backupId":"<id>","verifyOnly":true}
```

For restore, drain and resolve all attempts, enter `Maintenance`, then repeat
with `verifyOnly:false`. Restore refuses unresolved `active` or
`process-unknown` authority. Before replacement it creates a private safety copy
of the live store. It verifies schema compatibility and integrity after
replacement, automatically rolls back to that safety copy on failure, and
remains in `Maintenance` until an operator explicitly resumes normal service.

## Legacy single-writer migration

Legacy absolute paths and `watchPath` are migration inputs only. They never
become resource identity.

1. Call `POST /api/v1/management/migrations/legacy/inventory` with the legacy
   root and workspace name. Save the project/task/event/artifact counts,
   warnings, evidence-Git roots, and migration ID.
2. Stop every legacy writer. Confirm Studio task mutations and the in-process
   runner are stopped. A delta replay is acceptable only if it ends with the
   same exclusive writer freeze.
3. Put Task Server in `Maintenance` and call the matching `/import` route with
   `freezeConfirmed:true` and `expectedMigrationId` set to the saved inventory
   ID. Import fails if task metadata, prompts, timelines, or result artifacts
   changed after inventory.
4. The server creates a pre-import backup, imports the inventory in one
   transaction, preserves task `results/`, timeline events, stable generated
   identities, and copies evidence Git metadata into
   `migration-evidence/{migrationId}`.
5. Compare counts and save the returned integrity SHA-256. Start Task Server as
   the only writer, then point Studio/BFF and Runner at its URL.
6. The rollback boundary is the returned pre-import backup plus the untouched,
   frozen legacy root. Roll back before allowing either side to accept another
   write. After cutover, never reactivate the legacy writer against the same
   logical tasks.

The automated acceptance suite rehearses inventory, freeze enforcement,
transactional import, integrity verification, backup/restore, evidence Git
preservation, restart fencing, protocol rejection, and separate process
lifecycle.

## Release topology rehearsal

The release-blocking harness is intentionally separate from browser E2E. Build
the deployables once, then run the topology and compatibility gate:

```bash
dotnet build agent-taskboard.sln
dotnet test runner.Tests/AgentRunner.Tests.csproj \
  --no-build \
  --filter "FullyQualifiedName~AgentRunner.Tests.LogShipperCapTests|FullyQualifiedName~AgentRunner.Tests.BoundedOutputBufferTests"
dotnet test task-server.Tests/TaskServer.Tests.csproj \
  --no-build \
  --filter "FullyQualifiedName~TaskServer.Tests.TopologyTests|FullyQualifiedName~TaskServer.Tests.ProtocolTests" \
  --logger "console;verbosity=normal"
```

The test owns only its exact child PIDs and temporary directories. It never
sweeps by process name. Its parent-PID assertions require Task Server, Studio
BFF, and Runner to be siblings owned by the harness, so stopping Studio cannot
implicitly stop either service.
