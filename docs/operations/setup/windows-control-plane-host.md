# Windows control-plane host (tunnel keeper and watchdog)

Status: operator runbook for the interim local-profile topology, until the
central Task Server URL work (AGT-2404) removes the tunnel entirely.

Related work: AGT-2664 folded the previously loose
`deploy/windows/agent-runner-tunnel/` scripts into Studio's own host-setup
surface (guided registration, live status, elevation consent). This page is
the Windows-side sibling to
[linux-runner-host.md](./linux-runner-host.md): that page documents the
remote Linux `agent-host` daemon; this page documents the Windows machine that
runs Studio itself when it reaches that daemon through a reverse SSH tunnel.
Read [remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md)
first for why a supervised tunnel exists at all and for the host-dials-in
alternative (Option B, `autossh` plus systemd), which this page does not
duplicate.

## What the Windows control-plane host is

During the MVP local-profile topology, Studio (backend, Task Server, and
Engine) runs natively on an operator's Windows workstation and reaches the
Linux `agent-host` daemon only through a reverse SSH tunnel: Windows dials out
to the Linux host and asks for a `-R` forward, so `127.0.0.1:15031` on the
Linux side reaches Studio's `127.0.0.1:5031`. Two independent Scheduled Tasks
keep that route alive:

- **`AgentRunner-TunnelKeeper`** owns the `ssh -R` forward process itself. It
  runs at startup and every five minutes, functionally probes the route
  through SSH (not just "is the process alive"), and replaces a dead forward.
- **`AgentRunner-TunnelWatchdog`** is a slower, independent second tier. It
  probes every 60 seconds and, after two consecutive failures, kills any stale
  remote listener, restarts the keeper task, and verifies recovery. A second
  consecutive failed heal appends one line to the operator-alarm channel.

