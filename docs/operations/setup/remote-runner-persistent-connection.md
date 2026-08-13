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
tunnel and a Windows Scheduled Task keeps it alive.

`agent-runner` is an SSH alias defined in the studio user's `~/.ssh/config`:

```sshconfig
Host agent-runner
    HostName <runner-host-ip>
    User agent
    IdentityFile ~/.ssh/agent-runner
    ServerAliveInterval 30
    ServerAliveCountMax 3
    ExitOnForwardFailure yes
```

Use the repository-owned functional keeper instead of a bare `ssh -N` loop:

```powershell
Set-Location C:\Projects\agent-studio
.\deploy\windows\agent-runner-tunnel\register-tunnel-keeper.ps1 `
    -SshTarget agent-runner `
    -RemotePort 15031 `
    -TaskServerPort 5031 `
    -IntervalMinutes 5
.\deploy\windows\agent-runner-tunnel\register-tunnel-watchdog.ps1 `
    -SshTarget agent-runner `
    -RemotePort 15031 `
    -ProbeIntervalSeconds 60 `
    -FailureThreshold 2 `
    -DevspaceDirectory C:\Projects\agent-taskboard-devspace
```

The registration is idempotent. It creates or updates
`AgentRunner-TunnelKeeper`, uses `IgnoreNew` to prevent overlapping repair
runs, starts the first run immediately, and repeats every five minutes. Both
the keeper and the separate `AgentRunner-TunnelWatchdog` use an S4U principal,
so they are owned by Task Scheduler and survive sign-out instead of belonging
to an interactive session. They use the current Windows identity because that
identity owns the SSH key. Confirm that the key and SSH configuration are
readable without an interactive profile prompt. A machine that needs a
different security boundary should use a dedicated service identity with its
own protected SSH key.

The watchdog is a long-running loop with an at-startup trigger. A repeated
one-minute trigger and `IgnoreNew` act only as a restart backstop if that loop
has exited; they do not create overlapping watchdog processes. Its execution
time limit is disabled so Task Scheduler does not end the service after a
session-scoped timeout.

The keeper in
[`deploy/windows/agent-runner-tunnel/tunnel-keeper.ps1`](../../../deploy/windows/agent-runner-tunnel/tunnel-keeper.ps1)
does not equate a local `ssh.exe` process with a working route. It asks the
Linux host to request `http://127.0.0.1:15031/healthz` and accepts the probe only
when SSH returns the exact `AGENT_TASK_SERVER_ROUTE_OK` sentinel. If that
functional probe fails, the keeper stops only `ssh.exe` processes matching the
configured target and exact reverse-forward tuple, waits for the old listener
to clear, then starts:

```powershell
ssh.exe -N -T `
  -o BatchMode=yes `
  -o ExitOnForwardFailure=yes `
  -o ServerAliveInterval=30 `
  -o ServerAliveCountMax=3 `
  -R 15031:127.0.0.1:5031 agent-runner
```

State and bounded transition logs live under
`%LOCALAPPDATA%\AgentTaskboard\tunnel-keeper\`. Healthy scheduled runs stay
quiet. An ongoing failure is logged on transition and at most hourly, not on
every five-minute invocation. `ExitOnForwardFailure=yes` matters: if the host's
`15031` is still held by a half-dead previous session, SSH fails fast instead
of connecting without the forward.

The SSH process is launched through `run-tunnel-ssh.ps1`. Every SSH diagnostic
and its final exit code is timestamped in
`%LOCALAPPDATA%\AgentTaskboard\tunnel-keeper\ssh-exit.log`; inspect this file to
determine why a keeper SSH process exited instead of relying on Task Scheduler's
last result alone.

Incident note, 12 August 2026: the reverse tunnel died twice and silently
stalled both claim traffic and report submissions. The pre-change keeper
discarded the SSH process output, so the exact network or SSH exit reason is
unknown. Do not assign a speculative root cause to those two deaths. The
timestamped `ssh-output`, `ssh-exit`, and `ssh-launch-failed` records now retain
the evidence needed to diagnose the next occurrence.

### Independent tunnel watchdog

The one-minute watchdog is deliberately separate from the keeper. It runs the
functional probe from the runner's point of view:

```text
ssh agent-runner 'curl -sf --max-time 6 http://127.0.0.1:15031/healthz'
```

After two consecutive failures it follows the operator recovery order: find
and terminate only the agent-owned listener for `127.0.0.1:15031`, stop and
start `AgentRunner-TunnelKeeper`, and repeat the remote health probe for up to
45 seconds. No listener is a successful no-op. On hardened hosts where
`ss -p` hides the PID, cleanup resolves the listener's cgroup and signals only
processes in that agent-owned session. A listener that cannot be resolved or
removed remains a visible failed heal.

The watchdog journals timestamped probes and repairs in the devspace file
`.tunnel-watchdog.log`. Two consecutive failed heal cycles append one alarm
line to the existing devspace `.operator-alarm` channel. A successful health
probe resets the consecutive probe and heal counters.

The remote cleanup uses the normal `agent-runner` SSH account. It does not use
`sudo` and cannot terminate another account's listener. If another account owns
port 15031, the repair fails visibly and the second failed cycle raises the
operator alarm.

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
3. Run the destructive acceptance test from an elevated Windows PowerShell
   after both scheduled tasks are registered:

   ```powershell
   $env:JOB_RESULTS_DIR = 'C:\path\to\task-results'
   .\deploy\windows\agent-runner-tunnel\test-tunnel-watchdog-forced-kill.ps1 `
       -SshTarget agent-runner
   ```

   The test first restarts the healthy watchdog to reset its in-memory counters,
   then stops the keeper task, kills the real runner-side listener, and binds a
   dummy listener that returns a failing `/healthz`. Success requires two
   journalled failed probes, listener cleanup, keeper restart, and a healthy
   runner-side curl within 150 seconds. The test performs bounded cleanup and
   restarts the keeper on failure.
4. Preserve the generated `forced-kill-test.md`. It records elapsed recovery,
   both S4U principals, the watchdog journal tail, and the keeper SSH exit tail.

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
