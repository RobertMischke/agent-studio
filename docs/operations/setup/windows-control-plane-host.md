# Windows control-plane host

Status: Product setup doc for the Windows machine that runs Agent Studio
(Task Server + admin UI) and dials out to a remote Linux runner host over an
SSH reverse tunnel.

Related work: AGT-2664 (fold the loose tunnel keeper/watchdog scripts into
guided setup, admin-UI status, and product docs). This page is the Windows
sibling of [linux-runner-host.md](./linux-runner-host.md) and the product home
for what [remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md)
still documents as "Option A".

## What the Windows control-plane host is

In the current interim topology, the Windows machine that runs Studio is also
the one that must keep a private route to the remote Linux runner host alive:

```
[Windows control-plane host]             [Linux runner host: agent-runner]
  Task Server  127.0.0.1:5031  ◄───SSH reverse tunnel───  127.0.0.1:15031
                                                             agent-host --server http://127.0.0.1:15031
```

Two cooperating processes own that route, both registered as Windows
Scheduled Tasks so they survive logoff and reboot without an interactive
session:

- **The keeper** (`AgentRunner-TunnelKeeper`) owns the actual `ssh.exe -R`
  forward: a functional probe (not just "is the process alive"), bounded
  replacement, and rate-limited logging.
- **The watchdog** (`AgentRunner-TunnelWatchdog`) probes the same route every
  60 seconds from outside the keeper's own process tree, and after two
  consecutive failures kills the stuck remote listener and restarts the
  keeper task, then verifies recovery and journals every event.

The scripts that implement both are unchanged and remain the source of truth:
[`deploy/windows/agent-runner-tunnel/tunnel-keeper.ps1`](../../../deploy/windows/agent-runner-tunnel/tunnel-keeper.ps1)
and
[`deploy/windows/agent-runner-tunnel/tunnel-watchdog.sh`](../../../deploy/windows/agent-runner-tunnel/tunnel-watchdog.sh).
This page and the guided setup below are the **product-integration surface**
around them: one setup command, one status view, one place to read about
elevation. It is not a second implementation.

This is an interim, local-profile topology. Once the central Task Server URL
work (AGT-2404) lands, the Agent Host connects directly to a private
authenticated Task Server URL and this whole page stops applying. Do not keep
a tunnel merely because supervision now makes it safer.

## Provision: guided setup

Run the guided installer from the Studio checkout root:

```powershell
Set-Location C:\Projects\agent-studio
.\deploy\windows\agent-runner-tunnel\install-tunnel-supervision.ps1 `
    -SshTarget agent-runner `
    -RemotePort 15031 `
    -TaskServerPort 5031 `
    -DevspacePath C:\Projects\agent-taskboard-devspace
```

This one script is the product-facing entry point; the **Execution Hosts**
admin page under "Windows control-plane host" builds and shows the same
command with your own values so you can copy it directly.

### The elevation step

Registering an at-startup Scheduled Task with an S4U principal needs one
elevated session; Windows does not allow it from a standard session. The
script handles this itself:

1. It checks whether the current session is already elevated.
2. If not, it prints a short explanation of **why** (the at-startup
   registration requirement above, not the tasks' ongoing operation - both
   tasks run afterward with a limited, non-interactive S4U principal, not as
   Administrator) and requests elevation through the normal Windows UAC
   consent prompt.
3. Once elevated (in the same session or the relaunched one), it registers
   both `AgentRunner-TunnelKeeper` and `AgentRunner-TunnelWatchdog` via
   [`register-tunnel-keeper.ps1`](../../../deploy/windows/agent-runner-tunnel/register-tunnel-keeper.ps1)
   and
   [`register-tunnel-watchdog.ps1`](../../../deploy/windows/agent-runner-tunnel/register-tunnel-watchdog.ps1).

Both registrations stay idempotent (`Register-ScheduledTask -Force`), so
re-running the installer after a config change or an upgrade is safe. You are
asked for elevation only when a Scheduled Task actually needs to be created or
updated; reading status afterward (the admin panel, or `schtasks /Query`)
never needs it.

## Status in the Execution Hosts admin panel

Workspace Settings → Execution Hosts shows a "Windows control-plane host"
panel alongside the per-host Task Server route, using the same small
dot-and-label chip convention as the rest of that page: calm by default and
acute only when a task is not registered (R4). It reports, read-only and
without elevation:

