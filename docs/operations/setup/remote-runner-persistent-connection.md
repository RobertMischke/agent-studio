# Remote runner: persistent connection

Status: interim operations runbook for unattended remote operation
("Dauerbetrieb"), until the central URL lands
([distributed-agent-studio-target-architecture.md](../../concepts/distributed-agent-studio-target-architecture.md),
Task Server / central-URL phase). It is the companion to the host provisioning runbook
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
                                                             agent-host --server http://127.0.0.1:15031
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
tunnel and two Windows Scheduled Tasks (a keeper and a watchdog) keep it alive.
This is now a **product setup path**, not a loose script: guided install,
elevation handling, configuration reference, and the admin-UI status panel all
live in
[Windows control-plane host](./windows-control-plane-host.md). Read that page
for the full procedure; this section stays only as the short conceptual
summary for the connection diagram above.

`agent-runner` is an SSH alias defined in the studio user's `~/.ssh/config`:

```sshconfig
Host agent-runner
    HostName <runner-host-ip>
    User runner
    IdentityFile ~/.ssh/agent-runner
    ServerAliveInterval 30
    ServerAliveCountMax 3
    ExitOnForwardFailure yes
```

The keeper in
[`deploy/windows/agent-runner-tunnel/tunnel-keeper.ps1`](../../../deploy/windows/agent-runner-tunnel/tunnel-keeper.ps1)
does not equate a local `ssh.exe` process with a working route: it asks the
Linux host to request `http://127.0.0.1:15031/healthz` and only accepts the
probe when SSH returns the exact `AGENT_TASK_SERVER_ROUTE_OK` sentinel, then
replaces a dead forward with `ExitOnForwardFailure=yes` so a half-dead port
fails fast instead of silently not forwarding. The watchdog in
[`deploy/windows/agent-runner-tunnel/tunnel-watchdog.sh`](../../../deploy/windows/agent-runner-tunnel/tunnel-watchdog.sh)
probes the same route every 60 seconds from outside the keeper's own process
tree and, after two consecutive failures, kills the stuck remote listener and
restarts the keeper task.

Both Scheduled Tasks register with an S4U principal (no interactive logon
needed to run) and idempotent `-Force` registration, but creating an
at-startup Scheduled Task the first time needs one elevated session -
[Windows control-plane host](./windows-control-plane-host.md#the-elevation-step)
covers exactly why and how that step is handled.

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

- **Readiness probe.** `agent-host --health-check` hits the Task Server's
  open `/healthz` and exits `0` when reachable, `4` when not, printing a
  tunnel-pointing reason. It touches no task, needs no task key, and is safe to
  run on a schedule:

  ```bash
  agent-host --health-check --server http://127.0.0.1:15031
  # health-check ok: task server reachable at http://127.0.0.1:15031      -> exit 0
  # health-check failed: cannot reach the task server ... tunnel ... down  -> exit 4
  ```

  Gate work on it before handing the runner a task, e.g.:

  ```bash
  agent-host --health-check && agent-host "$TASK_KEY"
  ```

- **Run preflight.** A normal `agent-host <TASK-KEY>` probes `/healthz`
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

The long-running coding and review daemons also publish
`task-server:connectivity` with a three-minute freshness deadline and include
their last local route observation in `HostTelemetrySnapshotDto`. While the
route is broken, the host cannot send a new failure through that route. The
Task Server therefore treats expiration of the connectivity capability as the
remote alarm. Execution Hosts renders that specific capability as **Task Server
route unreachable**. The daemon logs the initial transition, escalates after
five continuous minutes, emits at most one summary per hour, and records one
recovery transition. A day-long outage no longer produces one journal entry per
claim poll.

## Verify

1. Bring the tunnel service up (Option A or B).
2. From the host: `curl -fsS http://127.0.0.1:15031/healthz` succeeds, and
   `agent-host --health-check --server http://127.0.0.1:15031` exits `0`.
3. Run the live fault test from the Windows studio. The fault injector only
   kills the runner-side listener, then waits up to 150 seconds for the
   watchdog-owned Scheduled Task restart and writes its journal excerpt to
   `JOB_RESULTS_DIR`:

   ```powershell
   .\deploy\windows\agent-runner-tunnel\test-tunnel-watchdog-forced-kill.ps1 `
       -SshTarget agent-runner `
       -RemotePort 15031 `
       -TimeoutSeconds 150
   ```

4. Confirm `<devspace>/.tunnel-watchdog.log` contains two `probe_failed` rows,
   followed by `remote_listener_cleanup`, `keeper_restart`, and
   `heal_succeeded`. With the default cadence, recovery should complete in
   about two minutes plus the bounded replacement verification time.
5. Confirm the next capability advertisement returns Execution Hosts to
   **reachable** and preserve the live test report in the task's absolute
   `results/` directory.

## Is the tunnel still the right topology?

The reverse tunnel is an interim local-profile route, not the target control
plane. The central Task Server work tracked by AGT-2404 removes the Windows
workstation from the Runner's availability path: the Agent Host connects
directly to a private authenticated Task Server URL and this scheduled task is
disabled. Do not retain a reverse tunnel merely because the keeper now makes it
safer.

When a tunnel remains necessary, supervision belongs on the side that can
initiate the connection. The current topology requires Windows to dial the
public Linux SSH endpoint, so the repository keeper is the applicable option.
If the Linux host can reach a protected studio or bastion endpoint, Option B's
`autossh` plus systemd gives stronger boot-time ownership. A central private
Task Server is preferable to either form because no workstation process then
sits on the claim, lease, log, and completion path.
