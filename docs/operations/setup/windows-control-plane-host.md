# Windows control-plane host (Studio + reverse tunnel)

Status: interim operations runbook for the Windows machine that hosts Studio
and the Task Server while a Linux Agent Host is reachable only over SSH. This
is the operator-facing companion to
[remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md),
which owns the connection design and health-check contract; read that first.
This page is the guided setup path, in the same spirit as
[linux-runner-host.md](./linux-runner-host.md) for the Linux side.

## Why this exists

`agent-orchestrator-setup`, the guided installer used by
[multi-machine.md](./multi-machine.md), only supports Linux x64. A Windows
machine running Studio as the control plane needs its own guided path for the
one piece of Windows-specific infrastructure it owns: the supervised reverse
SSH tunnel to a Linux Agent Host (Option A in
[remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md#option-a---windows-scheduled-task-studio-dials-out--r)).

That infrastructure used to be two scripts an operator ran by hand. It is now
one guided entry point,
[`deploy/windows/agent-runner-tunnel/setup-tunnel-supervision.ps1`](../../../deploy/windows/agent-runner-tunnel/setup-tunnel-supervision.ps1),
so the registration step, the elevation it needs, and the resulting status are
all part of the product's setup surface rather than a loose script an operator
has to already know about.

## 1. Register the tunnel keeper and watchdog from Studio

Open **Workspace Settings -> Execution Hosts**, expand the Linux host, and
choose **Set up agent host**. Select **Reverse tunnel** as the connection mode.
The guided flow adds a **Register Windows tunnel supervision** step before
provider authentication and the visible setup task. Enter the Windows
devspace path, copy the generated PowerShell command, and run it from the
Agent Studio checkout. The setup task stays gated until **Refresh status**
confirms that both Scheduled Tasks are registered.

The command is run in a terminal because Windows must present consent to the
signed-in operator. The Task Server does not attempt to elevate from a web
request or a service session. The generated command has this shape:

```powershell
Set-Location C:\Projects\agent-studio
.\deploy\windows\agent-runner-tunnel\setup-tunnel-supervision.ps1 `
    -SshTarget agent-runner `
    -RemotePort 15031 `
    -OrchestratorPort 5031 `
    -DevspacePath C:\Projects\agent-taskboard-devspace
```

Always pass `-DevspacePath` explicitly. The script's default (four directories
above the checkout) is a generic fallback, not the devspace convention this
runbook and [remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md)
otherwise use; relying on the default here would point the watchdog's own log
and state files (`<devspace>/.tunnel-watchdog.log`,
`<devspace>/.tunnel-watchdog-state/`) at a different directory than a manual
run of `register-tunnel-watchdog.ps1` with the documented path, silently
splitting one host's logs across two locations.

The product entry point wraps
[`register-tunnel-keeper.ps1`](../../../deploy/windows/agent-runner-tunnel/register-tunnel-keeper.ps1)
and
[`register-tunnel-watchdog.ps1`](../../../deploy/windows/agent-runner-tunnel/register-tunnel-watchdog.ps1)
which remain the Scheduled Task implementation. What the guided surface adds
is:

1. It explains, before doing anything, exactly why administrator elevation is
   needed (Scheduled Task registration under an S4U principal is a one-time
   privileged operation; the tasks themselves run unattended afterward with no
   further elevation).
2. It asks for explicit confirmation, then requests elevation with a UAC
   prompt (`Start-Process -Verb RunAs`). Pass `-Force` to skip only the
   script's own confirmation step on a re-run; the OS elevation prompt itself
   is never skipped.
3. The elevated child registers both Scheduled Tasks, then the script prints
   and persists a combined status snapshot - registered, running, and last
   heal - to
   `%LOCALAPPDATA%\AgentTaskboard\tunnel-keeper\supervision-status.json`.
4. The elevated child's console window closes the instant it finishes, before
   an operator could read a failure there. A transcript of that run is kept
   at `%LOCALAPPDATA%\AgentTaskboard\tunnel-keeper\elevated-registration.log`;
   the unprivileged parent points there if registration exits non-zero.
5. The watchdog refreshes the combined product snapshot after every probe by
   calling the same entry point with `-StatusOnly`. This keeps registered,
   running, last-probe, and last-heal visibility current without another UAC
   prompt.

Re-running the script is idempotent, same as the two scripts it wraps.

## 2. Check status without re-registering

Reading Scheduled Task state and the keeper/watchdog log files needs no
elevation. Use `-StatusOnly` any time, from an ordinary session, to refresh
the snapshot and print a summary. The registered watchdog does this after each
probe; the manual command is a diagnostic and recovery path:

```powershell
.\deploy\windows\agent-runner-tunnel\setup-tunnel-supervision.ps1 `
    -DevspacePath C:\Projects\agent-taskboard-devspace `
    -StatusOnly
```

Pass the same `-DevspacePath` used at registration - the watchdog half of the
status comes from that directory's `.tunnel-watchdog-state\status.json`.

```text
Tunnel supervision status
  keeper   : registered=True state=Running lastStatus=healthy lastObservedAt=...
  watchdog : registered=True state=Running lastProbe=... (ok) lastHeal=... (succeeded)
  snapshot : C:\Users\...\AppData\Local\AgentTaskboard\tunnel-keeper\supervision-status.json
```

## 3. Visibility in Studio

The Task Server backend reads `supervision-status.json` when it runs
colocated with the keeper and watchdog (the current single-Windows-machine
topology) and serves it at `GET /api/system/tunnel-supervision`. **Workspace
Settings -> Execution Hosts** shows the same registered / running / last-heal
facts next to the existing Task Server route status
([remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md#health-check-and-connection-loss-behavior)),
so an operator diagnosing "route unreachable" on a host card does not have to
separately go find the Windows Scheduled Task Library or tail a log file. The
panel is hidden entirely on a deployment where the file has never been
written - most deployments, which do not use the interim Windows tunnel at
all. The reverse-tunnel step of **Set up agent host** still shows the
not-configured state so a first-time operator can complete registration from
that flow.

The backend never triggers elevation or registration itself; it only reads
the file the script already writes. Registration stays an explicit,
consent-gated operator action run from a terminal, not something a web
request can trigger silently on a Windows admin machine.

## Is this still the right topology?

Same answer as
[remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md#is-the-tunnel-still-the-right-topology):
this is an interim path, retired once AGT-2404's central Task Server URL
removes the Windows workstation from the Agent Host's availability path. Do
not build further product surface on top of the reverse tunnel; this card
folds the existing scripts into the setup and admin UI, it does not extend
what they do.
