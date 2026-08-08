# agent-runner-01 service-account hardening

Status: implementation and host acceptance record for the 2 August 2026
secrets-posture audit.

## Finding and boundary

The `agent` service account on `agent-runner-01` had two independent paths to
host root:

- `/etc/sudoers.d/agent` granted `agent ALL=(ALL) NOPASSWD:ALL`.
- `agent` belonged to the `docker` group. Access to the Docker daemon is
  equivalent to host-root access even when the Agent CLI process itself has
  `NoNewPrivileges=true`.

The account also owned the active `/opt/agent-runner` directory. An Agent CLI
could therefore replace the next daemon binary even without sudo. This is not
a root escalation because both units run as `agent`, but it defeats a
reviewable deployment boundary.

The resulting contract is deliberately small:

| Boundary | Service-account access |
|---|---|
| Coding service | Exact passwordless restart and no-pager status for `agent-runner.service` |
| Review service | Exact passwordless restart and no-pager status for `agent-runner-review.service` |
| Release promotion | One root-owned, no-argument helper with fixed ingress and release paths |
| Docker daemon | No access. Docker-dependent infrastructure tests use an operator-owned host or a separate constrained executor. |
| Other root operations | No passwordless or group-derived access |

The versioned sudoers source is
[`deploy/agent-host/sudoers.d/agent-runner`](../../../deploy/agent-host/sudoers.d/agent-runner).
It intentionally includes `""` after the deploy helper path. In sudoers syntax
that means no arguments are accepted. Status commands include `--no-pager`, so
no root-owned interactive pager is opened.

## Fixed deployment paths

The root-owned helper
[`deploy/agent-host/agent-runner-deploy`](../../../deploy/agent-host/agent-runner-deploy)
accepts files only through these paths:

| Path | Owner and purpose |
|---|---|
| `/var/lib/agent-runner/deploy/incoming` | `agent:agent`, upload-only ingress used by `scp` |
| `/var/lib/agent-runner/deploy/accepted/<release-id>` | `root:root`, retained non-secret input record |
| `/opt/agent-host/releases/<release-id>` | `root:root`, immutable promoted release |
| `/opt/agent-host/current` | `root:root` atomic link to one complete release |
| `/opt/agent-runner` | `root:root` compatibility link to `current` for the deployed legacy units |

The helper accepts no paths or release ids as arguments. It reads a strict
`release-id` file from the fixed ingress, rejects symbolic links and special
files, requires the complete `agent-host` application, enforces a 1 GiB size
limit, strips uploaded ownership and modes, and refuses to overwrite an
existing immutable release. It does not restart either service.

## One-time host migration

Run the migration from a root-owned operator session. Do not ask an Agent CLI
to invoke it and do not use Docker access as the routine administration path.

```bash
cd /path/to/agent-taskboard
./scripts/harden-agent-runner-host.sh --apply
```

The migration performs these bounded changes:

1. Validates both systemd units and the versioned sudoers file.
2. Saves replaced files below a root-only, timestamped
   `/var/backups/agent-runner-hardening/` directory.
3. Installs the root-owned deploy helper and sudoers whitelist.
4. Installs the versioned `KillMode=process` and `KillSignal=SIGTERM` drop-in
   for both units, so a planned daemon restart preserves detached coding and
   review workers.
5. Removes the legacy `NOPASSWD:ALL` file.
6. Removes `agent` from both `sudo` and `docker`.
7. Creates the fixed upload ingress and verifies the effective sudo policy.

Existing login sessions retain their supplementary groups. End every old
`agent` login and restart both services from the operator session before
acceptance. A process started before group removal must not be used as proof of
the final group posture.

## Deploy recipe: scp, promote, restart

Run this recipe from the trusted operator workstation, never from an Agent CLI
inside `agent-runner.service`. Restarting that unit would terminate the active
CLI unless the detached-worker handoff contract applies.

```bash
release_id="$(date -u +%Y%m%dT%H%M%SZ)-$(git rev-parse --short=12 HEAD)"
publish_root="$(mktemp -d)"
dotnet publish runner/AgentRunner.csproj -c Release -o "$publish_root"
printf '%s\n' "$release_id" >"$publish_root/release-id"

ssh agent-runner-01 \
  'find /var/lib/agent-runner/deploy/incoming -mindepth 1 -maxdepth 1 -delete'
scp -r "$publish_root/." \
  agent-runner-01:/var/lib/agent-runner/deploy/incoming/
ssh agent-runner-01 \
  'sudo /usr/local/sbin/agent-runner-deploy && \
   sudo /usr/bin/systemctl restart agent-runner.service && \
   sudo /usr/bin/systemctl restart agent-runner-review.service && \
   sudo /usr/bin/systemctl status agent-runner.service --no-pager && \
   sudo /usr/bin/systemctl status agent-runner-review.service --no-pager'
```

The first promotion preserves a pre-hardening `/opt/agent-runner` directory as
`/opt/agent-runner.pre-hardening` before installing the root-owned compatibility
link. Keep that directory until the acceptance matrix passes. Rollback is an
operator-root action: repoint `/opt/agent-host/current` to the previous complete
release and restart both units.

## Acceptance matrix

Capture only non-secret outputs. Store the run-specific evidence with the task,
not in this source document.

| Probe | Command or observation | Pass condition |
|---|---|---|
| Sudo denial | `sudo -n /usr/bin/id` | Refused; no broad fallback rule |
| Effective whitelist | `sudo -n -l` | Only the four exact systemctl commands and no-argument deploy helper are passwordless |
| Groups | `id -nG agent` from a new operator session | Contains neither `sudo` nor `docker` |
| Release ownership | `namei -l /opt/agent-runner/agent-host` | Active release and links are root-owned and not writable by `agent` |
| Deploy | Run the scp recipe with a new release id | Helper promotes one complete immutable release; both units return active |
| Coding capability | Execution Hosts plus coding journal | Fresh advertisement and `runner-git-capability status=ready` or `ready-no-workflow-scope` |
| Review run | One normal immutable-subject Review claim | Claim and authoritative report complete under `agent-runner-01-review` |
| Coding run | One normal Ready capability probe card | Fenced claim, result upload, completion, and review transition succeed |
| Restart handoff | Restart each daemon while its role has work | Main PID changes; detached work settles once; no lease is lost merely because of the restart |

Docker is intentionally absent from the acceptance path. If a future regular
Runner capability requires a Docker operation, add a dedicated root-owned
command that validates exact resources and actions. Do not restore Docker group
membership or add `docker` to sudoers.

## Change log

| Date | Change | Non-secret evidence |
|---|---|---|
| 2026-08-02 | Audit finding recorded. | `agent` had `NOPASSWD:ALL`, `sudo`, `docker`, and a writable active release directory. |
| 2026-08-08 | Least-privilege policy, fixed-path deploy helper, restart-safe systemd drop-in, migration, deploy recipe, and acceptance matrix versioned. | Sudoers parses with `visudo`; shell and Runner contract tests cover the fixed commands and paths. |