- Keeper and watchdog presence: not registered, registered, running, or
  disabled, each read via `schtasks /Query`.
- The watchdog's last successful heal (a full probe-fail → cleanup → keeper
  restart → verify cycle) and how many heal attempts have failed since, read
  from the watchdog's own append-only journal.

This is served by `GET /api/v1/windows-tunnel-supervision/status`
(`backend/Features/WindowsTunnelSupervision/`). The endpoint only applies when
the Task Server process itself is running on Windows; elsewhere it reports
`isWindowsHost: false` and a short explanation instead of a task status.

To show heal history (not just task presence), set the watchdog journal path
in the Task Server's configuration:

```json
{
  "WindowsTunnelSupervision": {
    "WatchdogLogPath": "C:\\Projects\\agent-taskboard-devspace\\.tunnel-watchdog.log"
  }
}
```

This is the same `<DevspacePath>/.tunnel-watchdog.log` the watchdog itself
already writes; nothing new is generated for the admin panel.

## Configure

| Setting | Where | Default | Purpose |
|---|---|---|---|
| `-SshTarget` | installer / register scripts | `agent-runner` | SSH alias for the runner host, defined in the studio user's `~/.ssh/config`. |
| `-RemotePort` | installer / register scripts | `15031` | Loopback port the reverse forward opens on the runner host. |
| `-TaskServerPort` | installer / register scripts | `5031` | Local Task Server port the tunnel exposes to the runner host. |
| `-DevspacePath` | installer / register scripts | parent of the Studio checkout | Where the watchdog journal and operator-alarm channel live. |
| `-KeeperTaskName` / `-WatchdogTaskName` | installer | `AgentRunner-TunnelKeeper` / `AgentRunner-TunnelWatchdog` | Scheduled Task names; only change these if you run more than one tunnel pair. |
| `-ProbeIntervalSeconds` / `-FailureThreshold` | installer / watchdog register | `60` / `2` | Watchdog cadence and how many consecutive failures trigger a heal. |
| `-OperatorAlarmPath` | installer / watchdog register | `<DevspacePath>/.operator-alarm.log` | Append-only channel for the "heal failed twice" alarm line. |
| `WindowsTunnelSupervision:WatchdogLogPath` | Task Server `appsettings` | unset | Enables heal history in the admin panel; see above. |
| `WindowsTunnelSupervision:KeeperTaskName` / `WatchdogTaskName` | Task Server `appsettings` | same as script defaults | Only needed if you renamed the Scheduled Tasks. |

## Verify

1. Run the guided installer above and approve the UAC prompt.
2. `schtasks /Query /TN AgentRunner-TunnelKeeper /FO LIST /V` and the same for
   `AgentRunner-TunnelWatchdog` both show `Status: Running` (or `Ready`
   between the keeper's five-minute fallback triggers).
3. The Execution Hosts admin panel shows both tasks as registered/running.
4. From the runner host: `curl -fsS http://127.0.0.1:15031/healthz` succeeds.
5. Run the live fault test from the Windows control-plane host. It force-kills
   the runner-side listener, then polls up to `TimeoutSeconds` for the
   watchdog-owned recovery and writes its journal excerpt to `JOB_RESULTS_DIR`:

   ```powershell
   .\deploy\windows\agent-runner-tunnel\test-tunnel-watchdog-forced-kill.ps1 `
       -SshTarget agent-runner `
       -RemotePort 15031 `
       -TimeoutSeconds 150
   ```

6. Confirm `<DevspacePath>\.tunnel-watchdog.log` contains two `probe_failed`
   rows, then `remote_listener_cleanup`, `keeper_restart`, and
   `heal_succeeded`, and that the admin panel's "last heal" timestamp advances
   to match.

## Troubleshooting

- **Execution Hosts shows the Windows panel as "not applicable"** - the Task
  Server process answering the browser is not running on Windows. This panel
  only ever applies to the machine actually hosting the reverse tunnel's
  Windows side.
- **A task shows "not registered" after running the installer** - re-run it;
  the UAC prompt may have been cancelled. Nothing is partially registered:
  each `Register-ScheduledTask` call is atomic.
- **Heal history stays empty even though the panel shows both tasks
  running** - set `WindowsTunnelSupervision:WatchdogLogPath` (see Configure
  above); status queries the log path only when it is configured.
- **Everything else** (route unreachable, dropped tunnel, mid-run
  disconnects) - see [remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md),
  which still owns the full connectivity and health-check runbook.