Both scheduled tasks run under the operator's own account with `RunLevel
Limited` (S4U logon), not as administrator. Only *registering* an AtStartup
Scheduled Task needs administrator rights once.

## Guided setup (primary path)

Register both tasks from the product UI instead of a manual PowerShell
session:

1. **Workspace Settings -> Execution Hosts.** Expand the local host's row and
   open its **Connection** section. The **Windows tunnel keeper** panel shows
   live keeper/watchdog status and a **Register tunnel keeper** button.
2. Alternatively, open **Set up agent host** on a remote Linux host and choose
   connection mode **Reverse tunnel**. The same panel appears there as a
   local prerequisite, because reverse-tunnel mode only works once this
   Windows machine owns a healthy forward.
3. Choosing **Register tunnel keeper** runs the repository-owned
   `deploy/windows/agent-runner-tunnel/setup-windows-tunnel.ps1`. If the
   Studio process is not already elevated, the script explains why once
   ("registering an AtStartup Scheduled Task needs administrator rights
   once") and opens a native Windows "User Account Control" consent prompt.
   Approving it registers both tasks; the tasks themselves keep running
   unprivileged. Declining it leaves nothing registered and reports that
   choice back to the UI instead of failing silently.

The panel polls `GET /api/v1/management/windows-tunnel/status` every 30
seconds and shows:

| Field | Source |
|---|---|
| Keeper / Watchdog registered + Scheduled Task state | `Get-ScheduledTask` / `Get-ScheduledTaskInfo` |
| Keeper health | The keeper's own `%LOCALAPPDATA%\AgentTaskboard\tunnel-keeper\state.json` |
| Last heal | The watchdog's `<devspace>\.tunnel-watchdog.log` journal |
| Alarm | Whether the most recent `severity=alarm` line in the operator-alarm channel is newer than the last successful heal |

On a non-Windows Studio host the same endpoint returns `platform:
"unsupported"` and the panel renders a quiet not-applicable message instead of
an error, because the tunnel keeper only applies to a Windows control plane.

## What the guided flow runs

The scripts remain the implementation; the product only adds registration UX
and visibility around them:

- [`setup-windows-tunnel.ps1`](../../../deploy/windows/agent-runner-tunnel/setup-windows-tunnel.ps1)
  self-elevates with explicit consent, then calls the existing
  [`register-tunnel-keeper.ps1`](../../../deploy/windows/agent-runner-tunnel/register-tunnel-keeper.ps1)
  and
  [`register-tunnel-watchdog.ps1`](../../../deploy/windows/agent-runner-tunnel/register-tunnel-watchdog.ps1)
  unchanged. Because `Start-Process -Verb RunAs` cannot redirect standard
  output across the UAC boundary, the elevated child writes its JSON result to
  a temporary file that the waiting non-elevated parent reads and re-prints,
  so the backend always reads one JSON line from stdout regardless of whether
  elevation happened in this invocation or the process was already elevated.
- [`tunnel-status.ps1`](../../../deploy/windows/agent-runner-tunnel/tunnel-status.ps1)
  is the read-only counterpart: it never registers a task and never requests
  elevation, so the status panel can poll it on an ordinary schedule.

The backend feature is
[`backend/Features/Management/WindowsTunnelProvisioning.cs`](../../../backend/Features/Management/WindowsTunnelProvisioning.cs),
following the same bounded-process-launch pattern already used for provider
authentication provisioning
([`ProviderAuthProvisioning.cs`](../../../backend/Features/Management/ProviderAuthProvisioning.cs))
and for the boot-time Windows worktree sweep
([`WindowsWorktreeOrphanSweeper.cs`](../../../backend/Features/Runner/WindowsWorktreeOrphanSweeper.cs)).
It is registered only in the monolith (`backend/`), not the standalone Task
Server, because the Windows tunnel keeper is specific to a Windows-hosted
local-profile Studio instance, the same scope as provider-auth provisioning.

## API

| Route | Purpose |
|---|---|
| `GET /api/v1/management/windows-tunnel/status` | Read-only keeper/watchdog Scheduled Task state, keeper health, last heal, and alarm state. `no-store`. |
| `POST /api/v1/management/windows-tunnel/register` | Runs the self-elevating setup script. Body: `sshTarget`, `remotePort`, `taskServerPort`, `intervalMinutes`, `probeIntervalSeconds`, `failureThreshold` (the same bounds as the underlying `.ps1` parameters). Returns `{ platform, ok, elevated, detail, requestedAt }`. |

Both routes require the same operator/owner authorization already enforced
for `/api/v1/management/remote-hosts/*`.

## Manual fallback

The guided flow is the product path. For scripted environments or
troubleshooting, the underlying scripts still work stand-alone, run directly
from an elevated PowerShell session in the Studio checkout:

```powershell
Set-Location C:\Projects\agent-studio
.\deploy\windows\agent-runner-tunnel\register-tunnel-keeper.ps1 `
    -SshTarget agent-runner -RemotePort 15031 -TaskServerPort 5031 -IntervalMinutes 5

.\deploy\windows\agent-runner-tunnel\register-tunnel-watchdog.ps1 `
    -DevspacePath C:\Projects\agent-taskboard-devspace `
    -SshTarget agent-runner -RemotePort 15031 `
    -KeeperTaskName AgentRunner-TunnelKeeper `
    -ProbeIntervalSeconds 60 -FailureThreshold 2
```

Read-only status without the UI:

```powershell
.\deploy\windows\agent-runner-tunnel\tunnel-status.ps1
```

Full parameter and behavior reference for both `register-*.ps1` scripts, the
keeper's functional probe, and the watchdog's heal sequence remains in the
"Option A" section of
[remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md).

## Verify

1. Register through the guided flow (or the manual fallback above) and
   confirm the status panel shows both tasks registered and the keeper
   healthy.
2. From the host: `curl -fsS http://127.0.0.1:15031/healthz` succeeds.
3. Run the live fault test from the Windows studio and confirm the panel's
   "Last heal" timestamp advances after the injected failure:

   ```powershell
   .\deploy\windows\agent-runner-tunnel\test-tunnel-watchdog-forced-kill.ps1 `
       -SshTarget agent-runner -RemotePort 15031 -TimeoutSeconds 150
   ```

4. Confirm `<devspace>\.tunnel-watchdog.log` contains two `probe_failed` rows
   followed by `remote_listener_cleanup`, `keeper_restart`, and
   `heal_succeeded`, and that the status panel's alarm indicator stays off.

## Is the tunnel still the right topology?

The reverse tunnel is an interim local-profile route, not the target control
plane. AGT-2404's central Task Server work removes the Windows workstation
from the runner's availability path entirely; the guided registration above
exists to make the interim route safe and visible, not to encourage keeping
it once a central private Task Server URL is available.
