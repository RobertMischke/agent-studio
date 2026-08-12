#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
keeper="$repo_root/deploy/windows/agent-runner-tunnel/tunnel-keeper.ps1"
registration="$repo_root/deploy/windows/agent-runner-tunnel/register-tunnel-keeper.ps1"
ssh_runner="$repo_root/deploy/windows/agent-runner-tunnel/run-tunnel-ssh.ps1"
watchdog="$repo_root/deploy/windows/agent-runner-tunnel/tunnel-watchdog.ps1"
watchdog_registration="$repo_root/deploy/windows/agent-runner-tunnel/register-tunnel-watchdog.ps1"
forced_kill="$repo_root/deploy/windows/agent-runner-tunnel/test-tunnel-watchdog-forced-kill.ps1"

test -f "$keeper"
test -f "$registration"
test -f "$ssh_runner"
test -f "$watchdog"
test -f "$watchdog_registration"
test -f "$forced_kill"

grep -Fq "AGENT_TASK_SERVER_ROUTE_OK" "$keeper"
grep -Fq "curl --fail --silent --show-error --max-time 10" "$keeper"
grep -Fq "Get-CimInstance Win32_Process" "$keeper"
grep -Fq "Stop-MatchingForwards" "$keeper"
grep -Fq "Test-NativeArgument" "$keeper"
grep -Fq "[Regex]::Escape(\$forward)" "$keeper"
grep -Fq "run-tunnel-ssh.ps1" "$keeper"
grep -Fq "ssh-exit.log" "$ssh_runner"
grep -Fq "event=ssh-exit exit_code=" "$ssh_runner"
grep -Fq "ExitOnForwardFailure=yes" "$ssh_runner"
grep -Fq "ServerAliveInterval=30" "$ssh_runner"
grep -Fq -- "-MultipleInstances IgnoreNew" "$registration"
grep -Fq -- "-RepetitionInterval" "$registration"
grep -Fq -- "-LogonType S4U" "$registration"
grep -Fq "Register-ScheduledTask" "$registration"

grep -Fq "curl -sf --max-time 6" "$watchdog"
grep -Fq "ProbeFailureThreshold = 2" "$watchdog"
grep -Fq "Stop-ScheduledTask -TaskName \$KeeperTaskName" "$watchdog"
grep -Fq "Start-ScheduledTask -TaskName \$KeeperTaskName" "$watchdog"
grep -Fq "listener-cleanup=killed" "$watchdog"
grep -Fq "cgroup:[^ ]*" "$watchdog"
grep -Fq '^/user[.]slice/user-$own_uid[.]slice/session-[^/]+[.]scope$' "$watchdog"
grep -Fq 'process_scope="${record#*::}"' "$watchdog"
grep -Fq "detail=listener-still-present" "$watchdog"
grep -Fq "consecutive_heal_failures" "$watchdog"
grep -Fq ".tunnel-watchdog.log" "$watchdog"
grep -Fq ".operator-alarm" "$watchdog"
grep -Fq "event=heal-failed-twice" "$watchdog"
grep -Fq -- "-LogonType S4U" "$watchdog_registration"
grep -Fq "IntervalMinutes = 1" "$watchdog_registration"
grep -Fq "python3 -m http.server" "$forced_kill"
grep -Fq "RecoveryDeadlineSeconds = 130" "$forced_kill"
grep -Fq "forced-kill-test.md" "$forced_kill"
grep -Fq "the registered one-minute trigger supplied the second strike" "$forced_kill"

# PowerShell owns these commands as literal here-strings, but the remote host
# executes them with sh. Parse both payloads independently on every platform.
extract_remote_command() {
  local variable=$1
  local source=$2
  awk -v variable="$variable" '
    $0 == "    $" variable " = @\047" { capture=1; next }
    capture && index($0, "\047@.Replace") == 1 { exit }
    capture { print }
  ' "$source"
}
extract_remote_command command "$watchdog" | sed 's/__REMOTE_PORT__/15031/g' | bash -n
extract_remote_command injectCommand "$forced_kill" | sed 's/__REMOTE_PORT__/15031/g' | bash -n

if command -v pwsh >/dev/null 2>&1; then
  pwsh -NoProfile -NonInteractive -Command \
    "Get-ChildItem '$repo_root/deploy/windows/agent-runner-tunnel/*.ps1' | ForEach-Object { [void][System.Management.Automation.Language.Parser]::ParseFile(\$_.FullName,[ref]\$null,[ref]\$null) }"
fi

printf 'Tunnel keeper and watchdog assets contain functional probes, two-strike healing, cgroup-aware remote listener cleanup, exit capture, alarms, and session-independent task registration.\n'
