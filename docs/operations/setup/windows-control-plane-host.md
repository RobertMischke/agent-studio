# Windows control-plane host

This is the product setup path for a Windows Agent Studio control plane that
provides a private reverse tunnel to a Linux Agent Host. The guided
**Workspace Settings > Execution Hosts > Set up agent host** flow uses these
same assets when **Reverse tunnel** is selected.

The tunnel keeper and watchdog are Agent Studio components. They are installed
into <code>%LOCALAPPDATA%\Agent Studio\Tunnel</code>, registered as Windows
Scheduled Tasks, and reported in Execution Hosts beside the Task Server route.
Do not register scripts from a development checkout.

## Prerequisites

- Windows OpenSSH client with an SSH alias such as <code>agent-runner</code>.
- Git for Windows, including <code>C:\Program Files\Git\bin\bash.exe</code>.
- A Linux Agent Host that accepts the SSH key without an interactive password.
- Agent Studio and its local Task Server listening on
  <code>127.0.0.1:5031</code>.

Verify the SSH control route first:

~~~powershell
ssh.exe -T -o BatchMode=yes agent-runner "printf 'ssh-ready\n'"
~~~

## Guided setup and administrator consent

In Agent Studio, open **Workspace Settings > Execution Hosts**, expand the
host, and choose **Set up agent host**. Select **Reverse tunnel**. The dialog
explains the one-time elevation and requires explicit confirmation before it
can queue the visible setup task.

The task runs the product installer:

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\docs\operations\setup\windows-control-plane-host\install-tunnel-supervision.ps1" -SshTarget agent-runner -RemotePort 15031 -TaskServerPort 5031
~~~

The non-elevated process explains why administrator access is needed before it
opens Windows User Account Control. Approve that prompt once to let Agent
Studio register <code>AgentRunner-TunnelKeeper</code> and
<code>AgentRunner-TunnelWatchdog</code>. Choosing **No** leaves registration
pending and the guided setup stops before changing the remote host.

The installer is idempotent. It:

1. Copies the keeper, watchdog, registration helpers, and fault test from this
   setup asset directory into the Agent Studio application-data directory.
2. Registers both startup Scheduled Tasks against the installed copies.
3. Starts both tasks.
4. Writes <code>state\registration.json</code> only after both tasks can be
   read back from Task Scheduler.

The Scheduled Tasks never execute a file from the repository or a devspace.
An Agent Studio update can run the same installer to refresh the installed
copies and task definitions.

## What the two tasks do

<code>AgentRunner-TunnelKeeper</code> owns the long-lived
<code>ssh.exe -R</code> process. It probes the remote health route from the
Linux host, replaces only the matching reverse forward, and captures SSH
stdout and stderr under the product state directory.

<code>AgentRunner-TunnelWatchdog</code> performs a functional probe every
minute. After two consecutive failures it removes a stale matching remote
listener, restarts the keeper, and verifies recovery. Two failed heal attempts
append an operator alarm.

The implementation assets live beside this guide:

- [install-tunnel-supervision.ps1](windows-control-plane-host/install-tunnel-supervision.ps1)
- [tunnel-keeper.ps1](windows-control-plane-host/tunnel-keeper.ps1)
- [tunnel-watchdog.sh](windows-control-plane-host/tunnel-watchdog.sh)
- [register-tunnel-keeper.ps1](windows-control-plane-host/register-tunnel-keeper.ps1)
- [register-tunnel-watchdog.ps1](windows-control-plane-host/register-tunnel-watchdog.ps1)
- [test-tunnel-watchdog-forced-kill.ps1](windows-control-plane-host/test-tunnel-watchdog-forced-kill.ps1)

## Verify in Agent Studio

Open the host in **Execution Hosts > Connection**. A tunnel-backed host shows:

- Task Server route: reachable, degraded, or unreachable.
- Tunnel keeper: registered state and latest Task Scheduler state.
- Tunnel watchdog: registered state and current status heartbeat.
- Last tunnel heal: timestamp and succeeded or failed.

The bottom execution status indicator uses the same projection. A stale,
stopped, or degraded supervision task adds a warning and its tooltip directs
the operator to Execution Hosts.

The status files live below:

~~~text
%LOCALAPPDATA%\Agent Studio\Tunnel\state\registration.json
%LOCALAPPDATA%\Agent Studio\Tunnel\state\keeper.json
%LOCALAPPDATA%\Agent Studio\Tunnel\state\watchdog.json
%LOCALAPPDATA%\Agent Studio\Tunnel\state\watchdog-events.log
~~~

The watchdog status is refreshed every probe cycle. Agent Studio treats it as
stale after three minutes, so a dead watchdog cannot continue to look healthy.

## Live fault test

Run the forced-kill test only after the normal route is healthy:

~~~powershell
& "$env:LOCALAPPDATA\Agent Studio\Tunnel\test-tunnel-watchdog-forced-kill.ps1" -SshTarget agent-runner -RemotePort 15031
~~~

The test kills only the matching tunnel process and waits for watchdog-owned
recovery. Its result can be directed to a task results directory with
<code>-ResultsDirectory</code>.

## Remove or repair

Re-run the installer to repair missing files or task definitions. To
intentionally remove this interim topology, first point the Agent Host at a
central HTTPS Task Server, verify direct health, then unregister the two
Scheduled Tasks. Do not remove tunnel supervision while the runner still uses
the remote loopback listener.

For the Linux side of the runner installation, continue with
[linux-runner-host.md](linux-runner-host.md). For topology rationale and the
systemd alternative, see
[remote-runner-persistent-connection.md](remote-runner-persistent-connection.md).
