# Task Server deployment and recovery

Status: initial separated-service contract, AGT-2192, 2026-07-17.

This runbook implements the Task Server boundary from
[Distributed Agent Studio target architecture](../../concepts/distributed-agent-studio-target-architecture.md).
The service is the durable task and orchestration authority. Agent Studio and
Agent Runner are clients. Do not expose this initial service beyond loopback or
an SSH-only private tunnel until AGT-2193 supplies authentication and TLS.

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
dotnet publish task-server/TaskServer.csproj -c Release -o out/task-server
dotnet publish studio-bff/StudioBff.csproj -c Release -o out/studio-bff
dotnet publish runner/AgentRunner.csproj -c Release -o out/runner
```

For Linux, install `out/task-server/` under `/opt/agent-task-server`, copy
[`agent-task-server.service`](../../../deploy/systemd/agent-task-server.service)
to `/etc/systemd/system/`, and create `/etc/agent-task-server/server.env` from
[`agent-task-server.env.example`](../../../deploy/systemd/agent-task-server.env.example).
The data directory must be owned by the dedicated service account and backed up
independently of the installation directory.

The service manager owns process start, stop, restart, and upgrade:

```bash
sudo systemctl enable --now agent-task-server
sudo systemctl status agent-task-server
sudo systemctl stop agent-task-server
sudo systemctl restart agent-task-server
```

Before an upgrade, put the server in `Draining`, wait for active attempts to
finish, create a backup, switch to `Maintenance`, stop the unit, replace the
published package, and start it again. Startup applies additive schema
migrations before `/readyz` reports that lease and fence authority is restored.

## Configuration and health

Configuration uses the `TaskServer` section or standard double-underscore
environment variables.

| Setting | Meaning | Default |
|---|---|---|
| `TaskServer:DataDirectory` | Private database, backups, and migration evidence root | `data` beside the installed service |
| `TaskServer:ListenUrl` | Loopback listener when `ASPNETCORE_URLS` is absent | `http://127.0.0.1:5071` |
| `TaskServer:MinimumLeaseSeconds` | Lower clamp for Runner leases | `30` |
| `TaskServer:MaximumLeaseSeconds` | Upper clamp for Runner leases | `600` |

- `GET /healthz` proves the process is live.
- `GET /readyz` succeeds only after schema integrity and durable lease/fence
  authority are restored.
- `GET /api/v1/management/status` reports server identity, version, schema,
  data root, mode, and supported protocol range.
- `GET /api/v1/protocol` publishes the compatibility range. Runner requests
  must carry `X-Task-Protocol-Version`. An unsupported or missing version gets
  HTTP 426 before registration or claim.

For a zero-argument local profile, set `TASK_SERVER_PROFILE=local-compatibility`.
The service listens on `127.0.0.1:5031` and uses the current user's application
data directory. The topology test separately proves the service with another
process and temporary data root.

## Modes and durable authority

- `Normal` admits work and accepts writes.
- `Draining` stops new claims while allowing current fenced attempts to finish.
- `ReadOnly` permits observation and backup but blocks mutations.
- `Maintenance` blocks mutations and is required for import and restore.

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

## Fully remote review authority

`POST /api/v1/reviews/subjects` records one immutable subject after a fenced
coding completion has persisted the same repository identity, full Result-SHA,
and immutable ref or source-bundle digest. The review policy is a command plan:
completion interpretation, build and tests, requirements, code quality,
documentation, evidence, artifacts, and optional vision remain the existing
review steps, but their processes run only on a claimed Remote Review Executor.

Review lifecycle routes:

- `POST /api/v1/runners/{id}/review-claims`
- `POST /api/v1/reviews/attempts/{id}/lease/renew`
- `POST /api/v1/reviews/attempts/{id}/report`
- `POST /api/v1/reviews/attempts/{id}/cleanup`

The fenced report binds repository identity, expected and actual HEAD, tree
hash, dirty-before and dirty-after facts, environment, toolchain, exact command
arguments, exit or signal, output digests, artifacts, and typed aspect verdicts.
The Task Server validates containment and subject identity but starts no Git,
build, test, provider CLI, semantic, or vision process. Product and pass
outcomes advance to Human Review, which remains the final decision surface.
`ReviewInfra` stays in Auto Review and schedules another ReviewAttempt on the
same subject.

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
