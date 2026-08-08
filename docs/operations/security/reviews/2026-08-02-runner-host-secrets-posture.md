# 2026-08-02 runner host secrets-posture review

**Outcome:** Repository remediation complete. Application and live acceptance on
`agent-runner-01` remain pending because the host name was not resolvable from
the managed task environment on 2026-08-09.

## Finding

The task audit reported that the `agent` service account on
`agent-runner-01` had both `sudo NOPASSWD: ALL` and membership in the `docker`
group. Either grant lets every coding or review CLI obtain effective host root.
The systemd sandbox does not compensate for privileges held by the account that
launches or controls the CLI.

Severity is critical for a shared execution host. A prompt, repository script,
or compromised CLI process could alter units, read host secrets, mount the host
filesystem through Docker, or persist outside the runner work roots.

## Remediation contract

The runner account has no membership in the `docker` group and no direct access
to the Docker socket. Arbitrary Docker access is not replaced with a sudo
wrapper because any general Docker command is still a host-root interface. A
future workload that genuinely requires containers must use a separately
designed rootless engine or a fixed, root-owned service command and advertise a
matching capability. It must not restore Docker group membership.

The only passwordless root operations are:

- `/usr/local/sbin/agent-host-admin`, a root-owned command with fixed source,
  destination, configuration, and unit mappings;
- `systemctl restart` for `agent-host.service` and
  `agent-runner-review.service`; and
- `systemctl status --no-pager` for the same two units.

The administration command accepts no caller-supplied paths or unit names.
Release identifiers use a bounded safe character set. Release input can only
come from `/var/lib/agent-host-deploy/incoming/<release-id>`, and activation can
only write below `/opt/agent-host/releases` plus the fixed `current` symlink.
Links, special files, set-id entries, and group/world-writable release content
are rejected. Service configuration can only come from the fixed per-role
staging files, and each admitted environment key is checked before a fixed unit
is rendered.

## Versioned artifacts

| Artifact | Purpose |
|---|---|
| `deploy/agent-host/sudoers.agent-host` | Reviewable sudoers source, policy version `2026-08-02.1`. |
| `deploy/agent-host/agent-host-admin` | Root-owned validation and deploy boundary. |
| `deploy/agent-host/install-privilege-policy.sh` | Operator-only bootstrap, broad-grant revocation, Docker group removal, and effective-policy checks. |
| `scripts/remote-agent-host-deploy.sh` | Secret-free `scp`, activation, bounded restart, status, and capability recipe. |
| `scripts/remote-runner-onboard.sh` | Product onboarding updated to use only the scoped boundary. |

No credential, token, private key, host address, or sudoers backup is stored in
the repository.

## Operator application

Drain Coding and Review first and confirm that no detached worker remains. The
group database change does not remove supplementary groups from an existing
process, so every old `agent` session and worker must end before acceptance.
Run the bootstrap from a separate root or console session using a trusted,
reviewed checkout of the approved revision. Do not invoke it through an agent
CLI and do not use an agent-writable checkout whose revision was not reviewed.

Identify the one legacy sudoers file containing the broad grant with `visudo`.
Then apply the replacement and name that exact file explicitly:

```bash
deploy/agent-host/install-privilege-policy.sh \
  --user agent \
  --revoke-file /etc/sudoers.d/<confirmed-legacy-file> \
  --restart-units
```

The legacy file is moved to a root-only backup below
`/var/backups/agent-host-privilege`; it is not deleted. The installer refuses to
edit `/etc/sudoers` or guess another file. It fails if any passwordless `ALL`
grant remains, if the complete policy does not pass `visudo`, if `agent` still
belongs to `docker`, or if an existing `agent` process still holds the Docker
supplementary group.

Close the old login, start a fresh session, and run the non-secret security
probe:

```bash
id -nG agent
sudo -l -U agent
sudo -u agent sudo -n /usr/bin/systemctl restart ssh.service
sudo -u agent sudo -n /usr/bin/docker version
```

Acceptance requires no `docker` group, no `NOPASSWD: ALL`, the four exact
systemctl forms plus `agent-host-admin`, and denial of both negative probes.
Do not include environment files, credential contents, or private sudoers
backups in captured evidence.

## Functional acceptance

Build the release on the trusted workstation and deploy it with the versioned
recipe:

```bash
dotnet publish runner/AgentRunner.csproj -c Release -o <publish-dir>
bash scripts/remote-agent-host-deploy.sh \
  --host agent-runner-01 \
  --release-dir <publish-dir> \
  --release-id <immutable-release-id> \
  --role both
```

The recipe must prove `scp`, root-owned activation, restart and active status of
both units, and a fresh Coding and Review capability line. After that:

1. Run one normal Coding task through claim, CLI execution, immutable result
   handoff, and completion.
2. Run at least one exact-SHA Remote Review and record its terminal `Pass` or
   `ProductFailure` report plus cleanup proof.
3. Confirm the Coding startup Git capability is `ready` or
   `ready-no-workflow-scope`, and confirm the Review capability advertisement is
   present after restart.
4. Re-run the two denied privilege probes from a new `agent` login.

Record task keys, attempt ids, timestamps, release id, unit states, capability
statuses, and command exit codes. Do not record prompts, bearer credentials,
CLI auth files, Git private keys, or environment-file contents.

## Change log

| Date | State | Change or evidence |
|---|---|---|
| 2026-08-02 | Finding | Task audit reported `NOPASSWD: ALL` and Docker group membership for `agent` on `agent-runner-01`. |
| 2026-08-09 | Implemented in repository | Added policy `2026-08-02.1`, fixed deploy boundary, scoped deploy recipe, onboarding migration, tests, and this non-secret runbook. |
| 2026-08-09 | Live application pending | `ssh agent-runner-01` failed before authentication because the host name was not resolvable from the managed task environment. No host state was changed and no live acceptance claim is made. |

