# Remote runner: persistent connection

Status: interim operations runbook for unattended remote operation
("Dauerbetrieb"), until the Phase 2 central URL lands
([remote-ready-kickoff-2026-07.md](../../research/remote-ready-kickoff-2026-07.md)
Phase 2). It is the companion to the host provisioning runbook
[linux-runner-host.md](./linux-runner-host.md); read that first.

The standalone runner reaches the Task Server **only over an SSH tunnel** during
the MVP (no inbound port on the host beyond SSH until central-URL auth exists).
A one-off `ssh` invocation is enough to run one task by hand, but unattended
operation needs the tunnel to be a **supervised service that reconnects on its
own** after a reboot, a laptop sleep, or a network blip. Without that, the
tunnel dies silently and every later run fails at the first API call.

This page covers two things:

1. Keep the tunnel up as a service (autossh/systemd on the host, or a scheduled
   task on the Windows studio side).
2. The runner's connectivity **health-check**, so a dropped tunnel is reported
   once, cleanly, instead of cascading into a launch failure.

## The connection

```
[Windows studio]                         [Linux runner host: agent-runner]
  Task Server  127.0.0.1:5031  ◄───SSH reverse tunnel───  127.0.0.1:15031
                                                             agent-runner --server http://127.0.0.1:15031
```

- The Task Server runs on the studio (stable, `127.0.0.1:5031`). It is **not**
  exposed on the network; the tunnel is the only path in.
- The tunnel maps the studio's `5031` onto the host's loopback port `15031`.
  On the host, `RUNNER_SERVER_URL=http://127.0.0.1:15031` reaches the Task
  Server. Pick any free host port; `15031` is the convention used here.
- Which end **initiates** the SSH connection depends on reachability, and that
  decides which forwarding flag you use:
  - The studio is a workstation behind home NAT; the host has a public IP with
    inbound SSH. So the studio **dials out** to the host and asks for a
    **reverse** forward (`-R`): the listening port opens on the host. This is
    the current test-host topology and the default below (Option A).
  - If your topology lets the host reach the studio's SSH instead (a bastion, a
    static endpoint, or a VPN), run the supervisor on the host with a **local**
    forward (`-L`) — same resulting port on the host (Option B).

Either way the host ends up with `127.0.0.1:15031 → studio 5031`, and nothing
below the tunnel line changes.

## Option A - Windows scheduled task (studio dials out, `-R`)

Matches the current Hetzner test host: the studio initiates an outbound reverse
tunnel and a Windows Scheduled Task keeps it alive.

`agent-runner` is an SSH alias defined in the studio user's `~/.ssh/config`:

```sshconfig
Host agent-runner
    HostName 88.99.136.78
    User runner
    IdentityFile ~/.ssh/agent-runner
    ServerAliveInterval 30
    ServerAliveCountMax 3
    ExitOnForwardFailure yes
```

Auto-reconnect wrapper (`reverse-tunnel.ps1`) - `ssh` exits when the link drops
or a keepalive lapses (`ServerAliveCountMax`), and the loop dials straight back:

```powershell
# reverse-tunnel.ps1 - keep the Task Server reachable on the runner host.
while ($true) {
    ssh -N -R 15031:localhost:5031 agent-runner
    Start-Sleep -Seconds 5   # link dropped; back off briefly, then reconnect
}
```

Register it to start at logon (or at boot with a service account) and restart if
it ever exits:

```powershell
$action  = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument '-NoProfile -WindowStyle Hidden -File C:\ops\reverse-tunnel.ps1'
$trigger = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet -RestartInterval (New-TimeSpan -Minutes 1) `
    -RestartCount 999 -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName 'agent-runner reverse tunnel' `
    -Action $action -Trigger $trigger -Settings $settings
```

`ExitOnForwardFailure yes` matters: if the host's `15031` is still held by a
half-dead previous session, `ssh` fails fast instead of connecting **without**
the forward, which would otherwise present a live SSH session whose tunnel is
dead - the worst case for a health-check to reason about.

## Option B - autossh + systemd on the host (host dials in, `-L`)

Use this when the host can reach the studio's SSH endpoint. `autossh` is the
supervisor; systemd restarts it across reboots. `StudioSsh` below is the host's
SSH alias for the studio (or bastion).

```ini
# /etc/systemd/system/agent-runner-tunnel.service
[Unit]
Description=Persistent tunnel to the agent-taskboard Task Server
After=network-online.target
Wants=network-online.target

[Service]
User=runner
Environment=AUTOSSH_GATETIME=0
# -M 0 disables autossh's own monitor port; the SSH keepalive below detects a
# dead link and makes ssh exit, which autossh then restarts.
ExecStart=/usr/bin/autossh -M 0 -N \
    -o ServerAliveInterval=30 -o ServerAliveCountMax=3 \
    -o ExitOnForwardFailure=yes \
    -L 15031:localhost:5031 StudioSsh
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

```bash
sudo apt-get install -y autossh
sudo systemctl daemon-reload
sudo systemctl enable --now agent-runner-tunnel.service
systemctl status agent-runner-tunnel.service
```

The `ServerAliveInterval` / `ServerAliveCountMax` pair is what turns a silent
half-open TCP connection into a prompt exit + reconnect; keep it on whichever
option you choose.

## Health-check and connection-loss behavior

The runner treats a dropped tunnel as an **expected, recoverable** state, not a
crash. Two mechanisms make that clean:

- **Readiness probe.** `agent-runner --health-check` hits the Task Server's
  open `/healthz` and exits `0` when reachable, `4` when not, printing a
  tunnel-pointing reason. It touches no task, needs no task key, and is safe to
  run on a schedule:

  ```bash
  agent-runner --health-check --server http://127.0.0.1:15031
  # health-check ok: task server reachable at http://127.0.0.1:15031      -> exit 0
  # health-check failed: cannot reach the task server ... tunnel ... down  -> exit 4
  ```

  Gate work on it before handing the runner a task, e.g.:

  ```bash
  agent-runner --health-check && agent-runner "$TASK_KEY"
  ```

- **Run preflight.** A normal `agent-runner <TASK-KEY>` probes `/healthz`
  **before** it registers, acquires the lease, or spawns the CLI. If the tunnel
  is down it logs one line -
  `connection lost: cannot reach the task server at <url> ...` - and exits `4`
  without a half-started run. This is the "reports connection loss cleanly
  instead of a launch-fail cascade" guarantee: the failure names the tunnel, not
  a phantom lease or CLI problem, and no lease is acquired or CLI spawned on a
  dead link. The probe uses a short (10 s) timeout so a black-holed tunnel fails
  fast rather than hanging on the full request timeout.

Exit `4` is the single "task server unreachable / rejected" code the runbook
already documents; the preflight and `--health-check` simply make it fire early,
first, and with a diagnostic that points at the tunnel.

A mid-run drop is handled separately and already: the lease heartbeat logs
`heartbeat error (will retry)` and rides out a transient blip while the TTL has
headroom; a genuine takeover surfaces as `lease lost` (exit `3`). See
[linux-runner-host.md](./linux-runner-host.md) §4.

## Verify

1. Bring the tunnel service up (Option A or B).
2. From the host: `curl -fsS http://127.0.0.1:15031/healthz` returns `ok`, and
   `agent-runner --health-check --server http://127.0.0.1:15031` exits `0`.
3. Kill the tunnel (stop the service / end the scheduled task). The same
   `--health-check` now exits `4` with a "tunnel down" reason within ~10 s, and
   a `agent-runner <TASK-KEY>` run refuses at preflight instead of attempting a
   lease. Restart the service; the next probe returns to `0`.
