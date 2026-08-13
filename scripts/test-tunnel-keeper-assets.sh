#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
keeper="$repo_root/deploy/windows/agent-runner-tunnel/tunnel-keeper.ps1"
registration="$repo_root/deploy/windows/agent-runner-tunnel/register-tunnel-keeper.ps1"
ssh_wrapper="$repo_root/deploy/windows/agent-runner-tunnel/tunnel-ssh.ps1"
watchdog="$repo_root/deploy/windows/agent-runner-tunnel/tunnel-watchdog.ps1"
watchdog_registration="$repo_root/deploy/windows/agent-runner-tunnel/register-tunnel-watchdog.ps1"
forced_kill_test="$repo_root/deploy/windows/agent-runner-tunnel/test-tunnel-watchdog-forced-kill.ps1"

test -f "$keeper"
test -f "$registration"
test -f "$ssh_wrapper"
test -f "$watchdog"
test -f "$watchdog_registration"
test -f "$forced_kill_test"

grep -Fq "AGENT_TASK_SERVER_ROUTE_OK" "$keeper"
grep -Fq "curl --fail --silent --show-error --max-time 10" "$keeper"
grep -Fq "Get-CimInstance Win32_Process" "$keeper"
grep -Fq "Stop-MatchingForwards" "$keeper"
grep -Fq "Test-NativeArgument" "$keeper"
grep -Fq "[Regex]::Escape(\$forward)" "$keeper"
grep -Fq "ExitOnForwardFailure=yes" "$ssh_wrapper"
grep -Fq "ServerAliveInterval=30" "$ssh_wrapper"
grep -Fq "tunnel-ssh.ps1" "$keeper"
grep -Fq "ssh-exit.log" "$ssh_wrapper"
grep -Fq -- "-Event 'exit'" "$ssh_wrapper"
grep -Fq -- "-MultipleInstances IgnoreNew" "$registration"
grep -Fq -- "-RepetitionInterval" "$registration"
grep -Fq -- "-LogonType S4U" "$registration"
grep -Fq "Register-ScheduledTask" "$registration"

grep -Fq "curl -sf --max-time 6" "$watchdog"
grep -Fq "ProbeIntervalSeconds = 60" "$watchdog"
grep -Fq "FailureThreshold = 2" "$watchdog"
grep -Fq "Stop-RemoteListener" "$watchdog"
grep -Fq "listener-discovery=agent-account-sshd-fallback" "$watchdog"
grep -Fq "Stop-ScheduledTask" "$watchdog"
grep -Fq "Start-ScheduledTask" "$watchdog"
grep -Fq ".tunnel-watchdog.log" "$watchdog"
grep -Fq ".operator-alarm.log" "$watchdog"
grep -Fq "HealFailureAlarmThreshold = 2" "$watchdog"
grep -Fq -- "-Event 'operator-alarm'" "$watchdog"
grep -Fq -- "-LogonType S4U" "$watchdog_registration"
grep -Fq -- "-AtStartup" "$watchdog_registration"
grep -Fq -- "-MultipleInstances IgnoreNew" "$watchdog_registration"
grep -Fq "Register-ScheduledTask" "$watchdog_registration"
grep -Fq "RecoveryDeadlineSeconds = 150" "$forced_kill_test"
grep -Fq "event=heal-result status=healthy" "$forced_kill_test"

if command -v pwsh >/dev/null 2>&1; then
  pwsh -NoProfile -NonInteractive -Command \
    "[void][System.Management.Automation.Language.Parser]::ParseFile('$keeper',[ref]\$null,[ref]\$null); [void][System.Management.Automation.Language.Parser]::ParseFile('$registration',[ref]\$null,[ref]\$null); [void][System.Management.Automation.Language.Parser]::ParseFile('$ssh_wrapper',[ref]\$null,[ref]\$null); [void][System.Management.Automation.Language.Parser]::ParseFile('$watchdog',[ref]\$null,[ref]\$null); [void][System.Management.Automation.Language.Parser]::ParseFile('$watchdog_registration',[ref]\$null,[ref]\$null); [void][System.Management.Automation.Language.Parser]::ParseFile('$forced_kill_test',[ref]\$null,[ref]\$null)"
fi

printf 'Tunnel assets contain the functional probes, targeted two-failure heal, S4U scheduled tasks, alarm journal, SSH exit capture, and forced-kill acceptance test.\n'
